#!/usr/bin/env python3
"""Report a local variable declared in the scope of another local of the same name.

The user's compiler caught this, not me:

    GrindRunner.cs(200,29): error CS0136: A local or parameter named 'next' cannot be
    declared in this scope because that name is used in an enclosing local scope

It was real: the level-switch branch declared `int next` and the loop body around it had
`RoutePoint next`. Nothing here could see it - a parse check has no scopes, the arity and
field checks compare names to declarations, and the undeclared check asks the opposite
question (a name with no declaration at all).

The rule C# applies is small enough to model exactly:
  * a local's scope is its whole enclosing block, so a NESTED block cannot declare the
    same name (CS0136), no matter which declaration comes first;
  * the same name twice in ONE block is also an error (CS0128).

So: for each method-like body, note every local it declares (variables, out-var
declarations, local functions, parameters) and the block that owns it, then report pairs
where one block is an ancestor of the other, or the two are the same block. Sibling blocks
may reuse a name freely, which is why the check needs scopes rather than a name set.

Usage:
    python3 Tools/lint_cs_shadowing.py [path ...]    (default: the tree under work)
    python3 Tools/lint_cs_shadowing.py --all         (whole repository)

Exit code 1 when anything is reported.
"""

import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    print("lint_cs_shadowing: skipped - pip install tree_sitter tree_sitter_c_sharp")
    sys.exit(0)

PARSER = Parser(Language(tree_sitter_c_sharp.language()))

SKIP_DIRS = {".git", "obj", "bin", "node_modules", "packages", ".vs"}

DEFAULT_ROOTS = [os.path.join("Styx", "Logic", "Pathing", "ClickAim")]

# A body whose locals are scoped together.
BODY_NODES = {
    "method_declaration",
    "constructor_declaration",
    "destructor_declaration",
    "operator_declaration",
    "conversion_operator_declaration",
    "accessor_declaration",
    "local_function_statement",
}

# Nodes that open a scope for locals. Deliberately not `if_statement`: an unbraced `if`
# cannot declare anything, and its body block is a `block` anyway.
SCOPE_NODES = {
    "block",
    "switch_section",
    "for_statement",
    "for_each_statement",
    "using_statement",
    "while_statement",
    "do_statement",
    "try_statement",
    "catch_clause",
    "finally_clause",
}

DECL_NODES = {
    "variable_declarator",
    "declaration_expression",     # out var x / is var x
    "local_function_statement",
}


def walk(node):
    stack = [node]
    while stack:
        n = stack.pop()
        yield n
        stack.extend(reversed(n.children))


def cs_files(root):
    if os.path.isfile(root):
        yield root
        return
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for f in filenames:
            if f.endswith(".cs"):
                yield os.path.join(dirpath, f)


def name_of(node):
    n = node.child_by_field_name("name")
    return None if n is None else n.text.decode("utf-8", "replace")


def scope_of(node, body):
    """The innermost scope node between this declaration and the body."""
    n = node.parent
    while n is not None and n.id != body.id:
        if n.type in SCOPE_NODES:
            return n
        n = n.parent
    return body


def is_pointer_artefact(node):
    """True for `length * t` - which tree-sitter reads as a declaration of `t`.

    The C# grammar has one node, `declaration_expression`, for `out int x` and for an
    expression that merely LOOKS like `Type *name`: a multiplication. A real declaration has
    a type child (predefined_type, implicit_type `var`, a name); the artefact's is a
    pointer_type. Two false positives in this repository came from not knowing that, one of
    them in code that has compiled for a decade.
    """
    if node.type != "declaration_expression":
        return False
    t = node.child_by_field_name("type")
    return t is not None and t.type == "pointer_type"


def collect(node, body, decls):
    """Declarations of one body, NOT descending into nested bodies.

    The descent has to be pruned rather than skipped, and the first version of this file got
    that wrong: a flat walk cannot `continue` past a subtree, so a nested local function's
    locals were compared against the enclosing method's - which reported two false positives
    in code that has compiled for years.
    """
    for child in node.children:
        if child.type in BODY_NODES and child.id != body.id:
            continue
        if child.type in DECL_NODES and not is_pointer_artefact(child):
            name = name_of(child)
            if name:
                decls.append((name, scope_of(child, body), child.start_point[0] + 1))
        collect(child, body, decls)


def check_body(body, path, findings):
    """Locals declared in one method-like body, checked against each other."""
    decls = []          # (name, scope node, line)
    collect(body, body, decls)

    for child in body.children:
        if child.type == "parameter":
            decls.append((name_of(child) or "", body, child.start_point[0] + 1))

    decls = [d for d in decls if d[0]]

    by_name = {}
    for name, scope, line in decls:
        by_name.setdefault(name, []).append((scope, line))

    for name, places in sorted(by_name.items()):
        if len(places) < 2:
            continue
        for i in range(len(places)):
            for j in range(i + 1, len(places)):
                a_scope, a_line = places[i]
                b_scope, b_line = places[j]
                inner = outer = None
                if a_scope.id == b_scope.id:
                    inner, outer = None, a_scope
                elif _is_ancestor(a_scope, b_scope):
                    inner, outer = b_scope, a_scope
                elif _is_ancestor(b_scope, a_scope):
                    inner, outer = a_scope, b_scope
                if outer is None:
                    continue
                findings.append((path, min(a_line, b_line), name,
                                 "twice in one block (CS0128)"
                                 if inner is None else "in a nested block (CS0136)"))


def _is_ancestor(maybe_outer, node):
    n = node.parent
    while n is not None:
        if n.id == maybe_outer.id:
            return True
        n = n.parent
    return False


def main(argv):
    args = argv[1:]
    report_all = "--all" in args
    roots = [a for a in args if not a.startswith("--")] or (["."] if report_all else DEFAULT_ROOTS)

    files = []
    for r in roots:
        files.extend(sorted(cs_files(r)))
    if not files:
        print("lint_cs_shadowing: nothing to scan in " + " ".join(roots))
        return 0

    findings = []
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        for n in walk(root):
            if n.type in BODY_NODES:
                check_body(n, path, findings)

    for path, line, name, why in findings:
        print("%s:%d: local '%s' declared %s - C# will not compile this"
              % (path, line, name, why))

    print("%d file(s) scanned (%s), %d shadowed local(s)."
          % (len(files), "whole repository" if report_all else ", ".join(roots), len(findings)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

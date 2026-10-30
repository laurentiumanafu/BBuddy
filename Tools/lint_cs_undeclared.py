#!/usr/bin/env python3
"""Report a member that is CALLED but never DECLARED anywhere.

Written after ClickAimKeySteer lost `StopAndRest` in a rewrite: the method was still
called on the calibration path and its body was gone, which is

    error CS0103: The name 'StopAndRest' does not exist in the current context

and which no other lint here could see. The constants lint only judges names that are
constants somewhere; the arity lint only indexes names that exist; the fields lint only
looks at `_underscore` names. A deleted public method falls between all three.

How it judges, and why the false-positive rate is low: in C# almost every unqualified
call is a member of the same type or a local/parameter - BCL statics are written
`Math.Round`, `String.Format`, always qualified. So this collects unqualified calls only
(`Foo(...)`, not `x.Foo(...)`) and clears a name when it is

  * declared in the enclosing type (walking out through nesting),
  * declared as a local function, local variable or parameter in the same file,
  * declared as a member of ANY type in the repository (less precise than real scoping,
    deliberately: a false negative costs nothing, a false positive costs trust),
  * a type declared in the repository, or
  * in VALUES/ALLOW below, or a C# keyword.

Usage:
    python3 Tools/lint_cs_undeclared.py [path ...]

With no path it scans Styx/Logic/Pathing/ClickAim - the same narrow default as the
constants lint, because that is the tree that changes every hour. Pass "." for the repo.
Requires tree_sitter + tree_sitter_c_sharp; if they are missing it says so and exits 0
so that LINT.cmd stays usable without them.
"""

import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    print("lint_cs_undeclared: skipped - pip install tree_sitter tree_sitter_c_sharp")
    sys.exit(0)

DEFAULT_ROOT = os.path.join("Styx", "Logic", "Pathing", "ClickAim")

TYPES = {
    "class_declaration", "struct_declaration", "interface_declaration",
    "record_declaration", "enum_declaration",
}
# Nodes whose `name` field declares a member.
MEMBER_NODES = {
    "method_declaration", "constructor_declaration", "property_declaration",
    "field_declaration", "event_declaration", "delegate_declaration",
}
LOCAL_NODES = {"local_function_statement", "variable_declarator", "parameter",
               "enum_member_declaration", "property_pattern_clause",
               # `out var factory` and `is Foo f` - both declare a name
               "declaration_expression", "declaration_pattern"}

# Unqualified calls that are not members of the type and not in this repository.
ALLOW = {
    # C# operators and contextual keywords that parse as identifiers
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "await",
    # namespace-level BCL statics people do call unqualified via `using static`
    "Write", "WriteLine", "Sleep", "Max", "Min", "Abs", "Round", "Floor", "Ceiling",
    "Sqrt", "Pow", "Atan2", "Cos", "Sin", "Tan", "Log", "Exp", "Truncate", "Sign",
    # inherited from System.Object, so never declared here
    "ReferenceEquals", "GetType", "Equals", "ToString", "GetHashCode", "MemberwiseClone",
}

CLASS_FIELDS = {"name", "type"}


def walk(node):
    stack = [node]
    while stack:
        n = stack.pop()
        yield n
        stack.extend(reversed(n.children))


def name_of(node):
    n = node.child_by_field_name("name")
    if n is None:
        return None
    return n.text.decode("utf-8", "replace")


def declared_in_type(type_node):
    """Member names declared directly in a type (not in nested types)."""
    out = set()
    names = set()
    for child in walk(type_node):
        if child is not type_node and child.type in TYPES:
            names.add(child.id)     # do not descend into nested types' bodies
        if child.type in MEMBER_NODES or child.type in LOCAL_NODES and child.type == "enum_member_declaration":
            n = name_of(child)
            if n:
                out.add(n)
            if child.type == "field_declaration":
                for d in walk(child):
                    if d.type == "variable_declarator" and d.parent is not None:
                        v = name_of(d)
                        if v:
                            out.add(v)
        if child.type == "method_declaration":
            # a method declares itself, and its type parameters are names too
            tp = child.child_by_field_name("type_parameters")
            if tp is not None:
                for t in walk(tp):
                    if t.type == "type_parameter":
                        tn = name_of(t)
                        if tn:
                            out.add(tn)
    # drop names declared only inside nested types
    return out


def file_declarations(root):
    """Every name declared anywhere in this file, for the local-scope clearance."""
    out = set()
    for n in walk(root):
        if n.type in MEMBER_NODES or n.type in LOCAL_NODES:
            v = name_of(n)
            if v:
                out.add(v)
            if n.type == "field_declaration":
                for d in walk(n):
                    if d.type == "variable_declarator":
                        v = name_of(d)
                        if v:
                            out.add(v)
    return out


def repo_member_index(files):
    """Every member name declared in ANY of `files`, plus every type name."""
    names = set()
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        for n in walk(root):
            if n.type in MEMBER_NODES or n.type in TYPES or n.type in LOCAL_NODES:
                v = name_of(n)
                if v:
                    names.add(v)
            if n.type == "field_declaration":
                for d in walk(n):
                    if d.type == "variable_declarator":
                        v = name_of(d)
                        if v:
                            names.add(v)
    return names


def base_is_external(type_node, repo):
    """True when the type inherits from something this repository does not declare.

    A derived type can call any inherited member unqualified - `SetValue` inside a
    Control, `ContainsKey` inside a Dictionary - and no static index can know the
    surface of a BCL base without a full type system. Rather than guess, such a type is
    not judged. The classes that matter here (`internal static class ...`) have no base
    list at all, so they are always judged.
    """
    for child in type_node.children:
        if child.type != "base_list":
            continue
        for b in child.children:
            if b.type == "identifier":
                if b.text.decode("utf-8", "replace") not in repo:
                    return True
            elif b.type in {"generic_name", "qualified_name"}:
                head = next((c for c in walk(b) if c.type == "identifier"), None)
                if head is None or head.text.decode("utf-8", "replace") not in repo:
                    return True
        return False
    return False


def enclosing_type(node):
    n = node.parent
    while n is not None:
        if n.type in TYPES:
            return n
        n = n.parent
    return None


def calls_in(root, opaque_ids):
    """Unqualified calls: (line, col, name, in_opaque_type)."""
    out = []
    for n in walk(root):
        if n.type != "invocation_expression":
            continue
        fn = n.child_by_field_name("function")
        if fn is None:
            continue
        name = None
        if fn.type == "identifier":
            name = fn.text.decode("utf-8", "replace")
        elif fn.type == "generic_name":
            ident = next((c for c in fn.children if c.type == "identifier"), None)
            if ident is not None:
                name = ident.text.decode("utf-8", "replace")
        if name is None:
            continue
        t = enclosing_type(n)
        out.append((n.start_point[0] + 1, n.start_point[1], name,
                    t is not None and t.id in opaque_ids))
    return out


def cs_files(roots):
    for r in roots:
        if os.path.isfile(r):
            yield r
            continue
        for dirpath, dirnames, filenames in os.walk(r):
            dirnames[:] = [d for d in dirnames
                           if d not in {".git", "obj", "bin", "node_modules", "packages"}]
            for f in filenames:
                if f.endswith(".cs"):
                    yield os.path.join(dirpath, f)


PARSER = Parser(Language(tree_sitter_c_sharp.language()))


def main(argv):
    roots = argv[1:] or [DEFAULT_ROOT]
    scan = sorted(cs_files(roots))
    if not scan:
        print("lint_cs_undeclared: nothing to scan in " + " ".join(roots))
        return 0

    # Index the WHOLE repository for declarations, scan only `roots`. A name that exists
    # in another class is still CS0103 when reached unqualified - but that is the
    # constants lint's job (it checks exclusivity); here the cheap clearance is enough.
    everything = sorted(cs_files(["."]))
    repo = repo_member_index(everything)

    findings = 0
    for path in scan:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        local = file_declarations(root)
        opaque = {n.id for n in walk(root) if n.type in TYPES and base_is_external(n, repo)}
        for line, _col, name, in_opaque in calls_in(root, opaque):
            if name in local or name in repo or name in ALLOW:
                continue
            # A call inside a type with an external base may be an inherited member.
            if in_opaque:
                continue
            print("{0}:{1}: '{2}' is called here but no type in this repository declares "
                  "it (CS0103 unless it is a using-static or a local)".format(path, line, name))
            findings += 1

    print("{0} file(s) scanned, {1} finding(s).".format(len(scan), findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

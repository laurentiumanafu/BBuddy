#!/usr/bin/env python3
"""Report a call to something that is called WITHOUT an object and declared NOWHERE near it.

Every build break reported from the user's machine in this port has been one of two
things, and both are invisible to a parse check:

    CS0103  'RestMs' does not exist in the current context
    CS0103  'AimGaveUp' does not exist in the current context

Both were a name written into a method body that no reachable class declares - code that
reads perfectly, parses perfectly, and cannot compile. The other lints here compare against
what IS declared, so a name that was never declared has nothing to fail against.

The rule has to be FILE-local, and the first draft got this wrong: a bare call resolves
against the enclosing type (and its base classes), never against a sibling type in the same
namespace. Indexing the whole repository, that first version cleared `RestMs` because
`ClickAimMover` has a method by that name - and the real bug it was written for was exactly
that call living in `ClickAimKeySteer`, which cannot reach it.

Base classes are the one thing this cannot judge: `class X : Window` inherits
`InitializeComponent`, `Close`, `ContainsKey`, `SuspendLayout` and a thousand more names
that exist only in the framework. So a type whose base list names something this repository
does not declare is left alone - honest, and free, because the classes this check exists
for (ClickAim's static helpers) have no base list at all.

Two findings remain on a run over the whole repository, both delegate locals invoked as
`action(...)`: a field of delegate type and a method look identical here, and telling them
apart needs types this check does not have. That is why the gate runs it on the tree under
work, where it reports zero and where both historical breaks are caught.

Usage:
    python3 Tools/lint_cs_undeclared.py [path ...]    (default: the tree under work)
    python3 Tools/lint_cs_undeclared.py --all         (whole repository)

Exit code 1 when anything is reported.
"""

import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    print("lint_cs_undeclared: skipped - pip install tree_sitter tree_sitter_c_sharp")
    sys.exit(0)

PARSER = Parser(Language(tree_sitter_c_sharp.language()))

SKIP_DIRS = {".git", "obj", "bin", "node_modules", "packages", ".vs"}

DEFAULT_ROOTS = [os.path.join("Styx", "Logic", "Pathing", "ClickAim")]

TYPE_NODES = {
    "class_declaration", "struct_declaration", "record_declaration",
    "interface_declaration", "enum_declaration",
}

# Nodes that introduce a name usable without a receiver in this file.
DECLARES = {
    "method_declaration",
    "local_function_statement",
    "property_declaration",
    "event_declaration",
    "constructor_declaration",
    "destructor_declaration",
    "operator_declaration",
    "conversion_operator_declaration",
    "enum_member_declaration",
    "variable_declaration",
    "variable_declarator",
    "declaration_expression",      # out var x / is var x
    "parameter",
    "type_parameter",
    "delegate_declaration",
    "class_declaration",
    "struct_declaration",
    "interface_declaration",
    "record_declaration",
    "enum_declaration",
}

# Names that are not declared anywhere here and are not mistakes: object's own members and
# the language's contextual keywords.
ALLOWED = {
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "base", "this",
    "value", "add", "remove", "get", "set", "init", "when", "with",
    "ReferenceEquals", "GetType", "ToString", "Equals", "GetHashCode", "MemberwiseClone",
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
    if n is None:
        return None
    return n.text.decode("utf-8", "replace")


def is_pointer_artefact(node):
    """`length * t` parses as a declaration of `t`; a real one has a type, not a pointer."""
    if node.type != "declaration_expression":
        return False
    t = node.child_by_field_name("type")
    return t is not None and t.type == "pointer_type"


def declared_names(root):
    names = set()
    for n in walk(root):
        if n.type in DECLARES:
            name = name_of(n)
            if name and not is_pointer_artefact(n):
                names.add(name)
        elif n.type == "variable_declaration":
            for c in n.children:
                if c.type == "variable_declarator":
                    name = name_of(c)
                    if name:
                        names.add(name)
    return names


def type_bases(node):
    for child in node.children:
        if child.type == "base_list":
            out = []
            for b in child.children:
                if b.type in {"identifier", "qualified_name", "generic_name"}:
                    out.append(b.text.decode("utf-8", "replace").split("<")[0].split(".")[-1])
            return out
    return []


def opaque_types(root, file_names):
    """Types here that inherit from something declared OUTSIDE this file.

    Inherited members are invisible to a file-local check - and to delegate invocations of
    them, which is why `Runner(...)` in a TreeSharp composite looked undeclared until this
    rule: `Runner` is a property of a base class in another file, and `Runner(x)` is calling
    a delegate, not a method. Anything with a base it cannot see is left alone.
    """
    out = set()
    for n in walk(root):
        if n.type not in TYPE_NODES:
            continue
        name = name_of(n)
        if not name:
            continue
        for base in type_bases(n):
            if base not in file_names and base != "object":
                out.add(name)
                break
    return out


def enclosing_type(node):
    n = node.parent
    while n is not None:
        if n.type in TYPE_NODES:
            return name_of(n)
        n = n.parent
    return None


def bare_calls(root):
    """(line, name, enclosing type) for every call written without a receiver."""
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
            ident = fn.child_by_field_name("name")
            if ident is not None:
                name = ident.text.decode("utf-8", "replace")
        if name:
            out.append((n.start_point[0] + 1, name, enclosing_type(n)))
    return out


def main(argv):
    args = argv[1:]
    report_all = "--all" in args
    roots = [a for a in args if not a.startswith("--")] or (["."] if report_all else DEFAULT_ROOTS)

    files = []
    for r in roots:
        files.extend(sorted(cs_files(r)))
    if not files:
        print("lint_cs_undeclared: nothing to scan in " + " ".join(roots))
        return 0

    parsed = []
    repo_names = set()
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        names = declared_names(root)
        parsed.append((path, root, names))
        repo_names |= names

    findings = 0
    for path, root, names in parsed:
        skip = opaque_types(root, names)
        for line, name, owner in bare_calls(root):
            if name in names or name in ALLOWED:
                continue
            if owner is not None and owner in skip:
                continue
            findings += 1
            where = ""
            for other, _r, other_names in parsed:
                if other != path and name in other_names:
                    where = (" - the name exists in " + other
                             + ", which an unqualified call from here cannot reach")
                    break
            print("%s:%d: '%s(...)' - called without a receiver and not declared in this file%s"
                  % (path, line, name, where))

    print("%d file(s) scanned (%s), %d undeclared call(s), %d member name(s) indexed."
          % (len(files), "whole repository" if report_all else ", ".join(roots),
             findings, len(repo_names)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

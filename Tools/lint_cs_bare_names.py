#!/usr/bin/env python3
"""Report a bare NAME used where only a declared name can compile.

The third build break of this shape came back from the user's machine today:

    ClickAimKeySteer.cs(648,91): error CS0103: The name 'step' does not exist

It was a renamed local (`step` -> `delta`) where one occurrence in a wrapped
`string.Format(...)` argument list had been missed. Nothing here caught it: a parse check
sees a perfectly good argument list, and `lint_cs_undeclared.py` only looks at CALLS
(`Foo(...)`), not at names passed as arguments (`ms, step, error`). The two earlier CS0103s
(`RestMs`, `AimGaveUp`) were also names used as values.

So this check looks at every `identifier` that stands alone as a value - an argument, a
binary operand, a return value, a compound-assignment target - and asks whether the file
declares it anywhere at all. It is deliberately blunt: ANY declaration of that name in the
file counts (locals from other methods, fields, parameters, types, enum members), because
the goal is to catch typos and leftovers, not to prove scoping. A name that is declared
somewhere still cannot slip past the compiler, but a name that is declared NOWHERE is
always a bug - and that is exactly the class that keeps getting through.

Names that are legitimately not declared in the file itself are left alone:

  * members of a `partial` type whose other half is in the repository (the name is real);
  * members inherited from a base class this repository does not declare - the same
    `opaque_types` rule the undeclared-call check uses;
  * XAML-generated fields in `*.xaml.cs`, which have no source file to be found in;
  * C# keywords/contextual keywords and `nameof`/`typeof` operands.

Usage:
    python3 Tools/lint_cs_bare_names.py [path ...]     (default: the tree under work)
    python3 Tools/lint_cs_bare_names.py --all          (whole repository)

Exit code 1 when anything is reported.
"""

import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    print("lint_cs_bare_names: skipped - pip install tree_sitter tree_sitter_c_sharp")
    sys.exit(0)

PARSER = Parser(Language(tree_sitter_c_sharp.language()))

SKIP_DIRS = {".git", "obj", "bin", "node_modules", "packages", ".vs"}
DEFAULT_ROOTS = [os.path.join("Styx", "Logic", "Pathing", "ClickAim")]

TYPE_NODES = {
    "class_declaration", "struct_declaration", "record_declaration",
    "interface_declaration", "enum_declaration",
}

DECLARES = {
    "method_declaration", "local_function_statement", "property_declaration",
    "event_declaration", "constructor_declaration", "destructor_declaration",
    "operator_declaration", "conversion_operator_declaration", "enum_member_declaration",
    "variable_declaration", "variable_declarator", "declaration_expression",
    "parameter", "type_parameter", "delegate_declaration",
    "class_declaration", "struct_declaration", "interface_declaration",
    "record_declaration", "enum_declaration",
}

# The positions where an identifier is used as a VALUE. Anything else (member access,
# type names, declarations, named arguments) is out of scope on purpose.
VALUE_PARENTS = {"argument", "binary_expression", "return_statement", "equals_value_clause",
                 "initializer_expression", "interpolation"}

KEYWORDS = {
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "base", "this",
    "value", "add", "remove", "get", "set", "init", "when", "with", "var", "true",
    "false", "null", "new", "is", "as", "out", "ref", "in", "not", "and", "or",
    "ReferenceEquals", "GetType", "ToString", "Equals", "GetHashCode",
    "_",          # a discard: `_ = Foo();`, `out _`, `(_, b) = t`
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
    return n.text.decode("utf-8", "replace") if n is not None else None


def declared_names(root):
    names = set()
    for n in walk(root):
        if n.type in DECLARES:
            name = name_of(n)
            if name:
                names.add(name)
            if n.type == "variable_declaration":
                for c in n.children:
                    if c.type == "variable_declarator":
                        inner = name_of(c)
                        if inner:
                            names.add(inner)
        # `x is Foo f`, `case Foo f:` and lambda parameters all arrive as `parameter`, but a
        # bare `single_variable_designation` (out var / is var) has no name field.
        elif n.type == "single_variable_designation":
            names.add(n.text.decode("utf-8", "replace"))
        # `foreach (XmlNode hotspot in list)` has no declaration node at all: it is the
        # identifier immediately before the `in` keyword.
        elif n.type == "foreach_statement":
            before_in = None
            seen_in = False
            for child in n.children:
                if child.type == "in":
                    seen_in = True
                    break
                if child.type == "identifier":
                    before_in = child.text.decode("utf-8", "replace")
            if seen_in and before_in:
                names.add(before_in)
        # Lambdas: `x => ...` is an implicit parameter with no name field.
        elif n.type in {"implicit_parameter", "simple_lambda_expression"}:
            for child in n.children:
                if child.type == "identifier":
                    names.add(child.text.decode("utf-8", "replace"))
                    break
        elif n.type == "catch_declaration":
            name = name_of(n)
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


def enclosing_type_node(node):
    n = node.parent
    while n is not None:
        if n.type in TYPE_NODES:
            return n
        n = n.parent
    return None


def bare_values(root):
    """(line, column, name, enclosing type) for every identifier used as a value."""
    out = []
    for n in walk(root):
        if n.type != "identifier":
            continue
        parent = n.parent
        if parent is None or parent.type not in VALUE_PARENTS:
            continue

        # A named argument's NAME is not a value (`Foo(count: 3)`).
        if parent.type == "argument":
            named = parent.child_by_field_name("name")
            if named is not None and named.id == n.id:
                continue
            if parent.parent is not None and parent.parent.type == "attribute":
                continue

        # `nameof(x)` / `typeof(x)` operands are type or member names, not values.
        if parent.type == "argument" and parent.parent is not None:
            fn = parent.parent.child_by_field_name("function")
            if fn is not None and fn.text.decode("utf-8", "replace") in {"nameof", "typeof", "sizeof"}:
                continue

        owner = enclosing_type_node(n)
        out.append((n.start_point[0] + 1, n.start_point[1] + 1,
                    n.text.decode("utf-8", "replace"),
                    name_of(owner) if owner is not None else None))
    return out


def main(argv):
    args = argv[1:]
    report_all = "--all" in args
    roots = [a for a in args if not a.startswith("--")] or (["."] if report_all else DEFAULT_ROOTS)

    files = []
    for r in roots:
        files.extend(sorted(cs_files(r)))
    if not files:
        print("lint_cs_bare_names: nothing to scan in " + " ".join(roots))
        return 0

    parsed = []
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        parsed.append((path, root, declared_names(root)))

    # Partial classes: a name declared in ANOTHER file that also declares a type by the
    # same name is real (the other half of the partial), so it is not a finding.
    type_names_in_file = {}
    for path, root, _names in parsed:
        types = set()
        for n in walk(root):
            if n.type in TYPE_NODES:
                t = name_of(n)
                if t:
                    types.add(t)
        type_names_in_file[path] = types

    findings = 0
    for path, root, names in parsed:
        if path.endswith(".xaml.cs"):
            continue                      # XAML-generated fields have no source to find

        opaque = set()
        for n in walk(root):
            if n.type not in TYPE_NODES:
                continue
            t = name_of(n)
            if t and any(base not in type_names_in_file[path] and base != "object"
                         for base in type_bases(n)):
                opaque.add(t)

        for line, col, name, owner in bare_values(root):
            if name in names or name in KEYWORDS:
                continue
            if owner is not None and owner in opaque:
                continue

            where = ""
            for other, _r, other_names in parsed:
                if other == path or name not in other_names:
                    continue
                shared = type_names_in_file[path] & type_names_in_file[other]
                if shared:
                    where = " - declared in " + other + " for the partial type " + ", ".join(sorted(shared))
                    break
                where = " - it exists in " + other + ", which cannot supply it to an unqualified use here"
                break
            if where.startswith(" - declared in"):
                continue

            findings += 1
            print("%s:%d:%d: '%s' is used as a value and declared nowhere in this file%s"
                  % (path, line, col, name, where))

    print("%d file(s) scanned (%s), %d name(s) used but never declared."
          % (len(files), "whole repository" if report_all else ", ".join(roots), findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

#!/usr/bin/env python3
"""Report a call to something that is called WITHOUT an object and declared NOWHERE.

Every build break reported from the user's machine in this port has been one of two
things, and both are invisible to a parser:

    CS0103  'RestMs' does not exist in the current context
    CS0103  'AimGaveUp' does not exist in the current context

Both were a name written into a method body that no class ever declared - code that reads
perfectly, parses perfectly, and cannot compile. A parse check passes it, and the other
lints here only compare against what IS declared, so a name that was never declared has
nothing to fail against.

So: index every declared member name in the repository, then check every bare call
(`Foo(...)` with no receiver). A bare call must resolve to something - the enclosing
type, any type in the repository, a local, a parameter - and when it resolves to nothing
at all, it is either a mistake in this repository or an unqualified call into a namespace
nobody here declares. Both are worth a look before the user's compiler finds them.

Deliberately one-directional. It does not check receivers (`x.Foo()`), because it has no
type system and would be guessing; it does not check fields against types. It answers one
question honestly: is this name declared anywhere at all?

The rule has to be FILE-local, and the first draft got this wrong: a bare call is
resolved against the enclosing type (and its base classes), never against a sibling type
in the same namespace. So the index is built per file. Indexing the whole repository, the
first version declared `RestMs` "declared" because `ClickAimMover` has a method of that
name - and the real bug it was written for was exactly that call living in
`ClickAimKeySteer`, where it does not exist.

Scope is the tree under work, for one more reason: a bare call can also be a legitimate
implicit-`this` call into a base class that is not in this repository (`InitializeComponent`
on a WPF window, `SuspendLayout` on a Form, `ContainsKey` on a Dictionary), and no
receiver-less check can tell those from a mistake. In ClickAim that noise does not arise -
those classes are static and call helpers with explicit receivers - so a bare name that is
not declared in the file is a real CS0103 waiting for the user's compiler. `--all` runs
the whole repository and is for reading, not for a gate.

Usage:
    python3 Tools/lint_cs_undeclared.py [path ...]    (default: ClickAim and UI)
    python3 Tools/lint_cs_undeclared.py --all         (whole repository; expect base-class noise)

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

# Members that declare a name usable without a receiver inside the same file.
MEMBER_NODES = {
    "method_declaration",
    "local_function_statement",
    "declaration_expression",      # out var x / is var x
    "property_declaration",
    "event_declaration",
    "constructor_declaration",
    "destructor_declaration",
    "operator_declaration",
    "conversion_operator_declaration",
    "enum_member_declaration",
    "variable_declarator",
    "parameter",
    "type_parameter",
    "class_declaration",
    "struct_declaration",
    "interface_declaration",
    "record_declaration",
    "enum_declaration",
    "delegate_declaration",
}

# Names that are not declared here and are not mistakes: C# aliases and the handful of
# unqualified statics that are imported rather than written.
ALLOWED = {
    "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "base", "this",
    "value", "add", "remove", "get", "set", "init",
    # object's own members, callable with no receiver and declared by no file here
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


def declared_names(root):
    """Every name this file declares, usable unqualified inside it."""
    out = set()
    for n in walk(root):
        if n.type in MEMBER_NODES:
            name = name_of(n)
            if name:
                out.add(name)
        elif n.type == "variable_declaration":
            # int a, b, c;  - the declarators are separate nodes but be explicit
            for c in n.children:
                if c.type == "variable_declarator":
                    name = name_of(c)
                    if name:
                        out.add(name)
    return out


def bare_calls(root):
    """(line, name) for every call written without a receiver."""
    out = []
    for n in walk(root):
        if n.type != "invocation_expression":
            continue
        fn = n.child_by_field_name("function")
        if fn is None:
            continue
        if fn.type == "identifier":
            out.append((n.start_point[0] + 1, fn.text.decode("utf-8", "replace")))
        elif fn.type == "generic_name":
            ident = fn.child_by_field_name("name")
            if ident is not None:
                out.append((n.start_point[0] + 1, ident.text.decode("utf-8", "replace")))
    return out


DEFAULT_ROOTS = [os.path.join("Styx", "Logic", "Pathing", "ClickAim")]


def other_file(name, per_file, exclude):
    for path, _root, names, _calls in per_file:
        if path != exclude and name in names:
            return path
    return "another file"


def main(argv):
    args = argv[1:]
    report_all = "--all" in args
    roots = [a for a in args if not a.startswith("--")] or (["."] if report_all else DEFAULT_ROOTS)
    files = []
    for r in roots:
        files.extend(sorted(cs_files(r)))

    repo_names = set()
    per_file = []
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        names = declared_names(root)
        repo_names |= names
        per_file.append((path, root, names, bare_calls(root)))

    findings = 0
    for path, _root, local_names, calls in per_file:
        for line, name in calls:
            if name in local_names or name in ALLOWED:
                continue
            findings += 1
            where = (" - the name exists in " + other_file(name, per_file, path) + ", which an "
                     "unqualified call cannot reach") if name in repo_names else ""
            print("%s:%d: '%s(...)' - called without a receiver and not declared in this file%s"
                  % (path, line, name, where))

    print("%d file(s) scanned (%s), %d undeclared call(s), %d member name(s) indexed."
          % (len(files), "whole repository" if report_all else ", ".join(roots),
             findings, len(repo_names)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

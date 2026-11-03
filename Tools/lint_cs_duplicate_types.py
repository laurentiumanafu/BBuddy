#!/usr/bin/env python3
"""Report a TYPE name declared twice in different namespaces of this repository.

Written after `GrindArea` - a class added to `Styx.Logic.Pathing` - broke the build with

    Profile.cs(150,11): error CS0104: 'GrindArea' is an ambiguous reference between
    'Styx.Logic.AreaManagement.GrindArea' and 'Styx.Logic.Pathing.GrindArea'

The repository already had a `GrindArea`. Nothing in the file that DECLARES the new type
is wrong, which is why no other lint here can see it: the error appears in unrelated
files that happen to import both namespaces and use the bare name. Two types with one
name are not illegal in C# - they are a landmine whose blast radius is decided later, by
whoever adds a `using`.

So: index every type declaration, and report any simple name declared more than once
unless the declarations disagree only by file (a `partial` type legitimately spans
files). Generic arity is part of the name (`Foo` and `Foo<T>` are different types), so
the key includes the type-parameter count.

The whole repository is indexed, but only names declared in the TREE UNDER WORK are
reported - the same shape as lint_cs_constants.py. That matters: this codebase already
carries 51 duplicated type names (`Vector3` twice, `Wait` twice, three `WoW*` pairs),
all harmless because no file imports both namespaces. Reporting those would bury the one
that just broke the build. A name that is new in the tree under work and already exists
anywhere is the case that bites.

Usage:
    python3 Tools/lint_cs_duplicate_types.py [path ...]
        default: Styx/Logic/Pathing/ClickAim and UI (the tree that changes)
    python3 Tools/lint_cs_duplicate_types.py --all
        every duplicate in the repository, including the 51 legacy ones

Exit code 1 when anything is reported.
"""

import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:
    print("lint_cs_duplicate_types: skipped - pip install tree_sitter tree_sitter_c_sharp")
    sys.exit(0)

PARSER = Parser(Language(tree_sitter_c_sharp.language()))

TYPE_NODES = {
    "class_declaration": "class",
    "struct_declaration": "struct",
    "interface_declaration": "interface",
    "record_declaration": "record",
    "enum_declaration": "enum",
    "delegate_declaration": "delegate",
}

SKIP_DIRS = {".git", "obj", "bin", "node_modules", "packages", ".vs"}


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


def file_namespace(root):
    """The name of a `namespace X;` file-scoped declaration, or "" if there is none.

    In this grammar a file-scoped namespace has the declarations as SIBLINGS, not
    children (`file_scoped_namespace_declaration` holds only `namespace Name ;`), so
    walking up from a class never reaches it. Every ClickAim file uses that form, which
    is how RECT was first reported as living in the global namespace.
    """
    for child in root.children:
        if child.type == "file_scoped_namespace_declaration":
            name = child.child_by_field_name("name")
            if name is not None:
                return name.text.decode("utf-8", "replace")
    return ""


def namespace_of(node, file_ns=""):
    """Walk out to the innermost enclosing block namespace, else the file-scoped one."""
    parts = []
    n = node.parent
    while n is not None:
        if n.type == "namespace_declaration":
            name = n.child_by_field_name("name")
            if name is not None:
                parts.append(name.text.decode("utf-8", "replace"))
        n = n.parent
    if parts:
        return ".".join(reversed(parts))
    return file_ns


def is_nested(node):
    """True when the type is declared inside another type.

    A nested type's real name is `Outer.Inner`, so it cannot collide with a
    namespace-level `Inner` - that is what CS0104 is about. ClickAimInput.RECT is nested
    and therefore innocent, even though three RECTs exist in this repository.
    """
    n = node.parent
    while n is not None:
        if n.type in TYPE_NODES:
            return True
        n = n.parent
    return False


def arity_of(node):
    tps = node.child_by_field_name("type_parameters")
    if tps is None:
        return 0
    return sum(1 for c in tps.children if c.type == "type_parameter")


def is_partial(node):
    for c in node.children:
        if c.type == "modifier" and c.text.decode("utf-8", "replace") == "partial":
            return True
    return False


DEFAULT_ROOTS = [os.path.join("Styx", "Logic", "Pathing", "ClickAim"), "UI"]


def index(files):
    """key -> list of (file, line, namespace, kind, partial)."""
    seen = {}
    for path in files:
        try:
            src = open(path, "rb").read()
        except OSError:
            continue
        root = PARSER.parse(src).root_node
        file_ns = file_namespace(root)
        for n in walk(root):
            kind = TYPE_NODES.get(n.type)
            if kind is None:
                continue
            if is_nested(n):
                continue
            name_node = n.child_by_field_name("name")
            if name_node is None:
                continue
            name = name_node.text.decode("utf-8", "replace")
            key = (name, arity_of(n))
            seen.setdefault(key, []).append(
                (path, n.start_point[0] + 1, namespace_of(n, file_ns), kind, is_partial(n)))
    return seen


def main(argv):
    args = argv[1:]
    report_all = "--all" in args
    roots = [a for a in args if not a.startswith("--")] or (["."] if report_all else DEFAULT_ROOTS)

    everything = sorted({f for f in cs_files(".") for f in [f]})
    scan = set()
    for r in roots:
        scan.update(os.path.normpath(f) for f in cs_files(r))

    if not scan:
        print("lint_cs_duplicate_types: nothing to scan in " + " ".join(roots))
        return 0

    seen = index(everything)

    findings = 0
    for (name, arity), decls in sorted(seen.items()):
        if len(decls) < 2:
            continue
        namespaces = {d[2] for d in decls}
        # A partial type spread over files is ONE type; only different namespaces make
        # the bare name ambiguous.
        if len(namespaces) < 2:
            continue
        if not report_all:
            if not any(os.path.normpath(d[0]) in scan for d in decls):
                continue

        findings += 1
        shown = name if arity == 0 else "%s<...>" % name
        print("'%s' is declared %d times:" % (shown, len(decls)))
        for path, line, ns, kind, partial in decls:
            mark = "  <-- in the tree under work" if os.path.normpath(path) in scan else ""
            print("    %s:%d: %s %s%s in %s%s" % (
                path, line, kind, name,
                " (partial)" if partial else "",
                ns if ns else "<global>", mark))
        print("    -> a file that imports both namespaces cannot use the bare name "
              "(CS0104). Rename the new one.")

    scope = "the whole repository" if report_all else ", ".join(roots)
    print("%d file(s) scanned (%s), %d duplicated type name(s)." % (len(everything), scope, findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

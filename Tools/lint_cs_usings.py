#!/usr/bin/env python3
"""lint_cs_usings.py -- find a type used from a namespace the file never imported.

Why this exists: on 2026-10-29 `WowBSigScan.cs` failed the user's build with 13
x CS0103 "The name 'Logging' does not exist in the current context". The file
parsed cleanly (tree-sitter) and both other lints passed, because none of them
resolve names: `Logging` is a real class, in `Styx.Helpers`, and the file was in
`Styx.WoWInternals` without `using Styx.Helpers;`.

So: index every type this repo declares (name -> namespace), then for each file
look for a type it uses but cannot see - not declared in the file, not in the
file's own namespace, not imported by a using. That is a CS0103/CS0246 one edit
away from happening again.

    python3 Tools/lint_cs_usings.py [root]

Exit code 1 when something is found.
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."

SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".vs", "packages"}

DECL = re.compile(
    r"^\s*(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|\s)*"
    r"\b(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
    re.M)
# `static class` specifically: a static class can never be a variable, so `Name.`
# at a statement is the type itself and cannot be a false positive. That is what
# makes this check precise enough to keep.
STATIC_CLASS = re.compile(
    r"^\s*(?:public|internal|private|protected)?[\s]*static[\s]+(?:sealed |abstract )?"
    r"class\s+([A-Za-z_][A-Za-z0-9_]*)",
    re.M)
NS_FILE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;", re.M)
NS_BLOCK = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*\{?", re.M)
USING = re.compile(r"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z0-9_.]+)\s*;", re.M)

# Types whose name is so common that a bare mention is not evidence of a use.
GENERIC_NAMES = {
    "Point", "Timer", "Color", "Size", "Rectangle", "Matrix", "Vector3", "Vector2",
    "Exception", "Memory", "Thread", "Path", "File", "Directory", "Task", "Random",
    "Stopwatch", "Encoding", "Marshal", "Process", "Converter", "Action", "Func",
    "Guid", "Math", "Console", "Environment", "Enum", "Delegate", "Attribute",
    "Object", "String", "List", "Dictionary", "KeyValuePair", "Nullable", "Type",
    # A name that also exists in the BCL cannot be judged from a bare mention:
    # ResourceManager is System.Resources.ResourceManager in the BCL and
    # Styx.Resources.ResourceManager here, and which one a file means is decided
    # by imports this lint does not model (the .csproj enables ImplicitUsings).
    "ResourceManager",
}


def cs_files(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return ""


def main():
    files = list(cs_files(ROOT))
    if not files:
        print("no .cs files under %s" % ROOT)
        return 1

    # ---- pass 1: every declared type -> the namespace it lives in -------------
    owner = {}
    static_names = set()
    namespaces = set()
    for path in files:
        src = read(path)
        m = NS_FILE.search(src) or NS_BLOCK.search(src)
        if not m:
            continue
        ns = m.group(1)
        namespaces.add(ns)
        for d in DECL.finditer(src):
            name = d.group(2)
            if name in GENERIC_NAMES:
                continue
            owner.setdefault(name, set()).add(ns)
        for d in STATIC_CLASS.finditer(src):
            static_names.add(d.group(1))

    # Ambiguous names (declared in several namespaces) cannot be judged from a
    # bare identifier, so leave them out rather than guess.
    owner = {k: v for k, v in owner.items() if len(v) == 1}
    type_ns = {k: next(iter(v)) for k, v in owner.items()}

    findings = []
    for path in files:
        src = read(path)
        m = NS_FILE.search(src) or NS_BLOCK.search(src)
        here = m.group(1) if m else ""
        usings = set(USING.findall(src))
        declared_here = {d.group(2) for d in DECL.finditer(src)}

        # Strip comments and strings so a name in prose is not a use.
        code = re.sub(r"//[^\n]*", "", src)
        code = re.sub(r"/\*.*?\*/", "", code, flags=re.S)
        code = re.sub(r'"(?:[^"\\]|\\.)*"', '""', code)
        code = re.sub(r"@\"(?:[^\"]|\"\")*\"", '""', code)

        # One alternation for the whole file instead of a regex per type name.
        candidates = [n for n in static_names if n in type_ns and n not in declared_here]
        if not candidates:
            continue
        candidates.sort(key=len, reverse=True)
        alt = re.compile(r"(?<![A-Za-z0-9_.])(" + "|".join(map(re.escape, candidates)) + r")\s*\.")
        # C# resolves `A.B` by walking outward from the file's namespace, so inside
        # `Styx.WoWInternals`, `Helpers.Logging` means `Styx.Helpers.Logging` with no
        # using at all. A name that is a NAMESPACE here is therefore not a type use.
        prefixes = [""]
        if here:
            parts = here.split(".")
            prefixes += [".".join(parts[:i]) for i in range(1, len(parts) + 1)]

        def is_namespace_ident(ident):
            return any(((p + "." + ident).strip(".")) in namespaces for p in prefixes)

        seen = set()
        for hit in alt.finditer(code):
            name = hit.group(1)
            if name in seen:
                continue
            if is_namespace_ident(name):
                continue
            ns = type_ns[name]
            if ns == here or here.startswith(ns + "."):
                continue
            if any(u == ns or u.startswith(ns + ".") for u in usings):
                continue
            if any(ns.startswith(u + ".") for u in usings):
                continue
            seen.add(name)
            line = code[:hit.start()].count("\n") + 1
            findings.append((path, line, name, ns, here))

    # Worst offenders first: these are the ones that will not compile.
    for path, line, name, ns, here in findings:
        print("%s:%d: '%s' is in namespace %s but this file is in %s and does not import it"
              % (path, line, name, ns, here or "<global>"))

    print("\n%d file(s) scanned, %d finding(s)." % (len(files), len(findings)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())

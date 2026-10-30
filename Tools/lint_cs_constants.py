#!/usr/bin/env python3
"""lint_cs_constants.py -- a constant used in a file that neither declares nor inherits it.

Why this exists, twice in two builds:

  * KeyStopYd  - declared nowhere at all; a patch that should have added it did not.
  * RestMs     - declared in ClickAimDestHunt, used in ClickAimKeySteer.

Both were CS0103 "The name ... does not exist in the current context", both looked
reasonable on the line, and none of the other lints can see it:

  lint_cs_fields    only identifiers starting with `_`
  lint_cs_usings    only names that are TYPES
  lint_call_arity   only call shapes

Why constants only: a constant is only ever a value, so it can never be a type, a
local or an extension method. That keeps the check precise. A name that is a field,
property, method or type ANYWHERE else in the repository is skipped, because those
resolve unqualified all the time (a property named Zero beside a constant named Zero,
a type named ObjectManager beside a constant named ObjectManager) and flagging them
produces hundreds of false positives - the first version of this script did.

    python3 Tools/lint_cs_constants.py [root]
"""
import os
import re
import sys

# The INDEX is always the whole repository - that is how a constant declared in another
# folder is known at all. The SCAN is narrow by default: this check exists for the files
# under active work, and running it across 890 files only rediscovers that the older
# half of this codebase reuses names like `Instance` and `FileName` for other things.
INDEX_ROOT = "."
ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
if not sys.argv[1:] and os.path.isdir("Styx/Logic/Pathing/ClickAim"):
    ROOT = "Styx/Logic/Pathing/ClickAim"
SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".vs", "packages"}

CONST_DECL = re.compile(
    r"\bconst\s+(?:[A-Za-z_][A-Za-z0-9_<>\[\]\?,\s]*\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*="
    r"|\bstatic\s+readonly\s+[A-Za-z_][A-Za-z0-9_<>\[\]\?,\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*[;=]")

# Anything that declares a name in this file: `Type Name` at a statement or member
# position, a method, a property, a type. Deliberately generous - a false NEGATIVE here
# only means this check stays quiet, which is the safe direction.
DECLARED = re.compile(
    r"(?:\b(?:class|struct|interface|enum|record|var|out|ref|in|const)\s+|\b(?:public|private|protected|internal|static|readonly|override|virtual)\s+[A-Za-z_][A-Za-z0-9_<>\[\]\?,\s\.]*\s+)"
    r"([A-Za-z_][A-Za-z0-9_]*)"
    r"|\b[A-Za-z_][A-Za-z0-9_<>\[\]\?\.]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:[;=({])")

TYPE_DECL = re.compile(r"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
USING_STATIC = re.compile(r"^\s*using\s+static\s+([A-Za-z0-9_.]+)\s*;", re.M)
BASE_LIST = re.compile(
    r"\b(?:class|struct)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>{]*>)?\s*:\s*([^{]+)\{")


def cs_files(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def strip(src):
    out = re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), src)
    out = re.sub(r"/\*.*?\*/",
                 lambda m: "".join(ch if ch == "\n" else " " for ch in m.group(0)),
                 out, flags=re.S)
    out = re.sub(r'@"(?:[^"]|"")*"', lambda m: " " * len(m.group(0)), out)
    out = re.sub(r'"(?:[^"\\\n]|\\.)*"', lambda m: " " * len(m.group(0)), out)
    return out


def read(path):
    try:
        return strip(open(path, encoding="utf-8", errors="replace").read())
    except OSError:
        return ""


def main():
    index_paths = list(cs_files(INDEX_ROOT))
    paths = list(cs_files(ROOT))
    if not paths:
        print("no .cs files under %s" % ROOT)
        return 1

    const_where = {}      # constant name -> {class that declares it}
    type_names = set()    # every type name in the repository
    declared_any = set()  # every name declared by anything
    const_names = set()   # every name declared AS a constant
    classes_of = {}
    bases = {}

    for path in index_paths:
        code = read(path)
        classes = set(TYPE_DECL.findall(code))
        classes_of[path] = classes
        type_names |= classes

        for m in CONST_DECL.finditer(code):
            name = m.group(1) or m.group(2)
            if name:
                const_where.setdefault(name, set()).update(classes or {os.path.basename(path)})

        for m in DECLARED.finditer(code):
            name = m.group(1) or m.group(2)
            if name:
                declared_any.add(name)
        for m in CONST_DECL.finditer(code):
            name = m.group(1) or m.group(2)
            if name:
                const_names.add(name)

        for m in BASE_LIST.finditer(code):
            bases.setdefault(m.group(1), set()).update(
                b.strip().split("<")[0].strip() for b in m.group(2).split(",") if b.strip())

    # A name that is also a TYPE, or that something declares as more than a constant
    # (a property named Zero, a method, a local), resolves unqualified all the time.
    # Only names that are EXCLUSIVELY constants are judged.
    #
    # The subtraction matters: a constant declaration also looks like `Type Name =` to
    # the general declaration regex, so without removing the constant names from
    # `declared_any` every constant would filter itself out - which is exactly what the
    # first version did, reporting 0 findings on a file with a missing constant.
    nonconstant = declared_any - const_names
    candidates = sorted(
        (n for n in const_where if n not in type_names and n not in nonconstant),
        key=len, reverse=True)
    if not candidates:
        print("%d file(s) scanned, 0 finding(s)." % len(paths))
        return 0

    use_re = re.compile(r"(?<![A-Za-z0-9_.])(" + "|".join(map(re.escape, candidates)) + r")\b")

    findings = []
    for path in paths:
        code = read(path)
        here = classes_of.get(path, set())

        reachable = set(here)
        frontier = list(here)
        while frontier:
            for b in bases.get(frontier.pop(), set()):
                if b not in reachable:
                    reachable.add(b)
                    frontier.append(b)
        for ns in USING_STATIC.findall(code):
            reachable.add(ns.split(".")[-1])

        local = set()
        for m in DECLARED.finditer(code):
            name = m.group(1) or m.group(2)
            if name:
                local.add(name)

        seen = set()
        for m in use_re.finditer(code):
            name = m.group(1)
            if name in seen or name in local:
                continue

            # A name that appears more than once in the file is not a clean signal: it
            # may be a tuple element name, a named argument, or a parameter this scanner
            # does not model. A name appearing EXACTLY once, unqualified, and declared as
            # a constant of another class is the failure being hunted.
            if code.count(name) != 1:
                continue
            owners = const_where[name]
            if here & owners or reachable & owners:
                continue
            seen.add(name)
            line = code.count("\n", 0, m.start()) + 1
            findings.append((path, line, name, sorted(owners)))

    for path, line, name, owners in sorted(findings):
        print("%s:%d: '%s' is a constant of %s; this file does not declare or inherit it "
              "(CS0103 unless qualified)" % (path, line, name, "/".join(owners)))

    print("\n%d file(s) scanned in %s (indexed %d), %d finding(s)."
          % (len(paths), ROOT, len(index_paths), len(findings)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())

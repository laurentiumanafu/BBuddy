#!/usr/bin/env python3
"""lint_cs_fields.py -- catch a `_field` that is used but never declared (CS0103).

Why this exists: on 2026-10-30 a patch was applied with

    s.replace('    private double _aimHeadingBeforeDrag = double.NaN;', fields + anchor)

Nothing was replaced, because that line no longer existed - an earlier commit had
rewritten the block. `str.replace` returns the string unchanged and says nothing, so
the patch "succeeded", the commit went in, and the user's build produced seven
CS0103s: `_keyMode`, `_keyTried`, `KeyStopYd` do not exist in the current context.

The other lints cannot see this: the file parses, every call has an overload, and
every TYPE resolves. A missing FIELD is name resolution too - just for a different
kind of name - so it is checked here.

Scope: identifiers beginning with `_`, which by convention in this codebase are
fields. A constant written without an underscore (`KeyStopYd`) is NOT covered - the
2026-10-30 build had one of those too - because a bare CamelCase identifier is far
too often a type member, an extension method or a system name to judge from text. An identifier that is used but appears nowhere as a declaration is reported.
Locals are declarations too (`var _x = ...`, `int _x;`), so a local does not trip it.

    python3 Tools/lint_cs_fields.py [root]
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".vs", "packages"}

IDENT = re.compile(r"(?<![A-Za-z0-9_.])_([A-Za-z][A-Za-z0-9_]*)")


def cs_files(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def strip(src):
    """Comments and string/char literals blanked out, line count preserved."""
    out = re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), src)
    out = re.sub(r"/\*.*?\*/",
                 lambda m: "".join(ch if ch == "\n" else " " for ch in m.group(0)),
                 out, flags=re.S)
    out = re.sub(r'@"(?:[^"]|"")*"', lambda m: " " * len(m.group(0)), out)
    out = re.sub(r'"(?:[^"\\\n]|\\.)*"', lambda m: " " * len(m.group(0)), out)
    out = re.sub(r"'(?:[^'\\\n]|\\.)'", lambda m: " " * len(m.group(0)), out)
    return out


def main():
    findings = []
    scanned = 0

    for path in cs_files(ROOT):
        scanned += 1
        try:
            with open(path, encoding="utf-8", errors="replace") as fh:
                src = fh.read()
        except OSError:
            continue

        code = strip(src)

        # A declaration of _name is: a type or var followed by it, possibly in a
        # parameter/out/in position, a foreach, a catch, a lambda parameter, or an
        # assignment (`_x = ...` where the type sits on an earlier line).
        declared = set()
        for m in re.finditer(
                r"(?:\b(?:var|out|ref|in)\s+|\b(?:foreach)\s*\(\s*(?:var|int|string|double|float|bool|long|ulong|object)\s+)"
                r"_([A-Za-z][A-Za-z0-9_]*)", code):
            declared.add(m.group(1))
        # `Type _name` (fields, locals, parameters) - two identifiers in sequence.
        for m in re.finditer(r"[A-Za-z_][A-Za-z0-9_<>\[\]\.\?,]*\s+_([A-Za-z][A-Za-z0-9_]*)\s*[;=,)]", code):
            declared.add(m.group(1))
        # generic type args, e.g. Dictionary<int, string> _map
        for m in re.finditer(r">\s+_([A-Za-z][A-Za-z0-9_]*)\s*[;=,)]", code):
            declared.add(m.group(1))
        # Multi-declarator statements: `private double _legX, _legY;` only names the
        # type once, so every `_name` up to the semicolon on such a line is declared.
        KEYWORDS = {
            "return", "throw", "case", "else", "await", "yield", "goto", "new", "in",
            "out", "ref", "using", "if", "while", "for", "foreach", "switch", "do",
            "lock", "fixed", "sizeof", "typeof", "checked", "unchecked", "default",
            "is", "as", "and", "or", "not", "when", "from", "select", "where",
        }
        for stmt in re.finditer(
                r"^[^\n;]*?\b([A-Za-z_][A-Za-z0-9_<>\[\]\?,\.]*)\s+_[A-Za-z][A-Za-z0-9_]*[^;\n]*;",
                code, re.M):
            # `return _x;` is not a declaration: the token before a name is only a TYPE
            # if it is not a keyword.
            if stmt.group(1) in KEYWORDS:
                continue
            for m in re.finditer(r"(?<![A-Za-z0-9_.])_([A-Za-z][A-Za-z0-9_]*)", stmt.group(0)):
                declared.add(m.group(1))

        # Methods and properties can carry an underscore name too (`_EnumWindows()`),
        # and they declare the identifier just as well as a field does.
        for m in re.finditer(
                r"[A-Za-z_][A-Za-z0-9_<>\[\]\?,\.]*\s+_([A-Za-z][A-Za-z0-9_]*)\s*\(", code):
            declared.add(m.group(1))

        used = {}
        for m in IDENT.finditer(code):
            used.setdefault(m.group(1), m.start())

        for name, pos in used.items():
            if name in declared:
                continue
            line = code.count("\n", 0, pos) + 1
            findings.append((path, line, "_" + name))

    for path, line, name in sorted(findings):
        print("%s:%d: '%s' is used but never declared in this file (CS0103)" % (path, line, name))

    print("\n%d file(s) scanned, %d finding(s)." % (scanned, len(findings)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""lint_cs_overflow_literals.py -- catch `int` targets fed a hex literal typed `uint`.

Why this exists: ClickAimInput.cs failed to build twice on the same line with
CS0266 "Cannot implicitly convert type 'uint' to 'int'". The line was

    lp |= unchecked(0xC0000000);

`unchecked()` suppresses ARITHMETIC overflow checking; it does not change the type
of the literal. 0xC0000000 is greater than int.MaxValue, so C# gives it the type
uint, and `lp |= <uint>` is `lp = lp | <uint>` -> uint -> will not convert back to
int. The cast has to be inside:

    lp |= unchecked((int)0xC0000000);

Whether a cast is needed depends on a value being above int.MaxValue, which is
exactly the kind of thing that is invisible when reading the line, so it is checked
here instead.

    python3 Tools/lint_cs_overflow_literals.py [root]
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".vs", "packages"}

INT_MAX = 0x7FFFFFFF
LONG_MAX = 0x7FFFFFFFFFFFFFFF

# unchecked( 0x.... ) with nothing between the paren and the literal
UNCHECKED_HEX = re.compile(r"unchecked\s*\(\s*(0[xX][0-9A-Fa-f_]+)")
# ... and the forms that make it legal
CASTS = ("(int)", "(uint)", "(long)", "(ulong)", "(short)", "(ushort)", "(byte)", "(sbyte)")


def cs_files(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def value_of(literal):
    try:
        return int(literal.replace("_", ""), 16)
    except ValueError:
        return None


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

        # Comments are prose, not code: blank them out but keep the line count.
        code = re.sub(r"//[^\n]*", lambda mo: " " * len(mo.group(0)), src)
        code = re.sub(r"/\*.*?\*/",
                      lambda mo: "".join(ch if ch == "\n" else " " for ch in mo.group(0)),
                      code, flags=re.S)
        code = re.sub(r'"(?:[^"\\]|\\.)*"',
                      lambda mo: " " * len(mo.group(0)), code)

        for m in UNCHECKED_HEX.finditer(code):
            lit = m.group(1)
            v = value_of(lit)
            if v is None or v <= INT_MAX:
                continue                      # fits an int already
            if v > LONG_MAX:
                continue

            # Legal only if a cast sits between "unchecked(" and the literal.
            start = m.start(1)
            between = code[m.start() : start]
            if any(c in between for c in CASTS):
                continue

            line = code.count("\n", 0, m.start()) + 1
            findings.append((path, line, lit,
                             "uint" if v <= 0xFFFFFFFF else "ulong"))

    for path, line, lit, kind in findings:
        print("%s:%d: unchecked(%s) is typed %s (above int.MaxValue) with no cast - "
              "use unchecked((int)%s) or assign it to a %s" % (path, line, lit, kind, lit, kind))

    print("\n%d file(s) scanned, %d finding(s)." % (scanned, len(findings)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())

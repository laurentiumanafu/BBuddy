#!/usr/bin/env python3
"""
A tiny type-confusion lint for the two hand-maintained C# files.

Why it exists: the sandbox that writes this port has no C# compiler, so a wrong
declaration (a counter declared `bool`, say) reaches the user's machine as a
build error. 0.8.0 did exactly that - `_hijackSkippedBusyMoments` was declared
`bool` and used with `++` and `> 0` (CS0023/CS0019 on Windows).

This is NOT a compiler and does not try to be: it strips comments and literals,
collects declarations, and reports the few mistakes that have actually happened
here and are cheap to detect:

  * a `bool` used with an arithmetic/relational operator (++ -- += > < ...)
  * a numeric/IntPtr used as a condition (`if (x)`, `!x`, `x ? a : b`)
  * a `bool` assigned something that is not a condition
  * a numeric assigned `true`/`false`

Usage:  python3 Tools/lint_cs_types.py            (both files, repo-relative)
        python3 Tools/lint_cs_types.py FILE...    (any C# files)

Exit code 1 when anything is reported, so it can gate a commit.
"""

import collections
import io
import re
import sys

BOOLS = {'bool'}
NUMERIC = {'int', 'uint', 'long', 'ulong', 'short', 'ushort', 'byte', 'sbyte',
           'double', 'float', 'decimal', 'IntPtr', 'Int64', 'UInt64', 'UInt32', 'nint', 'nuint'}
DEFAULT_FILES = ['GreenMagic/LuaBridge64.cs', 'Tools/LuaBridgeProbe/Program.cs']


def strip_literals(src: str) -> str:
    """Blank out comments and string/char literals, keeping line structure intact."""
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                out.append(' ')
                i += 1
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                out.append('\n' if src[i] == '\n' else ' ')
                i += 1
            out.append('  ')
            i += 2
            continue
        if c in '"\'':
            quote = c
            out.append(' ')
            i += 1
            while i < n:
                if src[i] == '\\':
                    out.append('  ')
                    i += 2
                    continue
                if src[i] == quote:
                    out.append(' ')
                    i += 1
                    break
                out.append('\n' if src[i] == '\n' else ' ')
                i += 1
            continue
        if c == '@' and i + 1 < n and src[i + 1] == '"':          # verbatim string
            i += 2
            out.append('  ')
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        i += 2
                        out.append('  ')
                        continue
                    out.append(' ')
                    i += 1
                    break
                out.append('\n' if src[i] == '\n' else ' ')
                i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def declarations(clean: str):
    """name -> set of declared type names (heuristic; a name may appear twice)."""
    decl = collections.defaultdict(set)
    pattern = (r'\b(?:private|public|protected|internal|static|readonly|const|volatile)?\s*'
               r'([A-Za-z_][\w\.<>\[\]\?]*)\s+([A-Za-z_]\w*)\s*(?:=|;|,|\)|\[)')
    for m in re.finditer(pattern, clean):
        decl[m.group(2)].add(m.group(1))
    return decl


def lint(path: str):
    src = io.open(path, encoding='utf-8').read()
    clean = strip_literals(src)
    decl = declarations(clean)
    findings = set()

    for name, types in decl.items():
        if len(types) != 1:
            continue
        t = next(iter(types))

        if t in BOOLS:
            for m in re.finditer(r'(?<![\w\.])' + re.escape(name) + r'\s*(\+\+|--|\+=|-=|\*=|/=|>|<|>=|<=)', clean):
                line = clean[:m.start()].count('\n') + 1
                findings.add((line, name, t, m.group(1), 'bool used with an arithmetic/relational operator'))

        if t in NUMERIC:
            for m in re.finditer(r'(if\s*\(\s*' + re.escape(name) + r'\s*\)|\!\s*' + re.escape(name) +
                                 r'\b|\b' + re.escape(name) + r'\s*\?\s)', clean):
                line = clean[:m.start()].count('\n') + 1
                findings.add((line, name, t, 'if/!/?:', 'numeric used as a condition (looks like a bool)'))

        for m in re.finditer(r'(?<![\w\.])' + re.escape(name) + r'\s*=\s*([^;]{0,80});', clean):
            rhs = m.group(1).strip()
            line = clean[:m.start()].count('\n') + 1
            if t in BOOLS and not re.match(r'^(true|false)\b', rhs) and not re.search(
                    r'(==|!=|&&|\|\||!|\breturn\b|>|<|\(|\bIs\w|\bTry\w|\bHas\w|\bContains\b)', rhs):
                findings.add((line, name, t, '= ' + rhs[:40], 'bool assigned something that is not a condition'))
            if t in NUMERIC and re.match(r'^(true|false)$', rhs):
                findings.add((line, name, t, '= ' + rhs, 'numeric assigned true/false'))

    return sorted(findings)


def main(argv):
    files = argv[1:] or DEFAULT_FILES
    bad = 0
    for path in files:
        findings = lint(path)
        print('== %s: %d finding(s)' % (path, len(findings)))
        for line, name, t, op, why in findings:
            print('   line %-6d %-30s declared %-8s used with %-7s %s' % (line, name, t, op, why))
        bad += len(findings)
    if bad:
        print('\n%d finding(s). This is a heuristic: look at each one before believing it,' % bad)
        print('but every finding so far has been either a real bug or a comparison the regex missed.')
        return 1
    print('\nclean (heuristic - still no substitute for the compiler on the user\'s machine)')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))

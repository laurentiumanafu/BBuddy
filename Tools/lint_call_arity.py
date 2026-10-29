#!/usr/bin/env python3
"""Call-arity check for the C# sources, because this sandbox has no compiler.

Why: `Tools/CsParseCheck` (tree-sitter) catches syntax, not semantics. A renamed or
re-shaped method leaves call sites that still parse and still look right, and the
first sign of trouble is `error CS7036: There is no argument given that corresponds
to the required parameter ...` from the build on the Windows machine - one wasted
build cycle per mistake. That happened with
`Navigator.FindNearestPolyRef(mapId, position, Extents, out ref, out pt)` after the
extents overload was split into two scalars.

What it does: collects every method declaration in the tree (name -> smallest legal
argument count, largest legal argument count), then every call site by name, and
reports calls that no overload could accept. Optional parameters widen the minimum
down and `params` widens the maximum up, so a legal call is never flagged for the
common reasons; wherever it is unsure it stays quiet.

    python3 Tools/lint_call_arity.py .

Exit status is 1 when something was found, so it can gate a commit.
"""
import os
import re
import sys

CS_KEYWORDS = {
    'if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'return',
    'sizeof', 'typeof', 'nameof', 'checked', 'unchecked', 'default', 'fixed',
    'stackalloc', 'new', 'base', 'this', 'throw', 'when', 'where', 'select',
    'from', 'let', 'orderby', 'group', 'into', 'is', 'as', 'await', 'yield',
    'delegate', 'do', 'else', 'try', 'finally', 'get', 'set', 'add', 'remove',
}

# Names that cannot be checked by name alone: the tree declares several unrelated
# methods with the same name (framework overrides, params-array helpers, the
# obfuscated method_0..method_9 from the original assemblies), so an arity that
# matches no declaration here usually matches one in an assembly we cannot see.
IGNORED = {
    'Add', 'Stats', 'Refresh', 'Invoke', 'Call', 'Push', 'TraceSelector',
    'GetNumAuctionItems', 'GetGossipType', 'CanNavigateFully',
    'CreateCustomBehaviorInstance',
    # Win32 P/Invoke, declared extern in this tree and bound by the runtime.
    'GetWindowInfo', 'SendMessage', 'PostMessage', 'CreateRemoteThread',
    'GetExitCodeThread', 'VirtualAllocEx', 'ReadProcessMemory', 'WriteProcessMemory',
    'method_0', 'method_1', 'method_2', 'method_3', 'method_4',
    'method_5', 'method_6', 'method_7', 'method_8', 'method_9',
}

DECL = re.compile(
    r'(?P<mods>(?:\b(?:public|private|protected|internal|static|virtual|override|'
    r'abstract|sealed|async|extern|unsafe|partial|new|readonly)\b\s+)+)'
    r'(?P<ret>[\w\.<>\[\],\?]+)\s+'
    r'(?P<name>[A-Za-z_]\w*)\s*'
    r'(?:<[^<>()]*>)?\s*'
    r'\((?P<params>[^;{}()]*)\)\s*'
    r'(?:where\s+[^{;]+?)?(?P<body>\{|=>|;)'
)


def strip_comments_and_strings(text):
    """Blank out comments, strings and chars so call sites are not counted in them."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ''
        if c == '/' and nxt == '/':
            j = text.find('\n', i)
            i = n if j < 0 else j
        elif c == '/' and nxt == '*':
            j = text.find('*/', i + 2)
            i = n if j < 0 else j + 2
        elif c == '"' or (c == '@' and nxt == '"'):
            verbatim = c == '@'
            i += 2 if verbatim else 1
            while i < n:
                if verbatim and text[i] == '"' and text[i + 1:i + 2] == '"':
                    i += 2
                    continue
                if not verbatim and text[i] == '\\':
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                if not verbatim and text[i] == '\n':
                    break
                i += 1
            out.append('""')
        elif c == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == '\\' else 1
            i += 1
            out.append("''")
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def split_params(params):
    """Top-level comma split (generics included)."""
    parts, depth, cur = [], 0, []
    for ch in params:
        if ch in '<([':
            depth += 1
        elif ch in '>)]':
            depth -= 1
        if ch == ',' and depth == 0:
            parts.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    tail = ''.join(cur).strip()
    if tail:
        parts.append(tail)
    return [p for p in parts if p]


def param_bounds(params_text):
    """(minimum legal arguments, maximum legal arguments or None for unbounded)."""
    params = split_params(params_text)
    if not params:
        return 0, 0
    minimum, maximum, unbounded = 0, 0, False
    for p in params:
        if p.startswith('this '):
            continue                     # extension method receiver
        if re.search(r'\bparams\b', p):
            unbounded = True
            maximum += 1
            continue
        maximum += 1
        if '=' not in p:
            minimum += 1
    return minimum, (None if unbounded else maximum)


def collect_declarations(root):
    bounds = {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in
                       ('obj', 'bin', '.git', 'node_modules', 'Unselected files')]
        for fn in filenames:
            if not fn.endswith('.cs'):
                continue
            path = os.path.join(dirpath, fn)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            clean = strip_comments_and_strings(text)
            for m in DECL.finditer(clean):
                name = m.group('name')
                if name in CS_KEYWORDS or m.group('ret').endswith('operator'):
                    continue
                # P/Invoke declarations are only ever called through a qualified
                # name ("NativeMethods.Foo(...)"), which the call scan skips, so
                # their arity would just add a false interval to the name.
                if 'extern' in m.group('mods'):
                    continue
                bounds.setdefault(name, set()).add(param_bounds(m.group('params')))
    return bounds


def count_args(clean, open_index):
    """(number of top-level arguments, index just past the closing paren)."""
    depth, i, n = 0, open_index, len(clean)
    commas, empty = 0, True
    while i < n:
        ch = clean[i]
        if ch in '([{':
            depth += 1
            if depth == 1 and ch == '(':
                i += 1
                continue
        elif ch in ')]}':
            depth -= 1
            if depth == 0:
                return (0 if empty else commas + 1), i + 1
        elif ch == ',' and depth == 1:
            commas += 1
        if depth >= 1 and not ch.isspace() and ch != '(':
            empty = False
        i += 1
    return None, None


CALL = re.compile(r'(?<![\w\.])(?P<name>[A-Za-z_]\w*)\s*\(')
DECL_SKIP = re.compile(r'(?:\bnew\b|\bclass\b|\bstruct\b|\binterface\b|\benum\b|\boperator\b)\s*$')


def check_calls(root, bounds):
    problems = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in
                       ('obj', 'bin', '.git', 'node_modules', 'Unselected files')]
        for fn in filenames:
            if not fn.endswith('.cs'):
                continue
            path = os.path.join(dirpath, fn)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            clean = strip_comments_and_strings(text)
            for m in CALL.finditer(clean):
                name = m.group('name')
                if name in CS_KEYWORDS or name not in bounds:
                    continue
                head = clean[max(0, m.start() - 24):m.start()]
                if DECL_SKIP.search(head):
                    continue
                # A declaration itself, not a call: "<ret> Name(" with modifiers before.
                if re.search(r'\b(?:public|private|protected|internal|static|virtual|override|'
                             r'abstract|sealed|async|extern|unsafe|partial)\b[\w\s<>\[\],\?\.]*$', head):
                    continue
                args, close = count_args(clean, m.end() - 1)
                if args is None:
                    continue
                # A declaration, not a call: a body follows the parameter list.
                after = clean[close:close + 24].lstrip()
                if after[:1] in '{' or after.startswith('=>') or after.startswith('where'):
                    continue
                if name in IGNORED:
                    continue
                intervals = bounds[name]
                if any(lo <= args and (hi is None or args <= hi) for lo, hi in intervals):
                    continue
                line = clean.count('\n', 0, m.start()) + 1
                allowed = ', '.join(sorted(
                    '%d' % lo if hi is None else ('%d' % lo if lo == hi else '%d-%d' % (lo, hi))
                    for lo, hi in intervals))
                problems.append((path, line, name, args, allowed))
    return problems


def main(argv):
    args = [a for a in argv[1:] if not a.startswith('--')]
    flags = [a for a in argv[1:] if a.startswith('--')]
    root = args[0] if args else '.'
    bounds = collect_declarations(root)
    problems = check_calls(root, bounds)
    for path, line, name, args, allowed in problems:
        print('%s:%d: %s(...) called with %d argument(s); declarations in this tree take %s'
              % (os.path.relpath(path, root), line, name, args, allowed))
    print('%d method name(s) indexed, %d call(s) no overload accepts' % (len(bounds), len(problems)))
    # Advisory by default: this is a heuristic over names, not a compiler. --strict
    # makes it gate a commit, which is worth it only for code being changed now.
    return 1 if (problems and '--strict' in flags) else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))

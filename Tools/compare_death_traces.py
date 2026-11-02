#!/usr/bin/env python3
"""Compare sample-zero raw words across labeled 70707 traces; never infer gameplay state."""
import argparse
import re
from dataclasses import dataclass
from pathlib import Path

SAMPLE = re.compile(r'^sample=(\d+) startMs=\d+ endMs=\d+ utc=\S+$')
BASE = re.compile(r'^player\+0x([0-9A-F]{4}) baseline ([0-9A-F]+)$')
IDENTITY = re.compile(r'^player=(0x[0-9A-F]{32})\s')
HEADER = re.compile(r'^UTC .*; label=(.*)$')
MAX_WINDOW = 0x2800


@dataclass
class Trace:
    label: str
    identity: str
    chunks: dict
    samples: int
    status: str

    def word(self, offset, width=4):
        base = offset & ~255
        data = self.chunks.get(base)
        if data is None or offset - base + width > len(data):
            return None
        return int.from_bytes(data[offset - base:offset - base + width], 'little')


def read_trace(path):
    lines = Path(path).read_text(encoding='utf-8-sig').splitlines()
    if not lines or not lines[0].startswith('70707 COMBAT RESEARCH v1:'):
        raise ValueError('not a 70707 CombatResearch v1 trace')
    identity = label = None
    chunks = {}
    current_sample = None
    samples = 0
    status = 'missing end status'
    for line in lines:
        m = HEADER.match(line)
        if m:
            label = m.group(1)
        m = IDENTITY.match(line)
        if m:
            identity = m.group(1)
        m = SAMPLE.match(line)
        if m:
            current_sample = int(m.group(1))
            samples += 1
            continue
        if line.startswith('end: '):
            status = line[5:]
        m = BASE.match(line)
        if m and current_sample == 0:
            offset, data = int(m.group(1), 16), bytes.fromhex(m.group(2))
            if offset >= MAX_WINDOW or offset % 256 or len(data) != 256 or offset in chunks:
                raise ValueError('invalid or duplicate sample-zero baseline chunk')
            chunks[offset] = data
    if identity is None or label is None or not chunks or samples == 0:
        raise ValueError('missing identity, label or sample-zero baseline')
    return Trace(label, identity, chunks, samples, status)


def compare(paths):
    traces = [read_trace(p) for p in paths]
    if len(traces) != 3:
        raise ValueError('three traces required: alive, dead before release, ghost')
    if len({t.identity for t in traces}) != 1:
        raise ValueError('player GUIDs differ; refusing to compare different characters')
    lines = ['Raw sample-zero observations only; not verified death/ghost state.']
    for trace in traces:
        lines.append(f'{trace.label!r}: {trace.samples} sample(s), {trace.status}')
    lines.append('Fields below follow the input order shown above; supplied labels may be ambiguous:')
    for name, offset, width in [
        ('health candidate', 0x1E28, 8), ('maximum health candidate', 0x1E30, 8),
        ('other health-like word', 0x1BB8, 4), ('unknown word', 0x0110, 4),
        ('static ghost-byte candidate', 0x26D8, 1),
    ]:
        vals = [t.word(offset, width) for t in traces]
        text = ' / '.join('not recorded' if value is None else f'0x{value:0{width * 2}X}' for value in vals)
        lines.append(f'  +0x{offset:04X} {name}: {text}')
    lines.append('Labels are user supplied; one trace per state cannot establish semantics. Missing bytes are not zero.')
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('alive')
    parser.add_argument('dead')
    parser.add_argument('ghost')
    args = parser.parse_args()
    try:
        print(compare([args.alive, args.dead, args.ghost]))
    except (ValueError, OSError) as exc:
        parser.exit(1, f'Cannot compare: {exc}\n')


if __name__ == '__main__':
    main()

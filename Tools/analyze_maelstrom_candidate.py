#!/usr/bin/env python3
"""Offline 70938 research trace: candidate ID/stack word, not verified gameplay state."""
import argparse
import re
import struct
from pathlib import Path

STRIDE, MAX_SLOTS, IDENT = 0x128, 32, 408505
SAMPLE = re.compile(r'^sample=(\d+) startMs=(\d+)')
BASE = re.compile(r'^external baseline pointer=0x[0-9A-F]+ length=(\d+) capacity=(\d+) bytes=([0-9A-F]+)$')
CHANGE = re.compile(r' \+0x([0-9A-F]{4}):([0-9A-F]{8})>([0-9A-F]{8})')


def read(path):
    raw, count, clock, at, external_seen = None, None, None, None, False
    events, previous = [], object()

    def emit():
        nonlocal previous
        if at is None or not external_seen:
            return
        value = None
        if raw is not None and count is not None and clock is not None:
            slots = [(struct.unpack_from('<I', raw, i + 0xe8)[0], i) for i in range(0, len(raw), STRIDE)]
            ids = [ident for ident, _ in slots if ident]
            found = [i for ident, i in slots if ident == IDENT]
            if len(ids) == len(set(ids)) == count and len(found) == 1:
                i = found[0]
                stack = struct.unpack_from('<I', raw, i + 0x104)[0]
                duration = struct.unpack_from('<I', raw, i + 0xf4)[0]
                expiry = struct.unpack_from('<I', raw, i + 0xf8)[0]
                remaining = ((expiry - clock + 0x80000000) & 0xffffffff) - 0x80000000
                value = (i // STRIDE, stack, duration, remaining)
        key = None if value is None else value[:3]
        if key != previous:
            events.append((at, value))
            previous = key

    with Path(path).open(encoding='utf-8-sig') as stream:
        header = stream.readline()
        if not (header.startswith('70938 COMBAT RESEARCH v1:') or header.startswith('70959 COMBAT RESEARCH v1:')):
            raise ValueError('not a 70938/70959 combat research trace')
        for line in stream:
            line = line.rstrip('\r\n')
            m = SAMPLE.match(line)
            if m:
                emit()
                at, external_seen = int(m.group(2)), False
            elif line.startswith('player+0x1A00 baseline '):
                chunk = bytes.fromhex(line.split()[-1]); count = struct.unpack_from('<I', chunk, 0x60)[0] if len(chunk) == 256 else None
            elif line.startswith('player+0x1C00 baseline '):
                chunk = bytes.fromhex(line.split()[-1]); clock = struct.unpack_from('<I', chunk, 0x10)[0] if len(chunk) == 256 else None
            elif line.startswith('player+0x1A00 unreadable'): count = None
            elif line.startswith('player+0x1C00 unreadable'): clock = None
            elif line.startswith('player changes'):
                for change in CHANGE.finditer(line):
                    off, old, new = (int(v, 16) for v in change.groups())
                    if off == 0x1a60: count = new if count == old else None
                    if off == 0x1c10: clock = new if clock == old else None
            elif line.startswith('external unavailable:'):
                raw, external_seen = None, True
            elif (m := BASE.match(line)):
                length, capacity = int(m.group(1)), int(m.group(2))
                data = bytes.fromhex(m.group(3))
                if not (0 < length <= MAX_SLOTS and length <= capacity <= 36 and len(data) == length * STRIDE):
                    raise ValueError('external buffer outside bounds')
                raw, external_seen = bytearray(data), True
            elif line.startswith('external changes'):
                external_seen = True
                changes = list(CHANGE.finditer(line))
                if not changes or ''.join(m.group(0) for m in changes) != line[len('external changes'):]:
                    raise ValueError('malformed external changes')
                if raw is not None:
                    for change in changes:
                        off, old, new = (int(v, 16) for v in change.groups())
                        if off % 4 or off + 4 > len(raw) or struct.unpack_from('<I', raw, off)[0] != old:
                            raw = None
                            break
                        struct.pack_into('<I', raw, off, new)
            elif line == 'external stable': external_seen = True
    emit()
    return events


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace')
    args = parser.parse_args()
    print('Candidate fields only; unknown is not absent, timestamps and cast are unverified.')
    for ms, value in read(args.trace):
        print(f'{ms / 1000:.3f}s: unknown' if value is None else
              f'{ms / 1000:.3f}s: slot={value[0]} stack-word={value[1]} duration-word={value[2]} expiry-minus-clock={value[3]}')


if __name__ == '__main__':
    main()

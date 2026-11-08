#!/usr/bin/env python3
"""Summarize experimental 70862 external-candidate traces; never infer verified auras."""
import argparse
import re
import struct
from pathlib import Path

STRIDE = 0x128  # hypothesis inherited from the old inline observation
MAX_SLOTS = 24
ID_WITHIN_SLOT = 0xE8
SAMPLE = re.compile(r'^sample=(\d+) startMs=(\d+)')
BASELINE = re.compile(r'^external baseline pointer=0x([0-9A-F]+) length=(\d+) capacity=(\d+) bytes=([0-9A-F]+)$')
CHANGE = re.compile(r' \+0x([0-9A-F]{4}):([0-9A-F]{8})>([0-9A-F]{8})')


def candidate_slots(raw):
    if len(raw) % STRIDE or len(raw) > MAX_SLOTS * STRIDE:
        raise ValueError('candidate buffer outside expected bounds')
    slots = []
    for i in range(len(raw) // STRIDE):
        offset = i * STRIDE + ID_WITHIN_SLOT
        ident, duration, expiry = (struct.unpack_from('<I', raw, offset + x)[0] for x in (0, 12, 16))
        if ident:
            slots.append((i, ident, duration, expiry))
    return tuple(slots)


def read_trace(path):
    """Reconstruct candidate triples across samples. Failures erase state, never imply absence."""
    events, raw, last, samples, status = [], None, None, 0, 'missing end status'
    at = None
    with Path(path).open(encoding='utf-8-sig') as f:
        if not f.readline().startswith('70862 COMBAT RESEARCH v1:'):
            raise ValueError('not a 70862 Combat Research v1 trace')
        for line in f:
            line = line.rstrip('\r\n')
            m = SAMPLE.match(line)
            if m:
                samples += 1
                at = int(m.group(2))
                continue
            if line.startswith('end: '):
                status = line[5:]
                continue
            if line.startswith('external unavailable:'):
                raw, last = None, None
                events.append((at, 'unavailable', None))
                continue
            m = BASELINE.match(line)
            if m:
                length, capacity = int(m.group(2)), int(m.group(3))
                data = bytes.fromhex(m.group(4))
                if not (1 <= length <= MAX_SLOTS and length <= capacity <= 32 and len(data) == length * STRIDE):
                    raise ValueError('external baseline outside bounds')
                raw = bytearray(data)
                last = candidate_slots(raw)
                events.append((at, 'baseline', last))
                continue
            if line.startswith('external changes'):
                if raw is None:
                    events.append((at, 'changes without baseline; ignored', None))
                    continue
                matches = list(CHANGE.finditer(line))
                if not matches or ''.join(m.group(0) for m in matches) != line[len('external changes'):]:
                    raise ValueError('malformed changes')
                valid = True
                for m in matches:
                    offset, old, new = (int(x, 16) for x in m.groups())
                    if offset % 4 or offset + 4 > len(raw) or struct.unpack_from('<I', raw, offset)[0] != old:
                        valid = False
                        break
                    struct.pack_into('<I', raw, offset, new)
                if not valid:
                    raw, last = None, None
                    events.append((at, 'inconsistent change; baseline invalidated', None))
                    continue
                slots = candidate_slots(raw)
                if slots != last:
                    events.append((at, 'candidate fields changed', slots))
                    last = slots
    return samples, status, events


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace')
    args = parser.parse_args()
    samples, status, events = read_trace(args.trace)
    print(f'samples={samples}; {status}')
    print('Candidate triples only: IDs, layout, duration, expiry and completeness are NOT verified. Unavailable is not absent.')
    if not events:
        print('No external candidate data found; ensure the checkbox was enabled and inspect the original trace.')
    for at, kind, slots in events:
        print(f'{at / 1000:.2f}s {kind}' if at is not None else f'unknown time: {kind}')
        if slots is not None:
            for index, ident, duration, expiry in slots:
                print(f'  slot={index} candidate-id={ident} duration-word={duration} expiry-word=0x{expiry:08X}')


if __name__ == '__main__': main()

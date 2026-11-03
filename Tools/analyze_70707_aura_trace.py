#!/usr/bin/env python3
"""Inspect candidate 70707 aura-like slots in a CombatResearch trace, offline only.

This is a hypothesis based on one Mage session, not a gameplay aura reader.
An omitted ID is NOT evidence of absence: capacity, dynamic storage, slot
semantics, expiry and clock behavior are unverified. No memory or Lua access.
"""
import argparse
import re
import struct
from pathlib import Path

from analyze_combat_trace import SAMPLE, BASE, CHANGE, trace_label

WINDOW = 0x2800
CHUNK = 0x100
SLOT_ID = 0x6F0
STRIDE = 0x128
SLOT_COUNT = 12  # bounded research window, NOT a proven capacity
CLOCK = 0x1C10
UNREADABLE = re.compile(r"^player\+0x([0-9A-F]{4}) unreadable$")


def inspect(words, spell_filter):
    """Only report complete candidate triples and a readable same-sample clock."""
    clock = words.get(CLOCK)
    found = {}
    for slot in range(SLOT_COUNT):
        off = SLOT_ID + slot * STRIDE
        spell, duration, expiry = (words.get(off), words.get(off + 12), words.get(off + 16))
        if spell is None or duration is None or expiry is None or clock is None or spell == 0:
            continue
        if spell_filter and spell not in spell_filter:
            continue
        # Signed modular subtraction is only a candidate interpretation; never
        # use this value to make a gameplay decision or infer an indefinite aura.
        delta = ((expiry - clock + 0x80000000) & 0xFFFFFFFF) - 0x80000000
        found[slot] = (spell, duration, expiry, delta)
    return found


def read_trace(path, spell_filter=()):
    words = {}
    samples = 0
    status = None
    at = None
    timeline = []
    previous = None

    def invalidate(chunk):
        for off in range(chunk, chunk + CHUNK, 4):
            words.pop(off, None)

    def emit(force=False):
        nonlocal previous
        if at is None:
            return
        found = inspect(words, spell_filter)
        # Do not label a missing slot "removed": it might be unreadable, moved,
        # outside the observed range, or an unknown representation.
        snapshot = tuple(sorted((slot, value[0], value[1], value[2]) for slot, value in found.items()))
        if previous != snapshot or (force and (not timeline or timeline[-1][0] != at)):
            timeline.append((at, found))
            previous = snapshot

    with Path(path).open(encoding='utf-8-sig') as stream:
        if not stream.readline().startswith('70707 COMBAT RESEARCH v1:'):
            raise ValueError('not a 70707 CombatResearch v1 file')
        for raw_line in stream:
            line = raw_line.rstrip('\r\n')
            m = SAMPLE.match(line)
            if m:
                emit()
                at = int(m.group(2))
                samples += 1
                continue
            if line.startswith('end: '):
                status = line[5:]
                continue
            m = UNREADABLE.match(line)
            if m:
                chunk = int(m.group(1), 16)
                if chunk >= WINDOW or chunk % CHUNK:
                    raise ValueError('invalid unreadable chunk offset')
                invalidate(chunk)
                continue
            m = BASE.match(line)
            if m and m.group(1) == 'player':
                chunk = int(m.group(2), 16)
                data = bytes.fromhex(m.group(3))
                if chunk >= WINDOW or chunk % CHUNK or len(data) != CHUNK:
                    raise ValueError('invalid player baseline')
                invalidate(chunk)
                for i in range(0, CHUNK, 4):
                    words[chunk + i] = struct.unpack_from('<I', data, i)[0]
                continue
            if line.startswith('player changes'):
                for item in CHANGE.finditer(line):
                    off, old, new = (int(value, 16) for value in item.groups())
                    if off >= WINDOW or off % 4:
                        raise ValueError('invalid change offset')
                    if off not in words:
                        continue  # failed chunk: wait for a complete new baseline
                    if words[off] != old:
                        invalidate(off // CHUNK * CHUNK)  # truncated/inconsistent history
                        continue
                    words[off] = new
    emit(force=True)  # show the latest candidate clock difference even without a slot change
    return samples, status, timeline


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace')
    parser.add_argument('--spell-id', action='append', type=lambda value: int(value, 0), default=[],
                        help='only show a numeric ID; may be repeated')
    args = parser.parse_args()
    if any(not 0 < spell <= 0xFFFFFFFF for spell in args.spell_id):
        parser.error('spell ID must be a positive 32-bit integer')
    samples, status, timeline = read_trace(args.trace, set(args.spell_id))
    print(f'Label: {trace_label(args.trace)}; samples: {samples}; end: {status or "missing/incomplete"}')
    print('Candidate fields only: no proof of aura presence, absence, units, expiry or slot capacity.')
    for at, slots in timeline:
        print(f'{at / 1000:.2f}s candidate snapshot:')
        if not slots:
            print('  no complete matching candidate in the bounded window (not proof of absence)')
        for slot, (spell, duration, expiry, delta) in sorted(slots.items()):
            print(f'  slot={slot} id={spell} id-offset=0x{SLOT_ID + slot * STRIDE:04X} '
                  f'duration-word={duration} expiry-word=0x{expiry:08X} '
                  f'candidate expiry-minus-clock={delta} (units unverified)')
    if not samples:
        print('No samples found.')


if __name__ == '__main__':
    main()

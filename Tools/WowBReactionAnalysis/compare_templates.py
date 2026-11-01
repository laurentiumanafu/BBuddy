#!/usr/bin/env python3
"""Trace the table-only relation helper offline; NOT live reaction or attackability."""
import argparse
import json
import sys
from inspect_table import inspect


def template_relation(first, second):
    """Replicate the normal-path numeric result of RVA 0x27EE3B0 on decoded rows.

    Does not evaluate UnitReaction's caller-side, forced-reaction, reputation,
    owner/PvP, dynamic state, overlay, or UnitCanAttack conditions.
    """
    if len(first) != 7 or len(second) != 7:
        raise ValueError('expected seven fields')
    for row in (first, second):
        if any(type(row[i]) is not int for i in range(5)):
            raise ValueError('unavailable scalar field')
        for i in (5, 6):
            if not isinstance(row[i], dict):
                raise ValueError('unavailable array field')
            values = row[i].get('values')
            if not isinstance(values, list) or len(values) != 8 or any(type(v) is not int or v < 0 or v > 0xFFFF for v in values):
                raise ValueError('unavailable or malformed array palette')

    def contains_prefix(array, key):
        for value in array:
            if value == 0:  # client loop stops at the first zero, not the last
                break
            if value == (key & 0xFFFF):
                return True
        return False

    if (first[4] & second[2]) != 0 or contains_prefix(first[5]['values'], second[0]):
        return 1
    if (first[3] & second[2]) != 0 or contains_prefix(first[6]['values'], second[0]):
        return 4
    if (first[2] & second[3]) != 0 or contains_prefix(second[6]['values'], first[0]):
        return 4
    return 1 if (first[1] & 0x2000) != 0 else 3


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', help='private reaction samples JSONL')
    parser.add_argument('first', type=int, help='first FactionTemplate key')
    parser.add_argument('second', type=int, help='second FactionTemplate key')
    args = parser.parse_args()
    try:
        with open(args.file, encoding='utf-8-sig') as source:
            snapshots = [json.loads(line).get('dbProbe') for line in source if line.strip()]
        available = [x for x in snapshots if x and x.get('rowBytes') and x.get('arrayPaletteBytes')]
        if not available:
            raise ValueError('no table snapshot with array palettes')
        first = inspect(available[-1], args.first)
        second = inspect(available[-1], args.second)
        print('Template-only numerical results, NOT live UnitReaction or UnitCanAttack:')
        print(f'  {args.first} -> {args.second}: {template_relation(first, second)}')
        print(f'  {args.second} -> {args.first}: {template_relation(second, first)}')
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        print(f'Cannot compare: {exc}', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

#!/usr/bin/env python3
"""Compare two same-player, same-target state captures; diagnostic, not a classifier."""
import argparse
import base64
import json
import struct
import sys


def decode(value, expected):
    if not isinstance(value, str) or len(value) != ((expected + 2) // 3) * 4:
        raise ValueError('state bytes missing or wrong size')
    result = base64.b64decode(value, validate=True)
    if len(result) != expected:
        raise ValueError('state bytes missing or wrong size')
    return result


def compare(first, second):
    for key in ('playerGuid', 'targetGuid'):
        value = first.get(key)
        if not isinstance(value, str) or not value or value != second.get(key):
            raise ValueError('samples must have the same player and target GUIDs')
    a, b = first.get('stateProbe'), second.get('stateProbe')
    if not isinstance(a, dict) or not isinstance(b, dict):
        raise ValueError('state diagnostic not captured in both samples')
    ar = decode(a.get('reputationBytes'), 16000)
    br = decode(b.get('reputationBytes'), 16000)
    af = decode(a.get('playerCandidateBytes'), 256)
    bf = decode(b.get('playerCandidateBytes'), 256)
    # Output indices, not raw state values or GUIDs. A changed slot is a lead,
    # not proof of faction identity, reaction, at-war state or attackability.
    rep = [i for i in range(1000) if ar[i * 16:(i + 1) * 16] != br[i * 16:(i + 1) * 16]]
    player = [i for i in range(32) if af[i * 8:(i + 1) * 8] != bf[i * 8:(i + 1) * 8]]
    return rep, player


def unit_words(sample, field):
    """Known candidate health qwords and unit flags; missing chunks remain unknown."""
    chunks = sample.get(field)
    if not isinstance(chunks, list):
        return None
    matches = [c for c in chunks if isinstance(c, dict) and c.get('offset') == 0x1E00]
    if not matches:
        return None
    if len(matches) != 1:
        raise ValueError('repeated unit chunk')
    data = decode(matches[0].get('bytes'), 256)
    health, maximum = struct.unpack_from('<QQ', data, 0x30)
    flags = struct.unpack_from('<I', data, 0x60)[0]
    return health, maximum, flags


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', help='private JSONL samples')
    parser.add_argument('before', type=int, help='first JSONL line number, starting at 1')
    parser.add_argument('after', type=int, help='second JSONL line number, starting at 1')
    args = parser.parse_args()
    try:
        if args.before < 1 or args.after < 1:
            raise ValueError('line numbers start at 1')
        with open(args.file, encoding='utf-8-sig') as source:
            lines = source.readlines()
        if args.before > len(lines) or args.after > len(lines):
            raise ValueError('line number outside file')
        first, second = json.loads(lines[args.before - 1]), json.loads(lines[args.after - 1])
        rep, player = compare(first, second)
        print('Candidate reputation slots changed:', rep)
        print('Candidate player override slots changed:', player)
        for label, field in (('Player', 'playerChunks'), ('Target', 'targetChunks')):
            before, after = unit_words(first, field), unit_words(second, field)
            if before is not None and after is not None:
                print(f'{label} health candidate: {before[0]}/{before[1]} -> {after[0]}/{after[1]}')
                print(f'{label} flags candidate +0x1E60: 0x{before[2]:08X} -> 0x{after[2]:08X}')
            else:
                print(f'{label} health/flags: unavailable')
        print('Changes are leads only; no reaction or attackability is inferred.')
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        print(f'Cannot compare: {exc}', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

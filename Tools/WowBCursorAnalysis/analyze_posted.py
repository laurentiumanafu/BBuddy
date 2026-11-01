#!/usr/bin/env python3
"""Report posted-hover observations, not a cursor classifier or attack verdict."""
import argparse
import json
import sys


def summarize(sample):
    if not isinstance(sample, dict) or sample.get('schema') != 1:
        raise ValueError('unsupported posted-hover schema')
    foreground = sample.get('foregroundFingerprint')
    if not valid_hash(foreground):
        raise ValueError('invalid foreground fingerprint')
    observations = []
    for phase in ('before', 'away', 'returned'):
        item = sample.get(phase)
        if not isinstance(item, dict) or item.get('matchesTarget') not in (None, True, False):
            raise ValueError('invalid phase or target match')
        if item.get('matchesTarget') is not None and type(item['matchesTarget']) is not bool:
            raise ValueError('invalid target match type')
        if type(item.get('cursorShowing')) is not bool:
            raise ValueError('invalid cursor visibility')
        fingerprint = item.get('cursorFingerprint')
        if fingerprint is not None and not valid_hash(fingerprint):
            raise ValueError('invalid phase fingerprint')
        observations.append(item)
    observed = observations[1]['matchesTarget'] is False and observations[2]['matchesTarget'] is True
    if type(sample.get('candidateAwayThenTarget')) is not bool or sample['candidateAwayThenTarget'] != observed:
        raise ValueError('inconsistent GUID transition')
    before, away, returned = observations
    return {
        'transition': observed,
        'visible': [part['cursorShowing'] for part in observations],
        'guid_matches': [part['matchesTarget'] for part in observations],
        'hash_available': [part.get('cursorFingerprint') is not None for part in observations],
        'return_matches_foreground': (returned.get('cursorFingerprint') == foreground
                                      if returned['cursorShowing'] and returned.get('cursorFingerprint') else None),
        'away_return_equal': (away.get('cursorFingerprint') == returned.get('cursorFingerprint')
                              if away['cursorShowing'] and returned['cursorShowing'] and away.get('cursorFingerprint') and returned.get('cursorFingerprint') else None),
    }


def valid_hash(value):
    return isinstance(value, str) and len(value) == 64 and all(c in '0123456789ABCDEF' for c in value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', help='private wowb-posted-hover-samples.jsonl')
    args = parser.parse_args()
    try:
        with open(args.file, encoding='utf-8-sig') as source:
            samples = [summarize(json.loads(line)) for line in source if line.strip()]
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        print(f'Cannot analyze: {exc}', file=sys.stderr)
        return 2
    for i, sample in enumerate(samples, 1):
        print(f"sample {i}: GUID matches before/away/return={sample['guid_matches']}; "
              f"away→target transition={sample['transition']}")
        print(f"  desktop cursor shown={sample['visible']}, fingerprints available={sample['hash_available']}; "
              f"return matches foreground={sample['return_matches_foreground']}, "
              f"away equals return={sample['away_return_equal']}")
    print('The desktop cursor can belong to another foreground window; equality is not proof of game hover or attackability.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

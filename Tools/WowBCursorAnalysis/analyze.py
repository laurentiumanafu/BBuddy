#!/usr/bin/env python3
"""Summarize user-labeled Windows cursor observations; never classify or authorize input."""
import argparse
import json
import sys

LABELS = ('friendly', 'neutral_attackable', 'aggressive', 'off_target_control')


def analyze(samples):
    groups = {label: {'samples': 0, 'matched': 0, 'hidden': 0, 'missing': 0, 'hashes': set(), 'targets': set(), 'by_identity': {}} for label in LABELS}
    for sample in samples:
        if not isinstance(sample, dict) or sample.get('schema') != 1 or sample.get('label') not in LABELS:
            raise ValueError('unknown cursor sample schema or label')
        if type(sample.get('matchesSelected')) is not bool or type(sample.get('cursorShowing')) is not bool:
            raise ValueError('invalid cursor sample booleans')
        label = sample['label']
        group = groups[label]
        group['samples'] += 1
        if sample['matchesSelected']:
            group['matched'] += 1
        if not sample['cursorShowing']:
            group['hidden'] += 1
        image = sample.get('cursorImage')
        fingerprint = image.get('fingerprint') if isinstance(image, dict) else None
        if fingerprint is not None:
            if not isinstance(fingerprint, str) or len(fingerprint) != 64 or any(c not in '0123456789ABCDEF' for c in fingerprint):
                raise ValueError('invalid cursor fingerprint')
        else:
            group['missing'] += 1
        # Controls intentionally point away from the selection: mouseover can be empty,
        # another GUID, or stale. The user must confirm the pointer was off-target.
        if (sample['matchesSelected'] or label == 'off_target_control') and sample['cursorShowing'] and fingerprint:
            group['hashes'].add(fingerprint)
            guid = sample.get('targetGuid')
            player = sample.get('playerGuid')
            if isinstance(guid, str) and guid:
                group['targets'].add(guid.upper())
                if isinstance(player, str) and player:
                    identity = (player.upper(), guid.upper())
                    group['by_identity'].setdefault(identity, set()).add(fingerprint)
    return groups


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', help='private wowb-cursor-samples.jsonl')
    args = parser.parse_args()
    try:
        with open(args.file, encoding='utf-8-sig') as source:
            samples = [json.loads(line) for line in source if line.strip()]
        groups = analyze(samples)
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        print(f'Cannot analyze: {exc}', file=sys.stderr)
        return 2
    for label, group in groups.items():
        print(f"{label}: {group['samples']} samples, {group['matched']} selected-GUID matches, "
              f"{group['hidden']} hidden/suppressed, {group['missing']} missing fingerprints; "
              f"{len(group['hashes'])} eligible fingerprints across {len(group['targets'])} GUID(s)")
    friend = groups['friendly']['hashes']
    for label in ('neutral_attackable', 'aggressive'):
        other = groups[label]['hashes']
        if not friend or not other:
            print(f'friendly vs {label}: insufficient eligible samples')
        elif friend & other:
            print(f'friendly vs {label}: fingerprint overlap; this is not a usable separator')
        else:
            print(f'friendly vs {label}: no overlap in these observations; this is only a lead')
    control = groups['off_target_control']['hashes']
    for label in ('friendly', 'neutral_attackable', 'aggressive'):
        on = groups[label]['hashes']
        if control and on:
            relation = 'overlap' if control & on else 'no overlap in these observations'
            print(f'off_target_control vs {label}: {relation}; control position is user-reported')
            shared = groups[label]['by_identity'].keys() & groups['off_target_control']['by_identity'].keys()
            overlaps = sum(bool(groups[label]['by_identity'][key] & groups['off_target_control']['by_identity'][key])
                           for key in shared)
            print(f'  same-player/selected-target groups: {len(shared)}, with on/off fingerprint overlap: {overlaps}')
    print('Labels are user-supplied; visible dagger state is not recorded. Desktop cursors may not be game-drawn.')
    print('A candidate mouseover match can be stale. No attackability or input authorization is inferred.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

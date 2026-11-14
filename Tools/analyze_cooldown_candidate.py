#!/usr/bin/env python3
"""Summarize 70938 research-list changes. This does not determine cooldown readiness."""
import argparse
import re
from pathlib import Path

SAMPLE = re.compile(r'^sample=(\d+) startMs=(\d+) ')
NODE = re.compile(r'\[[^\]]+\]')
FIELD = re.compile(r'\b([0-9A-F]{2})=([0-9A-F]{8})\b')
CLOCK_CHANGE = re.compile(r'\+0x1C10:[0-9A-F]{8}>([0-9A-F]{8})')
STATES = re.compile(r' state[01] head=')


def parse_nodes(line):
    parts = STATES.split(line)
    if len(parts) != 3 or ' end=0x' not in parts[1] or ' end=0x' not in parts[2]:
        return None
    result = []
    for part in parts[1:]:
        entries = []
        for match in NODE.finditer(part):
            fields = dict(FIELD.findall(match.group()))
            keys = ('10', '14', '1C', '20', '24', '28', '2C', '30', '34', '38', '3C', '40')
            if any(k not in fields for k in keys):
                return None
            entries.append(tuple(int(fields[k], 16) for k in keys))
        result.append(tuple(sorted(entries)))
    return tuple(result)


def read_trace(path):
    samples = []
    current = None
    clock = None
    with Path(path).open(encoding='utf-8-sig') as stream:
        header = stream.readline()
        if not header.startswith('70938 COMBAT RESEARCH v1:'):
            raise ValueError('expected 70938 combat research trace')
        for line in stream:
            match = SAMPLE.match(line)
            if match:
                if current is not None:
                    samples.append(current)
                current = {'ms': int(match.group(2)), 'clock': clock, 'lists': None}
                continue
            if current is None:
                continue
            if line.startswith('player+0x1C00 baseline '):
                try:
                    raw = bytes.fromhex(line.split()[-1])
                    clock = int.from_bytes(raw[0x10:0x14], 'little') if len(raw) == 256 else None
                except ValueError:
                    clock = None
                current['clock'] = clock
            elif line.startswith('player+0x1C00 unreadable'):
                clock = None
                current['clock'] = None
            elif line.startswith('player changes'):
                match = CLOCK_CHANGE.search(line)
                if match:
                    clock = int(match.group(1), 16)
                    current['clock'] = clock
            elif line.startswith('cooldown candidate stable (research only; not readiness):'):
                current['lists'] = parse_nodes(line)
            elif line.startswith('cooldown candidate unknown:'):
                current['lists'] = None
    if current is not None:
        samples.append(current)
    return samples


def possible_duration_clears(samples, max_gap_ms=1000):
    """Report a narrow 8000->0 signature, never readiness or a confirmed proc.

    Nodes are compared by state and all captured fields except +0x20; addresses
    are intentionally not retained. Identical field signatures can collide. A
    gap, unknown sample, duplicate signature or missing node breaks the chain.
    """
    previous = None
    events = []
    for sample in samples:
        states = sample['lists']
        if states is None:
            previous = None
            continue
        if previous is not None and 0 < sample['ms'] - previous['ms'] <= max_gap_ms:
            for state_index, (before, after) in enumerate(zip(previous['lists'], states)):
                def signatures(nodes, duration):
                    counts = {}
                    for node in nodes:
                        # Stormstrike's observed spell-specific entry; the
                        # separate category-133/1500 entry is not a reset.
                        if node[0] != 17364 or node[3] != duration or node[9] != 0 or node[10] != 0:
                            continue
                        key = node[:3] + node[4:]
                        counts[key] = counts.get(key, 0) + 1
                    return counts
                active = signatures(before, 8000)
                cleared = signatures(after, 0)
                active_after = signatures(after, 8000)
                cleared_before = signatures(before, 0)
                for key in active.keys() & cleared.keys():
                    if active[key] == 1 and cleared[key] == 1 and key not in active_after and key not in cleared_before:
                        events.append((previous['ms'], sample['ms'], state_index, key[2]))
        previous = sample
    return events


def format_state(states):
    if states is None:
        return 'unknown or not recorded'
    labels = ('id', 'arg14', 'start1C', 'duration20', 'category24', 'start28', 'duration2C', 'flag30', 'start34', 'category38', 'duration3C', 'field40')
    return ' | '.join('state{}: '.format(index) + ('; '.join(' '.join('{}={}'.format(k, v) for k, v in zip(labels, node)) for node in nodes) or '(no nodes)') for index, nodes in enumerate(states))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace')
    args = parser.parse_args()
    samples = read_trace(args.trace)
    print('Raw candidates only: no readiness, success, GCD or reset conclusion; node persistence is not cooldown duration.')
    print(f'Samples: {len(samples)}')
    previous = object()
    for sample in samples:
        states = sample['lists']
        if states != previous:
            clock = 'unavailable' if sample['clock'] is None else hex(sample['clock'])
            print(f"{sample['ms'] / 1000:.3f}s clock_candidate={clock}: {format_state(states)}")
            previous = states
    for before_ms, after_ms, state, start in possible_duration_clears(samples):
        print(f'Possible matching Stormstrike 8000->0 field transition in state{state}: '
              f'{before_ms / 1000:.3f}–{after_ms / 1000:.3f}s, start1C={start}; '
              'not proof of readiness, a talent proc, or identical node address')


if __name__ == '__main__':
    main()

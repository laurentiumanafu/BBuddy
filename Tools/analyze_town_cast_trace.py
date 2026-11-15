#!/usr/bin/env python3
"""Summarize candidate cast-cluster transitions in a CombatResearch trace; not proof of cast success."""
import argparse
from analyze_combat_trace import read_trace, trace_label


def summary(path):
    changes, _, samples, end = read_trace(path)
    clocks = {t: new for t, _, new in changes.get(('player', 0x1C10), [])}
    ids = changes.get(('player', 0x3C0), [])
    output = []
    for i, (at, old, value) in enumerate(ids):
        if old != 0 or value not in (556, 8690):
            continue
        mirrored = [(t, new) for t, previous, new in changes.get(('player', 0x3EC), [])
                    if 0 <= t-at <= .5 and previous == 0 and new == value]
        starts = [(t, new) for t, previous, new in changes.get(('player', 0x418), []) if 0 <= t-at <= .5 and previous == 0]
        ends = [(t, new) for t, previous, new in changes.get(('player', 0x41C), []) if 0 <= t-at <= .5 and previous == 0]
        if not mirrored or not starts or not ends:
            continue
        duration = (ends[0][1]-starts[0][1]) & 0xffffffff
        cleared = next((t for t, before, after in ids[i+1:] if before == value and after == 0), None)
        if cleared is None:
            output.append((value, at, duration, None, None)); continue
        clock = clocks.get(cleared)
        remaining = None if clock is None else (ends[0][1]-clock) & 0xffffffff
        output.append((value, at, duration, cleared, remaining))
    return samples, end, output


def main():
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('trace'); args=parser.parse_args()
    count, end, events=summary(args.trace)
    print(f'{trace_label(args.trace)}: {count} samples; {end}')
    for spell, start, duration, cleared, remaining in events:
        print(f'candidate id={spell} start={start:.3f}s duration-word={duration} clear={cleared} clock-to-end={remaining}; non-atomic, not proof of success/interruption')
    if not events: print('No matching candidate transition; absence is not proof of no cast.')
if __name__=='__main__': main()

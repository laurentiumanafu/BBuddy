#!/usr/bin/env python3
"""Summarize raw 70707 CombatResearch trace changes; never infer verified offsets."""
import argparse
import re
import struct
from collections import defaultdict
from pathlib import Path

SAMPLE = re.compile(r"^sample=(\d+) startMs=(\d+) endMs=(\d+) utc=(\S+)$")
BASE = re.compile(r"^(player|target)\+0x([0-9A-F]{4}) baseline ([0-9A-F]+)$")
CHANGE = re.compile(r" \+0x([0-9A-F]{4}):([0-9A-F]{8})>([0-9A-F]{8})")


def read_trace(path, spell_id=None):
    changes = defaultdict(list)
    baseline_matches = []
    sample = None
    status = "no end status"
    count = 0
    with Path(path).open(encoding="utf-8-sig") as stream:
        if not stream.readline().startswith("70707 COMBAT RESEARCH v1:"):
            raise ValueError("not a 70707 CombatResearch v1 file")
        for line in stream:
            line = line.rstrip("\r\n")
            match = SAMPLE.match(line)
            if match:
                sample = int(match.group(2)) / 1000.0
                count += 1
                continue
            if line.startswith("end: "):
                status = line[5:]
                continue
            match = BASE.match(line)
            if match and sample is not None and spell_id is not None:
                raw = bytes.fromhex(match.group(3))
                if len(raw) != 256:
                    raise ValueError("invalid baseline length")
                for off in range(0, len(raw), 4):
                    if struct.unpack_from("<I", raw, off)[0] == spell_id:
                        baseline_matches.append((match.group(1), int(match.group(2), 16) + off, sample))
                continue
            if sample is None:
                continue
            for actor in ("player", "target"):
                if not line.startswith(actor + " changes"):
                    continue
                for item in CHANGE.finditer(line):
                    offset, old, new = (int(v, 16) for v in item.groups())
                    if offset >= 0x2400 or offset % 4:
                        raise ValueError("change offset outside trace window")
                    changes[(actor, offset)].append((sample, old, new))
    return changes, baseline_matches, count, status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace")
    parser.add_argument("--around", type=float, help="approximate event time in seconds from start")
    parser.add_argument("--radius", type=float, default=2, help="seconds on each side (default: 2)")
    parser.add_argument("--spell-id", type=lambda text: int(text, 0), help="optional observed numeric spell ID; decimal or 0x hex")
    parser.add_argument("--max", type=int, default=40, help="maximum offsets to print (default: 40)")
    args = parser.parse_args()
    if args.radius < 0 or args.max < 1 or (args.spell_id is not None and not 0 <= args.spell_id <= 0xFFFFFFFF):
        parser.error("radius, --max or spell ID outside allowed bounds")
    changes, matches, count, status = read_trace(args.trace, args.spell_id)
    print(f"Samples: {count}; end: {status}")
    print("Uninterpreted dword changes; correlations and numeric matches do NOT establish spell state.")
    if args.around is None:
        chosen = changes
    else:
        chosen = {key: [event for event in events if abs(event[0] - args.around) <= args.radius]
                  for key, events in changes.items()}
        chosen = {key: events for key, events in chosen.items() if events}
        print(f"Window: {args.around - args.radius:.2f}–{args.around + args.radius:.2f} seconds")
    ranked = sorted(chosen.items(), key=lambda item: (-len(item[1]), item[0]))
    print(f"Changed offsets in window: {len(ranked)} (showing up to {args.max})")
    for (actor, offset), events in ranked[:args.max]:
        example = ", ".join(f"{t:.2f}s {old:08X}>{new:08X}" for t, old, new in events[:4])
        marker = " [value equals spell ID]" if args.spell_id is not None and any(old == args.spell_id or new == args.spell_id for _, old, new in events) else ""
        print(f"{actor}+0x{offset:04X}: {len(events)} changes; {example}{marker}")
    if args.spell_id is not None:
        print(f"Baseline dwords numerically matching {args.spell_id}: {len(matches)} (not proof)")
        for actor, offset, time in matches[:args.max]:
            print(f"  {actor}+0x{offset:04X} at {time:.2f}s")


if __name__ == "__main__":
    main()

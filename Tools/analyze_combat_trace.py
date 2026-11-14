#!/usr/bin/env python3
"""Summarize raw supported CombatResearch traces; candidate matches are not verified offsets."""
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
        if not re.match(r"^(70707|70755|70790|70862|70888|70938) COMBAT RESEARCH v1:", stream.readline()):
            raise ValueError("not a supported CombatResearch v1 file")
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
                    if offset >= 0x2800 or offset % 4:
                        raise ValueError("change offset outside trace window")
                    changes[(actor, offset)].append((sample, old, new))
    return changes, baseline_matches, count, status


def trace_label(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        for _ in range(8):
            line = stream.readline()
            if not line:
                break
            if "; label=" in line:
                return line.rstrip("\r\n").split("; label=", 1)[1]
    return "(not recorded)"


def player_offsets_not_in_control(changes, control):
    # Compare the player only: the target may be a different creature or missing.
    return sorted(offset for actor, offset in changes if actor == "player" and (actor, offset) not in control)


def candidate_events(changes):
    """Recognize correlated candidate patterns, never a general-purpose state reader."""
    grouped = defaultdict(dict)
    for (actor, offset), events in changes.items():
        for time, old, new in events:
            grouped[(actor, time)][offset] = (old, new)
    results = []
    player_times = sorted(time for actor, time in grouped if actor == "player")
    # These offsets were observed together in Mage Fireball and Shaman Lightning
    # Bolt traces. Require corroborating words in the same sampled interval;
    # absent != inactive, and the extra counter is only supporting evidence.
    for (actor, time), values in sorted(grouped.items(), key=lambda item: item[0][1]):
        if actor == "player" and 0x3C0 in values:
            old, spell = values[0x3C0]
            mirrored = values.get(0x3EC)
            start = values.get(0x418)
            end = values.get(0x41C)
            if old == 0 and spell != 0 and mirrored == (0, spell) and start and end and start[0] == 0 and end[0] == 0:
                duration = (end[1] - start[1]) & 0xFFFFFFFF
                if 0 < duration <= 60000:
                    counter = values.get(0x3E8)
                    increment = counter is not None and counter[1] == ((counter[0] + 1) & 0xFFFFFFFF)
                    extra = "; +0x03E8 incremented" if increment else ""
                    results.append((time, f"player cast-like start: id={spell}, timing-word difference={duration}{extra} (unverified units)"))
            elif old == 0 and spell != 0:
                # The recorder reads non-atomically: one candidate start can be
                # split across consecutive intervals. Mark this only as a lead;
                # short gaps can still contain unrelated transitions.
                for later in player_times:
                    if later <= time:
                        continue
                    if later - time > 0.5:
                        break
                    after = grouped[("player", later)]
                    if 0x3C0 in after:
                        break
                    second_id = after.get(0x3EC)
                    second_start = after.get(0x418)
                    second_end = after.get(0x41C)
                    if (second_id == (0, spell) and second_start and second_start[0] == 0
                            and second_end and second_end[0] == 0):
                        duration = (second_end[1] - second_start[1]) & 0xFFFFFFFF
                        if 0 < duration <= 60000:
                            results.append((time, f"possible split-sample cast-like start: id={spell}, other fields at {later:.2f}s (not verified)"))
                        break
            elif old != 0 and spell == 0 and mirrored == (old, 0) and start and end and start[1] == 0 and end[1] == 0:
                results.append((time, f"player cast-like fields cleared: id={old}"))
        # A separate static branch writes these other words and clears the first
        # pair. Its meaning (including whether it is a channel) is unverified.
        if actor == "player":
            alt = values.get(0x424)
            start = values.get(0x430)
            end = values.get(0x434)
            counter = values.get(0x3E8)
            if (alt and alt[0] == 0 and alt[1] != 0 and start and start[0] == 0 and start[1] != 0
                    and end and end[0] == 0 and end[1] != 0 and counter
                    and counter[1] == ((counter[0] + 1) & 0xFFFFFFFF)):
                results.append((time, f"player alternate field cluster populated: word={alt[1]}, +0x03E8 incremented (meaning unknown)"))
        if actor == "target" and 0x6F0 in values:
            old, effect = values[0x6F0]
            duration = values.get(0x6FC)
            expiry = values.get(0x700)
            if old == 0 and effect != 0 and duration and duration[0] == 0 and duration[1] != 0 and expiry and expiry[0] == 0 and expiry[1] != 0:
                results.append((time, f"target effect-like slot populated: id={effect}, duration-word={duration[1]} (unverified units)"))
            elif old != 0 and effect == 0 and duration and duration[1] == 0 and expiry and expiry[1] == 0:
                results.append((time, f"target effect-like slot cleared: id={old}"))
    return results


def timer_candidate_events(changes):
    """Report a correlated timer-like word, never a verified cooldown or spell.

    A single 70938 combat trace showed +0x1C14 populate near the continuously
    changing +0x1C10 and later clear. These are non-atomic raw samples; values
    can describe unrelated mechanisms, including a general combat timer.
    """
    events = changes.get(("player", 0x1C14), [])
    clock = {t: new for t, _, new in changes.get(("player", 0x1C10), [])}
    found = []
    for i, (t, old, deadline) in enumerate(events):
        if old != 0 or deadline == 0 or t not in clock:
            continue
        delta = (deadline - clock[t]) & 0xFFFFFFFF
        if not 0 < delta <= 60000:
            continue
        detail = f"player +0x1C14 timer-like word set; +0x1C10 difference={delta} (unverified units/meaning)"
        if i + 1 < len(events):
            later, prior, new = events[i + 1]
            if prior == deadline and new == 0 and 0 <= later - t <= 60:
                detail += f"; cleared {later - t:.2f}s later"
        found.append((t, detail))
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace")
    parser.add_argument("--around", type=float, help="approximate event time in seconds from start")
    parser.add_argument("--radius", type=float, default=2, help="seconds on each side (default: 2)")
    parser.add_argument("--spell-id", type=lambda text: int(text, 0), help="optional observed numeric spell ID; decimal or 0x hex")
    parser.add_argument("--control", help="optional no-action trace; compare player offsets only")
    parser.add_argument("--max", type=int, default=40, help="maximum offsets to print (default: 40)")
    args = parser.parse_args()
    if args.radius < 0 or args.max < 1 or (args.spell_id is not None and not 0 <= args.spell_id <= 0xFFFFFFFF):
        parser.error("radius, --max or spell ID outside allowed bounds")
    changes, matches, count, status = read_trace(args.trace, args.spell_id)
    print(f"Label: {trace_label(args.trace)}; samples: {count}; end: {status}")
    print("Uninterpreted dword changes; correlations and numeric matches do NOT establish spell state.")
    if args.control:
        control, _, control_count, control_status = read_trace(args.control)
        new_offsets = player_offsets_not_in_control(changes, control)
        print(f"Control label: {trace_label(args.control)}; samples: {control_count}; end: {control_status}")
        print(f"Player offsets changed here but not in control: {len(new_offsets)} (different conditions can still cause changes)")
        print("  " + (", ".join(f"+0x{x:04X}" for x in new_offsets[:args.max]) if new_offsets else "none"))
    events = sorted(candidate_events(changes) + timer_candidate_events(changes))
    print(f"Observed research patterns: {len(events)} (one-build candidates, not verified state)")
    for time, description in events:
        print(f"  {time:.2f}s {description}")
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

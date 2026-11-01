#!/usr/bin/env python3
"""Find correlations in labeled read-only 70291 samples. Never produces a classifier."""
import argparse
import base64
import collections
import json
import struct
import sys

LABELS = ("friendly", "neutral_attackable", "aggressive")
SIGNATURE = "488BC4554157488D68A14881ECB80000"
WINDOW = 0x2800
CHUNK = 0x100


def load(path):
    samples = []
    with open(path, encoding="utf-8-sig") as source:
        for number, line in enumerate(source, 1):
            if not line.strip():
                continue
            try:
                obj = json.loads(line)
                if obj.get("schema") != 1 or obj.get("diagnosticSignature") != SIGNATURE or obj.get("label") not in LABELS:
                    raise ValueError("unknown schema, signature or label")
                guid = obj["targetGuid"]
                player = obj["playerGuid"]
                for name, value in (("target", guid), ("player", player)):
                    if not isinstance(value, str) or len(value) != 34 or not value.lower().startswith("0x"):
                        raise ValueError(f"invalid {name} GUID")
                    int(value[2:], 16)
                data = {}
                for chunk in obj["targetChunks"]:
                    offset = chunk["offset"]
                    if type(offset) is not int or offset < 0 or offset >= WINDOW or offset % CHUNK or offset in data:
                        raise ValueError("invalid or repeated chunk offset")
                    raw = base64.b64decode(chunk["bytes"], validate=True)
                    if len(raw) != CHUNK:
                        raise ValueError("invalid chunk size")
                    data[offset] = raw
                samples.append({"label": obj["label"], "guid": guid.upper(), "player": player.upper(),
                                "hint": obj.get("targetEntryHint"), "chunks": data})
            except (ValueError, KeyError, TypeError, json.JSONDecodeError) as exc:
                raise ValueError(f"line {number}: {exc}") from exc
    return samples


def template_observations(samples):
    """Return values observed at +0x1E5C; missing bytes remain unknown."""
    result = {label: set() for label in LABELS}
    for sample in samples:
        chunk = sample["chunks"].get(0x1E00)
        if chunk is not None:
            result[sample["label"]].add(struct.unpack_from("<I", chunk, 0x5C)[0])
    return result


def correlations(samples, minimum=3):
    players = {s.get("player") for s in samples}
    if len(players) > 1:
        raise ValueError("different player characters must be analyzed separately")
    # Conflicting labels make this object unusable, even if the reason is an
    # actual reputation/phase change. A GUID is never split into train/test.
    guid_labels = collections.defaultdict(set)
    for sample in samples:
        guid_labels[sample["guid"]].add(sample["label"])
    conflicts = {guid for guid, labels in guid_labels.items() if len(labels) > 1}
    usable = [s for s in samples if s["guid"] not in conflicts]
    counts = {label: len({s["guid"] for s in usable if s["label"] == label}) for label in LABELS}
    hints = {label: len({s["hint"] for s in usable if s["label"] == label and isinstance(s["hint"], int) and s["hint"] != 0}) for label in LABELS}
    if any(counts[label] < minimum or hints[label] < minimum for label in LABELS):
        return counts, hints, conflicts, [], []
    words, bits = [], []
    for offset in range(0, WINDOW, 4):
        chunk_base = offset - offset % CHUNK
        values = {label: set() for label in LABELS}
        for s in usable:
            chunk = s["chunks"].get(chunk_base)
            if chunk is None:
                break  # never substitute zeros or compare a biased subset
            value = struct.unpack_from("<I", chunk, offset - chunk_base)[0]
            values[s["label"]].add(value)
        else:
            groups = [values[label] for label in LABELS]
            if all(not groups[i] & groups[j] for i in range(3) for j in range(i + 1, 3)):
                words.append((sum(len(g) for g in groups), offset, groups))
            for bit in range(32):
                masks = [{(v >> bit) & 1 for v in group} for group in groups]
                if all(len(g) == 1 for g in masks) and len({next(iter(g)) for g in masks}) > 1:
                    bits.append((offset, bit, tuple(next(iter(g)) for g in masks)))
    words.sort(key=lambda x: (x[0], x[1]))
    return counts, hints, conflicts, words, bits


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", help="wowb-reaction-samples.jsonl")
    parser.add_argument("--player", help="full player GUID to analyze when multiple characters are present")
    args = parser.parse_args()
    try:
        samples = load(args.file)
        players = collections.Counter(s["player"] for s in samples)
        if args.player:
            player = args.player.upper()
            samples = [s for s in samples if s["player"] == player]
            if not samples:
                raise ValueError("no samples match --player")
        elif len(players) > 1:
            print("Samples span multiple player characters; do not pool their reactions:")
            for player, count in sorted(players.items()):
                print(f"  {player}: {count} samples")
            print("Re-run with --player and one full GUID. Reputation and phase can still vary within a character.")
            return 0
        counts, hints, conflicts, words, bits = correlations(samples)
    except (OSError, ValueError) as exc:
        print(f"Cannot analyze: {exc}", file=sys.stderr)
        return 2
    print(f"{len(samples)} samples. Full GUIDs / unverified entry hints by label:")
    for label in LABELS:
        print(f"  {label}: {counts[label]} / {hints[label]}")
    print(f"Excluded {len(conflicts)} GUID(s) with conflicting labels.")
    print("Hints are not proven NPC entry IDs. Repeated samples are not independent NPCs.")
    print("Observed +0x1E5C FactionTemplate keys (user labels are not game-computed reactions):")
    for label, values in template_observations(samples).items():
        print(f"  {label}: " + (", ".join(str(v) for v in sorted(values)) if values else "unreadable"))
    print("Template IDs alone do not determine reaction, reputation, attackability or aggressiveness.")
    if any(counts[label] < 3 or hints[label] < 3 for label in LABELS):
        print("Insufficient independent examples: collect at least 3 distinct GUIDs and 3 distinct entry hints per label.")
        return 0
    print("Potential disjoint dwords (max 30):")
    for _, offset, groups in words[:30]:
        print(f"  +0x{offset:04X}: " + " | ".join(f"{label}={','.join(f'{v:08X}' for v in sorted(g)[:8])}" for label, g in zip(LABELS, groups)))
    print("Potential consistent bit partitions (max 30; friendly, neutral, aggressive):")
    for offset, bit, groups in bits[:30]:
        print(f"  +0x{offset:04X} bit {bit}: {groups}")
    print("Correlations are leads only: unit identity, faction, race, level, location, phase, health and pointers can confound them.")
    print("Prove fields statically and validate held-out units, reputation changes, attackability and both reaction directions before use.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

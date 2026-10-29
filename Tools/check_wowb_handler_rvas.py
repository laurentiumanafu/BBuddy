#!/usr/bin/env python3
"""Cross-check selected build-70291 Lua-handler RVAs against the saved table.

This checks transcription only: it does not validate a handler's semantics,
runtime bytes, ABI, thread affinity, or that executing it is safe.
"""
from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HANDLERS = {
    "UnitHealth": "FnUnitHealth",
    "UnitHealthMax": "FnUnitHealthMax",
    "UnitPower": "FnUnitPower",
    "UnitPowerMax": "FnUnitPowerMax",
    "UnitLevel": "FnUnitLevel",
    "UnitReaction": "FnUnitReaction",
    "UnitCanAttack": "FnUnitCanAttack",
    "UnitIsEnemy": "FnUnitIsEnemy",
    "UnitIsDead": "FnUnitIsDead",
    "IsLoggedIn": "FnIsLoggedIn",
    "GetRealZoneText": "FnGetRealZoneText",
    "CanLootUnit": "FnCanLootUnit",
    "StartAttack": "FnStartAttack",
    "TargetUnit": "FnTargetUnit",
    "UnitGUID": "FnUnitGUID",
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--table", type=Path, default=ROOT / "hunt" / "api_handlers.tsv")
    parser.add_argument("--offsets", type=Path, default=ROOT / "Styx" / "Offsets" / "WowBOffsets.cs")
    args = parser.parse_args()

    constants: dict[str, list[int]] = {}
    text = args.offsets.read_text(encoding="utf-8")
    # Only the selected literal ulong declarations matter to this audit.
    for name, literal in re.findall(r"\bpublic\s+const\s+ulong\s+(\w+)\s*=\s*(0x[0-9a-fA-F]+)\s*;", text):
        constants.setdefault(name, []).append(int(literal, 16))

    rows: dict[str, list[int]] = {}
    with args.table.open(encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        if not reader.fieldnames or not {"name", "handler_rva"}.issubset(reader.fieldnames):
            raise ValueError("handler table must include name and handler_rva columns")
        for row in reader:
            if row["name"] in HANDLERS:
                rows.setdefault(row["name"], []).append(int(row["handler_rva"], 0))

    problems = []
    for api, constant in HANDLERS.items():
        observed, declared = rows.get(api, []), constants.get(constant, [])
        if len(observed) != 1 or len(declared) != 1 or observed != declared:
            problems.append(f"{api}/{constant}: table={observed} offsets={declared}")
        else:
            print(f"OK {api:16} {constant:22} RVA=0x{declared[0]:X}")
    if problems:
        for problem in problems:
            print("FAIL " + problem)
        return 1
    print(f"PASS: {len(HANDLERS)} handler RVAs match the saved registration table.")
    print("Transcription check only; these are not validated game actions or field offsets.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

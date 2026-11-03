# WowBCtmPathWatch — staged, read-only click-to-move probe

## Why

The field sampler proved the anchor is live: `record+0x60/+0x64/+0x68` is a
float triple (≈10589.4, 958.6, 1317.4) that changed immediately after a
right-click, and `+0xA4/+0xA8/+0xAC` held the identical value. So the earlier
injected run that reported zero writes to that page was a run where the
character did not actually move — not a bad anchor.

The same record also contains three more position-like triples, 51–96 yards
away:

| offset | mirror | value at start | first-stage changes |
|---|---|---|---|
| `+0x060` | `+0x0A4` (identical) | 10589.4, 958.6, 1317.4 | few |
| `+0x150` | `+0x194` (identical) | 10530.4, 1034.5, 1322.6 | 151 |
| `+0x6A0` | `+0x6E4` (identical) | 10563.9, 913.0, 1312.0 | 268 |
| `+0x7D0` | — | 10516.6, 896.2, 1309.0 | (appeared at t=531 ms) |

To command movement from the engine we must know which triple is the live
position and which, if any, is the click destination. This tool settles that.

## What it does

Two stages, both read-only:

1. **BASELINE** (4 s) — hands off. Measures how much each triple drifts by
   itself, so later movement can be told apart from jitter.
2. **CLICK** (8 s) — you right-click **one** distant ground spot (~40–80 yards)
   and then keep your hands off. The player triple moves smoothly toward the
   point; a destination triple jumps once at the click and then stays put.

Sampling is every 20 ms. Every change is logged with timestamp, distance moved
in that sample, the new float triple, and how long ago the last right-button
press was. A summary compares change counts, path length per stage, the largest
single step and the net displacement.

It **only reads**: `OpenProcess` with
`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE`. No writes, no
`VirtualAllocEx`, no `VirtualProtectEx`, no remote thread, no APC, no DLL, no
debugger. The client is not modified and does not need a restart afterwards.

## Run

```
"C:\ghidra-scripts\WowBCtmPathWatch\WowBCtmPathWatch.exe"
```

Options: `--pid N`, `--baseline ms`, `--click ms`, `--offsets 60,150,6A0,7D0`.

Report: `C:\ghidra-scripts\WowBCtmPathWatch-<timestamp>.txt`.

## How to read the verdicts

* `DESTINATION candidate (one large jump, then holds)` — a triple that jumped
  ≥ 3 yards within one sample near the click and then stayed constant. This is
  the field an engine-level move call would populate.
* `POSITION candidate (many small steps during the click stage)` — a triple
  that walked. This is what a bot reads to know where it is.
* `static (nothing changed at all)` — rejected for movement purposes.
* If **nothing** moved, the click did not reach the game. That is an input
  problem and must be settled before any offset work continues.

## Limits

* 20 ms polling is a sample, not a hook: a field that moves and returns between
  samples is invisible, and two changes in one sample look like one step.
* Only the listed offsets are watched (default four, each read as a 12-byte
  triple). A destination stored elsewhere in the object is not seen.
* `plausible()` rejects NaN, zero and absurd magnitudes, so a misaligned read is
  reported as unusable rather than reported as coordinates.
* The player lookup is the same GUID/pool heuristic as the injected probe:
  non-atomic.
* Cross-compiled for Windows x64 with Zig; the pure helpers (distance,
  plausibility) pass offline tests, but the tool has not been run on Windows.

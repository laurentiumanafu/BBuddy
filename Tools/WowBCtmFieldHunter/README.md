# WowBCtmFieldHunter — find what drives right-click movement

**v3 note (what changed from the run you just sent):** the 389 "cursor followers"
in v2's section B were **not** 389 signals. They were overlapping 3-float windows
of a handful of matrices: `record+0x170` and `record+0x280` each produced six
windows. Every row is a unit vector (norm 1.0003), so those are rotation matrices
that changed while the mouse orbited the camera — not terrain picks. v3 only
reports the **start of a changed run** and prints the run length and the vector
norm, so one matrix row appears once, and `norm≈1` immediately identifies a
rotation instead of a world point.

## Why v2

v1 tested one shape only: a value that ends up where the character stopped. It
found nothing in 128 KiB (32,766 triples tested, 0 candidates). Two reasons to
expect that:

* a destination that is **consumed on arrival** cannot be observed after the walk;
* on this client family the right-click handler is `CameraOrSelectOrMove`, and the
  Winkr documentation for the sibling client concludes there is **no ClickToMove
  feature and no CTM CVar at all** — the move point may be re-derived from the
  cursor ray every frame rather than stored.

So v2 tests both shapes at once.

## The two shapes

**A. A stored destination** must be: constant during the walk, ahead of the
player, getting closer, and equal to where the character actually stopped.

**B. A cursor-derived point** never freezes while you move the mouse, so it is
found the opposite way: during a sweep where the mouse moves and the player
stands still, it is one of the few plausible triples that changes.

## Run

```
"C:\ghidra-scripts\WowBCtmFieldHunter\WowBCtmFieldHunter.exe"
```

Four phases, printed as prompts:

1. `BASELINE 2500 ms` — hands off, mouse still.
2. `CURSOR SWEEP 2500 ms` — move the mouse slowly across the ground, **no
   clicking**. This is what identifies shape B.
3. `NOW right-click ONE distant ground spot (50-100 yd)` — then hands off.
4. `waiting 10000 ms for the walk to finish` — hands off.

Report: `C:\ghidra-scripts\WowBCtmFieldHunter-<timestamp>.txt`.

Options: `--full` (scan the whole process instead of an 8 MiB window, 6 s budget),
`--span hex`, `--cursor ms`, `--walk ms`, `--budget ms`, `--pid N`.

Read-only: `PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE`. No writes, no
allocation in the target, no protection changes, no remote threads, no DLL. The
client is untouched and needs no restart.

## How to read the report

```
=== A) STORED-DESTINATION CANDIDATES (stable during walk, ahead, approaching) ===
record+0x1A34 (span+0x401A34) = (10501.20, 999.10, 1319.60) dWalk1=41.02 dEnd=0.31 held=1 cursorFollower=1
```
`held=1` means it was still identical after the walk. `dEnd` is the horizontal
distance from the stopping point. `cursorFollower=1` means it also moved during the
cursor sweep — the strongest single signal available here, because it means "value
that follows the cursor and then freezes when clicked".

```
=== B) CURSOR-SWEEP FOLLOWERS (where the cursor ray is stored) ===
record+0x7D0 (span+0x4007D0) run=3 floats norm=34.207 base=(10515.07, 892.77, 1309.66) afterSweep=(10512.44, 901.10, 1309.02) frozen=1
```
`norm` is the length of the vector. Around 1.0 means a rotation/unit basis, not a
position — a terrain point is tens of yards long. `run=3 floats` means three
consecutive floats moved together, which is what a coordinate does; a single
float moving is noise and is now filtered out.

`frozen=1` means it stopped tracking the cursor after the click. That is the
signature of a point that was adopted as the move target.

Offsets are always relative to the player record (`record+…`), so they can be
copied into the offsets table directly.

## Decision table

| Result | Meaning | Next |
|---|---|---|
| A hits, `dEnd` small | a stored destination exists | write it, CTM solved in memory |
| A empty, B hits with `frozen=1` | cursor-derived, adopted at click | read it, then feed a cursor position |
| A empty, B hits, nothing frozen | move point re-derived every frame | implement world→screen projection + synthesized right-click |
| A and B both empty | nothing in the window | re-run with `--full` |

## Limits

* Only the first 40 A-candidates and 25 B-followers are printed, and the sweep list
  is capped at 4,096 followers; a busy camera can fill it, in which case the list
  is truncated and says so.
* Window mode defaults to 8 MiB centred on the record. Shape A is collected
  process-wide with `--full`, but shape B (cursor followers) is always window-only,
  because it needs two comparable snapshots.
* Values that change by one ULP every frame are excluded by the bitwise stability
  test — that is deliberate, but it would also exclude a destination that is
  continuously rounded.
* Snapshot reads are not atomic.
* Cross-compiled for Windows x64 with Zig. The selection rule
  (`fh_destination_like`) passes offline tests built from the real 44-yard walk
  numbers, including the 2-D case where z is wrong by 80 yd, the behind-the-player
  case, the perpendicular case, and a zero-length walk. The tool has not been run
  on Windows.

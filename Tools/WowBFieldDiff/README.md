# WowBFieldDiff — read-only field-change sampler

## Why

The injected PAGE_GUARD probe (v7) finally ran end to end: manual mapping
succeeded, `ProbeStart` returned 0, the 10-second window completed and
`ProbeStop` returned 0. But the window itself reported:

```
active=0 pages=1 guardFaults=1 writeFaults=0 nearWrites=0 events=0
```

One access in ten seconds, and it was a read. The page holding the player
record was never written, so `record+0x60` was not updated while you were
clicking. That means the anchor we were watching is not demonstrably the live
player position - a question about the anchor, not about click-to-move.

This tool answers that question without touching the client.

## What it does

* reads a window of memory in `wowb.exe` at 25 ms intervals for 10 seconds
  (default; `--seconds 1..60`);
* reports every 4-byte word that changed, with byte offset, first-change time,
  first and last values, and the same value interpreted as a float;
* watches the player record and also the window one page later
  (`record+0x1000`);
* logs physical right-button transitions with timestamps so changes can be
  lined up with clicks.

It is **strictly read-only**: `OpenProcess` is called with only
`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE`. There is no
`WriteProcessMemory`, no `VirtualAllocEx`, no `VirtualProtectEx`, no remote
thread, no APC, no DLL and no debugger. The client is not modified, so the
restart rule from the injected probes does not apply here.

## Run

```
"C:\ghidra-scripts\WowBFieldDiff\WowBFieldDiff.exe"
```

Add `--pid 1234` if more than one `wowb.exe` is running. When it prints
`ACTIVE`, walk for the whole window: right-click a few ground destinations,
**and hold W for a couple of seconds**. The keyboard part matters - if the
right-clicks did not reach the game, keyboard movement still tells us which
fields are live.

The report is written to `C:\ghidra-scripts\WowBFieldDiff-<timestamp>.txt`.

## How to read it

* `recordWindow summary: ... distinctOffsets=N` - how many words are live.
* Per-offset lines are sorted by change count. An offset that changes dozens of
  times while walking is real state; one that changes once at the start is
  usually a timestamp or a cached flag.
* If `record+0x60/+0x64/+0x68 unchanged during this window` appears, that record
  is not the live position, and the offsets that did change are the next
  candidates to test.
* Nothing in this report proves anything about click-to-move; it only locates
  live state.

## Limits

* Sampling is not atomic and is not a hook: two writes between polls collapse
  into one, and a field that changes and changes back is reported once.
* 4-byte granularity; 8-byte or 16-byte values appear as two or four words.
* Only the two configured windows are sampled. A live field elsewhere in the
  object will not appear.
* The player lookup is the same GUID/pool heuristic used by the injected probe:
  non-atomic, and only as good as the anchor it found.
* Cross-compiled for Windows x64 with Zig and checked statically (PE headers,
  imports, offline diff-tracker tests). It has not been run on Windows here.

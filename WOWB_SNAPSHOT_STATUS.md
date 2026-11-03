# Build 70291 — read-only world snapshot

This branch restores the previous source/doc handoff and starts step 2. It is
not a functional bot port and it must not execute gameplay actions.

## What changed

- Removed repeated constant declarations in `WowBOffsets.cs`; the candidate
  entity-array `EntityObjectOffset = 0x28` and the unrelated pool-entry `+0x08`
  are distinct names. This does not validate the entity-array layout.
- `WowBReader.TryCaptureWorldSnapshot` scans the observed handle pools and
  publishes only objects with a matching record/unit GUID and back-pointer,
  known player or CGUnit vtable, and a readable finite transform. A structurally
  validated local player is required. Unsupported object types are omitted.
  The next overlay additionally rereads the target GUID after the scan and
  rejects a changed or unreadable target; ABA transitions can conceal changes,
  and the capture is still non-atomic. The previous recheck's synthetic tests
  passed on Windows. Its main-app rebuild was not confirmed from the log alone.
  The inspector overlay temporarily bypasses the thread-local memory cache
  during snapshot/diagnostic reads and restores the previous flag on exit. The
  user reports its fake-memory tests passed and its inspector worked.
  An experimental snapshot-only path also permits a data address that is 4
  modulo 8, but only with a readable header, known CGUnit vtable, complete
  GUID and record back-pointer; the pointer/header are rechecked after the
  transform read. The inspector overlay bypasses the thread-local cache during
  capture, but the non-atomic reads still cannot establish lifetime. This does
  not relax the legacy unit or stats accessors.
- `ObjectManager.WowBWorld` exposes this read-only snapshot at attachment and
  when `ObjectManager.Update()` is called. On failure it becomes null rather
  than presenting an old capture as current. A snapshot is non-atomic, and
  pool reuse, concurrent updates and incomplete reads still require live
  evaluation. `CapturedUtc` is not a guarantee of validity.
- In x64 diagnostic mode, the window reports a read-only attachment and skips
  routine, bot and plugin initialization. This avoids running legacy startup
  against the incomplete x64 model, but does **not** resolve SQLite or make
  gameplay available. The x86 startup path is unchanged.
- A nonzero target is looked up using both GUID halves in the captured units.
  The log states whether it was found and, if so, its position and approximate
  distance from the player. Unknown layouts may legitimately be absent.
- The inspector overlay exposes a separate read-only World snapshot window
  in x64 diagnostic mode. The user reported it worked. Its Refresh button requests a new snapshot without
  restarting; it does not call the legacy Developer Tools, enable gameplay,
  or infer hostility. Legacy x86 Developer Tools remain unchanged.
- `TreeRoot.Start()` refuses to start the bot on x64 targets. The old
  `ObjectList` / `WoWUnit` model still uses uint addresses and 64-bit GUIDs;
  placing modern records into it would truncate addresses and conflate GUIDs.
  The new snapshot is **not** wired into targeting, questing or movement.
- Corrected the old target-setter typo and overconfident execution claims in
  `X64_OFFSET_GAPS.md`.

The record/vtable layout is based on prior observations, not a complete proof
for every NPC/player type. Health, reaction, attackability, names and other
unit attributes are intentionally not asserted by this snapshot; the newest
overlay exposes raw health-offset candidates for comparison only. An empty list is
not evidence that the world contains no other units. Unknown vtables are
skipped, and map loading / client changes can invalidate any capture.

## Windows validation

These commands use the existing .NET 10 SDK. They cannot be run in the
sandbox, which does not contain `dotnet`. The user reported the main x64 build
succeeded on Windows after applying the step-2 dependency overlay. The
standalone fake-memory suite later passed with the target-stability overlay.

```powershell
dotnet run --project Tools\WowBReaderTests\WowBReaderTests.csproj -c Release
dotnet build -c Release -p:Platform=x64 .
```

The first command uses a fake byte-addressed memory provider; it does not
attach to or modify WoW. It tests full 128-bit GUIDs, known/unknown vtables,
local player identity, and failed transform/target reads. The second command
builds the main application; a syntactic parse is not a substitute for it.

Once the build passes, attach to the 70291 client **read-only** while logged
in. The application log should include `[ObjectManager] WowB pool snapshot:`
with the validated unit count, pool slots scanned, local GUID, and target GUID,
or an explicit `unavailable` message. Compare local and selected target GUIDs
and nearby unit counts with the game; do not start a bot. Attach-time capture
is a single sample, so changes require an explicit `ObjectManager.Update()` or
reattachment. These diagnostics alone do not prove snapshot accuracy.

## Live attach observation (2026-10-28)

The user reported a successful Windows x64 build and provided an attach log for
`WowB.exe` build 70291. The process attached read-only, found a player record,
and captured **22 structurally validated units from 87 scanned pool slots**.
The local GUID matched between the global and player record. The initial target GUID
was zero. On a subsequent attach with an in-game target selected, a nonzero
128-bit target GUID was observed, but matching it to a pool unit was not yet
reported by that version. A later rebuilt version reported the selected target
was not in the validated unit snapshot. The reason is unknown: the target may
be absent from the scanned pools, rejected by structural/vtable/transform
filters, or have changed during the non-atomic observation. A separate,
read-only rescan then found the selected target's exact GUID in family 1 slot
30, but its record+0xD8 value ended in 0x61C (4 modulo 8). The strict unit
filter rejected it solely for alignment; this does not prove the address is
invalid. Earlier hunt31 observations found similarly unaligned values with
0x2F4 spacing, possibly packed-array entries. The next bounded, read-only
diagnostic then reported a user-identified monster (GUID ending 0x3EA) at the
same slot/data address. The 32-byte header decodes to vtable 0x7FF7E5F7DDC0,
back-pointer 0x25C86A7EE80 and the full matching target GUID. The diagnostic
reported zero pool neighbours exactly 0x2F4 away, and no alternative aligned
header among the inspected record fields. Those observations do not prove a
packed-array or universal object layout. Subtracting the CGUnit vtable RVA gives
plausibly aligned module base 0x7FF7E0EB0000, but the actual module base was not
logged, so the vtable identity remains unconfirmed until compared live.

A subsequent Windows attach at 14:01 used the snapshot-only 4-mod-8
candidate path and found the selected monster with the same full GUID ending
0x3EA: position (10359.2, 987.8, 1331.8), reported distance 10.2 yards,
playerVtable=False. The snapshot had 36 structurally validated units from 88
scanned slots, versus 18 in the earlier attach. The comparison spans different
non-atomic captures, so the change in count cannot be attributed solely to the
new filter. Finding the monster shows the running reader accepted a matching
CGUnit header and finite transform; position and distance have not yet been
independently compared with the game. The Windows main build was running the earlier four-byte-candidate behavior;
the user later reported `WowBReader snapshot tests passed` after applying the
target-stability overlay. That covers simulated target changes, clearing and
failed reads during capture. The 14:09 attach log does not by itself prove that
the main application was rebuilt with the newest overlay.
The preexisting strict legacy unit/stats accessors stay unchanged.

At 14:04, the user selected a different monster: full target GUID
0x200118002001F140000000000000007D resolved in a capture of 37 units / 88
slots, at (10350.2, 1018.1, 1336.6), distance 41.8 yards and
playerVtable=False. At 14:05 the user cleared the target: a separate attach
reported the all-zero target GUID and 37/88 units. This verifies two distinct
selected GUIDs and the cleared state across separate captures, not live target
switching within a capture. Position and distances still need independent
comparison to the game. Future checks should include a changed player position.
Each attach is non-atomic and proves neither object lifetime, hostility,
attackability, nor health semantics.

`inWorld=False` is not a verified game-state result: `GameState` is still zero.
The reported max-power value was zero, and the `UnitDataMaxPowerOffset` remains
a candidate, not an established measurement. Combat routine selection reported
no legacy player, which is expected while `ObjectManager.Me` is not wired up.
A subsequent full diagnostic log identified a missing `System.Data.SQLite`
assembly during plugin/bot reflection. The repository-bundled DLL is a mixed-mode
x86 image, so an x64-compatible solution and any required encrypted-database
support remain open. Skipping these startup paths in read-only mode is not a
SQLite fix.

## Working checkpoint after live target tests

The user already verified two different selected monsters and clearing the
selection; do not ask them to repeat those checks. At 14:21 the user reported
`WowBReader snapshot tests passed`, an attach with 36/88 units and no target,
and that the read-only inspector worked. This covers the inspector overlay and
its synthetic cache/accessor tests; it does not prove lifetime or live
mid-scan changes.

The pool-lifetime overlay reads the complete 16-byte pool slot and complete record GUID, then rechecks the pool,
array, capacity, slot tag, opaque +4 marker and record pointer before admitting
an observation. It rechecks the local player's slot, GUID, object pointer and
vtable near the end of capture. Added synthetic cases simulate slot marker,
occupied bit, pointer, readability, pool array and local-object changes. The
+4 marker's semantics are not proven; it is only compared as opaque bytes.
These checks are conservative and may drop rapidly changing records. ABA,
subsequent reuse and changes between reads remain possible. At 14:25 the user
reported the pool-lifetime fake suite passed and the rebuilt app captured 38
validated units / 88 slots with no target. This is a successful sample, not a
proof of lifetime. No legacy object model or gameplay integration is claimed.

## Candidate unit data checkpoint

A newer overlay samples 16 raw bytes at data+0x1E30 and +0x1E38 only
for a structurally validated local player and selected target. The snapshot and
inspector preserve both full raw qwords and their low 32-bit interpretations;
field width, signedness and semantics are not established. An unreadable or
partial sample is represented as unavailable, not zero; an actual readable
zero remains distinguishable. Candidate data never determines whether a unit
is alive, hostile, attackable or selectable. The older diagnostic stat reader
now reports failure if a required low-32-bit read fails, rather than silently
returning zero. New synthetic tests cover raw high bits, missing bytes, zero,
4-mod-8 addresses, and data-pointer changes during the read. The user confirmed
the latest fake tests passed; the main overlay build/live raw display has not
been independently confirmed.

## 14:49 health observation and capture comparison checkpoint

The user reports their displayed player health is 72; the attach diagnostic
also printed `hp=72/72`. This supports the low-32-bit candidate for one local
player observation; the user did not independently report max health, and
this does not establish field width, signedness or validity for other units.
The pasted line is the earlier diagnostic format, not the new raw-qword line;
the raw high halves were not supplied. The user later confirmed the fake tests
passed, including the latest comparison/candidate-data suite.
No screenshot was requested or required.

At 14:25, local GUID 0x08011800000000000000000000000008 appeared at
family 1, record 0x25C86A7D1E0; at 14:49 the same GUID appeared at family 2,
record 0x25C86C5D320, in the same PID. The user clarified that they logged out
to character selection and logged back into the same character between these
samples. Therefore the address difference is across a logout/login boundary,
not evidence that an object moved during uninterrupted in-world play. The
samples are non-atomic and do not establish object lifetime or the precise
cause of the address change. At 14:49 the capture contained 37 validated units
/ 79 slots and no target. At 14:54 another attach reported 37 / 82 with the
same local GUID, record and no target; its log does not establish whether the
comparison overlay was rebuilt into the main application. The user confirmed
the latest fake-memory tests passed.

A new comparison checkpoint adds an optional count of GUIDs newly or
no longer observed, and of record/unit address changes for matching full GUIDs,
between two successful inspector refreshes. It clears the previous sample on
failed capture. These are observation differences, not spawn/despawn or reuse
proof. New synthetic tests cover unchanged, different high GUID halves,
new/missing observations and changed addresses; the user confirmed the fake
suite passed. The live comparison-window result was not reported.

## Next work

Validate the reader against multiple NPCs, local player and target changes;
reconcile read caching, lifetime/generation checks and snapshot consistency.
Design a 128-bit identity and 64-bit address model for legacy consumers before
adapting target selection; preserve the 3.3.5a path. Map unit reaction,
health-width/semantics and targetability. CTM and Lua-thread execution remain
separate, unresolved work; do not infer they work from successful reads.

At 14:09 the user reported the standalone fake-memory suite passed and sent a
read-only attach log with no target and 38/88 units. These are separate tests:
the fake suite simulates mid-scan changes; the attach confirms a stable empty
target. Changes that occur entirely between reads may not be detected. The
inspector overlay bypasses the thread-local cache during scans; its manual
Refresh button requests another observation without restarting the application.

## User testing convention

The user instructed: always consider fake tests passed unless they report a
failure, and explicitly confirmed the latest comparison/candidate-data fake
tests passed. Preserve the distinction between an assumed pass and a test
actually run; do not claim a test was executed when it was not. The main
application's latest comparison-window build/live behavior is not confirmed by
the 14:54 attach log alone.

# Latest resume — 2026-10-28, CTM capture v1 results and v2

Earlier v1 resume: https://drive.google.com/file/d/1k-jjA4n-1SHfrgCKWD18bSiPGwEeAaRw/view
Detailed history: `hunt/WOWB_70291_TRACE_FINDINGS.md`. Restore latest source
overlay over clean base 21f058f032f114afd72969586d4f677b32a1f8b3 after backup;
overlay is not a full repository. Earlier full backup:
https://drive.google.com/file/d/1oNZn9t6SGoEg2jAtyeprfj-GjRrRYXFV/view.

User provided v1 CTM capture report `1laiySdax8s4p6zXSdmx8oxg72TfTNTsE` MD5
a576006fd21b44ce3d1a1913236c56fa, fully read. Compiled/ran on Windows, PID
12184 base 0x7FF7E0EB0000; identity sample matches. 128MiB scope capped, 4
chunks failed idle1, 266 full chunks; six passes 0.61–0.85 s. 31,109 stable A,
139 changed AB, 49 stable B, all shown; none in image, all private. Many
entries are overlapping/zero/one partial values; none proves CTM. v1 omitted
player position, so can't know whether it covered player-related allocations.

Modified `Tools/WowBCtmCapture/` to v2: verify local record via full GUID and
occupied pool slot with repeated checks, verify unit vtable/back-pointer/GUID
when present, prioritize windows around those allocations and stratify other
private chunks, record player positions after each capture and approximate A/B
distances. Partial non-atomic selection and ABA caveats remain. Memory budget
128MiB x six ~768MiB + overhead. Exact manual click coordinates, same-click
repeat and independent motion control unmeasured; all candidates unverified.
Tree-sitter C# syntax parsed; independent stratification model passed. v2's
Windows build/live run and bundled `--selftest` not run here; v1 success does
not prove v2. Repo lint returns unrelated existing LuaBridge64.cs:1752 regex
false positive (`clientRip > _base + _moduleSize`). Extract v2 package over v1
in C:\ghidra-scripts\WowBCtmCapture; one run produces a report. Capture user
values can be camera/player/other; analyze motion and scope, then seek controlled
repeats and Ghidra + user-requested rea/Gemini tracing before any offset/call.
User dislikes repeated manual runs: batch and report only; no unsafe calls.
Fake tests assumed passed unless user reports failure, not proof of execution.
CTM core, safe ABI, x64 startup, SQLite and combat/quest/loot remain unresolved.

## Local debugger-trace experiment

User approved invasive probing on their local server and accepts crashes.
`Tools/WowBCtmTrace/` now contains a separate single-run v2-style capture plus
experimental Windows debugger watchpoint probe. It is NOT custom DLL injection:
DebugActiveProcess, DebugBreakProcess and per-thread debug registers are used;
target stops during debug events. Fresh candidates only; select up to 4
non-overlapping triples and watch largest-changing component. User labels manual
click C / different click D / keyboard-only K, then Q; cap 90s/3000 events.
Reports contain `RIP-after-write`, instruction bytes, registers and raw stack;
this is not a reliable unwind or verified CTM. New-thread handling, slot conflicts,
restoration and detach are implemented but **not OS-tested**. Client restart on
uncertain cleanup or abnormal exit. Fresh address reuse and candidate filter may
miss true CTM, and event budget may be exhausted before the manual clicks.
C# syntax parse and offline ctypes layout/DR7 model passed; unrelated known
lint false positive remains. No C# build, `--selftest`, Windows API or live test
in sandbox. ZIP and current source overlay must be uploaded after creation;
consult newest Drive handoff file names, not older capture-only v2 artifacts.

## 22:02 trace and injected follow-up

The user's watch-trace `191D6ANwVdEotK-k7NVGSZVWD4RgpNgDN` was read in full;
capture `1IAkUKz6qrAe2NC7ERqbLpn9C4Gaeg_Q3` middle-truncated in transport.
They used only mouse movement, including phase label K, so no keyboard control.
71 threads armed, 274 events, restored/detached true. Image scan coverage was
zero. Two coordinate-like candidates were copied from another allocation by
non-image `0x7FFA4F2B30FB` after-write RIP. Derived old-process source addresses
`0x27B9FA423C0`, `0x27B9FA42280` from exact SIMD copy and R9, not validated CTM
and never reusable after process change. See findings for full details.

`Tools/WowBCtmTraceInject/` now chains fresh capture + debugger + injected native
`Tools/WowBCtmInject/` page-guard/VEH probe. Exact SIMD sequence and stopped
source/destination byte equality identify fresh copy-source targets; fallback to
fresh candidates or checked player position. PID + process creation checked.
User explicitly authorized injection and accepts crashes. Remote DLL/handler/
TLS/config persist until client exits; **restart after experiment** even if
stop says successful. Page guarding and TF races may cause errors, freezes,
missed events and stale addresses. 25-second/512-event cap. Native components
cross-compiled Windows AMD64 and binary/exports checked; C# syntax only; no
Windows build, injected live run, or teardown validation yet. Windows user gets
three logs; follow fresh pre-write RIP paths using Ghidra + rea + Gemini; mouse-
only capture is not an independent keyboard comparison. Injection is research,
not proof of CTM offset or call ABI. Latest Drive ZIP and overlay supersede v1
trace backups; all sources available, clean base still 21f058f.

## 22:19 crash: pause invasive probe; audit existing writers offline

User reported crash during the mouse-click experiment and uploaded four files;
IDs, checksums and evidence are recorded in the findings. Crash timestamp
22:19:25.296 labels `Security Crash`, `0xC00000E5`, params `0xE1D05001/0x700`.
Debugger report start-name 22:18:54.752 + 11.58s => approx 22:19:06.332;
injector report start-name 22:19:32.712 => 7.416 seconds after crash assuming
same unchanged clock. Injector says remote DLL not found / return 0 / loaded=0
started=0 stopped=0; source exits before ProbeStart, so no injected page guard
was installed by this run. DLL-load observations aren't absolute proof of no
transient load. Cause of security-labelled exception and relation to prior
debugger remain unknown; process still existing is not proof of health. Do not
ask user to repeat the invasive sequence yet. Old zip remains historical.

Debugger reports 106 hits, all slot 3 by signature summary (not the more
coordinate-like slot 0), restore/detach true; zero exact copy sources. Store
instruction RVAs 0x1F412C9, 0x20409D6, 0x1F40F8B, 0x1F40EBF are writer
instructions only; candidate possibly transform/render data, no CTM evidence.
Prepared syntax-parsed, unrun `hunt/WowBCtmWriterAudit70291.java` for offline
Ghidra byte-checked audit. User uses Ghidra/rea/Gemini; unknown runtime control
flow and safety. Do not falsely claim full crash/capture/trace content was read:
connector response omitted middle sections. Plan analyze offline first, then
reassess client safety and only design new controlled testing if necessary.

## 22:27 Ghidra audit / user-requested quick injected probe

Full Ghidra writer audit Drive `1qIXdLI5gUVRJDnCb7RiPT2HiXJl5ydNu`, MD5
071502f6d6095a13e7206e652864caa0. Completed script, all four live/image store
bytes match, no containing-function metadata/xrefs. 1F412C9 scalar field copy,
20409D6 block SIMD copy, 1F40F8B/1F40EBF shuffles and 0x30-byte stores suggest
generic vector/matrix data; not evidence of CTM, not validated flow or ABI.
User explicitly requested an injection-only 10-second mouse-click experiment
instead of repeated capture/debugger steps despite prior security-labelled
crash. `Tools/WowBCtmQuickProbe/` contains source and cross-compiled AMD64 EXE
and DLL. EXE identifies local player via checked GUID/slot (no static old VAs),
then injects page-guard/VEH on player position for 10 seconds. Physical right
button polling on foreground client is not proof of game input; guard events
are before write and may be missed. Report streams while running. It is a
writer probe for player position, **not** identified CTM or direct click
handler. Restart client after every attempt; DLL/handler/TLS persist to exit.
Prior `Security Crash` cause unknown, could recur. Actual portable lookup passed
synthetic Linux tests; Windows native builds and PE/exports checked; no Windows
run, injection, debugger, timing or live-game validation here. ZIP and updated
Drive overlay must be uploaded. Future plans: inspect fresh report and failures;
no automation or offset promotion without evidence, preserve x86/3.3.5a.

## Quick probe exited immediately; diagnose loader without touching client

User pasted quick-probe log PID 3560: partial image identity matched, local
player GUID/slot anchor found, remote allocations written. Then `Remote DLL not
found after LoadLibrary; return=0x0`, `loaded=0 started=0 stopped=0 status=1`.
ProbeStart and 10-second PAGE_GUARD were never invoked; no clicks recorded.
32-bit thread exit value and module scan do not reveal remote GetLastError or
precise cause; do not assert protection, missing dependencies, or client crash.
Restart client before future live probes to clear remote allocations.

Created `Tools/WowBCtmQuickProbeDoctor/`: native x64 standalone EXE checks the
existing DLL via normal LoadLibrary in its own process, then performs the same
remote load into a disposable child helper it owns. It never opens or touches
WoW and never calls ProbeStart. Report tests file, exports, local Win32 error,
helper remote path/readback/thread/module observation. Place doctor executable
next to existing QuickProbe DLL; no game clicks. Cross-compiled Windows AMD64,
PE/import verified, not Windows-run here. Wait for doctor report before
choosing a fix; a passing helper does not prove a game-specific cause. No
security bypass. Existing security-labelled crash's cause remains unknown.

## Update 2026-10-28 (later): doctor passed, probe v2 ready

Doctor run on the user machine: DLL metadata/exports/local load OK, helper
remote LoadLibrary OK, path readback match, module found, status 0. So the
loader failure inside wowb.exe is not a broken DLL/package and its cause is
still unknown (no GetLastError was captured in-game).

Probe v2 = same standalone 10-second flow, but the remote call is now a stub
that saves the full 64-bit HMODULE and calls GetLastError on NULL. ZIP
`1Kcri0dYL1RvAflMPl8EubFO5NFR8PkJJ` (MD5 cdafd34867d0dc1c3c76d855bff90439),
extract into `C:\ghidra-scripts\WowBCtmQuickProbe` (overwrite both files).
Restart WoW before running it. Report line to read: `diagnostic ... phase=...
fullModule=... loadLastError=...`. If it fails again, that error code is the
next evidence; if it succeeds, the 10-second window runs and the event report
answers whether record+0x60 writes correlate with right-click destinations.

## Update 2026-10-28 (probe v2 result -> v3)

v2 in-game: `exit=0 phase=0 fullModule=0 loadLastError=0`. The stub never wrote
its first field, so LoadLibrary was never reached - the failure is "injected
code did not execute", not "DLL load returned NULL".

v3 ZIP `1nyZU3h-fV9gefFUtzOyHbKBIGk7o97FN`
(MD5 b9c8c43daa9e8b53982638da23557d81) adds preflight readback, a minimal
execution test, CreateRemoteThread -> QueueUserAPC fallback with the route that
works reused for load/ProbeStart/ProbeStop, and a read-only mitigation policy
dump. Extract over `C:\ghidra-scripts\WowBCtmQuickProbe`, restart WoW first.
Read in the report: `mitigation ...`, `minimalStub ...`, `executionRoute=`,
`loaderStub ...`, `probeStart ...`. If mitigation shows DynamicCode=0x1 or
Signature=0x1, that alone explains blocked injected code/unsigned DLLs. If
neither route executes the minimal stub, remote execution is being prevented
before the first instruction and context hijack (which did reach payloads in the
0.8.x runs) is the remaining route.

## Update 2026-10-28 (probe v3 result -> v4)

v3 (pid 18504): all mitigation policies 0; CRT stub did not execute (phase 0);
APC stub executed (phase 42); the APC load stub then ran 74 times concurrently
(my bug) so its load errors 87/570 are contaminated - 570 is an internal
ERROR_NET_OPEN_FAILED placeholder.

Established: thread start routines via CreateRemoteThread do not run in
wowb.exe, while APC code on existing client threads does. Not established: any
genuine LoadLibraryW failure code.

v4 = strict single-shot: sleep-timed CRT test, one minimal stub, one APC per
candidate thread, one control load (System32\version.dll), one probe load, one
ProbeStart, 10 s watch, one ProbeStop. Local ZIP `WowBCtmQuickProbe-v4.zip`
(MD5 80aac08fb535d0426c66b7006bd46523), Drive `1ze7aWJUCEUkO4NzYuhzjSKIfEtzqeb8Z` (uploaded 2026-10-28).
Read in the report: `sleepTest verdict=`, `minimalStub`, `executionRoute=`,
`controlLoad`, `probeLoad`, `probeStart`. If the control load succeeds but the
probe load fails, the problem is our DLL's path/image, not a generic block.

## Update 2026-10-28 (probe v4 invalid -> v5)

v4 ran against the same client instance as v3 (same pid/creation), so its
"nothing executed" line is an artefact of leftovers, plus a real bug of mine:
the CRT method flag was uninitialised, so that stage called OpenThread(0) and
failed with 87. Its one valid datum: `sleepTest verdict=routineExecuted` -
CreateRemoteThread start routines DO run in wowb.exe (kernelbase!Sleep,
1500 ms), so the unresolved anomaly is executing our anonymous stub page via
that route, not thread creation.

v5 ZIP (Drive `1jCeP7-0Ju-0AaGyPFCmn4X6bU7JNqory`): MD5 74100e4dfc06b349c342e5a7713ccf24, EXE b8c345952c43ae8ef78201d65b701755.
Adds a hard restart guard (last pid+creation recorded in
C:\ghidra-scripts\WowBCtmQuickProbe-last-target.txt; same instance is refused,
--force overrides), fixes the CRT stage, one APC per thread with 12 s waits,
and separates phase=0 (not delivered) from phase=1 (entered, never returned).

## Update 2026-10-28 (probe v5 clean result -> v6)

v5 on a fresh client (pid 10720): CRT start routine runs kernelbase!Sleep but not
our stub; APC on tid 10636 executes in 16 ms; LoadLibraryW is entered and
returns NULL + error 87 for BOTH System32\version.dll and our probe DLL.
Conclusion so far: the block is not specific to our DLL (control fails
identically) and not a documented mitigation policy (all read 0). 87 with a
valid path is not the documented loader outcome (missing path would be 126).

v6 (Drive `13b99jWOnSArN46H8P9v53GgRYnwKs_N6`, MD5
35313ed813cd486383d140017cd62bf0) adds callVerify (GetCurrentProcessId must
match the real PID), bogusLoad (expect 126; anything else means the reported
error is not from path resolution) and ldrLoad (ntdll!LdrLoadDll with a
hand-built UNICODE_STRING, bypassing kernel32/kernelbase). Read those three
lines first in the next report.

## Update 2026-10-29 (v6 result -> v7 with manual mapping)

v6 (pid 15980): callVerify matched the real PID; bogusLoad gave 87 instead of
126; LoadLibraryW gave 87 for every path; ntdll!LdrLoadDll gave 0xC000071C
(STATUS_INVALID_THREAD). The OS loader path is closed to injected calls here.

v7 (Drive `1-53rI_Jn9war1URR6ma5o0rt7sTL3DQm`, MD5 a59647e26ed31d1d40b061906e32381d): adds
read-only codeDiff hook inspection and replaces LoadLibrary entirely with a
manual PE mapper. The probe DLL is now CRT-free (kernel32-only imports) and is
mapped by hand, imports resolved against already-loaded modules, then
entry -> ProbeStart -> 10 s watch -> ProbeStop. Both EXE and DLL changed; the
user must replace both files.

## Update 2026-10-29 (v7 SUCCESS -> anchor validation)

v7 (pid 8104) is the first complete injected run: no loader hooks
(codeDiff patched=false x6), manual PE mapping succeeded at the preferred base
with 11/11 imports resolved and no relocations needed, mapEntry returned 1,
ProbeStart returned 0, 10 s observed with 12 right-clicks, ProbeStop 0, overall
status 0.

But the observation was empty: pages=1, guardFaults=1, writeFaults=0,
events=0 - one read, zero writes to the record page. So `record+0x60` is not
demonstrably live, and injection further is pointless until the anchor is
validated.

WowBFieldDiff v1 (Drive `1CpEtDTff2EvOTiTDygJDdvfwOtlbjUpl`, MD5
842780aa47dd198815810cb61bd91b4e) is read-only polling: it reports which words
in the record (and record+0x1000) change while the character walks, with float
values and right-click timestamps. Run it in `C:\ghidra-scripts\WowBFieldDiff\`
and send `C:\ghidra-scripts\WowBFieldDiff-*.txt`. No client restart needed.

## Update 2026-10-29 (field diff analysed -> path watch)

The field report `1ReFqU14qSmPECBh2gGeJheXubtmLUjGv` validated the anchor:
record+0x60/64/68 is a live position triple (mirrored at +0xA4) that updates
~31 ms after a right-click. v7's zero-write window simply had no movement.

The record also holds three further position-like triples at +0x150 (+0x194),
+0x6A0 (+0x6E4) and +0x7D0, 59-96 yards from the player triple. Which one is
the player and which is a click destination is the open question; it decides
whether an engine-level move can be driven by writing a destination field.

WowBCtmPathWatch v1 (Drive `1BzsDH1i0y87snQukPhB5w6EjrU4jwlHX`, MD5
1c95aa3aeedd203dd0d85f4e51028579) answers it in one 12-second read-only run:
4 s baseline, then one right-click plus hands off, then verdicts per triple.
No restart needed. Next after that: if a DESTINATION candidate exists, test
writing it (injected, manual-map route already proven) and confirm the
character walks; if none exists, the next step is PAGE_GUARD write tracing with
the movement trigger as the start condition.

## Update 2026-10-29 (path watch: no destination field -> call harness)

Path watch (`15CzU2hO1T5jBrRQ6pu_fKZPXy4jdPtbZ`): one right-click produced a
clean 44-yard walk tracked in record+0x60 (mirror +0xA4), 31 ms after release.
Both watched triples were perfectly static during the 4 s baseline. No
destination candidate appeared: +0x7D0 drifted 16 yd with 2.6 yd net (looks like
a cursor pick), +0x150/+0x6A0 read implausible in this run. Click delivery works.

So writing a destination field is unsupported; the route is to call engine
movement code. WowBRemoteCall v1 (Drive `1G_4wSPvi3gAk52qi5P3WTH9oZxFCFDtb`,
MD5 ab4b092d276bf8abf35cff3890a84324) does that through the proven APC stub:
read-only prologue dump by default, `--call RVA[:args] --go` for one call, and
`--moves --go` to call MoveForwardStart/Stop and report the measured position
delta. Restart the client after any --go run.

Next: user runs it read-only first (prologue bytes confirm the RVAs), then
`--moves --go`. A nonzero `moved=` value means the bot has a working movement
primitive without click-to-move; CTM then becomes an optimisation for long paths.

## Update 2026-10-29 (MoveForward abandoned -> hunt the CTM destination)

User decision: MoveForwardStart/Stop are locked and are a dead end; stop
investigating them. Focus: the click-to-move memory offset.

WowBCtmFieldHunter v1 (Drive `1gD4JK07b4r5T8YiSyqr8QahUh7skvsqF`, MD5
89c0a793c3cc26c993c2de43090cf0ef) is read-only and needs no restart. It takes
three snapshots of a 128 KiB window around the player record (before click, just
after click, after the walk) and reports float triples that changed at the
click, stayed constant, and ended where the character stopped, with the cosine
against the walked direction. Offsets are printed relative to the record.

Run: `"C:\ghidra-scripts\WowBCtmFieldHunter\WowBCtmFieldHunter.exe"`, hands off
for 3 s, one right-click 50-100 yd away on flat ground, hands off, send the
report. If the candidate list is empty, re-run with `--span 40000`.

The harness bug that blocked the previous call attempt (no alertable thread
found among 4 candidates) is patched to 24 candidates with one retry each, but
calling game code stays deprioritised per the user's decision.

## Update 2026-10-29 (Winkr-Public analysed -> FieldHunter v2)

v1 found 0 candidates in 128 KiB (32,766 triples). Its test was endpoint-based,
so a destination consumed on arrival is invisible, and a 3-float test rejects an
(x,y) point.

User supplied <https://github.com/laurentiumanafu/Winkr-Public>. It targets a
different client (1.15.9 / 69109 `WowClassic.exe`), so no offset transfers, but
two findings reframe the problem: (a) movement keybinds are gated by ONE global
`cmp qword [g_taint], 0`, the same global behind our `FORCETAINT_STRONG`;
(b) **ClickToMove does not exist** there - no string, no CVar - and right-click
runs through `CameraOrSelectOrMove`. So the move point may be cursor-derived
rather than stored. Full notes: `hunt/WINKR_REFERENCE.md`.

v2 (Drive `1QjQQuvIDxQCXKHxysPuHP6vx4VhVPb4P`, MD5 fb84eaa5e266ab1f4caab56f6d9e1d02)
tests both shapes in one run: baseline -> cursor sweep (no click) -> one
right-click -> two samples during the walk -> end. Shape A = constant during walk
+ ahead + approaching; shape B = follows the cursor, flagged frozen if it stops
after the click. Still read-only, no restart.

Decision table: A hit with small dEnd -> write it, CTM solved in memory. A empty
and B frozen=1 -> cursor-derived and adopted at click. A empty and nothing frozen
-> re-derived every frame, so build world-to-screen projection and synthesise the
right-click (external input is the documented zero-RE fallback, and our client
already responds to real right-clicks). Both empty -> re-run with --full.

## Update 2026-10-29 (v2 run analysed; Winkr read in depth; sig scanner built)

v2 run (pid 7208): cursorFollowers=389, frozenAfterSweep=0, 24 A-candidates with
best dEnd=22.99 yd, walked 76.57 yd. Analysis:
- The 389 "followers" were mostly OVERLAPPING windows of a few matrices
  (record+0x170 and record+0x280 each yielded six). Every row is unit length
  (norm 1.0003) => rotation/basis data that changed while the mouse orbited the
  camera, NOT terrain picks. Tool flaw, fixed in v3 (run-start detection +
  `run=N` + `norm`).
- The 24 A-candidates are 12 distinct positions each duplicated at exactly +0x44
  (the same duplication as the player position at +0x60/+0xA4). So that region
  holds a cache of nearby entity positions, not a destination. Best dEnd 22.99 yd
  versus a 76.57 yd walk: not a click destination.

Winkr read in depth (code, not just docs) - see hunt/WINKR_REFERENCE.md:
- `HANDLE_TERRAIN_CLICK` is the ground-target SPELL finaliser, not movement.
- No `MoveTo` implementation exists in the repo. Their walker is
  "write facing (pos_base+0x50) + hold MoveForward" with taint cleared.
- Injection is reflective manual map + thread hijack (Nt* thread APIs), not
  LoadLibrary - consistent with what our client allows.
- EXPECTED_BUILD=69547 gates everything: a mismatched client attaches in SAFE
  MODE but has no working movement/Lua until the RVA set is re-derived.

New: `Tools/WowBSigScan` - read-only scanner that applies Winkr's 19 scannable
byte signatures to the LIVE wowb.exe image (the on-disk exe is encrypted; the
in-memory image is not, so no dump is needed). Positive control: the known
UnitHealth prologue at +0x1D63190 must match exactly once, otherwise the report
says SCAN SUSPECT and nothing else in it is trustworthy. Targets WORLD_INTERSECT
and WORLDFRAME_PROJECT_POINT, which together enable click-to-move with no CTM
offset at all: project the destination to a screen point and send a right-click
there (our client already walks from a real right-click).

Bundle: WowBCtmTools-v1.zip (Drive `1olEJJtkLHoXbVfjVbaeusjVkxAsSErDp`, MD5
80d6fb9896b5682db21cdbb50f69c887) = WowBSigScan v1 (EXE a05494b3b1a0338561ddc2cf7ffaa512)
+ WowBCtmFieldHunter v3 (EXE d02c2122a2ff807c01ab90209d0e7398).

## Update 2026-10-29 (sig scan v1 self-failed; v2 section-based)

The v1 signature scan reported 0 of 19 with its own control failing. That was a
tool bug: `if (got<CHUNK) break;` aborted the scan on the first partial 4 MB read
(stopped at 0x3A3C000 of 0x863C000), and the per-chunk `limit = got-maxLen+1` made
boundary-straddling patterns invisible. The client was fine - the tool's own
identity check read the same 16 bytes at +0x1D63190 successfully first.

v2 (Drive `1gyy9tZ7KD2zc5VmdRAsGVGbF3geeEXlL`, MD5 b97e487c01c0d9840327214734d72ea4)
scans the PE section table, whole section per buffer, with a validity map, and its
scan core is unit-tested on buffer-edge and unreadable-hole cases. Report now shows
per-section `unreadable=` bytes, so NOT FOUND in a partly unreadable section is not
read as absence.

Next: user re-runs `WowBSigScan.exe`; the control must say SCAN OK before any
WORLD_INTERSECT / WORLDFRAME_PROJECT_POINT result is trustworthy.

## Update 2026-10-29 (sig scan result + click aiming)

Sig scan on 70291: 4 of 19 patterns matched UNIQUE in .text - LUAL_LOADBUFFER
0x48A1A50, HANDLE_TERRAIN_CLICK 0x25D6670, LUAH_NEW 0x472A3D0, WOWGUID_TO_STRING
0x47B96A0. WORLD_INTERSECT and WORLDFRAME_PROJECT_POINT NOT FOUND -> the
"project a world point and click it" route needs those derived another way.

Correction recorded: the 16-byte UnitHealth prologue is NOT unique (9 copies,
first at 0xAF71D0). It was always a presence check, never a fingerprint. All
probes still get their build identity from it, which is fine for that purpose.

New tool: Tools/WowBClickAim - measures the click mapping empirically using only
the confirmed position field. `--dry` first (prints the pixels it would click),
then the real run (8 clicks: dx -300..+300 at fixed dy, then three dy values).
Fits pixel_dx -> world (dx,dy) and pixel_dy -> walk distance, reports rms, and
says CLICK SYNTHESIS WORKS / NOTHING MOVED. `--sendinput` switches from
PostMessage to moving the real cursor. No memory writes.

## Update 2026-10-29 (CTM CONFIRMED: synthesised clicks work)

WowBClickAim v1: 8 probes, 8 movements, ~6.8 yd each. Click-to-move is real and
needs no memory offset at all.

v1's numbers were invalid though: constant distance across dy=-300..-60 means the
fixed 0.85 s window saturated at run speed; the character never stopped, so each
probe mixed leftover motion from the previous click with the new one (also why two
dx=0 probes disagreed by 15 deg). v2 measures rest-to-rest.

Simulating the controller exposed the real constraint: a click only reaches
headings inside the camera cone, so a goal behind the camera needs a right-drag to
turn the camera first; clicking at the screen edge diverges. v2 implements
calibrate (5 clicks) + closed-loop walk with drags, and the loop is validated in
simulation (front goal 5 legs, behind goal 6 legs with 2 drags).

Sig scan: process mode is non-deterministic (.text partly resident; two runs gave
different unreadable sizes, 9 vs 5 control copies, and HANDLE_TERRAIN_CLICK
UNIQUE vs NOT FOUND). v3 adds --file to scan the decoded image instead -
deterministic and complete.

Next: user runs ClickAim `--dry`, then calibration, then `--walk 30 90`; and
WowBSigScan `--file wowb-decoded.exe` to settle WORLD_INTERSECT for good.

## Update 2026-10-29 (WALKER CONVERGED; file scan was reading ciphertext)

ClickAim v2 field run: 14 legs, 45.33 -> 1.93 yd, REACHED. Movement primitive done.

Fixes in v3 (built, unit-tested): dy does not control distance (one dy=-20 gave
0.09-12.35 yd), so the distance model is deleted and distance is closed by
iteration; learning now ignores legs travelling <3 yd and rejects sign flips and
3x outliers (the field run's exit model was sign-flipped and 3x off); adds BLOCKED
detection and moves the click point after two legs of no progress.

SigScan v4: the Desktop wowb-decoded.exe is the ENCRYPTED original (.text 1 KiB
raw vs 76 MiB virtual, .data 6.9 MB raw vs 44 MB virtual), so every "match" was a
lookalike inside the compressed blob. v4 adds per-section structure checks
(raw/virtual, entropy, prologue and int3 density), mapping candidates scored by
the control site, and never counts a hit in a non-executable section.

Next: user runs ClickAim v3 `--walk 30 90` to confirm the hardened loop; SigScan
only needs re-running against a genuinely decoded image (or the live process).

## 2026-10-29 (later) - ClickAim v4 and SigScan v5

### ClickAim v3 failed in the field, and the fault was three unset parameters

The v3 calibraion succeeded and the walker refused at the first leg:

    WALK 30 yd -> "Cannot plan a leg (goal outside the cone and no drag sensitivity)."
    model at exit: headingAtZero=0.0 degPerPx=-0.07861 degPerDragPx=0.00000

1. degPerDragPx=0: the drag calibration was removed together with the distance
   model in the v3 rewrite, so nothing could rotate the camera.
2. coneHalfDeg=0 (never set after memset): every goal is "outside the cone".
3. headingAtZero=0 (never assigned): the first leg would plan against due east.

v4 probes both turn channels (200 px right-drag; if that turns nothing, 500 ms
holds of VK_RIGHT then VK_LEFT, each followed by one measured centre click),
uses whichever produced more than 2 deg, derives the cone from the measured
sensitivity (0.75 * |deg/px| * halfWidth, capped at 75 deg), and sets
headingAtZero from the last measured centre click. 200 px, not 500: at the
measured 0.37 deg/px a 500 px drag rotates past 180 deg and aliases to the wrong
sign. BLOCKED now needs four CONSECUTIVE non-moves (v3 counted them run-wide).

Also measured this session: click sensitivity -0.07861 deg/px against -0.0936 and
-0.1029 earlier - 25% between sessions, so calibration must run every session.
dy spread was 13.79 to 72.27 yd for one click offset (5.2x): dy is a position
hint, never a distance control.

Command to run next:
    "C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --walk 30 90
The report should now show cal[drag] (or cal[key]) with a real deg/px, a cone,
and "heading at centre right now".

### SigScan v4 live scan: mapping confirmed, only 20.6% of .text readable

4 of 19 patterns matched, all in executable code, mapping confirmed by the
UnitHealth control at 0x1D63190:

    LUAL_LOADBUFFER       0x048A1A50
    LUAH_NEW              0x0472A3D0
    WOWGUID_TO_STRING     0x047B96A0
    HANDLE_TERRAIN_CLICK  0x025D6670   (second independent scan agreeing)

.text: size 0x0494343C, readable 0x0F17000 = 20.6%, noaccess 0x3A2C43C in 1334
ranges. So the other 15 patterns (WORLD_INTERSECT, WORLDFRAME_PROJECT_POINT,
SPELL_STOP_TARGETING, INTERACT_UNIT, Lua secret gates) are UNSEEN, not absent.

SigScan v5 prints coverage on every NOT FOUND and adds --watch SECONDS: rescan
while the client runs so lazily decrypted pages enter the scan, dedupe hits by RVA
(unit-tested sc_dedupe_hits, so five passes cannot turn one function into an
"ambiguous" pair), and report the pass on which each pattern first appeared.

    WowBSigScan.exe --watch 600

### Still open

- v4's turn-probe sequence has not been run against a live client yet.
- Navigation beyond a straight-line goal: obstacles still need a path/mesh layer.
- Combat, quest and loot offsets: unchanged from the earlier gap list.
- SigScan needs a genuinely decrypted image (runtime dump) or a long --watch run
  before any of the 15 unmatched patterns can be called absent.

## 2026-10-29 (third) - v4 converged; v5 fixes the two impossible legs

ClickAim v4 REACHED: 30 yd in 17 legs, 1.74 yd remaining, converged=1. Drag channel
worked (-0.22653 deg/px), so the key probe was skipped (degPerKeyMs=0 at exit).

Two legs were geometrically impossible: remaining went 2.04 -> 30.83 yd after
moving 5.45 yd, then 30.83 -> 110.23 yd after moving 0.63 yd (that one came right
after the 20 s rest timeout). For a fixed goal the distance cannot grow by more
than the distance walked, so those position reads were stale/discontinuous.

ClickAim v5:
- plans from the same read the leg is measured from (rest_click takes the sample);
- am_leg_consistent + am_plausible_step reject impossible legs and sample jumps;
  such a leg is re-planned, learns nothing, and five of them stop the walk;
- wait_rest re-baselines on a discontinuous sample instead of calling it movement;
- learning only from legs where both rests settled, 3-22 yd travel, candidate
  within 2x of calibration, and weighted by travel (a 21 yd leg moves the model
  >4x more than a 4 yd one). The v4 run drifted 78% off calibration because a
  4.3 yd leg counted as much as a 21 yd one;
- exit report prints drift % versus calibration and the discontinuity count;
- near-goal short hop now applies inside 15 yd (v4 used 8; leg 2 overshot 20 yd).

SigScan v5 --watch is incremental: later passes only re-read previously unreadable
pages, 80 byte lead-in, coverage from a page map, per-pass timing printed.

Next: re-run `--walk 30 90`. Expect the same convergence with no IMPOSSIBLE
GEOMETRY lines, or those lines appearing and being corrected automatically.

## 2026-10-29 (fourth) - v5 converged; v6 adds STOP, SigScan v6 fixes phantom hits

ClickAim v5: 30 yd in 6 legs, 1.68 yd, converged, no impossible legs, drift +3%/+6%.
Validation works. Remaining weakness is the final approach: legs of 7-11 yd to close
a 4 yd gap, one moving away from the goal, because click distance is terrain-driven.

ClickAim v6 (capture):
- poll position every 60 ms during the run; when remaining <= capture + coast, tap W
  for 70 ms to cancel click-to-move (a movement key replaces the pathing move);
- coast is MEASURED per stop (distance from key press to standstill), smoothed into
  stopDistance, first guess 0.8 yd; if the key does not cancel within 6 s, stops
  self-disable and say so;
- am_should_stop / am_learn_coast are unit-tested, including a continuous-motion
  model showing a correct coast lands inside capture from 3-60 yd and an
  under-estimated coast overshoots;
- --nostop restores old behaviour, --capture YD moves the radius.

SigScan v6:
- fixed the phantom rva=0 duplicate (counts advanced but hits rewritten from index
  0) -> unique functions no longer reported AMBIGUOUS; summary count corrected to
  4 of 19 (was reported as 5);
- documented that coverage FALLS across passes (32.6% -> 28.3% over 26 passes, ~29
  pages re-locked per pass), so scan once, right after play; --watch is a union,
  not a path to 100%.

Next: `WowBClickAim.exe --walk 30 90` and check the "(stop at X yd, coasted Y yd)"
lines appear and the goal is met in fewer legs. If stops disable themselves, the W
key is not cancelling CTM on this client and --nostop is the fallback.

## 2026-10-29 (later) — v7 of both tools

Built and unit-tested offline; Windows build/run is the acceptance step.

* `Tools/WowBClickAim` **v7** — multi-offset click calibration with least squares
  over +150/+300/-150/-300 px, a +/-300 px span sanity band (30-150 deg), and two
  held-out offsets (+/-225) that must be predicted or **the tool refuses to walk**;
  two opposite drags giving a slope plus a direction bias; a pass-by stop
  (`am_passby_stop`, 6 yd radius, 0.5 yd give-up, 3 polls or 4x give-up); `closest=`
  and `stop`/`pass-by stop`/`no stop` on every leg line; coast learning gated at
  0.30 yd (`AM_MIN_COAST_LEARN`). New helpers in `aimmath.h`:
  `am_fit_through_origin`, `am_prediction_ok`, `am_passby_t/am_passby_reset/
  am_passby_stop`. EXE `c1953362f1d5590936035386ffdbb926`.
* `Tools/WowBSigScan` **v7** — the "All executable bytes were readable" line is now
  behind `lastPct >= 99`, with a separate sentence for flat coverage; the per-pass
  count stops at `g_sigCount` (control excluded); `NOT FOUND (..., retry in
  progress)` became `UNSEEN (only N% of executable bytes were readable)`. EXE
  `efb552b810ba4c109490b620e4d66df9`.

Offline suites pass: `test_aimmath` (fit, holdout rejection, pass-by, coast gating)
and `test_scancore`. Next run on the user machine:

    WowBClickAim.exe --walk 30 90
    WowBSigScan.exe --watch 60

Expect: 6-8 calibration probes with residuals, two holdout lines, `closest=` per
leg, `pass-by stop` on lateral misses, and 4 of 19 in both the per-pass and summary
lines of the scan.

## 2026-10-29 (v7 field reports, then v8)

v7 reports from the user machine:

* **SigScan v7 works as intended** (`WowBSigScan-20261029-144326.txt`): consistent
  `3 of 19` everywhere, `UNSEEN (only 20% ...)`, no "all readable" contradiction,
  new "Coverage held at 19.8% across 13 passes" line. Only 3 unique matches because
  `.text` readable fell to 19.7 % (0xE66000) from 29.1 % (0x1552000);
  `HANDLE_TERRAIN_CLICK 0x025D6670` was **UNSEEN, not absent**. No change needed.
* **ClickAim v7 refused to walk, correctly**, and its probe list exposed the real
  defect: the camera yaw at a click is the **heading of the previous walk** (follow
  camera), so referencing every probe to one fixed origin put three of six slopes
  on the wrong side of zero. Against the previous walk they agree: median
  -0.0857 deg/px (matches v5's -0.0936/-0.1029/-0.10983). Response also saturates
  (+225 and +300 buy the same angle), so no global line fits.

`Tools/WowBClickAim` **v8**: per-probe slope with the previous walk as reference,
`am_median_slope` (median + 2x outlier rejection), held-out check on the moving
reference, and a failed check now downgrades to UNVERIFIED-and-walk-instead-of-refuse;
`headingAtZero` = measured heading of the last leg (no back-solving the click model);
v7's pass-by stop / `closest=` / gated coast learning retained. EXE
`a61fd31581066349fa075249406334d7`. `test_aimmath` covers the v7 probe table
explicitly (sequential vs stale reference) and passes.

Next field run: `WowBClickAim.exe --walk 30 90`. Watch the per-leg `click slope ->`
lines: residuals should stay flat. If click residuals grow leg by leg, the
follow-camera rule is the thing to doubt, and the fallback is to re-measure the
centre heading before every leg.

## 2026-10-29 (v8 field run -> v9)

v8's first field run (`--walk 30 90`) walked, kept the honest UNVERIFIED label, and
then ended BLOCKED at 13.79 yd. Diagnosis of its own log:

* `leg 1: moved 0.00 yd in 0.25 s` = rest detector armed before the client started
  walking (race). Fixed: CLICK_ARM_MS 900.
* `leg 3: IMPOSSIBLE GEOMETRY` on 50.19 yd / 15.70 s = 3.2 yd/s = false positive
  from the 3 s dt clamp in am_plausible_step. Fixed: am_plausible_leg (average
  speed); `test_aimmath` asserts 50.19/15.70 passes the new check and fails the old.
* drag channel was one direction (-0.19706) and drove +532 px legs. Fixed: four
  drag probes (+/-200, +/-300), moving-reference median, drag capped at 420 px.
* obstacles are the norm: added DETOUR (55 deg off the goal bearing, far click,
  alternating sides, 6 attempts) before declaring BLOCKED.
* calibration cached to `C:\ghidra-scripts\clickaim-cal.txt`; `--load` skips probing.

EXE `4e1b60deefece12269adeac40488823e`. Offline suite passes including the v8 data.

Next run: `WowBClickAim.exe --walk 30 90` (first run calibrates and saves), then
`--load --walk 30 90` for repeat attempts. Watch for DETOUR lines and whether `moved`
recovers after them; a detour that also moves <0.5 yd means the spot is enclosed.

## 2026-10-29 (v9 field runs -> v10)

Two v9 runs. Click calibration was good in the first (median -0.07861 deg/px,
4 probes, residuals < 3 deg) and everything after it failed: drag probes 0.07-2.90
yd, key probes 3.41/6.37 yd, all rejected by the 8 yd floor, so NO turn channel was
calibrated; then the run saved `drag=0 key=0` and the `--load` run loaded those
zeros and refused to plan. Both runs moved 0.00 yd.

v10 (`Tools/WowBClickAim`, EXE `ef7a513631a990f5e6a0104846604be8`):
* calibration probes at dy=-100 (~6-10 yd) instead of dy=-140 (20-70 yd): the probe
  sequence had been a 230 yd random walk that left the character jammed;
* CAL_MIN_TURN_WALK 8.0 -> 2.5: a turn needs a clean walk, not a long one;
* a calibration with no turn channel is NOT saved; --load treats 0 as unknown and
  probes it;
* REACH-AROUND (up to 8 legs at the cone edge) instead of "cannot plan a leg" when
  the goal is outside the cone and no turn channel exists;
* dead/rooted/inside-geometry diagnosis when two probes in a row move < 2 yd;
* dy-spread evidence clicks now opt-in (--dyevidence).

Mesh question (user raised CopilotBuddy): a Recast/Detour mesh is build-independent
and is the correct long-term navigation answer, but CTM is immediate-mode, so the
mesh must supply mutually-visible click points (A* + funnel), and the actual blocker
is the beta client's map-data layout, not any offset.

## 2026-10-29 (v9 run -> v11): calibration VERIFIED, runaway input state

The click channel is now measured: median -0.10709 deg/px from four probes with
residuals < 3.3 deg, BOTH held-out offsets inside tolerance (VERIFIED), drag
-0.18471 from three probes, and `clickaim-cal.txt` saved with
`degPerPx=-0.107086 degPerDragPx=-0.184707 degPerKeyMs=0.000000`. Treat the camera
model as solved for this client; do not re-derive it from single samples again.

The run failed later: leg 6 moved 171.52 yd in 25.00 s and the character kept running
20 s past the leg, taking the goal from 27.67 to 348.88 yd and costing ~15 legs of
walking back. Diagnosis: the client was left with the right mouse button down (a
dropped WM_RBUTTONUP); a held right button means "run toward the cursor".

v11 `Tools/WowBClickAim` EXE `c23b3f4306b9f150d2065f927c0d8355`: double button
release; PANIC STOP sequence; REST_TIMEOUT 8 s; runaway detection at leg start and
after a click (RUNAWAY_MOVE 40 yd); stop after 2 runaways or 3x goal distance;
heading never taken from a walk < 0.5 yd; CLICK_MAX_PX 300; drag probe re-anchor.

Next: `WowBClickAim.exe --load --walk 30 90` from open ground, and watch for
`PANIC STOP` / `RUNAWAY` lines. If they appear twice, the same spot needs
`--sendinput` (real cursor, no synthetic button state to lose).

## 2026-10-29 — CONVERGED (30 yd, 8 legs, 1.61 yd)

The movement tool is working end to end. Latest field run: verified click model
(-0.12674 deg/px, both holdouts predicted), drag -0.17871, `REACHED` at 1.61 yd in 8
legs, drift -1 %/+2 %, and the capture stop fired for the first time on a real
approach (1.79 yd, coasted 0.18 yd, correctly not learned from).

Convergence history: v5 6 legs/1.68 yd, v6 17 legs/1.63 yd, v10 8 legs/1.61 yd.

v12 `Tools/WowBClickAim`, EXE `d0e37339d97be08a1f1566539f850f25`:
* AIM_VERSION macro drives the banner (the previous build said v9 while being v10);
* `--load` prints the file it reads and pools ALL saved calibrations with a robust
  median, because the two verified runs measured -0.10709 and -0.12674 deg/px (18 %
  apart); the file is now append-only and reports the spread above 25 %;
* a single drag is capped at 45 deg of turn and the remainder is taken by the next
  leg (leg 4 asked for 420 px, ran 37.10 yd and ended further away);
* float precision of the saved file is 6 dp, key channel included.

State of play for movement: CTM is immediate-mode, so the deliverable is this
synthesiser, not an offset. Do not re-open the offset hunt for CTM. Remaining work
is environment handling (obstacles/monsters) and, if a long-range router is wanted,
a navmesh - whose blocker is the beta client's map-data layout, not any code offset.

## 2026-10-29 (v13) — movement is production-good

Two more field walks both converged: 60 yd in 5 legs (0.79 yd) and 30 yd in 14 legs
(0.86 yd, through an obstructed spot). Movement no longer needs offset work or
model work; what remains is environment (obstacles, monsters) and, optionally, a
navmesh whose blocker is the beta client's map-data layout.

v13 fixes, all derived from those two logs: runaway = rest timeout or a near hop
that travelled like a far click (not "any leg over 40 yd", which false-panicked on a
planned 99.35 yd click); the no-progress nudge stays short (-70/-90, not -200); one
`--load` message instead of two; converged runs append their learned model so
`--load` pools a population rather than trusting one line; plan offsets capped at
225 px because the fit steepens past it (0.86 vs 1.08 deg/px) and the ±300 holdouts
missed by 4-9 deg although "VERIFIED" was still printed; footer leg count fixed.
EXE `2c97345f3a85bce6d407ee0ca11d8e55`.

## 2026-10-29 (v14) — 40 yd in 2 legs

Three field runs: `100 0` calibrated then failed to converge (24 legs, 65.21 yd
left), `25 180` refused outright, `40 270` arrived in **2 legs at 0.77 yd**. The
40 yd run is the target behaviour: one click to find the camera, two legs to walk.

v14 fixes: rest wait 20 s plus a distance-based runaway (120 yd continuous travel)
instead of the 8 s timeout that rejected a legitimate 56.09 yd click; the camera
yaw is only re-measured from walks >= 3 yd (1 yd headings are noise and made the
planner circle with 45 deg drags); a third detour trigger on three no-gain legs;
turn legs plan the far click when the goal is >30 yd out so the post-turn walk can
re-measure the yaw. EXE `0147a62be6a9985796b0620080a257c1`.

Open: the 100 yd run's spot needs a real obstacle detour (it was walled in at
~40 yd). Detours exist but alternate fixed 55 deg; the next improvement is choosing
the side from where the character actually got stuck, not from an alternating
counter.

## 2026-10-29 (v15) — movement is good; profiles are a different problem

`--walk 100 0` covered 78.8 yd in 5 click legs before an obstruction stopped it,
which is the best long approach so far. Both 15:5x runs then degenerated into drag
legs, and the log shows why: a drag runs the character along its OLD facing while
the camera turns, so a turn leg's displacement heading is not a camera heading. The
walker used it as the next yaw, the estimate swung 172 deg, and the goal never
re-entered the click cone. v15 advances the yaw by the model on turn legs.
EXE `5a2bdc3761e94461808f1b9508fd59d0`.

Feasibility against a real CopilotBuddy profile (`Ashenvale (22-24).xml`): the
tool can serve hotspot-to-hotspot hops inside a cluster (6-97 yd, typically 27-60),
cannot do quest-to-quest travel (276-1485 yd), cannot do RunTo/transport/portal
legs, and ignores the Z in every hotspot (cluster Z spread 2.9-12.8 yd). Next
feature, if wanted, is a waypoint chain walker (`--route`/`--profile`) with a
per-waypoint leg budget and a Z sanity check - not more click tuning.

## 2026-10-29 (v16) — the movement is ported into the bot

`Styx/Logic/Pathing/ClickAim/`: `ClickAimMath` (measured camera model + 26-check
self-test), `ClickAimInput` (PostMessage mouse/key, double button-up, panic release),
`ClickAimCalibration` (pools `clickaim-cal.txt`, the same file the field tool writes),
`ClickAimMover` (an `IPlayerMover`, tick-driven, non-blocking) and `ClickAimPilot`
(blocking `WalkTo(x,y)` usable today, because the x64 bot loop still refuses to start).

Design decisions worth keeping: calibration is NOT repeated inside the bot (run the
tool once, share the file); turn legs advance the yaw by the model because a drag
runs the character along its old facing; hops are banded by distance (-60 under
15 yd, -100 under 40, -140 beyond) because a single goal never cared about
overshoot but a path waypoint every 2-4 yd does.

Not compiled here (no C# toolchain in the sandbox): `Tools/lint_cs_types.py` clean
only. First real check is `dotnet build -c Release -p:Platform=x64 .` on the user's
machine, then the in-app self-test line at startup.

## 2026-10-29 (v17) — the solution configuration was the first build error

```
CopilotBuddy.slnx.metaproj : error MSB4126: The specified solution
configuration "Release|x64" is invalid.
```

`CopilotBuddy.slnx` declared `<Platform Name="Any CPU|x86|x64" />` but no
configuration entries at all, so the solution only knew `Debug` and `-c Release`
died during solution load, before a single `.cs` file was compiled. In the `.slnx`
format `Debug`/`Release` are `<BuildType>` elements, not platforms; both are now
declared next to the platforms and the two `Solution="*|x86|x64"` mappings.

The recommended command does not touch the solution file at all, and is what
`Publish-Release.ps1` already used:

```
dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64
```

Nothing in the ClickAim port depends on this fix; it was publishing plumbing that
had been broken since the dual-platform csproj was added.

## 2026-10-29 (v18) — the x64 client had no player position for the mover

Their attach log (PID 18232, build 70291) is healthy: 30 validated units, 69 pool
slots, local GUID 0x0801…0006, record 0x11EBA8291C0, pos (-614.8, -4254.2, 39.0),
hp 53/53, objMgr 0x11F2B319EF0. What it did NOT contain is any `[ClickAim]` line -
the mover was never constructed, and that hid a real defect:

`ObjectManager.Update()` on a 64-bit target publishes a `WowBWorldSnapshot` and
returns before `Me` is ever assigned. `ClickAimMover.PlayerPosition` read
`StyxWoW.Me`, so on the only client it was ported for it would have answered "no
local player" forever - and the bot loop (which would have called it) is disabled
anyway on x64.

Fixed:
* `ClickAimPositions.TryRead`: legacy `ObjectManager.Me` first, else
  `WowBReader.TryGetPlayerObject` + `TryGetObjectTransform`, cached 40 ms. Also
  `Z()` and `Source` ("legacy" / "wowb").
* `ClickAimMover` uses that instead of `StyxWoW.Me`.
* `ClickAimDiagnostics.EnableAfterAttach()`: logs the 26-check self-test, the
  position source, and accepts walks from `--clickaim-walk x,y[,tol]` or from
  `Settings\clickaim-walk.txt` (consumed then deleted), 240 s budget, refuses a
  second walk while one runs, waits 30 s for a readable position.
* `UI/MainWindow.xaml.cs`: calls it in the x64 diagnostic branch, so the triggers
  work on the build that currently ships.

Not run: everything above is lint/text only (no C# compiler here). The transport
itself (PostMessage right-click) is the same code the field tool proved, but it has
never been driven from *inside* the bot process - that is the next acceptance step,
`--clickaim-walk <visible point 30-60 yd>`, expecting `[ClickAim] from … to …`,
then `arrived: … yd remaining, N legs`.

## 2026-10-29 (v19) — profile waypoint chains, and the mesh verdict

Asked for: "load a profile like ashenvale.xml and make the bot run to a point in the
world using the mesh". The profile half is now implemented; the mesh half is not
possible on this client, for two verifiable reasons:

* `Lib/Navigation.dll` PE machine is `0x014C` (i386) - the x64 bot cannot load it,
  so Tripper/Detour never initialises there.
* Its tiles are V5 `.mmtile` from 3.3.5a map data; no tile set exists for 70291 and
  3.3.5a tiles cannot be reused.

New:
* `ProfileRoute` - parses Hotspot/RunTo in document order, quest filter, cluster
  split (gap > 150 yd starts a new cluster), nearest-cluster pick, `Describe`.
  Measured on `Ashenvale (22-24).xml`: 50 waypoints, 20 clusters
  ([5,8,1,1,2,1,2,2,1,3,8,8,1,1,1,1,1,1,1,1]), 86,343 yd naive chain, longest leg
  17,705 yd (a RunTo transport hop).
* `RouteRunner` - per leg: `Navigator.GeneratePath` when a mesh is loaded and the
  polyline is followed point by point; otherwise the waypoint is walked directly,
  and legs > 100 yd are refused rather than attempted.
* `ClickAimDiagnostics` - `--clickaim-profile <xml>` (`--profile-quest`,
  `--profile-legs`, `--profile-nearest`, `--profile-all`), plus `.xml` / `profile:`
  in `clickaim-walk.txt`; profile jobs are a single job, not a point queue.
* `ClickAimPilot.WalkTo(mover, ...)` overload so a route reuses one mover and the
  calibration is read once; the self-test is logged once per process.

Not compiled (no C# toolchain here). Not run: the parser was mirrored in Python
against the real file to check counts/ordering, which is why the 20-cluster and
17,705 yd numbers above are measurements rather than guesses.

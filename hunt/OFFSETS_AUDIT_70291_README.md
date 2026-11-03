# Build 70291 offset audit — read-only Ghidra batch

This batch prepares leads for the remaining unit, reaction, login, targeting,
movement and loot offsets. It **does not discover or validate new offsets by
itself** and does not call game functions. CTM remains a first-class goal.

## Run on Windows

1. Download `WowBOffsetsAudit70291.java` into `C:\ghidra-scripts\`.
2. Open your existing **decoded build 70291 image** in Ghidra. Allow its analysis
   to finish. Do not use the encrypted on-disk EXE as if it were decoded.
3. Open **Window → Script Manager**. Add `C:\ghidra-scripts\` as a script
   directory if necessary; refresh and run `WowBOffsetsAudit70291.java`.
4. The script prints the output path and writes a timestamped
   `C:\ghidra-scripts\WowBOffsetsAudit70291-YYYYMMDD-HHMMSS-SSS.txt`.
   The decompiler and string searches can take several minutes. Send the log
   via the existing Drive handoff folder, even if it reports partial results or
   errors; include any compile error from Ghidra's console if no log appears.

The script makes no program edits and does not attach to, write to, or call the
client. Its file operations are limited to its timestamped output. Searches,
references, decompiler time and output are bounded per item; Ghidra's search
still scans initialized readable memory blocks and may be slow. The emitted
`UnitHealth` bytes are merely a sanity check: a match does **not** establish
that the binary has the correct build or that all code is decoded. If they
mismatch, treat all results cautiously and send the log rather than using them
as offsets. If Ghidra reports a containing function rather than an exact entry,
that distinction is printed; don't treat the requested RVA as a function start.

## What it examines

- Registration-table handlers: UnitHealth/Max, UnitPower/Max, UnitLevel,
  UnitReaction, UnitCanAttack, UnitIsEnemy, UnitIsDead, CanLootUnit,
  IsLoggedIn, GetRealZoneText, TargetUnit and StartAttack.
- Prior movement handler and crash-stack location; the latter may be a callsite.
- Bounded direct xrefs to pieces of the player/target GUID globals and object
  manager global. WRITE is Ghidra reference metadata, not proof of a setter.
- ASCII and UTF-16LE movement string substrings and exact xrefs; these are leads
  only. `enableClickToMove` returned nil in a prior game-side CVar query; do not
  infer a CVar exists from a string or its absence from no string hit.
- Each handler's decompiled C and bounded callee list, plus a capped number of
  data-xref functions. Indirect calls/references and not-yet-decrypted functions
  can be missing. Output truncation is identified in the log.

## Next analysis

Use the user's specified **rea + Gemini CLI (Flash 3.5 Lite) + Ghidra** toolchain
on the report and decoded image. Trace dataflow and structure base, read width,
guards and call hierarchy for unit health/power/reaction and login. Follow target
and CTM/input paths separately, looking for candidate setters, destination and
stop semantics. A Lua registration entry or GUID writer alone is not a native
action entry point. Do not execute discovered functions or change x64 gameplay
until ABI, thread, state and protections are independently understood.

Validate candidates with explicit user-approved read-only live comparisons,
including controlled value changes, 128-bit GUIDs and multi-object samples. For
CTM establish destination, stop, ABI, GUID width and thread restrictions before
implementation. No 3.3.5a constants are substituted for 70291.

## Added research constants

`Styx/Offsets/WowBOffsets.cs` adds `[S]` registration-derived Lua handler RVAs,
not operational gameplay functions. `python3 Tools/check_wowb_handler_rvas.py`
cross-checks 15 table entries against `hunt/api_handlers.tsv`; it checks
transcription only. Legacy 3.3.5a constants and behavior are untouched.

## Verification limitations

The Arena sandbox has no local Ghidra, Java, .NET SDK or decoded 70291 image.
Java and C# were syntax-parsed, and the documented Ghidra API was checked against
upstream source, but the script has **not been compiled or run in Ghidra**, the
project has not been built, and these leads have not been live-validated here.
The C# heuristic lint has one pre-existing false-positive-style comparison at
`GreenMagic/LuaBridge64.cs:1752` (`_base != 0`), not a build result.

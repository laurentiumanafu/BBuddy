# RESUME — CopilotBuddy x64 port (read this first)

Written 2026-10-13, immediately before a ≥15-day break. The sandbox workspace will be
gone; everything needed to continue is in the Google Drive folder below and in this
repository's text files.

**Drive folder (start here):** `BBuddy x64 port — handoff 2026-10-13`
<https://drive.google.com/drive/folders/1ggNucGNR_i7cLefwJi3EZqWqtzIOSr41>
It contains: the full workspace zip, every release zip (v0.5.0 … v0.8.2), the crash
reports, and these documents.

## What this project is

Porting **CopilotBuddy** (a C# WoW bot, legacy 3.3.5a) to the **x64 Classic Beta
client**: `wowb.exe`, build **70291** (1.60.1). The bot reads the client's memory and
drives it through a **Lua bridge** (`GreenMagic/LuaBridge64.cs`) plus a diagnostic
probe (`Tools/LuaBridgeProbe`). The user builds on Windows:

```bat
dotnet build -c Release -p:Platform=x64 .
```

## Where things stand (2026-10-13)

* **Phase 1 — attach + read: DONE.** Object manager, entities, globals, Lua ABI.
* **Phase 2 — offsets for 70291: in progress.** 430 constants in
  `Styx/Offsets/WowBOffsets.cs`; movement Lua-handler table complete; the biggest hole
  is the **unit-field/descriptor layer** and the **action functions** (CTM, target,
  cast) — full list in `X64_OFFSET_GAPS.md`.
* **Phase 3 — execution:** injected resident thread is proven (798 Lua calls/60 s:
  `GetTime`, `UnitName`, `GetMoney`, `RunScript`). **Dispatching** functions assert
  `FrameScript_IsLuaThread()` on a foreign thread, so they must be called on the
  client's **own Lua thread** — the bridge's *hijack* mode. The 2026-10-13 attempts
  took the thread over but crashed inside the payload's first instruction: the thread
  was parked in a kernel wait and Windows applied only CONTROL, not the integer
  registers (RBX/RCX → `mov [rbx+0xE0],0` wrote into a code page). **0.8.2 refuses
  that case** and verifies RIP+RSP+RCX. No Lua call has been executed over a hijack
  yet.

## First 30 minutes after the break

1. Read, in this order: `RESUME.md` (this) → `LUA_BRIDGE_64.md` (0.8.2 section: the
   crash and the fix) → `hunt/WOWB_DECODED_MODEL.md` (what was decoded, the movement
   handler table, the chat-probe verdicts) → `X64_OFFSET_GAPS.md` (what is missing) →
   `FUTURE_PLAN.md` (what to do).
2. Extract the newest release zip over the repository root, rebuild:
   `dotnet build -c Release -p:Platform=x64 .`
3. Run, with the client logged in at the character, in this order:
   ```bat
   LuaBridgeProbe.exe --check --on-lua-thread
   LuaBridgeProbe.exe --on-lua-thread --call GetTime
   LuaBridgeProbe.exe --on-lua-thread --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
   ```
   * Expect the first to be clean, the second to print a value, and the third to
     either move the character (the vector is proven, the taint gate does not apply to
     C-level calls) or to be **refused/blocked** (then the Lua surface stays closed and
     the engine-level route in `X64_OFFSET_GAPS.md` Finding 1/2 is the way).
   * A crash report on the Desktop after a failed takeover is *expected*; the client
     must be ended from Task Manager, never resumed.
4. If a run wedges: kill the client, start a fresh one, and continue (the thread is
   deliberately left parked with its pages allocated).

## Facts that save hours

* The client decrypts code pages on first execution; the same handler can be cold in
  the morning and warm after one use. The probe's gate reports which.
* A **hijack may only start on a moment whose RIP is inside the game module** (that is
  the 0.8.2 fix — a parked thread gets a partial context and the payload runs with
  stale registers).
* `--dump` interprets values **below the image base as module RVAs** (`rva:`/`va:`
  prefixes force either); chunks end on 4 KB boundaries; a refused page is retried in
  64-byte blocks.
* The chat `/run` path **cannot** execute protected functions (`FORCETAINT_STRONG` on
  `MoveForwardStart`, `ToggleAutoRun`), and `ClickToMove` does not exist in Lua. The
  only Lua-level movement is the bridge.
* Click-to-move **works in-game** and is the bot's legacy core mechanic
  (`WoWMovement.CallClickToMove` → `CGPlayer_C__ClickToMove`); the equivalent 70291
  function has not been found yet — recipe in `X64_OFFSET_GAPS.md`.
* Warm-up list (used, therefore decrypted, once `/run Name(...)` has run): `RunScript`
  `0x136FFF0`, `TargetUnit` `0x1B46C60`, `CastSpellByName` `0x2C24040`, `RunMacro`
  `0x2CC2DB0`. Movement handlers: `MoveForwardStart 0x23C8BE0`, `MoveForwardStop
  0x23C9000`, `JumpOrAscendStart 0x23C5560`, `ToggleRun 0x23C76C0`, `ToggleAutoRun
  0x23C7F50`, `StartAutoRun 0x23C83A0`, `StopAutoRun 0x23C87C0` (+21 in the probe).
* `python3 Tools/lint_cs_types.py` before packaging: a `bool` used as a counter is a
  build break on the user's machine (that was 0.8.0's CS0023/CS0019).
* Patch by offset with read-back verification; byte arrays use leading commas.

## Inventory (Drive file ids, parent folder `0AES61cfIsEk1Uk9PVA`)

| artefact | id |
|---|---|
| handoff folder | `1ggNucGNR_i7cLefwJi3EZqWqtzIOSr41` |
| bridge v0.8.2 zip (latest) | uploaded with the handoff |
| bridge v0.8.1 | `1Y2U8drgkZbwGJFZ5oGu8nNV64qmzBf_L` |
| bridge v0.8.0 | `1nPHFj0CV_fTb6H68-G0wotYWHRCRG-gt` |
| bridge v0.7.0 | `1s7BlmtJY5_o0lWKqApZcw0XaVOKw9cuq` |
| crash report 14952 (`--call GetTime`) | `1B8uicrSGJjZKNjuNWdBc3dqp6qDOf4MA` |
| crash report 14472 (`--seq MoveForward`) | `1GjYcZOiT9JIH3Aa0WfBMB05EBGsqcFOl` |
| crash report 16108 (FrameScript assert) | `18jtYcw9FUB44L4izILKVE-TckDvI0_7R` |
| decoded client image (offline RE) | `1cipD5-ylc1p_sdGxOzSirH57GbrHIURE` |
| packed client image | `1mpKbi54ieOW_7_9n9ksFKpH4JUzfKTol` |
| hunt45 v2 / hunt46 / hunt37 / hunt43 | `133PfyAeXaWFfwTNkbr3Lui-aHmpnoqvE` / `1AwpLBWDDmGvwWu11VutqboDeV9UQxR-R` / `166UECDEqp094TO151Td3Afv92uJ1INKM` / `1aY9Wx7k7hec9qvH5PPcxmw-c7jQdQMIc` |
| client video (live.rar) | `1mBLM2XVc9x3Y4jaC5mEetsdZOMCs9bMe` |

The workspace zip in the handoff folder contains the whole repository at
`arena/4f527248-bbuddy` HEAD `5b87623` (0.8.2), including `hunt/` scripts,
`Styx/Offsets/WowBOffsets.cs`, all documents and the two crash logs.

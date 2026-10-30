# RESUME — CopilotBuddy x64 port (read this first)

> **State as of 2026-10-30 — read this before anything below.** The Lua bridge is
> **retired**: both mechanisms crashed `wowb.exe` (70291) within a second of Start
> (`FrameScript_IsLuaThread` at `FrameScriptEvents.cpp:208`; then
> `"Lua stack is unbalanced"` at `Ui/GameUI.cpp:8250` plus a `0xC0000028` unwind fault).
> `GameLua.TryOpen` now refuses before touching the client. The grind loop reads the
> client's memory only (`Styx/WoWInternals/WowBReader.cs`) and fights by right-clicking
> the mob — the client's own auto-attack — with the outcome measured from the mob's
> health. The window drives everything: Load Profile → Grind → Start, plus an **Offsets**
> button that prints unit fields, the stored action bar and the raw word windows that the
> remaining offset work needs. Do not resurrect any of the Lua plans in the sections
> below; the measurements in `LUA_BRIDGE_64.md` are still good, the code path is not.

Written 2026-10-13, immediately before a ≥15-day break. The sandbox workspace that
produced this will be gone; everything needed to continue is in the Google Drive
folder below and inside the archives it lists.

**Drive folder — the handoff:**
`BBuddy x64 port — handoff 2026-10-13 (resume in 15 days)`
<https://drive.google.com/drive/folders/1ggNucGNR_i7cLefwJi3EZqWqtzIOSr41>

---

## If you are a NEW agent starting cold — do these five things

1. **Read, in this order:** `RESUME.md` (this) → `LUA_BRIDGE_64.md` (bridge design +
   changelog; the **0.8.2** section is the crash post-mortem you must know) →
   `hunt/WOWB_DECODED_MODEL.md` (the reverse-engineering record: what was decoded, the
   movement handler RVAs, the FrameScript assert, the chat-probe verdicts, the CTM
   legacy table) → `X64_OFFSET_GAPS.md` (what offsets are still missing and how to find
   each) → `FUTURE_PLAN.md` (the six milestones and the standing rules).
2. **Get the code.** `bbuddy-project-part1-sources-docs.zip` is the whole working
   project (977 files: all sources, docs, `hunt/` scripts, offsets, crash logs). The
   `bbuddy-history-<date>.bundle` file is the complete git history — `git clone
   bbuddy-history-2026-10-13.bundle BBuddy` restores the repository with its branch and
   commits. `part2` holds `datadb/` + `Lib/` (38 MB, needed to build), `output.zip` is
   a 2026-09-17 export of the tree kept for reference.
   *Extract part1 over a copy of the user's local checkout, or clone the bundle.*
3. **Rebuild on the user's Windows machine** (there is no .NET in the sandbox):
   `dotnet build -c Release -p:Platform=x64 .` — the user runs this; it is the only
   real compile check.
4. **Continue with the vector test** (see "First 30 minutes" below). It is the single
   gate everything else waits on.
5. **Keep the delivery habits:** large artefacts (scripts, logs, zips) go to the Drive
   folder and the link is posted in chat; the user's machine keeps scripts and logs in
   `C:\ghidra-scripts\`; do not ask blocking questions unless truly unavoidable — the
   user prefers visible autonomous progress.

### OUTSTANDING questions asked 2026-10-13 (not answered yet)

* In-game: is **Click to Move** enabled, and does **left-click on the ground** walk the
  character to the point (and does right-click too)?
* `/run print(type(ClickToMove), type(ToggleAutoRun))` and `/run ToggleAutoRun()` — does
  autorun start, or is it blocked with a `FORCETAINT` box?
* The three probe commands below (0.8.2) — outputs pasted back.

---

## What this project is

Porting **CopilotBuddy** (a C# WoW bot, legacy 3.3.5a codebase) to the **x64 Classic
Beta client**: `wowb.exe`, build **70291** (1.60.1), native x64. The bot reads client
memory and drives the client through a **Lua bridge**
(`GreenMagic/LuaBridge64.cs`) with a diagnostic probe (`Tools/LuaBridgeProbe`).
Branch in the sandbox: `arena/4f527248-bbuddy`, HEAD `a5c2ee0` (0.8.2 + handoff docs).
The session whose PR was closed **cannot push to GitHub** — use the bundle/archives
instead; a *new* session can create its own branch and PR normally.

## Where things stand (2026-10-13)

* **Phase 1 — attach + read: DONE.** Object manager, entity enumeration, globals, Lua
  ABI, region/zone, camera.
* **Phase 2 — offsets for 70291: in progress.** 430 constants in
  `Styx/Offsets/WowBOffsets.cs`; the movement Lua-handler table is complete. The two
  big holes: the **unit-field/descriptor layer** (the bot still reads through the
  3.3.5a flat-descriptor model — see `X64_OFFSET_GAPS.md` Finding 0) and the
  **action functions** (CTM, set-target, cast, cooldown, LOS: all `= 0` placeholders).
* **Phase 3 — execution: the live frontier.** The injected resident thread is proven
  (798 Lua calls / 60 s: `GetTime`, `UnitName`, `GetMoney`, `RunScript`). *Dispatching*
  functions assert `FrameScript_IsLuaThread()` on a foreign thread, so they must be
  called on the client's **own Lua thread** — the bridge's **hijack** mode. The
  2026-10-13 attempts took the thread over but crashed **inside the payload's first
  instruction**: the thread was parked in a kernel wait and Windows applied only
  CONTROL (RIP/RSP), not the integer registers — so the payload ran with the client's
  stale `RCX` and `mov [rbx+0xE0],0` (its call counter) wrote into a client code page.
  **0.8.2 refuses that case** and verifies RIP+RSP+RCX. **No Lua call has been executed
  over a hijack yet** — that is the next thing to prove.

## First 30 minutes after the break

1. Extract `bbuddy-luabridge-x64-v082.zip` over the repo root and rebuild (step 3 of the
   new-agent list).
2. With the client logged in on a character, in this order:
   ```bat
   LuaBridgeProbe.exe --check --on-lua-thread
   LuaBridgeProbe.exe --on-lua-thread --call GetTime
   LuaBridgeProbe.exe --on-lua-thread --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
   ```
   * The second is the first *honest* test of the vector: takeover → payload runs on the
     Lua thread → a Lua call returns a value.
   * If the third moves the character, the script taint gate does not apply to C-level
     calls and the whole Lua surface (targeting, casting, questing, looting) opens up.
     If it is refused, use the engine-level route in `X64_OFFSET_GAPS.md` Findings 1–2.
   * A crash report on the Desktop after a failed takeover is *expected and wanted*;
     end the client from Task Manager, **never resume** a hijacked thread.
3. Optional (2 minutes, very useful): the fixed read-only dump of the decrypted
   movement chain —
   ```bat
   LuaBridgeProbe.exe --dump "rva:0x23C5500:0x300,rva:0x23C8BE0:0x600,rva:0x23DBE00:0x400,rva:0x23DD400:0x400,rva:0x1DA4400:0x600" --out movchain.bin
   ```
   Upload `movchain.bin` + `movchain.bin.txt` to the handoff folder for disassembly.

## Facts that save hours

* The client decrypts code pages on first execution; the same handler can be cold in the
  morning and warm after one use. The probe's gate reports which (`--force` overrides).
* **A hijack may only start on a moment whose RIP is inside the game module** (the 0.8.2
  fix: a parked thread gets a partial context and the payload runs with stale registers).
* `--dump` reads values **below the image base as module RVAs** (`rva:`/`va:` prefixes
  force either reading); chunks end on 4 KB boundaries; a refused page is retried in
  64-byte blocks.
* Chat `/run` **cannot** execute protected functions (`FORCETAINT_STRONG` on
  `MoveForwardStart` / `ToggleAutoRun`) and `ClickToMove` does not exist in Lua — but it
  *does* warm (decrypt) the handler, which is why the movement functions read WARM now.
* Click-to-move **works in-game** and is the bot's legacy core mechanic
  (`WoWMovement.CallClickToMove` → `CGPlayer_C__ClickToMove`); it also opens quest
  giver / vendor / loot / corpse windows through the same call with `NpcInteract` +
  guid. The 70291 equivalent has not been found yet — recipe in `X64_OFFSET_GAPS.md`.
* Warm-up list: `RunScript 0x136FFF0`, `TargetUnit 0x1B46C60`, `CastSpellByName
  0x2C24040`, `RunMacro 0x2CC2DB0`. Movement handlers: `MoveForwardStart 0x23C8BE0`,
  `MoveForwardStop 0x23C9000`, `JumpOrAscendStart 0x23C5560`, `ToggleRun 0x23C76C0`,
  `ToggleAutoRun 0x23C7F50`, `StartAutoRun 0x23C83A0`, `StopAutoRun 0x23C87C0`
  (+21 more in the probe's table). These are Lua **handler RVAs**, not call addresses
  for the injected thread.
* Run `python3 Tools/lint_cs_types.py` before packaging any `.cs` change (a `bool` used
  as a counter was a build break on the user's machine — 0.8.0's CS0023/CS0019).
* Patch by offset with read-back verification; byte arrays use leading commas; after a
  hijack timeout the client is left parked on purpose (kill from Task Manager).

## Round-trip rules the user asked for

* Large scripts/artifacts → Google Drive, link in chat (pasting long text truncates).
* Scripts and logs on the user's machine live in **`C:\ghidra-scripts\`**.
* Toolchain for offset hunting is fixed: **rea + Gemini CLI (flash 3.5 lite) + Ghidra**.
* Click-to-move is a **core mechanic**, not a fallback.
* The user builds and runs every probe; the sandbox has no .NET. Keep freezes short.

## Inventory — what is in the handoff folder

`00-START-HERE.txt` (in the folder) is the live index with file ids and checksums.
Archive layout:

| archive | content |
|---|---|
| `bbuddy-project-part1-sources-docs.zip` | the entire working project: all sources, docs, `hunt/` scripts, offsets, crash logs (977 files) |
| `bbuddy-history-2026-10-13.bundle` | complete git history + branch `arena/4f527248-bbuddy` (`git clone` the bundle) |
| `bbuddy-project-part2-data-and-libs.zip` | `datadb/` (spawns, item loot, spells…) + `Lib/` (SlimDX) — needed to build |
| `output.zip` | 2026-09-17 export of the whole tree (3275 entries), kept for reference |
| `bbuddy-luabridge-x64-v082.zip` | **the build to extract before the next run** (md5 `d2bfa7c83fd998ea4925375c98c3cd28`) |
| `bbuddy-releases-and-logs-2026-10-13.zip` | all release zips v0.7.0→v0.8.2 + every crash report and hunt log |
| docs | `RESUME.md`, `X64_OFFSET_GAPS.md`, `FUTURE_PLAN.md`, `LUA_BRIDGE_64.md`, `X64_PORT_PLAN.md`, `WOWB_DECODED_MODEL.md` |

Older, still-valid Drive ids (parent folder `0AES61cfIsEk1Uk9PVA`):
bridge v0.8.1 `1Y2U8drgkZbwGJFZ5oGu8nNV64qmzBf_L`; v0.8.0
`1nPHFj0CV_fTb6H68-G0wotYWHRCRG-gt`; v0.7.0 `1s7BlmtJY5_o0lWKqApZcw0XaVOKw9cuq`;
crash 14952 `1B8uicrSGJjZKNjuNWdBc3dqp6qDOf4MA`; crash 14472
`1GjYcZOiT9JIH3Aa0WfBMB05EBGsqcFOl`; crash 16108
`18jtYcw9FUB44L4izILKVE-TckDvI0_7R`; decoded client image
`1cipD5-ylc1p_sdGxOzSirH57GbrHIURE`; packed image
`1mpKbi54ieOW_7_9n9ksFKpH4JUzfKTol`; hunt45 v2
`133PfyAeXaWFfwTNkbr3Lui-aHmpnoqvE`; hunt46 `1AwpLBWDDmGvwWu11VutqboDeV9UQxR-R`;
client video `1mBLM2XVc9x3Y4jaC5mEetsdZOMCs9bMe`.


## 2026-10-30, after the offsets run — combat presses the character's own spells

The 15:57:29 Offsets block pinned health/max-health/level/power/unit-flags and the
action-slot packing (see `CLICKAIM_PORT.md`, "the offsets arrived"). What changed in
the tree:

* `Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs` (new) — the stored action bar
  decoded (`id = low32`, item bit in the high dword), the slot→key map, and the
  spell table with the Singular priorities.
* `GrindRunner.Fight` — Singular's lowbie druid rotation (heal at 60%, auto-attack,
  Wrath on the cooldown, melee fallback), every step verified against mana and the
  mob's health rather than assumed.
* `GrindRunner.TryClickAttack` / `TryEngage` — corpse filter, level band
  (`player-4 … player+3`), `UnitFlags & 0x800` as a preference, and "killed or fled"
  counted as a kill.
* `WowBReader.TryGetUnitFields` — max health/power read from their own dwords
  (`+0x1E38`/`+0x1BD0`); `DumpWords` no longer rejects 4-byte-aligned units.
* `ClickAimDiagnostics.OffsetsCore` — dumps the unit object at `+0x1E00`/`+0x1B80`,
  the data block, and the decoded bar.


## 2026-10-30, 16:06 run — casting works; the loop now fights instead of walking past

`Wrath does damage: mob health 102 -> 86 (mana 115 -> 90)` is the first confirmed
spell cast through the memory action bar. What the run exposed and what changed:

* `RouteRunner.ShouldInterrupt` / `LastRunInterrupted` — a walk is abandoned as soon as
  something fightable is within 18 yd, with forward released first.
* `ClickAimKeySteer.Face()` — turn in place toward a point with the calibrated arrows;
  used before TAB (it only sees what is in front) and before every right-click/cast.
* `DwellAtHotspot` — 9 s of looking for a fight on arrival, instead of leaving at once.
* `Fight` — polls mana/mob health for up to 3.2 s per cast and my health for 3.6 s per
  heal instead of reading once; melee fallback only if the key has never cast; death
  is detected (`PlayerDead`) instead of pressing keys at a corpse.
* `UnitFlags & 0x800` is *not* reaction (the player carries it too, `uflags=0x808`) and
  is no longer used for targeting; hostility stays with TAB.


## 2026-10-30, 16:42 run — facing, distance, and auto-attack

* `ClickAimKeySteer.Face()` rewritten: measures its own tap rate, releases both arrow
  keys around every tap, and swaps the turn keys once if a tap goes the wrong way. This
  is the "turned its back on the monster" bug.
* `Face()` is now a gate before every cast keypress and before every melee right-click,
  so a refused cast no longer costs 3.2 s of "changed nothing".
* `GiveUpYd = 45` replaces `ChaseYd` as the give-up distance (18 instant give-ups in 20 s
  at 21-39 yd in that run); `ChaseYd` is only the melee chase limit now.
* cast-first rotation: approach to 28 yd, stop, face, Wrath; the right-click melee path
  runs only when casting is unavailable, so the character no longer walks into melee
  while it has mana.


## 2026-10-30, 17:22 run — it attacked a friendly NPC in town

* `TryClickAttack` is deleted: a right-click cannot distinguish a mob from a guard and it
  clicked a townsman 3.6 yd away. TAB (the client's own enemy filter) is now the only way a
  target is chosen; nothing selected means nothing attackable, and the walk continues.
* `UnitFlags & 0x800` is settled as IN COMBAT (player 0x80808/0x808 fighting, 0x8 standing in
  town). The walk interrupt needs it - on the player, or on a unit within 18 yd - instead of
  "a level-appropriate unit is nearby", which every NPC in a town satisfies.
* a target nothing could touch is added to `_notViable` for the session.


## 2026-10-30, 17:26 run — first kill, then a 40 s freeze

* `Wrath 71 -> 56`, auto-attack `56 -> 41 -> 26 -> 11`, `Wrath 11 -> 0`: the rotation works.
* bug 1: the cast branch fell through to melee when the global cooldown had not elapsed, so
  the tick after a landed Wrath ended the fight with "cannot cast at it and it is 22 yd
  away" at 139 mana. A viable cast now always continues (approach / cast / wait).
* bug 2: a refused (given-up) target was also refused while it was eating the character,
  so interrupt -> refuse -> walk -> interrupt spun 40x/s with the character rooted. A refused
  target is now fought when the player is in combat and the unit carries the in-combat bit,
  and a fruitless interrupt silences the next for `InterruptRetryMs` (4 s).


## 2026-10-30, 17:36 run — kills, then three fights against the corpse

* TAB cycles past `health == 0` units; when nothing live is offered, ESC clears the body and
  the guid is banned for the session (`the only thing the client offered was a corpse`).
* `Fight` returns immediately if the target is already at 0 health - the in-loop death check
  needs a health reading that was once above zero.
* no `Steer` within 2 yd: the bearing flips 180 deg there, which is what produced 25 s of
  arrow taps at a body. Inside 2 yd it faces with a 60 deg tolerance and swings.
* `right-click auto-attack` counts only when health actually moved, so the summary and the
  "nothing landed -> banned" rule are truthful.

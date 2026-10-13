# The plan from here (written 2026-10-13, for the resume after the break)

State at the time of writing: the bridge **attaches, reads, and writes**; the
injected-resident-thread mode is proven at 798 calls/60 s; the **hijack** (calling
dispatching functions from the client's own Lua thread) takes the thread over cleanly
but has not yet executed a single Lua call — the two 2026-10-13 attempts crashed the
client *inside the payload's first instruction*, because the takeover happened on a
thread parked in a kernel wait and Windows applied only CONTROL, not the integer
registers (full post-mortem: `hunt/WOWB_DECODED_MODEL.md`, `LUA_BRIDGE_64.md` 0.8.2).
0.8.2 refuses that case; the next run is the first honest test of the vector.

---

## Milestone 0 — the vector (immediate, 0.8.2)

```bat
LuaBridgeProbe.exe --check --on-lua-thread                     :: expect: clean, tids listed
LuaBridgeProbe.exe --on-lua-thread --call GetTime              :: expect: takeover + value
LuaBridgeProbe.exe --on-lua-thread --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
```

* `GetTime` proves: takeover → payload runs on the Lua thread → Lua call returns.
* `MoveForwardStart` answers the **one open question**: does a *C-level* call from the
  Lua thread bypass the script taint gate that blocks `FORCETAINT_STRONG` on the chat
  `/run` path? Yes → the whole Lua surface is open. No → route 2 (native calls).
* Wedge policy unchanged: on a timeout the client is left parked; end it from Task
  Manager; a crash report on the Desktop is expected and wanted.
* If the takeover now reports *no* moment with RIP inside the module, the client is
  spending all its time in kernel waits — then raise `HijackWindowMs` (property) or
  take over on the *command thread* the `--check` table shows as busiest-in-module.

## Milestone 1 — the native-call stage (code only, no RE)

Add to the 641-byte payload a stage that calls an arbitrary address with
`RCX/RDX/R8/R9 + XMM0..3 + shadow space`, writes back `RAX` and the control block
status. Storage already exists (`ctrl + arg area`, `name` slot). This is what the
legacy `Executor` did with `push/call` on x86 and it unblocks **every** engine-level
action below — movement, CTM, target, cast, loot, quest turn-in — **without the Lua
thread**, therefore without the FrameScript assert and without taint.

Acceptance: call a pure function with a known result (e.g. `GetTime`-equivalent
engine function) and read the return value back through the ctrl block.

## Milestone 2 — click-to-move (movement *and* all interactions)

1. In-game: enable Click to Move, click the ground 20 yd ahead, confirm the walk.
2. Dump the region around the known movement handlers and the CTM struct candidates
   (`--dump rva:0x23C5500:0x300,rva:0x23C8BE0:0x600,… --out movchain.bin`) — the pages
   are decrypted in a session where those handlers have been used.
3. Find `CGPlayer_C__ClickToMove` (legacy `0x727400`) and its struct (legacy
   `0xCA11D8`: type +0, turn speed +4, dest +0x8C/+0x90/+0x94, guid +0x20,
   action +0x1C) — recipe in `X64_OFFSET_GAPS.md` Finding 1.
4. Call it from the native-call stage: `ClickToMove(type=Move, guid=0, x, y, z)`.
   Acceptance: the character walks to the point; `MoveStop`/`ClickToMoveStop` stops it.
5. Interactions (quest giver, vendor, loot, corpse, mailbox) ride the same call with
   `type = NpcInteract` + guid — see the legacy `ClickToMoveType` enum in
   `Styx/WoWInternals/WoWMovement.cs`.

## Milestone 3 — combat plumbing

`CGGameUITarget` (set target), auto-attack start, `SpellCCastSpell` + cooldown/GCD
(either the inner calls of the warm Lua handlers `TargetUnit` / `StartAttack` /
`CastSpellByName`, or the spellbook record). Acceptance: target an NPC, start auto
attack, cast a spell, read the cooldown back.

## Milestone 4 — the `UnitFields` mapping (the decision layer)

`hunt49_unitfields`: map the ~20 fields the core needs from the 3.3.5a descriptor
enum to the 70291 actor offsets (reaction/hostility is the important missing one).
Until this exists the bot can *act* but cannot *decide*. Acceptance: the bot can tell
a hostile mob from a friendly NPC and read its level.

## Milestone 5 — bot integration

`LuaBridge64` becomes the executor behind `Styx/WoWInternals/WowBReader`, replacing
`ObjectManager.Executor`'s x86 JIT with (a) the hijack for Lua-level calls and (b) the
native-call stage for engine calls. First real test: a bot profile that targets a mob,
moves to it with CTM, attacks it, loots it, then accepts and turns in a quest.

## Milestone 6 — the overlay

`DX11PresentHook64` (in-process) — needs the swap-chain pointer
(`WowBOffsets.SwapChainPtr = 0`, string xrefs exist) and a trampoline that survives the
present call. Cosmetic; do it last.

---

## Standing rules (do not relearn these the hard way)

1. **Never hijack a moment with RIP outside the game module** — the context is applied
   only in part there (this was the crash). The bridge refuses it now.
2. Run `python3 Tools/lint_cs_types.py` before packaging any `.cs` change; the user's
   `dotnet build -c Release -p:Platform=x64 .` is the real acceptance test.
3. Patch by offset, never by a byte pattern without read-back verification; leading
   commas in byte arrays.
4. After a hijack timeout: never resume/restore; kill the client from Task Manager.
5. Hunt scripts and their logs go to `C:\ghidra-scripts\` on the user's machine.
6. Large artifacts go through Google Drive; the sandbox workspace is not permanent.
7. The `.cs` sources and the release zips are the only canonical code; a zip extracts
   over the repository root.

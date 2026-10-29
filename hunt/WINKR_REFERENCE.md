# Winkr-Public — what it gives us, what it does not

Source: <https://github.com/laurentiumanafu/Winkr-Public> (public mirror of BA3LY7H),
read 2026-10-29. Docs used: `Documents/Movement.md`, `Documents/PatchDay.md`,
`Documents/LiveMesh.md`.

## Scope mismatch first

Winkr targets **WoW Classic Era 1.15.9 (build 68940) and 1.15.10 (build 69109)**,
client `WowClassic.exe`. Our target is **wowb.exe build 70291 (1.60.1 beta)**.
The repo contains no mention of 70291, `wowb.exe`, or 1.60 — verified by grepping
a sparse clone of `Documents/`, `tools/`, `README.md`.

So: **no offset transfers directly.** What transfers is *method and structure*, and
two of its findings change what we should be doing.

## Finding 1 — why our movement (and Lua calls) are "locked"

Every movement keybind handler starts with one inline gate:

```
MoveForwardStart @ 0x015D8A00 (68940)
  015D8A1F  cmp qword [rip -> 0x03BDDDB0], 0   ; g_taint_handler_ptr — the ONLY gate
  015D8A2C  je  015D8A37                       ; zero -> do the real work
  015D8A30  call 0x01AD1FA0                    ; non-zero -> blocked-action reporter, bail
```

Confirmed on MoveBackwardStart, StrafeLeft/RightStart, TurnLeft/RightStart,
PitchUp/DownStart, StartAutoRun, StopAutoRun, TurnOrActionStart,
CameraOrSelectOrMoveStart, MoveAndSteerStart. Absent on unprotected APIs
(UnitHealth, GetTime, GetBindingKey...).

Critically: `0x03BDDDB0` is the **same** global the Lua secret gates use. That is
exactly consistent with what our client reported in chat: `*** FORCETAINT_STRONG ***
... only available to the Blizzard UI`. One global, two symptoms.

Consequence for 70291: the gate is findable **read-only**, without injecting or
calling anything — scan the prologue of `MoveForwardStart` (0x23C8BE0) for
`48 83 3D <rel32> 00` (`cmp qword [rip+rel], 0`), follow the rel32 to the global,
and confirm it is the same address the secret gates reference. Documented fix is
to zero that global around a call (`winkr_Call`), which is how every mainstream
unlocker does it. **Not pursued here** per the standing decision on
MoveForwardStart/Stop — recorded because it also explains casting and targeting
refusals, not only movement.

## Finding 2 — click-to-move is not a feature with a destination

> **ClickToMove does not exist on this build.** "ClickToMove", "clickToMove",
> "CLICK_TO_MOVE", "TerrainClick", "NavMesh", "Detour" are all absent (ASCII and
> UTF-16). A sweep of all 524 CVar registration sites found no CTM or pathfinding
> CVar. Only related facility: CVar `autoInteract`.

And the right-click handlers are `CameraOrSelectOrMoveStart` (0x15DDA40) and
`MoveAndSteerStart` (0x15DE880) — camera/steer handlers, not a "move to point"
API. That matches the measurement in `WowBCtmPathWatch` where `+0x7D0` started
moving **78 ms before** the button went down, because the cursor moved onto the
spot: the value tracks the cursor.

**Reassessment:** the most likely truth is that right-click sends a *direction
derived from the cursor ray each frame*, and no absolute destination triple is
stored. That would explain the v1 hunt finding 0 candidates in 128 KiB. It is a
hypothesis, not a conclusion — v2 tests it directly with a cursor sweep.

## Finding 3 — the engine movement primitive (no keybind, no gate)

```
#define RVA_INPUT_CONTROL          0x03A05BA0  // 68940; 0x03ABEB20 on 69109
#define OFF_INPUTCTL_FLAGS         0x04        // live movement flags dword
#define OFF_INPUTCTL_GATE          0xA8
#define OFF_INPUTCTL_MOVE_ENABLED  0xBC
#define RVA_INPUTCTL_SET_BIT       0x015E8080  // (this, mask, ...) -> changed?
#define RVA_INPUTCTL_CLEAR_BIT     0x015E8A90
#define RVA_INPUTCTL_APPLY         0x015E78A0  // (this, eventTime, 1, 1) -> emit movement
#define RVA_INPUT_EVENT_TIME       0x03C3CC68
```

Local flag bits: forward 0x10, backward 0x20, strafe L/R 0x40/0x80, turn L/R
0x100/0x200, pitch 0x400/0x800, autorun 0x1000, ascend 0x2000, descend 0x4000.
Independent confirmation that the flags dword is real: Lua `IsPlayerMoving` is
nothing but `mov eax, dword [0x03A05BA4]; test ...`.

These functions carry **no taint check**, so this is a movement path that does not
need `g_taint`. `RVA_INPUTCTL_APPLY` is what makes the client emit the movement
packet — writing the flags dword alone gets rubber-banded on the next position sync.

Anchor to find it on 70291: the `lea rcx, [rip + X]` inside MoveForwardStart
resolves to the CInputControl singleton; `IsPlayerMoving` reading `+0x04` of it is
the cross-check. Read-only derivation.

## Finding 4 — external input works (the zero-RE CTM)

> **PostMessage works on Classic** (fallback #3, zero RE): WowClassicGrindBot is
> fully external and drives 1.15 with `PostMessage(WM_KEYDOWN/WM_KEYUP)` using
> layout-translated VK codes and hand-built lParam — background-capable, no focus
> required.

We already proved the game reacts to real right-clicks (44 yd and 61 yd walks).
Combined: a synthesized right-click at the right screen point IS click-to-move,
with no memory offset at all. The missing piece is world→screen projection:
camera eye, fov and view-projection matrix, which Winkr's patch-day confirm step
also treats as struct offsets with no signature.

## Finding 5 — patch-day procedure (reusable directly)

* The **on-disk** `WowClassic.exe` is **encrypted** (entropy 7.96, zero x64
  prologues); the client decrypts in memory, so **every build bump needs a runtime
  dump**. Matches our workflow: we already analyse a decoded dump, never the
  shipped file.
* `tools/patch_day.py` buckets defines into STALE (signature moved, paste-ready),
  RIPREL MISMATCH (take if the vote count is healthy), BROKEN (hand-derive) and
  UNVERIFIED (no signature; derive by anchored disassembly).
* **Region shifts are not uniform** — on 69109 the Lua region moved +0xB9C50 while
  others moved +0xA6960 / +0xABA20. Never extrapolate one delta. This is a direct
  warning against how we guessed some of our earlier offsets.
* Two obfuscated Eidolon validators (`RETADDR_CHECK`, `VALIDATE_CFUNC_PTR`) have
  no signature; if stale, the first Lua C-API call NULL-derefs and kills the
  client. 69109: `0x001D0580 -> 0x001D0590` (3 callers agree, fp 79%) and
  `0x0030EB70 -> 0x0030EB50` (fp 81%). Always confirm the entry byte is `0x48`.
* Navmesh is build-independent (26,071 baked `.dnm` tiles); all Lua is
  build-independent. Only compiled-in addresses move.

## What we can actually reuse, in order of value

1. **The cursor sweep test** (implemented in FieldHunter v2) — settles whether a
   destination exists or the move point is cursor-derived.
2. **Read-only derivation of `g_taint` + CInputControl on 70291**, anchored in
   MoveForwardStart's prologue. Gives the taint global (explains every
   FORCETAINT/refusal) and the input-control singleton with its flags — the
   movement state read our snapshot code lacks.
3. **World→screen projection + synthesized right-click** as the CTM implementation
   that needs no CTM offsets at all.
4. **`patch_day.py` bucketing + validator derivation** for the next build bump,
   instead of re-deriving by hand.
5. **Detour/mmaps navigation** — largest scope; note that their mmaps are a WotLK
   extract, so Classic-only geometry is missing, which is why they added LiveMesh
   (runtime tile bake by raycasting the client's own collision).

## What we should NOT take from it

* Their assumption that CTM exists as an addressable feature (they explicitly
  concluded it does not) — do not spend more runs looking for a named CTM API.
* Navmesh/mmaps integration now: 2.7 GB of tiles and a vendored Detour fork, for a
  bot whose movement primitive is not settled yet.
* Any hardcoded RVA from their headers: different client, different build.

## Reading the code, after the docs

### `HANDLE_TERRAIN_CLICK` is a spell finaliser, not movement

`Winkr/dll/bridge/dinkr_bridge_actions.c`:

```c
/* a1 = GUID128* (null for terrain), a2 = Vec3*, a3 = flag, a4 = cast context ptr */
typedef char (__fastcall *HandleTerrainClick_t)(void* targetGUID, float* position, char flag, void* castContext);
```

Its own comment: "Checks (targetingFlags & 0x60) and targetingGUID, writes position
to spell object, clears targeting flags, and finalizes the pending AOE cast."
So it completes a **ground-targeted spell**. It does not walk the character.
Exposed to Lua as `dinkr_ClickPosition(x,y,z)`.

The call site also documents that its RVA had been stale for three builds and the
signature has never matched since — they refuse to call it without a resolved
signature. Good discipline to copy: never call through an unresolved address.

### Their working walker uses facing + MoveForward, not a move-to call

`dinkr_FaceDirection` writes the ECS entity-position block's facing field
(`pos_base+0x50`, not the retail-era `cgobj+0x38C`) and notes: "The client reads
this field when composing movement packets, so writing it and then holding
MoveForward moves in the new direction." Their measure doc says writing the wrong
field was one of two independent reasons it silently never turned.

There is **no `MoveTo` implementation** in the repo — the only occurrence is a
section comment listing `FaceDirection, ClickPosition, MoveTo`. So the product
that solves this problem for 1.15.x also concluded that movement is
"write facing, hold forward", with taint cleared for the keybind path.

### Injection

`Winkr/gui/injection/gui_injection.cpp`: reflective DLL injection — manual PE map
(RvaToFileOffset, sections, imports) plus **thread hijack** via
`NtOpenThread/NtSuspendThread/NtGetContextThread/NtSetContextThread`. Not
`LoadLibrary`, not `CreateRemoteThread`. Same family of technique as our APC stub,
so it is compatible with our finding that the loader refuses injected DLLs.

### Builds, gating, and what a port actually costs

* `EXPECTED_BUILD 69547`; `RVA_SET_IS_VERIFIED`/`RVA_G_TAINT_IS_VERIFIED` all
  compare against it, so a mismatched client puts the DLL in **SAFE MODE**: it can
  attach and dump, but performs no taint writes and executes no mods.
* Therefore "it will also work for our build" is half true: it will **attach and
  run safely**, but every movement/Lua feature stays disabled until the RVA set is
  re-derived. Their own patch-day notes call that a ~15 minute job *with tooling
  for a client they already know*, and warn that region shifts are non-uniform.
* We skip the hardest patch-day step: they need a **runtime dump** because the
  on-disk exe is encrypted; we already have a decoded image, so the derivation can
  run on that directly.
* Lua is vendored **5.1**, single-compilation-unit, operating on WoW's own
  `lua_State` relying on identical struct sizes (TValue 24, CallInfo 56, taint
  fields). That is only valid against a build whose layouts match; our client has
  taint gates too, so it is plausible but must be verified per build.

## Signature results on build 70291 (WowBSigScan, live image)

4 of 19 patterns matched, all unique, all in `.text` preceded by `0xCC` padding:

| Pattern | RVA on 70291 | First bytes |
|---|---|---|
| `LUAL_LOADBUFFER` | `0x048A1A50` | `40 53 48 83 EC 30 49 83 F8 03 72 19` |
| `HANDLE_TERRAIN_CLICK` | `0x025D6670` | `48 89 5C 24 10 48 89 6C 24 18` |
| `LUAH_NEW` | `0x0472A3D0` | `48 89 5C 24 08 48 89 6C 24 10` |
| `WOWGUID_TO_STRING` | `0x047B96A0` | `48 89 5C 24 18 48 89 6C 24 20 56 48` |

NOT FOUND, and therefore **not** usable as anchors: `WORLD_INTERSECT`,
`WORLDFRAME_PROJECT_POINT`, `SPELL_C_CAST_GUID_DIRECT`,
`SPELL_STOP_TARGETING`, `LUA_PCALL`, `LUA_NEWSTATE`, `INTERACT_UNIT`,
`LUA_PUSHBOOLEAN`, and the six secret-gate/Eidolon patterns.

`.text` reported 57 MB unreadable of 76 MB, so a NOT FOUND there is weaker
evidence than it looks; `WowBSigScan v3` now prints a protection histogram per
section so the difference between "not present" and "not scannable" is visible.
The four matches all came from readable code, so the scanner itself works.

Useful negative: the Lua *allocation* primitives match (`luaL_loadbuffer`,
`luaH_new`) but not `lua_pcall`/`lua_newstate`, i.e. our Lua is the same family
but this build differs enough that only long, distinctive bodies survive. A
patching strategy keyed on these signatures would work for some functions and
silently fail for others - which is precisely the failure mode the patch-day
notes warn about (`BROKEN` bucket = needs hand-derivation).

## Verdict for our bot

| Asset | Usable? | Why |
|---|---|---|
| 19 byte signatures | **Yes, now** | converted to `Tools/WowBSigScan/sigtable.h`; scanner built |
| `WORLD_INTERSECT` + `WORLDFRAME_PROJECT_POINT` roles | **Yes, if they resolve** | gives project+raycast, i.e. CTM by synthesized click |
| Patch-day procedure + `derive_*.py` | **Yes, method** | adapted to our decoded image; avoids hand-derivation |
| Taint-clear technique | **Yes, method** | `cmp qword [g_taint],0` gate, find read-only in the prologue |
| Facing + MoveForward walker design | Partly | needs g_taint + CInputControl on 70291; user has ruled MoveForward out |
| Reflective injector | Optional | we already have a working APC stub route |
| Detour + Recast nav, mmaps | **Yes, later** | build-independent geometry; 2.7 GB tiles; needs our own raycast to feed it |
| Lua 5.1 bridge | Risky | struct-layout dependent; only after a 70291 layout check |
| Their GUI, Warden patches | No | different anti-cheat generation, and irrelevant on a local server |

# Can Winkr get us the click-to-move offsets?

Short answer: **no — there is no click-to-move function to find an offset for, and
`WoWMovement.cs` would not work even with one.** Both halves of that are checked
against Winkr's own source, not against memory. Details below, then what to do instead.

## What `WoWMovement.cs` actually needs

`Styx/WoWInternals/WoWMovement.cs` is the 3.3.5a implementation and it is CTM-based.
It needs exactly three things:

| What | 3.3.5a value | Source |
|---|---|---|
| `CGPlayer_C__ClickToMove` | `0x00727400` | `CTM_Function`, line 23 |
| `CGPlayer_C__ClickToMoveStop` | `0x0072B3A0` | `CTM_Stop_Function`, line 25 |
| the CTM struct (state: type, click position) | `0x00CA11D8` | `ClickToMove_Base`, line 27 |

and it calls the first one like this (line ~400):

```csharp
executor.AddLine("push {0}", (uint)BitConverter.SingleToInt32Bits(facing));
executor.AddLine("push {0}", allocatedMemory["ClickPos"]);
executor.AddLine("push {0}", allocatedMemory["GUID"]);
executor.AddLine("push {0}", (uint)clickToMoveType);
executor.AddLine("call {0}", GetActivePlayerObject_Function);
executor.AddLine("mov ecx, eax");
executor.AddLine("call {0}", CTM_Function);
```

That is the **x86 `__thiscall` ABI**: arguments pushed right-to-left, `this` in `ecx`,
return value in `eax`, one `push` per argument because every argument is 32 bits.

The build-70291 client is **x64**. On x64 the same call is `rcx` = player, `edx` = type,
`r8` = GUID, `r9` = pointer to the click position, and `facing` on the stack at `[rsp+0x20]`
— a different number of registers, a different argument order, and 8-byte slots. So
"find the offset and set the constant" is not the work: the call site, the executor
(`ExecutorRand`, a 32-bit `ManagedFasm`), and the CTM struct reads all have to be
rewritten for x64 before a single offset would help. Then it would still need the
address, which brings us to Winkr.

## What Winkr has, and what it does not

`github.com/laurentiumanafu/Winkr-Public` (branch `main`) is an injected DLL for the
**Classic Era 1.15.x family**: `rva_defs.h` is verified against build **69547**
(68940 and 69109 before it), and `sig_defs.h` carries a byte pattern for every
address-like RVA. Two tools matter:

* `Winkr/dll/core/dinkr_scanner.c` — scan every readable committed page of the module
  image for a pattern; exactly one match wins, zero or many fall back to the RVAs.
* `tools/resig.py` — when a pattern stops matching, shorten it from the tail one byte
  at a time and report where the hit count collapses to a handful. That locates the
  function's prologue and the instruction that changed.

That machinery is exactly what "get the offsets" needs. **But the table has no
click-to-move entry**, and the reason is in Winkr's own `Documents/Movement.md`:

> **ClickToMove does not exist on this build.** "ClickToMove", "clickToMove",
> "CLICK_TO_MOVE", "TerrainClick", "NavMesh", "Detour" are all absent (ASCII and
> UTF-16). A sweep of all 524 CVar registration sites found no CTM or pathfinding CVar.
> […] **Do not pursue CTM here.**

The one entry that *looks* promising, `RVA_HANDLE_TERRAIN_CLICK` (`0x01C06240`), is not
movement. Winkr's own call site says what it is:

```
/* Spell_C terrain click handler (RVA 0x1F7BF70)
 * Checks (targetingFlags & 0x60) and targetingGUID, writes position to spell object,
 * clears targeting flags, and finalizes the pending AOE cast.
 * a1 = GUID128* (null for terrain), a2 = Vec3*, a3 = flag, a4 = cast context ptr */
typedef char (__fastcall *HandleTerrainClick_t)(void* targetGUID, float* position, char flag, void* castContext);
```

That is ground-targeted **spell** placement — the green circle — not walking. And that
file adds:

> `RVA_HANDLE_TERRAIN_CLICK` has carried its 68887 value through 68940, 69109 and 69547,
> and **its signature has not matched since** […] Deriving it needs the PREVIOUS build's
> dump […] pattern recovery gave a candidate whose body reads like a container bounds
> check rather than a click handler, which is not good enough to call blind.

So calling it would not move the character, and on a newer build it would be calling an
address that three builds of Winkr already refused to call for fear of a crash.

## Why clicks still walk the character

Because the *client's UI* does it, not a CTM function the bot can call. Whatever
facility turns "user clicked the world here" into movement lives inside the client's
input/UI layer, which is why:

* Winkr finds no CTM symbol (`Movement.md`, quoted above),
* yet posting real mouse clicks walks the character — measured repeatedly on
  2026-10-29: 5.8 yd, 6.5 yd, 7.2 yd per click, distance to goal falling
  `21.8 → 15.3 → 8.0 yd` on three consecutive centre clicks.

The click is the interface. It already moves. What was broken was the control loop
around it (see `CLICKAIM_PORT.md`).

## What was done instead

1. **`--sigscan` in the bot** (`Styx/WoWInternals/WowBSigScan.cs`) — Winkr's scanner and
   `resig.py` ported to C#, with Winkr's table regenerated *from* `sig_defs.h` rather
   than retyped. Run it in the client and it prints, per entry, whether the pattern has
   exactly one match on **this** build (that is the address), several (ambiguous), or
   none plus the longest prefix that still matches (the prologue, and candidates).
   Nine entries: the terrain-click and interact handlers, and the taint/Lua set
   (`IS_VALUE_NOT_SECRET`, `LUAG_SECRETERROR`, `LUA_SETSECRET`, `LUA_NEWSTATE`,
   `LUA_PCALL`, `C_API_SECRET_ARG_GATE`, `g_pObjectMgr`).

   This is the honest version of "get the offsets with Winkr": it answers the question
   by running against 70291 instead of by arguing from a 69547 table.

2. **The mover's aim loop** — replaced sign-guessing with measurement (drag the camera
   to face the point, measure how many degrees per pixel it actually turned, then click
   the middle of the screen). See `CLICKAIM_PORT.md`.

## What Winkr *is* good for on this project

Not CTM, but two facts that matter for the 70291 offsets work generally:

* **`g_taint` is a single global** that gates every movement keybind handler, every
  secret-value check and every protected call — three independent anchors agree on it
  (`is_value_not_secret`, `c_api_secret_arg_gate`, and `MoveForwardStart` itself). If
  movement is ever done through the client's own input functions, that global is the
  whole problem, and zeroing it around the call is the documented fix.
* **`PostMessage(WM_KEYDOWN/WM_KEYUP)` works on Classic, fully externally** — Winkr
  states `WowClassicGrindBot` drives 1.15 that way with layout-translated VK codes and a
  hand-built `lParam`, background-capable, no focus required. That is a *keyboard*
  movement route, independent of CTM and of the Lua natives (which are the thing that
  was already tried and abandoned).

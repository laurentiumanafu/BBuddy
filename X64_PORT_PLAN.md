# CopilotBuddy → x64 World of Warcraft: Audit & Migration Plan

Status: **foundation layer landed** (see "What is done" below). Everything
else is planned but not started.

## 1. Why this is not "just flip a build flag"

CopilotBuddy targets WoW **3.3.5a build 12340, x86** at every layer. The
`<PlatformTarget>x86</PlatformTarget>` in the csproj is the smallest part.
A 64-bit WoW process breaks the bot in seven independent places:

| # | Layer | x86 assumption | What breaks on a 64-bit target |
|---|-------|----------------|-------------------------------|
| 1 | `GreenMagic/Native/Imports.cs` | `ReadProcessMemory(IntPtr, uint lpBaseAddress, …)`, `VirtualAllocEx → uint` | Any address above 4 GB cannot even be represented. A 32-bit bot process can also receive truncated pointers from a 64-bit target. |
| 2 | `GreenMagic/Memory.cs` | `_imageBase` is `uint` (default `0x400000`); ~400 `uint`-addressed signatures across ~38 files; pointer chains read 4-byte pointers (`ReadInternal<uint>`) | All pointer reads return garbage; `GetAbsolute` wrong; cache keys collide. |
| 3 | `GreenMagic/Native/Context.cs` | i386 `CONTEXT` (Eip/Eax/…, `FLOATING_SAVE_AREA`, 512-byte `ExtendedRegisters`), `CONTEXT_i386 = 0x10000` | `Get/SetThreadContext` fails outright against a 64-bit thread — Windows requires the 1232-byte AMD64 `CONTEXT` with `CONTEXT_AMD64 = 0x100000`. |
| 4 | `Executor.cs` / `ExecutorRand.cs` | Hook `d3d9.dll!EndScene`; emit `pushad`/`popad`/`pushfd` (**illegal in x64 long mode**); stdcall `push arg… / call / add esp,N`; thiscall `mov ecx, this`; EIP hijack; **440 hand-written asm lines in 20 files** | Every byte is wrong for x64: the instructions are undefined, the calling convention is RCX/RDX/R8/R9 + 32-byte shadow space + 16-byte stack alignment, pointers are 8 bytes, and a 64-bit client renders with DX11/DX12/Vulkan — there is no d3d9 to hook. |
| 5 | `Styx/Offsets/GlobalOffsets.cs`, `Styx/Patchables/GlobalOffsets.cs` | ~122 hardcoded absolute addresses for build 12340 (`InGame = 0xBD0792`, `LuaState = 0xD3F78C`, ~90 `FrameScript_*`/`CG*` functions) plus struct offsets that assume **4-byte pointers** (`NextObjectOffset = 0x3C`, `DescriptorMultiplier = 0x4`, `NameCacheNextOffset = 0xC`, `ObjectGuidOffset = 0x30`, …) | Every address is wrong for any other build/architecture, and every pointer-bearing struct doubles in size on x64 (all `+4` strides after the first pointer shift). |
| 6 | `Lib/*.dll` | `Navigation.dll`, `Navigation 1x1.dll`, `BlackMagic.dll`, `fasmdll_managed.dll`, `System.Data.SQLite.dll`, `RecastManaged.dll`, `Tripper.Tools.dll`, `Tripper.XNAMath.dll` are all **PE32/x86** (verified from PE headers) | A 64-bit CopilotBuddy cannot load any of them. x64 rebuilds (or replacements) are mandatory. |
| 7 | Runtime content | `data.bin`, `Spells.bin`, `datadb/`, `mmaps/` are extracted from 3.3.5a MPQ/DBC | Only valid if the x64 target still speaks 3.3.5a game data. |

## 2. Which x64 client?

**Chosen target (confirmed):** `WowB.exe`, game version **1.60.1**, build
**70291** — the **World of Warcraft Classic Beta/PTR client**. It is
Blizzard's official modern Classic engine: native x64, DX11/DX12 renderer,
hash-table object manager, 128-bit GUIDs, CASC data. It is NOT the 3.3.5a
engine, so none of the build-12340 offsets apply — see §6 for the target
work list. The attach layer already accepts this client
(`ObjectManager.SupportedBuildWowB = 70291`, `WowB` in the process-name
lists).

Blizzard never shipped a 64-bit 3.3.5a client (first x64 client: patch
4.3.2, 2012; 32-bit retired at 8.0.1, 2018). Other "64-bit WoW that plays
WotLK 3.3.5a content" candidates, for reference:

| Target | Reality | Effort for CopilotBuddy |
|--------|---------|-------------------------|
| **WoWee** — [github.com/Kelsidavis/WoWee](https://github.com/Kelsidavis/WoWee) | Open-source C++20/SDL3/Vulkan reimplementation; x86-64 + ARM64; supports Vanilla/TBC/WotLK 3.3.5a; tested vs TrinityCore/AzerothCore/MaNGOS; very active | **Best option.** Source is available → struct layouts and call sites can be read directly, and a small in-process bridge (named pipe / shared memory export layer) can replace memory scraping entirely. No offsets to RE, no FASM needed. |
| **whoa** — [github.com/whoahq/whoa](https://github.com/whoahq/whoa) | Open-source effort rebuilding the 3.3.5a client | Same approach as WoWee; maturity varies. |
| Custom/private x64 3.3.5a build | Some server teams ship rebuilt clients | Needs the binary; offsets must be RE'd per build. Full faithful-port path below. |
| Wrath Classic / modern retail 64-bit | 128-bit GUIDs, CASC/DB2, Warden, DX11/12 | Effectively a **new bot**, not a port. Out of scope for this codebase. |

The foundation work in §4 is required by **all** paths; nothing here is
client-specific.

## 3. Work packages (ordered)

- **WP0 — Foundation (done, see §4).** Bitness-agnostic memory layer,
  x64 CONTEXT, PE32+ parsing, dual-platform project. x64 pattern scanning
  (`PatternManager.LoadFile64`) also landed.
- **WP1 — Target client (done).** WowB.exe 1.60.1 / build 70291 (WoW
  Classic Beta). Attach + build acceptance already on master.
- **WP2 — Object model abstraction.** `ObjectManager`, `WoWObject` and
  friends read through `uint` chains with 4-byte strides. Introduce a
  small adapter (`IProcessMemory` with `ReadPointer`/pointer-size-aware
  chain walks) so the same Styx code can run against either target
  bitness. Affected: ~38 files.
- **WP3 — Offset layer.** Split `Styx/Offsets/GlobalOffsets.cs` +
  `Styx/Patchables/GlobalOffsets.cs` into per-target offset tables
  (e.g. `Offsets335x86`, `Offsets<Target>x64`). Struct offsets that encode
  pointer size (`NextObjectOffset`, `NameCacheNextOffset`,
  `DescriptorMultiplier`…) become computed values. For WoWee/whoa these
  come from reading their source; for a closed binary they must be
  reverse-engineered per build.
- **WP4 — Execution engine.** `Executor`/`ExecutorRand` need an x64 twin.
  **Scaffolding landed (this branch):** `GreenMagic/X64Emitter.cs`
  (hand-rolled x64 machine-code emitter — fasmdll_managed is x86-only),
  `GreenMagic/Executor64.cs` (RIP-hijack executor: full AMD64 context
  save, cave payload with x64 fastcall framing, shared kernel event,
  context restore; one round-trip per call) and
  `GreenMagic/DX11PresentHook64.cs` (IDXGISwapChain::Present hook via
  **vtable slot swap** — no instruction copying/disassembler needed;
  per-frame injection gate with the same thread affinity EndScene gave).
  Remaining: per-build device/swapchain offset (WowBOffsets.SwapChainPtr),
  battle-testing against the live client, and porting the 440 inline
  x86 asm call sites in 20 files onto `Executor64.Execute`.
- **WP5 — Native libraries.** x64 builds of `Navigation.dll` (Detour
  runtime — rebuild from the Navigation-C- sources), `fasmdll_managed.dll`
  (or drop FASM and emit machine code directly), `System.Data.SQLite.dll`
  (official x64 build exists), `Tripper.Tools/XNAMath` (pure C++ wrappers,
  rebuild), `BlackMagic.dll` (audit — may be unused).
- **WP6 — Data layer.** Re-extract/convert `data.bin`, `Spells.bin`,
  `datadb/`, mmaps if the target's data formats differ from 3.3.5a MPQ/DBC.
- **WP7 — Attach/UI flow.** Process picker (`Styx/CommonBot` +
  `UI/`) currently filters for the classic client; extend for x64,
  including `IsWow64Process` sanity checks and clearer error messages.
- **WP8 — Verification.** No Windows/WoW in the current dev sandbox: the
  x64 platform has **not been compiled or run** anywhere yet. First
  milestone is `dotnet build -p:Platform=x64` on Windows, then attach to
  the chosen client.

## 4. What is done (this branch)

All additive — the classic x86/3.3.5a path is byte-for-byte untouched:

- `CopilotBuddy.csproj` / `CopilotBuddy.slnx` — new **x64 platform**;
  `PlatformTarget` is now conditional (x86 stays the default).
- `GreenMagic/Native/Context64.cs` — the 1232-byte AMD64 `CONTEXT`
  (`Context64`), `M128A`, and `ContextFlags64` (`CONTEXT_AMD64 | …`).
- `GreenMagic/Native/MEMORY_BASIC_INFORMATION64.cs` — 64-bit
  `MEMORY_BASIC_INFORMATION` (required whenever a 64-bit bot process calls
  `VirtualQueryEx`).
- `GreenMagic/Native/Imports.cs` — 64-bit-capable P/Invokes
  (`ReadProcessMemory64`, `WriteProcessMemory64`, `VirtualAllocEx64`,
  `VirtualFreeEx64`, `VirtualProtectEx64`, `VirtualQueryEx64`,
  `Get/SetThreadContext64`, `IsWow64Process`), all pointing at the real
  kernel32 exports via `EntryPoint`.
- `GreenMagic/Native/PeHeaderParser.cs` — PE32+ support
  (`ImageNtHeader64`/`ImageOptionalHeader64`, `Is64BitImage`,
  `NtHeader64`); parsing no longer crashes on modules above 4 GB.
- `GreenMagic/Memory.cs` —
  auto-detects a 64-bit target from the PE machine field
  (`Is64BitTarget`), full 64-bit image base (`ImageBase64`,
  `GetAbsolute64`), and a complete ulong-addressed API:
  `ReadBytes64`, `Read64<T>`, `ReadPointer64`, `ReadChain64<T>`,
  `ReadChainRelative64<T>`, `ReadStruct64<T>`, `ReadString64`,
  `WriteBytes64`, `Write64<T>`, `WritePointer64`, `WriteObject64`,
  `AllocateMemory64`, `FreeMemory64`, `QueryMemory64`,
  `Get/SetThreadContext64`, `CreateRemoteThread64`.
  `ReadChain64` walks 8-byte pointer chains exactly like the existing
  `Read<T>(params uint[])` walks 4-byte ones.
- `GreenMagic/Internals/PatternManager.cs` — x64 pattern scanning:
  scan range derived from PE **section headers** (PE32+ has no
  `BaseOfData`), `LoadFile64`/`Find64` resolve patterns to absolute
  64-bit addresses (`GetPattern64`), same XML semantics; `Rel` reads
  the rel32 displacement used by x64 RIP-relative instructions.
- `Styx/Offsets/WowBOffsets.cs` — scaffold of the build-70291 offset
  table: engine-level object-model constants filled in, per-build
  addresses documented as placeholders with discovery hints, plus an
  `IsComplete` gate the attach flow should check.
- `GreenMagic/X64Emitter.cs` — minimal x64 machine-code emitter
  (imm64 moves, push/pop, fastcall framing with shadow space +
  alignment, reg calls, label/rel32 jumps, r11-indirection memory
  access). Replaces FASM, which cannot load in a 64-bit process.
- `GreenMagic/Executor64.cs` — 64-bit executor: RIP hijack with the
  1232-byte AMD64 CONTEXT, code cave + shared kernel event, fastcall
  `Execute(function, args…)`, raw payload support, safe context
  restore in `finally`.
- `GreenMagic/DX11PresentHook64.cs` — DX11 Present hook via vtable
  slot swap (slot 8) with protect/write/restore, per-frame injection
  gate, `ExecuteOnFrame` queueing. The EndScene replacement.
- `Styx/WoWInternals/WowBGuid.cs` — the 128-bit GUID value type.
- `Styx/WoWInternals/WowBReader.cs` — read-only object-model view:
  in-world state, local/target/mouseover GUIDs, zone/area ids and
  text, entity-array enumeration (hash-table model), camera position,
  and `DescribeSnapshot()` for attach-time diagnostics.
- `Styx/WoWInternals/ObjectManager.cs` — attach flow: 64-bit targets
  enter **read-only WowB mode** (reader created, snapshot logged)
  instead of attempting the EndScene hook.

<<<<<<< ours
=======
- `GreenMagic/LuaBridge64.cs` + `Tools/LuaBridgeProbe` — the **execution vector that
  works on this build**, entirely from outside the process: attach, verify (module canary,
  `jmp qword ptr [rcx]` gadget, trampoline shape, `kernel32!Sleep`/`GetTickCount64`), start
  **one** resident payload thread, then `Call(name, arg, results)` per command. The client's
  Lua thread is found by two signals (per-thread CPU time + RIP sampling with
  `SuspendThread`/`CONTEXT_CONTROL`) and frozen for the duration of each call, because the
  2026-10-13 crash was a data race on the `lua_State`, not a broken vector. Proven live:
  `GetTime`, `UnitName`, `UnitLevel`, `UnitHealth`, `UnitExists`, `UnitGUID`, `UnitClass` all
  return correct in-game values (`hunt45`, 11/11; the 287-call `hunt46`-protocol run).
  `--call NAME [--arg TEXT]` is the invocation primitive the action layer will use; it
  refuses to call a handler whose page this client has not decrypted yet (cold pages hold
  ciphertext) and says how to warm it. **`--on-lua-thread` runs each command ON the client's
  own Lua thread** (suspend + saved CONTEXT + RIP/RSP redirect), which is the only way to call
  anything that raises a FrameScript event: `MoveForwardStart` from an injected thread made the
  client terminate itself with `ASSERT(FrameScript_IsLuaThread())` (2026-10-13, report read and
  resolved). `--seq "A ; sleep=1200 ; B"` runs several guarded calls
  in one session, in order - the shape of the action queue, and the way to test anything that
  has to be undone. **Verified live (0.5.0): 798 of 798 calls ran with the client's Lua thread
  frozen, zero faults, 1.14 ms frozen per call, the client alive at the end (the forced-tid
  escape hatch `--lua-thread N` carried a 791-call run on its own), and the first WRITE went
  through - `RunScript("print('bbuddy: the bridge can write')")` executed inside the client
  and the chat frame showed it.** **0.8.0 makes the hijack path self-proving**: the first movement
  attempt never reached the client at all - `SetThreadContext` was refused because the code asked
  for `0x0040` (`THREAD_QUERY_INFORMATION`) instead of `THREAD_SET_CONTEXT` (**0x0010**), and the
  last error was read after `ResumeThread` had overwritten it (hence the useless `failed: 0`).
  Windows errors are now captured first, the raw `ntdll!NtSetContextThread` NTSTATUS is printed when
  Win32 refuses, every candidate thread is proved with a real CONTEXT round-trip before a command
  depends on it (`--check --on-lua-thread`), every write is verified by read-back and retried with
  `CONTROL|INTEGER`, and a failed takeover is a `[takeover]` line (exit 8) - not a claim that the
  client's Lua refused something. **The Lua-level gate is separately confirmed dead for `/run`**:
  `/run MoveForwardStart()` in the chat box answers `*** FORCETAINT_STRONG*** has been blocked from
  an action only available to the Blizzard UI`, i.e. `MoveForwardStart` is *forbidden* to script
  code - so the route to movement is a C call on the client's own Lua thread, never an API call.
  New `--dump va:len,...` copies live (decrypted) pages out read-only, which is the only way to see
  the movement handlers: exactly those pages hold ciphertext in the offline image.
  Details + cold-page rule: `LUA_BRIDGE_64.md`.

>>>>>>> theirs
## 5. Known gaps / risks

- The x64 platform **builds** (confirmed with .NET on Windows) but has
  not yet been run against a live client — attach is the next milestone.
- Native Lib DLLs are still x86; the x64 platform will fail at load time
  for `fasmdll_managed`/`System.Data.SQLite` until WP5 lands.
- `ExecutorRand`'s SEH/VEH gate and `Styx/Offsets` struct strides are the
  two hardest WP4 items — plan real time for them.
- An x64 WoW above 4 GB also invalidates the cached `uint` addresses in
  any plugin/botbase that bypasses `Memory` — audit `Bots/` during WP2.
- The **modern Classic object model** (hash table + 128-bit GUIDs, §6)
  does not map 1:1 onto the 3.3.5a linked list the Styx layer assumes;
  WP2/WP3 are where that impedance gets resolved.

## 6. Target: WowB.exe 1.60.1 (build 70291)

Confirmed target client. This section collects what is known about this
engine and what is still needed before the bot can drive it.

### 6.1 Engine facts (modern Classic, x64)

- Native 64-bit, DX11/DX12 renderer — **no d3d9.dll**, so the EndScene
  hook is out. Options for the execution engine: DX11/DX12 `Present`
  vtable hook (needs the device pointer), Vulkan submit hook, a
  remote-thread trampoline into a code cave, or — simplest for Lua —
  driving the game purely through memory reads + hardware input.
- Object manager is a hash table, not a linked list. Verified-stable
  layout constants across recent Classic builds:

  ```
  ClntObjMgr (s_curMgr)
    +0x08  entity array            +0x10  entity count
    +0x40  hash array capacity     +0x48  hash array    +0x50  hash array count

  entity entry (stride ~0x30)
    +0x10  GUID (128-bit: low qword + high qword)
    +0x28  CGObject* pointer

  CGObject
    +0x08  object type (byte)      +0x18  GUID (128-bit)
  ```

  (`Styx/Offsets/WowBOffsets.cs` holds these constants.)
- GUIDs are 128-bit. `PlayerTargetGuid` and `LastTargetGuid` are two
  consecutive 16-byte GUIDs (`last_target = target + 0x10`).
- Lua: reached through the FrameBase chain (`FrameBase -> +0xF18 ->
  +0x148 -> lua_State` on earlier Classic builds — re-verify). Modern
  Classic enforces Lua taint: many FrameScript actions require a recent
  hardware event, so the bot may need to fake/feed
  `HardwareEventPtr` or restrict itself to untainted paths.
- Position/movement still goes through the CGUnit/CGPlayer movement
  block; ClickToMove exists but its signature changed vs 3.3.5a.

### 6.2 Offset checklist for build 70291 (all image-relative)

Data pointers (need one-time RE per build; signatures can be written so
`PatternManager.LoadFile64` re-resolves them across patches):

- [ ] GameState / "in world" flag (Script_IsPlayerInWorld data ref)
- [ ] ObjectManager (s_curMgr) — find via the "objects waiting to be
      freed" string reference
- [ ] PlayerGuid, PlayerTargetGuid (+0x10 LastTarget), MouseoverGuid
- [ ] ZoneId / AreaId / SubAreaId, ZoneText (+0x8 subzone, +0x10 minimap)
- [ ] CameraManager + active-camera offset
- [ ] FrameBase (Lua chain root)
- [ ] HardwareEventPtr

Functions (fastcall x64; calling convention is uniform, unlike 3.3.5a):

- [ ] FrameScript_ExecuteBuffer / GetTop / PCall / ToLString / Load
- [ ] ClntObjMgrEnumVisibleObjects / GetActivePlayerObj / ObjectPtr
- [ ] CGGameUI::Target
- [ ] Spell_C::CastSpell / GetSpellCooldown
- [ ] CGPlayer_C::ClickToMove / ClickToMoveStop
- [ ] CGWorldFrame::Intersect (TraceLine)

Until `WowBOffsets.IsComplete` is true, the bot must refuse to attach to
a WowB process with a clear log line rather than reading garbage.

### 6.3 Suggested order of attack

(Code for steps 1–2 has landed: `WowBReader` + the read-only attach mode;
step 3's engine is scaffolded as `Executor64`/`DX11PresentHook64`. What
remains at each step is filling in 70291 offsets and live validation.)

<<<<<<< ours
=======
**Status after hunt45/hunt46 (live, build 70291):** step 3's hard part is
solved. Step 3's *call machinery* — getting a return address past the client's
tail validator at `module+0x1D8E80` — works: a remote thread started at a warm
`jmp qword ptr [rcx]` gadget, with `lua_pcall` entered through a two-hop
trampoline (`hunt/hunt45.ps1`), returned real data 11/11 (`GetTime`,
`UnitHealth`, `UnitLevel`, `UnitExists`, `UnitGUID`, `UnitName`, `UnitClass`,
`GetMoney`, `IsLoggedIn`). `hunt/hunt46.ps1` then turns it into a *resident*
bridge: one injected thread serving commands from a mailbox in its own page
(`CMD`/`SEQ`/`ACK`, `kernel32!Sleep` idle, deadline failsafe), which is the
interface `Executor64` should implement in-process. Result type words carry a
value flag (`tt & 0xFF` is the type).
Steps 1–2 (data pointers, camera/object enumeration) are still offsets work and
can be served either by `WowBReader` or by the bridge.

>>>>>>> theirs
1. Fill the data pointers first (read-only milestone: log in-world
   state, player GUID, target GUID, zone text) — no injection needed,
   validates the whole 64-bit read path end-to-end.
2. Camera + object enumeration → object list rendering in the overlay.
3. Lua execution (FrameScript_ExecuteBuffer via the new executor) →
   questing/profile engine.
4. Movement (ClickToMove or hardware input) → botbases.

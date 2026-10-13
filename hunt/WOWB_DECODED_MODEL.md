# WowB.exe (1.60.1, build 70291) — decoded model as of hunt33

Authoritative summary of everything the live probes have **verified against the
running client** (not inferred). Image-relative RVAs; add the live module base
(ASLR — read it, e.g. `0x7FF73B080000` in the hunt25..28 runs).

Tooling lives in this folder:
| artifact | what it is | state |
|---|---|---|
| `hunt31.ps1` | live probe #9: page-protection map, per-record handle inventory, entity hunt, fixed registry dump | **delivered, not yet run** |
| `hunt30.ps1` | live probe #8: unit-object verify, 6 s poll watch, page probes | **RUN — see the hunt30 findings below** |
| `hunt29.ps1` | live probe #7: handle audit, unit stats, name harvest, API-registry scan | **RUN — results decoded below** |
| `WowBHunt29.java` | Ghidra probe #7: FnApiRegister, name lookup, API string xrefs | **RUN — results decoded below** |
| (older probes hunt5..hunt28) | live memory probes, kept on the user machine in `C:\ghidra-scripts\` | hunt28 log received |

---

## 1. Object model (all live-verified)

```
HandlePools (module + 0x582D150)         pool table, 8-byte slots
  [fam] -> pool object {u32 flag, void* elems @+0x08, u32 cap @+0x10, u32 max @+0x18}
  element = elems + slot*0x10  {u32 tag (bit31 = occupied), u32 generation, u64 record*}
  record  = the object itself

record (class B, 0x110 stride - real world objects)
  +0x00 u64 packed handle  {lo32 = element index, mid24 = generation, top8 = pool index}
  +0x04 u32 family << 24   (second confirmation of the pool index)
  +0x08 u64 arena/zone descriptor  (NOT the unit: 82 records shared 6 values)
  +0x10 u32 sequence, +0x14 u32 0xCECECE00 poison
  +0x18 u64 GUID low   (live player: 0x2)
  +0x20 u64 GUID high  (live player: 0x0801180000000000)
  +0x40 u64 0xEACECECECECECECE poison
  +0x48 u32 flags      (0x206 player, 0x217 NPC/other, 0x17 NPC slot1)
  +0x60 float x,y,z    PROVEN: camera sits 15.19 yd behind / 2.71 yd above
  +0x6C float 1.0
  +0x70 float facing   (0..2pi)
  +0x80 3x3 basis, column-major, col0 = (cos f, sin f, 0) = forward
  +0xA4 position copy, +0xB4 basis copy
  +0xD8 u64 -> unit DATA BLOCK

record (class A, 0x60 stride - slots 2..15 live, no transform)
  same head, but +0x48 reads the 0xCECECECE marker and +0x60 is the next record.
  => the reader must gate the transform on flags != 0xCECECECE.

unit data block = *(record+0xD8)   == THE UNIT OBJECT (hunt29 live proof)
  +0x00 vtable: CGUnitFnTable 0x50CDDC0 (NPC) / 0x50127920 (local player)
  +0x08 pointer back to the record
  +0x10/+0x18 GUID lo/hi (record stores the same pair at +0x18/+0x20)
  +0x1E30 u32 health      (live 0x54 = 84 player, 0x66 = 102 NPC)
  +0x1E38 u32 maxHealth   (live 84 player / 102 NPC)
  +0x1BC8 u32 power       (live 0x60 = 96 player, 0 NPC)
  +0x1E60 u32 unitFlags   (live 8 player, 0x300 NPC)
  +0x238     name pointer == 0 (name comes from a FUNCTION, see below)
  +0x398/+0x39F  packed handle / family byte consumed by FnEntityGetGuid
                 (hunt30 verifies the live values here)
```

**Handle/generation audit is 100% on every occupied slot (hunt29):**
`handle.lo32 == element index`, `handle.top8 == family`,
`generation XOR tag == 0`, `record+0x04 == family << 24` — 83/83 readable.
Element tags are generational: `0x80000000` x78 plus rare
`0xBFFFFFFF`/`0xC0000000`/`0x8000000C`/`0x80000005`/`0x80000004` — always
mask `0x3FFFFFFF` off before comparing; never assume gen == 0.

`FnEntityGetGuid` 0x2BBB7F0 (clean 46-line C) proves the *reverse* path
(entity -> handle -> pool -> record -> GUID) and the guard fn 0x2BBB770.
The "entity" object of that pair has still not been located; it is **not**
`*(record+0x08)` (hunt27 refutation) and **not** adjacent to the record
(hunt28: exactly one copy of the packed handle within +/-0x10000 of the
record). Nothing in the current design needs it — enumeration walks the pools.

## 2. Verified globals / constants

| RVA | what |
|---|---|
| 0x582D150 | handle-pool table (family index = handle top byte) |
| 0x7B7B738 | ObjectManager global (== pools[1] object in hunt25/27) |
| 0x61C72F0 / 0x61C72F8 | player GUID low / high |
| 0x61C7300 | player NAME, stored **inline** ("Brutallex" as raw ASCII) |
| 0x7BDFF38 (+0x10) | target GUID low(+high) / last target |
| 0x7B76AB8 | **lua_State\*** (statically confirmed by WowBHunt24) |
| 0x5FF99F0 -> +0x490 -> +0x10 | camera frame -> camera -> origin vec3 |
| camera +0x1C fwd, +0x28 left, +0x34 up, +0x40 hfov, +0x274 zoom (live 15.00) |

## 3. Transform proof (hunt27/hunt28, same numbers twice)

```
player pos (-6180.18, 359.08, 398.94)   camera (-6191.60, 368.73, 401.65)
cam - pos = -15.19 * col0 + 2.71 * up          (horizontal dot with col0 = -1.0000)
basis col0 = (0.7639,-0.6454,0) |col1| = 1.000000  col0.col1 = 0.000000
facing = 5.5817   atan2(-col0.y, col0.x) = 0.7015 = 2*pi - facing   (exact)
=> forward = (cos facing, sin facing, 0)
```

## 3b. FrameScript API registry (hunt29 live + WowBHunt29 static)

```
entry = { char* name; void* fn; }   stride 0x10, fn stored as absolute VA
ApiTableSpellbook = 0x59096B0   (CastSpellByName entry; walked -0xC0..+0x120)
ApiTableMovement  = 0x58CF320   (MoveForwardStart entry; 58 entries)
ApiTableActionBar = 0x590CB20   (GetActionInfo entry; 9 entries)

FnApiRegister 0xAE96A0(L**, name, cfunc, ns, flags):
  gettop before; pushcclosure(0x471B800);
  setfield(L, -10002, name)  -> globals[name] = fn
  rawset(L, -10000, name)    -> registry
  flags bit0 -> also globals; bit1 -> when a table is at stack -1;
  flags bit2 -> FUN_140769510(L, gettop(L), 0)
  stack-balance asserts (swi(3)) between every step.

NAME PATH (all addresses static, no string in the object):
  unit = FnGuidToUnitChecked 0x1FC37F0(&guid128, 0x20, caller, line)
  name = FnUnitNameFetch 0x25129E0(unitish, &guid128, 0x41, 0, 0)
         -> FnUnitNameCopy 0x2512110(x, buf, 0x132, 0) -> ANSI bytes
         -> MultiByteToWideChar(0xFDE9, 0, name, -1, out) via [0x57E3538]
  (FnGuidToUnitChecked is a virtual call on the object returned for the
   GUID; FnEntityGetGuid 0x2BBB7F0 is its vtable slot friend.)
```

## 3c. hunt30 results — corrected facts

* Player unit vtable is **base+0x50A7920** (my 0x50127920 was a hex typo).
  NPC is CGUnitFnTable 0x50CDDC0. Both share most vtable slots from [7]
  onward (0x27D1900, 0x27F8130, 0x27D1F60, 0x281BFB0, 0x281C010,
  0x27D2670, 0x27F8A30, 0x2846D80) — a usable class check.
* **`unit+0x398` reads 0 and `unit+0x39F` reads 0xFF for BOTH player and
  NPC** — so the stat/unit object is NOT the object `FnEntityGetGuid`
  reads +0x398 from. hunt27 saw `*(record+0x08)` (call it E) with
  `E+0x398 = 0xFFFFFFFFFFFFFFFF` and `E+0x39F = 0xFF` — i.e. E *has* the
  field, currently holding the "invalid handle" sentinel. The entity is a
  third object shape; hunt31 tables it across every record.
* Only ONE copy of the player GUID pair within +/-0x4000 of the record
  (rec+0x18), and exactly one packed-handle copy (rec+0x00).
* **15/15 poll samples identical** over 6 s — the record is a stable
  poll target; the milestone-1 read path is functionally complete.
* Packed binary: several handler RVAs are UNREADABLE at runtime
  (0x23C8BE0, 0x23C9000, 0x23C76C0, 0x2C24040, 0x2CC2DB0, 0x2CDA340)
  while others read fine (0x23C5560, 0x312CB70, 0x1FC37F0, 0x25129E0,
  0x2512110, 0xAE96A0, 0x1D98820) — guard/decrypt-on-demand pages.
  hunt31 maps protection with VirtualQueryEx and retries after a
  temporary VirtualProtectEx.
* hunt30's registry lookup failed because I anchored on the *string*
  address (0x5120A70) instead of the *entry* address (0x59096B0).
  hunt31 scans for the name-string pointer near the entry.

## 3d. hunt31 results — live inventory, registry extents, page protection

**Registry (hunt31 §5) — the fixed anchor worked.** Searching near the *entry*
VA (not the name-string VA) found all three tables; walking until an entry
leaves the module measured their true extents:

| table | base | entries | anchor entry |
|---|---|---|---|
| movement | 0x58CF2B0 | 58 (0x3A0) | MoveForwardStart @ 0x58CF320 |
| spellbook | 0x5909500 | 31 (0x1F0) | CastSpellByName @ 0x59096B0 |
| action bar | 0x590CB20 | 9 (0x90) | GetActionInfo @ 0x590CB20 |

All 98 {name -> fn RVA} pairs are now in `WowBOffsets.cs`; **every RVA already
in the file (79) matched the live table exactly** (zero differences) — the
hunt9 static dump and the live registry agree.

**Unit object truth (hunt31 §2/§3).**
* The unit object stores **its own packed handle at +0x38**
  (`0x0200000000000010` for the player, equal to rec+0x00).
* `unit+0x398` is **not** a handle field: across all 86 occupied records the
  values are 0, 0x15D, 0x3F800000 or 0xDEDEDEDEDEDEDEDE; `unit+0x39F` = 0xFF.
* `*(rec+0x08)` ("E") does carry the pair (E+0x398 = -1 sentinel,
  E+0x39F = 0xFF) but is a descriptor-like object (E+0x10 = 0x39 = flags,
  E+0x18 = pointer, and E2 = *(E+0x08) is shared by player and NPC alike).
  FnEntityGetGuid's final `u8@(E+0x10) & 0x20` check passes only by accident.
  => the object FnEntityGetGuid expects is still a third shape.
* vtable histogram of `rec+0xD8`: 0x50A7920 (player), 0x50CDDC0 (NPC, ~23
  slots), 0x50B1838 (~17 slots, +0x398 = 0x15D), 0x50D8258 (1 slot,
  +0x398 = float 1.0). **~45% of the occupied records hold a value at +0xD8
  that is not an object at all**: consecutive slots differ by exactly 0x2F4
  (packed-array entries) and the value itself is often not 8-byte aligned
  (0x15DC2D1C24C, 0x15D35A548C4, ...).
  => `WowBReader.TryGetUnitObject` now requires alignment +
  `*(unit+8) == record` + the GUID pair at unit+0x10/+0x18.

**Level (hunt31 §6).** Descriptor fields are 8-byte spaced (health 0x1E30,
maxHealth 0x1E38) => modern int64 layout. `+0x1E40` = 2 on the player and 5 on
the NPC, the NPC repeating 5 at +0x1E48 and +0x1E50 (effective/scaling level).
`+0x1E5C` (3 / 55) is something else (class / display id). hunt32 prints
+0x1E40 for every record it validates as a real unit.

**Page protection (hunt31 §1) — the Phase-3 blocker.**
Every RPM failure returns `state = MEM_COMMIT (0x1000)`,
`Protect = PAGE_NOACCESS (0x1)`. Unreadable at rest: 0x23C8BE0, 0x23C9000,
0x23C76C0, 0x2C24040, 0x2CC2DB0, 0x2CDA340, 0x2BBB7F0, 0x1D98820.
Readable PAGE_EXECUTE_READ (0x20): 0x23C5560, 0x1FC37F0, 0x25129E0,
0x2512110, 0xAE96A0, 0x27FBD0, 0x471A960, 0x312CB70.
The unreadable set is exactly the handlers the client has NOT executed yet;
every hot handler (called from startup) is RX => **decrypt-on-execute**, not
dead code. hunt31's VirtualProtectEx retry never actually ran: the P/Invoke was
declared as `VirtualProtectExRaw` (EntryPointNotFoundException - no such export
in kernel32). hunt32 retries it properly and compares the live bytes with
(a) `Desktop\wowb-decoded.exe` and (b) the original WowB.exe - if the unlocked
page equals the decoded dump, Executor64 can remote-call handlers directly.

## 3e. hunt32/hunt33 results — the packing model and the Lua ABI

**Packing (hunt32 §1, decisive).** For 13 probed RVAs, `live == the
Desktop\wowb-decoded.exe bytes` in 9 cases and `live == the shipped
WowB.exe bytes` in 0 cases: the shipped `.text` is packed, the decoded dump
is the plaintext ground truth. The 4 failures were all `PAGE_NOACCESS`, and
hunt31 found the same for exactly the handlers the client had NOT executed
yet => **decrypt-on-execute**. The interactive timeline proved the follow-up
fact: once a handler runs, its page becomes `PAGE_EXECUTE_READ` and STAYS
that way for the life of the process (that is why the second interactive run
showed no transitions — nothing was left to flip; not a failure).

**Packing does NOT cover .rdata/.data.** In the shipped file:
`CastSpellByName` @ rva 0x5120A70 is plaintext ASCII and the table entry at
rva 0x59096B0 holds `0x145120A70, 0x142C24040` = preferred base + (name rva,
handler rva): the FrameScript registry is readable **offline**. Extracted:
`hunt/api_map.txt` — **63 tables / 3240 {name -> handler RVA} entries**
(hunt31's live walk matched 79/79 of them). `.pdata` is NOT usable: it claims
lua_pcall is 0x15 bytes and has no entry for lua_gettop, so its function map
is bogus; live dumps stay the source for code bytes.

**The Unit*/Target*/RunScript APIs are runtime-registered.** Their name
strings ARE in `.rdata` (`UnitHealth` 0x4FA0F68, `RunScript` 0x4F45FA8,
`TargetUnit` 0x4F8BDF0, `UnitName` 0x4F8ED08, `UnitExists` 0x4F9FDE8...)
but NO `.data` pointer references them, so they have no static table entry.
Consequence for Phase 3: do not hunt their RVAs — **run Lua instead**
(the VM resolves the names for us).

**Lua 5.1 ABI (hunt33, from hunt32's live dump, proven structurally).**
`lua_State`: +0x28 top, +0x30 base, +0x38 l_G, +0x40 ci (ci->top at +0x10),
+0x58 stack (savestack = p - stack). `global_State`: +0x70 GCthreshold,
+0x78 **totalbytes** (and +0x88 GCdebt) — corrected in the hunt41 post-mortem:
`luaM_realloc_` does `add [G+0x78], newsize - oldsize` and pushcclosure compares
`cmp [G+0x78], [G+0x70] / jb skip`, i.e. totalbytes is +0x78 and the threshold is
+0x70. (hunt40/41 logged them the other way round, and the "GC disarm" of hunt40/41
wrote 0 into *totalbytes*, not into the threshold.) Stack slot = **24 bytes** {value, uint16 type at +0x08,
extra qword at +0x10} — `lua_gettop` divides (top-base) by 24 with the
0x2AAAAAAA...AB magic, `index2adr` does (idx-1)*3*8. Types: 0 nil, 1 bool,
3 number, 5 table, 6 function.

| function | RVA | evidence |
|---|---|---|
| lua_pcall(L,nargs,nres,errfunc) | **0x471B5C0** | nargs+1 stack check (line 0x3B6), MULTRET adjust, c.func = top-(nargs+1)*24 |
| f_call(L,ud) | 0x47190A0 | reads c->func/c->nresults, tail-calls luaD_call |
| luaD_pcall | 0x471ECE0 | called by lua_pcall + luaD_protectedparser |
| luaD_call | 0x471EA50 | |
| luaD_protectedparser(L,z,name) | 0x4719A80 | passes f_parser = 0x4718F60 to luaD_pcall |
| f_parser | 0x4718F60 | pushes the parsed Proto as tt=6 |
| lua_pushcclosure(L,fn,n) | 0x471B800 | luaF_newCclosure + n upvalue copies, tt=6 |
| lua_pushboolean | 0x471B790 | writes tt=1 |
| index2adr(L,idx) — the real one | 0x4719140 | 54 callers, (idx-1)*24 addressing |
| lua_tolstring | 0x471B4F0 | index2adr + luaV_tostring (0x4722310) |
| assert helper (msg,file,line) | 0x33C780 | 190 call sites |
| unlock epilogue(L) | 0x1D8E80 | 63 tail-call sites, clobbers eax |

Still missing: **lua_load / luaL_loadbuffer** (nobody inside
0x4718000..0x4722000 calls luaD_protectedparser, so the loader is just
outside) — hunt33 §2 dumps 0x4700000..0x4740000 to find it. With
loadbuffer + pcall the bot can run any Lua string:
`luaL_loadbuffer(L, code, len, name)` then `lua_pcall(L, 0, 0, 0)` — both
plain (L, char*, size, name) C calls, no frame setup needed, no page
flipping needed (the Lua library is hot = already RX).

## 4. What is still missing

1. **Names of OTHER units** — the player name is inline at 0x61C7300, but a
   unit name is produced by the `FnUnitNameFetch` path above, not stored.
   Options: remote-call that path, or accept `UnitName` via Lua once the
   executor exists. hunt30 verifies the offsets only.
2. **Level** — `+0x1E40` is the candidate (player 2, NPC 5, NPC repeats it at
   +0x1E48/+0x1E50); needs a character-sheet eyeball to be certain.
3. **Lua execution** — FnApiRegister is fully decoded and the C-API cluster
   (gettop/pushcclosure/rawset/setfield/type) is identified, but
   `FrameScript_ExecuteBuffer` / `lua_pcall` are still `0`. hunt32 attacks it
   from both sides: (a) it dumps the whole Lua C-API cluster
   0x4718000..0x4722000 so settop/pushlstring/pushstring/pcall/getglobal can
   be mapped offline, and (b) it scans the data windows for the registration
   tables to get `RunScript`'s and friends' handler RVAs, then dumps those
   bodies (live + decoded file) so the internal execute-buffer callee can be
   found. Recipe once both are known:
   `pushcclosure(L, RunScriptFn, 0)` + `pushlstring(L, text, len)` +
   `pcall(L, 1, 0, 0)` from a remote thread.
4. **Class-A records** — hunt29 found ZERO poison-flag records in that run
   (the earlier ones were transient), so treat them as an anomaly, not a
   class: always gate with `IsWorldObject` before reading a transform.
5. **Taint / hardware event** — not started; needed for protected frames.

## 5. Reader wiring status (`Styx/WoWInternals/WowBReader.cs`)

* `EnumeratePoolObjects()` — walks every family pool, returns guid + record +
  family + slot for every occupied element.
* `TryFindObjectByGuid()` / `TryGetPlayerObject()` — GUID lookup via the pools.
* `TryGetObjectTransform()` — position/facing at +0x60/+0x70.
* `IsWorldObject()` — gates class-A records out (`flags == 0xCECECECE`).
* `TryGetUnitStats()` — health/max/power/max/unit-flags from the data block.
* `ReadPlayerName()` — the inline module string.
* `DescribeSnapshot()` — one-line dump for the attach log.

## 3f. hunt34 results — decoded image, registration map, Lua bridge

Inputs this round (user-uploaded to Drive, all downloaded and analysed):

* `wowb-decoded.exe` 103,496,912 B, sha256 `8f864e73e73ad673...` — the shipped PE
  with `.text` **decrypted in place** (same section table -> normal file mapping).
* `wowb-live.exe` 140,754,944 B = exactly `SizeOfImage 0x863C000` — a raw module
  image dump (still to be fetched; the Drive download limit is 100 MiB).
* hunt33's log arrived too, and its verdict is: **the whole run is invalid because
  OpenProcess asked for 0x0428 = QUERY|VM_WRITE|VM_OPERATION**, which lacks
  `PROCESS_VM_READ` (0x10). Every ReadProcessMemory failed, hence
  `live= none` everywhere, 0 registry hits, 0 units, null lua_State. The correct
  right set is **0x0418** (or 0x043A / 0x1F0FFF). hunt34 fixes this.

### Packing model, settled

* `.rdata` / `.data`: byte-identical between shipped and decoded -> plaintext at
  rest, as established earlier.
* `.text`: 18,474 / 18,755 4 KB pages differ after decryption; the **282 identical
  pages are the still-encrypted ones** (99.5% non-zero bytes, no period, 256-value
  XOR histogram, per-page keystream). So **NOACCESS (prot=0x1) pages hold
  ciphertext** and become plaintext only when the game itself executes them.
  Never flip-and-call a cold page; call warm functions or let the game dispatch.
* `logs/lua_cluster.bin` (live bytes from hunt32) vs the decoded image at
  0x4718000: **0 / 40,960 bytes differ** -> decoded image is byte-faithful.

### Runtime registration — hunt/api_handlers.tsv (829 entries)

Every `FnApiRegister` (0xAE96A0) call site in the decoded image has the shape

```
lua_gettop(L)                        ; 0x471A960
lea r8,  [rip + handler]             ; the Lua C function
mov byte [rsp+0x20], 3               ; flags
lea rdx, [rip + name]                ; .rdata name string
lea rcx, [rbp - 0x30]                ; lua_State**
call 0xAE96A0
```

Parsing all 831 sites recovered 829 `{name -> handler RVA}` pairs, including every
API that has no static `.data` table entry:

| API | handler |
|---|---|
| UnitHealth | 0x1D63190 |
| UnitName | 0x1D7B840 |
| UnitExists | 0x1D596F0 |
| UnitGUID | 0x1D5AD60 |
| TargetUnit | 0x1B46C60 |
| RunScript | 0x136FFF0 |

(`CastSpellByName` etc. stay with their static-table values, e.g. 0x2C24040.)

### Lua C-API completeness (structural ID on the decoded image)

* `lua_pushlstring` **0x471BC20** — luaC_checkGC -> `luaS_newlstr(L,s,len)` 0x472BA70
  -> tt=4 -> `top += 0x18` -> ret.
* `lua_pushnil` **0x471BD00** (the old `FnLuaCloseCandidate` label is wrong).
* `lua_setfield` **0x471D240**, `lua_pushcclosure` **0x471B800**,
  `lua_pcall` **0x471B5C0**, `lua_tolstring` **0x471B4F0**, `lua_gettop` 0x471A960.
* `luaD_protectedparser` **0x4719A80** (L, ZIO*, name) is invoked exactly once in
  the whole binary (game loader, 0x3188BC3); the bridge does not need it.

### Phase-3 bridge (hunt34.ps1 §2)

```
pushcclosure(L, base + handlerRva, 0)   ; warm function, game uses it at startup
pushlstring (L, argPtr, argLen)         ; warm
pcall       (L, nargs, nresults, 0)     ; warm - Lua runs constantly
=> result slot at L->top - 24*n: value @+0x00, type u16 @+0x08 (3 number, 4 string)
   strings: lua_tolstring(L, -1, NULL) -> const char* -> ReadProcessMemory
```

Always through `lua_pcall` (it installs the CallInfo frame that `index2adr` needs
for argument index 1). hunt34.ps1 runs this from a remote thread with a
479-byte stub, tests `lua_gettop` (machinery), `UnitHealth("player")` (cold-handler
decrypt path) and `UnitName("player")` (string readback), and logs to
`C:\ghidra-scripts\hunt34-out.txt`.

### hunt34 run verdict (2026-10-13) — flip-then-call is dead, thread start blocked

* The `[SAN] live bytes differ` note was my own bug (16 read bytes compared with a
  13-byte constant). Live `lua_pcall` @0x471B5C0 == decoded dump 16/16 => page WARM,
  rights fine, decoded dump == live.
* **[1] CIPHERTEXT for all four NOACCESS probes.** `VirtualProtectEx` RX + read
  returns the *shipped* packed bytes and restoring the protection decrypts nothing =>
  the packer does not decrypt on a protection change. **Never flip a cold page and
  call it: the CPU would execute ciphertext.**
* **[2] the remote thread executed nothing.** `CreateRemoteThread` returned a tid,
  `WaitForSingleObject` signalled immediately, `exitCode=0x0` (not the stub's
  0x55667788) and the whole scratch block stayed zero, while the process remained
  alive and readable. So no instruction of the payload ran: either foreign thread
  starts are killed before their first instruction, or the very first instruction
  faulted harmlessly (process survived).
* `wowb-decoded.exe` is plaintext for **every** handler page we care about
  (UnitHealth, UnitName, UnitExists, CastSpellByName, RunMacro, GetNumCompanions,
  FnEntityGetGuid, MoveForwardStart) while live those pages are NOACCESS => it is a
  long-session/decrypted image. Ground truth for *bytes*, not for *current
  protection*: only a live VirtualQueryEx says warm vs cold now.

### hunt35.ps1 — execution-vector bring-up (delivered)

```
[0]  warm/cold map of all 27 pages the payload touches (VirtualQueryEx protection +
     live-vs-cipher/plaintext compare) -> shows which callees are cold RIGHT NOW
[1]  remote thread with a STAGE-stamped stub: lua_pushnil smoke test, then the real
     UnitHealth/UnitName bridge; STAGE=0x1001..0x100B is written into scratch+0x50
     after every single step, so a partial execution pinpoints where it died
[1b] CREATE_SUSPENDED + GetThreadContext probe (is the start address even installed?)
[2]  same payload on a hijacked idle game thread (SuspendThread -> SetThreadContext
     (Rip=stub) -> ResumeThread -> restore) - no thread creation at all, so a "kill
     foreign thread starts" hook cannot see it
[3]  call a still-cold handler WITHOUT touching its protection: works => the packer
     decrypts on fault (VEH, process-wide) and every registered API is reachable;
     dies => cold handlers need the game itself to execute them
[4]  cleanup - the page is freed only if no thread can still be inside it
```

Implementation notes: the x64 CONTEXT buffer (1232 B) must be 16-byte aligned, so it
comes from `VirtualAlloc`, not from a `byte[]`; the hijack path always restores the
saved context before resuming; three stubs (RT / HJ / COLD) share one builder
(`logs/mkstub35.py`) and one instrumented prologue.

### hunt35/36/37 verdict (2026-10-13) — the reads were fine, MY PATCHING was broken

Two separate bugs, both mine, both now understood from the logs:

**1. The "zeros" (hunt35/36).** `function NewBytes([int]$n) { return [byte[]]::new($n) }`
returns an array without the leading comma, so PowerShell **unrolled** it: the caller
held `Object[]`, every P/Invoke converted that to a throwaway `byte[]`, the kernel
filled the throwaway and the caller's buffer stayed zeroed - for reads, for
`VirtualQueryEx` (hence `Protect=0x0` everywhere) and for read-backs. hunt34 never hit
it because it used `New-Object byte[] $n` + `return , $buf`. Everything not dependent
on that buffer was real all along (return codes, byte counts, ntdll statuses,
thread-set context, `GetExitCodeThread`, `GetThreadTimes`). **There is no anti-cheat
shim; hunt36's inventory confirmed a clean environment.**

**2. The crash (hunt35/37) - the payload was never patched.** hunt37's log shows it
outright:
```
[RT] payload bytes=560 head=4883ec2848bbaaaaaaaaaaaaaaaa48b8
[RT] stub read-back ok=True  first16=4883ec2848bbaaaaaaaaaaaaaaaa48b8
```
`mov rbx, 0xAAAAAAAAAAAAAAAA` is still there: the stub patched its template by
searching for 8-byte marker patterns and `U64Bytes 0xAAAAAAAAAAAAAAAA` cannot bind in
PowerShell (the literal exceeds Int64; the negative Int64 will not convert to
`[uint64]`), so the find-pattern was empty and `PatchAll` did nothing **silently**.
The first store (`mov [rbx+0x50], rax`, the stage stamp) then wrote to
0xAAAAAAAAAAAAAAA8 -> access violation. That is the crash the user saw, twice - not
a defence, and not the hijack.

Consequences now enforced: patch **by offset**, assert no marker byte survives, verify
every injected page by read-back, and put a **register-free RIP-relative stamp** as the
stub's first instruction so any execution leaves a trace instead of dying on a store.

### hunt37 run data (real memory, 2026-10-13) — the map we needed

* canary `base+0 = 4d5a9000...`, `lua_pcall` byte-identical to the decoded dump;
  our injected page survives (write -> read-back -> 1.5 s -> 4.5 s, all matching).
* **warm/cold map: 21 warm / 6 cold.** WARM: the whole Lua cluster (`lua_pcall`,
  `lua_pushnil`, `lua_pushcclosure`, `lua_pushlstring`, `lua_tolstring`, `lua_setfield`,
  `lua_index2adr`, `luaS_newlstr`, `f_parser`, `luaD_protectedparser`), **UnitHealth,
  UnitName, UnitExists, UnitGUID**, `MoveForwardStart`, `GetTime`, `GetMoney`,
  `IsLoggedIn`, `GetFramerate`. COLD: `TargetUnit`, `RunScript`, `CastSpellByName`,
  `RunMacro`, `GetNumCompanions`, `FnEntityGetGuid`.
  => the read-only/unit-query bridge can be built entirely from **warm** handlers today.
* `lua_State = 0x24B210FA358` (this session), same module base 0x7FF73B080000: the
  global offset 0x7B76AB8 is right and stable.
* `[RT]` with a **verified** payload: still `wait=0 exit=0x0`, `STAGE=0` - i.e. the
  remote thread did not execute the payload even when the page was proven readable and
  writable. (With the broken payload that thread would have faulted instead.) hunt38's
  rung [1] settles this with a call that cannot fault.
* `[HJ]`: `SetThreadContext=True` took effect on all three threads; the restore
  afterwards returned `setctx=False` on all three - the signature of threads that were
  no longer in a settable state, i.e. the faulting payload. The client died during the
  section, which is why the following sections had nothing left to work with.
* `lua_gettop` @0x471A960 is a pure leaf - `((L->top - L->base)/24)`, read-only, no
  call, no GC. Verified live against the decoded dump. That makes it the ideal
  **remote-thread start routine**: with a fake `lua_State` we build ourselves
  (top - base = 123*24) it must return exactly 123, so the thread's exit code proves
  execution without touching a byte of game state.

### hunt37.ps1 — hunt35 rerun with a working read path (delivered)

Same experiment, four additions: `[CAN]` canary (buffer type must be `byte[]`,
`base+0` must be `4D 5A 90 00`, `lua_pcall` must read as plaintext - any failure
aborts before anything is written or executed and the code-execution sections are
gated on it); `[PG]` page-integrity test (write a pattern into our own page, read it
back immediately, then after 1.5 s and 4.5 s - catches a scanner that zeroes injected
memory, which would also explain hunt34's `RemoteThread` appearing to execute
nothing); `[0]` the warm/cold map; `[1]` remote thread with the STAGE counter;
`[2]` thread hijack with a read-back check of the payload page and a non-null
`lua_State` required before any RIP is moved; `[3]` cold-handler test; `[4]` cleanup.
Switches: `-SkipRT`, `-SkipHJ`, `-SkipCold`.

### hunt38.ps1 — execution ladder with a fault-proof probe per rung (delivered)

`[PG]` page integrity + fake `lua_State` (top/base differ by 123*24) ·
`[1]` **remote-thread proof**: `CreateRemoteThread(lua_gettop, fakeState)` - exit code
123 proves foreign threads run our calls, exit 0 reproduces the hunt34/37 "never ran"
pattern, and neither can corrupt anything · `[2]` remote-thread bridge (pushnil,
UnitHealth, UnitName) with a stage counter stamped after every step · `[3]` hijack
proof on a borrowed thread using a 44-byte payload that only touches our own page
(enabled with `-AllowHJ`; it is the vector to try when [1] fails) · `[4]` hijack bridge ·
`[5]` cold-handler test (`-AllowCold`, the only rung that can execute ciphertext) ·
`[6]` cleanup with retries when restoring a borrowed thread's context.
Payload patching is **by offset** with a "no marker byte survives" assertion, and every
payload is read back byte-for-byte before any thread is pointed at it.

### wowb-live.rar — the live dump, extracted (2026-10-13)

The connector wrote the 17,442,765 B RAR into the workspace; no unrar/7z exists in the
sandbox and `pip install unrar-cffi` fails to build, so it was opened with the WASM
build of unrar via node (`npm install node-unrar-js`, a 20-line script) ->
`/home/user/logs/live/wowb-live.exe`, **140,754,944 B = 0x863C000 = SizeOfImage**.

It is a **raw image of the module range**: offset == RVA, `ImageBase = 0x7FF71DFE0000`
(a *different session* from the hunt31-37 runs at 0x7FF73B080000, so every absolute
address differs - that is why a naive page census shows 7,893 pages equal to the
decoded dump and 10,863 "other" rather than a clean split).

What it already tells us:

* the globals are live: `lua_State @0x7B76AB8 = 0x1FDB15E4338` (heap, so a
  module-only dump cannot follow it), `player_name @0x61C7300 = "Brutallex"`,
  `player_guid @0x61C72F0 = 02 00 00 00 ...`, `target_guid = 0`,
  `objectMgr @0x7B7B738 -> 0x1FE6CBC7640`, `map_root @0x7B7B4F8 -> 0x1FE2D03B3E0`;
* the warm/cold split matches hunt37 exactly: `lua_gettop/…/UnitHealth/UnitName/
  UnitExists/UnitGUID/GetTime` are plaintext, while `TargetUnit/CastSpellByName/
  RunMacro/GetNumCompanions/MoveForwardStart/FnEntityGetGuid` read as **all zeros**
  (the dumper left unreadable pages zeroed - not ciphertext; the decoded dump kept the
  ciphertext instead);
* `pools @0x582D150 = 0x7FF723F7FBF0 = module+0x5F9FBF0`, but the values at that
  address do not look like a pool-pointer array (`0, heap, 1, 1, 0, 0, 0, 8, heap, …`),
  so the "pool = *(0x582D150 + fam*8)" reading from the FnEntityGetGuid disassembly
  still needs to be reconciled with the live data - open item for Phase 2.

This dump is now the best Phase-2 asset we have: it contains the runtime `.data`/`.bss`
with live pointers, which the shipped file image does not.

### hunt38 run #1 (2026-10-13) — the canary data was RIGHT, my comparison was wrong

```
[CAN] lua_gettop live = 488b512848b8abaaaaaaaaaaaa2a482b  expect 488b512848b8abaaaaaaaaaaaa2a48
```

The live bytes ARE the decoded plaintext; the constant I compared against was written as
15 bytes while `HexOf ... 16` yields 32 hex chars. An off-by-one-*chars* string compare
blocked the whole run - the same mistake I logged after hunt34. Fixed: the constants are
now the full 16 bytes, the verdict is three-way (WARM / COLD / OTHER, each printed), and
`-ForceExec` overrides the canary. `pscheck.py` now **lints** this bug class:

```
E: HexOf 16 bytes compared with a 30-char hex literal (line 3)
```

(Verified against the offending line.) This run's real data: writes/read-backs still
perfect, `lua_State = 0x1533C540358` (third launch, offset 0x7B76AB8 stable), and
`L->top == L->base` at idle - an empty stack, which is normal and not an error.

### Live-dump finding: `pools[1] == objectMgr`

Reading `0x582D150` as the pool ARRAY (the FnEntityGetGuid reading) in the live dump:

```
pool[0] = module+0x5F9FBF0        (a module-resident pool object)
pool[1] = 0x1FE6CBC7640           == objectMgr (global 0x7B7B738) exactly
pool[2..15] = 0
```

So family index 1 of the entity read path IS the object manager we already track, and
`pools[0]` is a second, module-resident container. `unknown_0x58CF2B0` and
`unknown_0x5909500` (the movement / spellbook / action-bar registry anchors from hunt30)
also resolve to module-resident pointers (0x50712D8, 0x4DA55C8), which is consistent
with them being static registration tables rather than heap state.

### hunt38 run #2 (2026-10-13) — the vector is ALIVE; the block is on the START ADDRESS

The canary fix paid off immediately:

```
[P1] tid=17788 wait=0 exit=123 (expect exactly 123 = the stack depth we faked)
[P1] VERDICT: foreign threads DO execute calls we point them at (this rung is proven)
```

`CreateRemoteThread(start = wowb+0x471A960 /* lua_gettop, module code */,
lpParameter = our fake lua_State)` returned **exactly 123**. So:

* foreign thread creation is NOT blocked;
* module code runs fine in a fresh thread;
* the same call with `start = our page` ran **nothing** ([RT] exit 0, STAGE 0) - and the
  payload was read-back-verified this time, so the page contents were correct;
* a borrowed game thread DID execute our page ([HJ] proof: stage 0x2000, `lua_gettop`
  on the fake state returned 123, context restored).

**Conclusion: the guard is on the thread's START ADDRESS being outside the image, not on
thread creation and not on our memory.** hunt34/37's "remote threads never run" was
wrong - they only ever started threads in our page.

The full bridge on the borrowed thread ([4]) executed nothing (stage 0) and the client
crashed again, without an access violation. So something about that step is fatal, and
the candidates are now narrow: (a) a foreign thread *started* outside the image gets the
process flagged and killed shortly after, (b) the same for a *game* thread redirected
into a non-image page, or (c) `lua_pcall` from a thread that is not the one the Lua
state belongs to corrupts the state.

### hunt39.ps1 — run our code through MODULE code (delivered)

`jmp qword ptr [rcx]` (`FF 21`) appears 261 times in `.text`; the script carries 40
spread candidates and picks the first whose bytes read `ff 21` live (a free
warmth/plaintext proof) and whose page is executable. Then:

```
[3] CreateRemoteThread(start = base + gadgetRva, param = page + 0x400)
    with [page+0x400] = page+0x300 and page+0x300 = `stamp 0x3000; mov eax,0x11223344; ret`
    exit code 0x11223344 proves OUR code ran while the START address was module code
[4] the same route runs the full Lua bridge; `ret` hands control back to the
    thread-start frame, so the thread's exit code 0x55667788 confirms the whole payload
[7] -TestKill: a thread STARTED at our page whose only instruction is `ret`; if the
    client dies anyway, the kill is triggered by the start address itself, not by what
    the payload did.
```

Every section ends with an `[ALIVE]` line, so a crash is pinned to the step that caused
it. If `[3]`/`[4]` succeed, Phase 3 gets its injection vector: no borrowed game threads,
no cold-page jumps, and every handler that is warm (UnitHealth/UnitName/UnitExists/
UnitGUID today) callable with normal arguments.

### hunt39 run #1 (2026-10-13) — THE GADGET VECTOR WORKS (no crash)

```
[P1] tid=18336 wait=0 exit=123                      <- fresh thread, module start, our data
[G]  gadget = 0x7932CD (page prot=0x20, bytes ff 21 verified live)
[G3] tid=5656 wait=0 exit=0x11223344  stage=0x3000  <- OUR CODE RAN
[G3] VERDICT: module-start thread -> our code. The gadget vector WORKS.
[ALIVE] after [3]: alive, 71 threads
```

`CreateRemoteThread(start = wowb + 0x7932CD /* jmp qword ptr [rcx] */,
lpParameter = page+0x400 -> page+0x300 /* our payload */)` executed our payload
(`stamp 0x3000; mov eax,0x11223344; ret`) and handed `0x11223344` back as the thread
exit code. The client survived every section - **first run with zero crashes.**

`[4]` then aborted with `payload read-back mismatch`, which was my page-layout bug: the
bridge payload is **571 bytes** (`+0x300..+0x53C`) and the jump pointer was at `+0x400`,
i.e. **inside the payload**, so storing the pointer overwrote eight payload bytes after
the write. The page map is now explicit and checked offline:

```
+0x000 scratch | +0x100 fake lua_State | +0x200 "player" |
+0x300 payload (max 576 B, ends +0x53C) | +0x600 pointer -> payload | +0x700 pattern
```

**Phase-3 consequence:** the injection vector is solved without borrowing game threads
and without executing a single cold page. Combined with the warm/cold map from hunt37/38
(`UnitHealth`, `UnitName`, `UnitExists`, `UnitGUID`, the whole Lua cluster and
`GetTime/GetMoney/IsLoggedIn/GetFramerate` are plaintext), the read-only bridge -
including unit queries with normal arguments - is buildable as soon as `[4]` completes.

Crash attribution, finally complete:
1. **hunt35/37** - the payload was never patched (`mov rbx, 0xAAAAAAAAAAAAAAAA` survived
   because the marker patch silently failed) and threads were started **in our page**, so
   the first store faulted: access violation. My bug, twice.
2. **hunt38 run #2** - the hijack ladder proved the same payload executes on a borrowed
   thread, then the full bridge on that thread executed nothing and the client died
   without an AV. Not yet explained, and no longer on the critical path.
3. Everything since the start-address insight has been crash-free, which is consistent
   with the start address (not the payload, not the memory) being what the client
   polices.

### hunt39 run #2 (2026-10-13) — the payload reached lua_pushnil and died in lua_pushcclosure

```
[G4] payload bytes=571 read-back ok, stage before=0x3000
[G4] tid=12556 wait=258 exit=0x103 (STILL_ACTIVE)
[G4] STAGE=0x1004  -> pre lua_pushcclosure
[ALIVE] after [4]: alive, 72 threads        <- client lived through the section
```

The gadget vector carried the payload through the RIP-relative stamp, the L->top read
(`top_before=0x1D2C1D0D6A8`, the real state) and a **real `lua_pushnil`** on the game's
own `lua_State` - a foreign thread mutating the live Lua stack worked. It then stopped
dead at stage `0x1004`, i.e. inside `lua_pushcclosure`, with the thread still active
after 12 s. The client crashed a while later, without an access violation.

`lua_pushcclosure` @0x471B800 does this first:

```
0x471B806  mov r9, [rcx+0x38]        ; G
0x471B81F  mov rax, [r9+0x70]        ; G->GCthreshold   (see the correction above)
0x471B830  cmp [r9+0x78], rax        ; G->totalbytes vs threshold
0x471B834  jb  0x471b841             ; totalbytes < threshold -> SKIP the collector
0x471B836  cmp byte [rcx+0x7f], 0    ; second gate
0x471B83C  call 0x472dbc0            ; luaC_step: "GCdebt = threshold - totalbytes;
                                     ;             while (debt > 0) singlestep()"
```

0x472DBC0 is a collector step driver, and a full cycle driven from a foreign thread
while the game's own threads use the Lua state is exactly how you get a hang followed by
a corrupted heap. Because the guard is an unsigned `jb` on `totalbytes < threshold`,
writing 0 into the **totalbytes** word does skip the step, but it also corrupts the
accounting (which is why that "disarm" is gone in hunt42 — the new bridge calls nothing
that allocates, so the collector never runs anyway).

### hunt40.ps1 — bridge with the collector disarmed (delivered)

* `[CAN]` now also reads `G = L->l_G (L+0x38)` and the collector counters
  (`G+0x70` totalbytes, `G+0x78` threshold, `G+0x88` debt) and says whether a
  collection is due, so the next log shows the state the call would have hit;
* `[1]`, `[2]`, `[3]` are unchanged (proofs only);
* `[4]` writes `G+0x78 = 0`, runs the full bridge through the gadget, restores the
  original threshold immediately afterwards, and **terminates the payload thread if it
  is still running** (`exit 0x103` = STILL_ACTIVE) rather than leaving it inside the
  game's Lua heap;
* `[4b]` calls `lua_pushcclosure` alone (nothing else) with the collector disarmed, to
  separate "pushcclosure hangs" from "a later step hangs";
* every section still ends with an `[ALIVE]` line.

### hunt39/hunt40 crashes — root causes and the hunt41 design (2026-10-13)

Three findings from the last two runs, all cheap to state and expensive to miss:

1. **The payload was fine; the *stub instantiation* was broken.** `hunt40.ps1` had two
   lines injected as `\n$stubHex…` (a literal backslash-n instead of a newline, from my
   own generator). PowerShell then tried to run a command named `\n$stubHexEXIT`. The
   clean copies further down meant the run still worked, but it proves the injection
   path is fragile - `pscheck.py` now **lints** for it (`E: literal \n before a
   variable`) and it is clean in hunt41.
2. **Never reuse an Add-Type class name across scripts.** The user's PowerShell session
   still had hunt39's `H38` type loaded, so hunt40's `Add-Type` failed and every
   `[H38]::` call silently used the OLD type - which has no `TerminateThread`. That is
   why `[G4] the payload thread is STILL INSIDE the game Lua call` was followed by
   `MethodNotFound` instead of a kill: **the stuck thread was never terminated**, the
   client was left with a thread wedged inside Lua, and the next section (`[4b]`) died
   with `0xC0000005`, taking the client with it. hunt41 uses a fresh type name (`H41`)
   and terminates + **stops the whole script** if a payload thread is ever left running.
3. **pushcclosure's callees are the untested part.** Its body calls `0x47190C0` and then
   `0x4720B50` - the CClosure factory, on page `0x4720000`, which *no* script has ever
   probed (the hunt37/38 map covered `0x472B000`, `0x472D000`, `0x471B000`, ... but not
   `0x4720000`). If that page is still cold, the call can only fault or wedge - and the
   GC disarm in hunt40 changed nothing, which is consistent with the wedge being in the
   factory rather than in the collector. `lua_sethook`/GC aside, `pushnil` (warm) always
   worked and `pushcclosure` never did.

### hunt41.ps1 — callee map, closure hunt, allocation-free bridge (delivered)

* `[0]` protection of **every** function the bridge calls (including the factory page
  `0x4720000`, the helper `0x4718E50`, the collector `0x472DBC0`, `assert` `0x33C780`)
  - read-only, and if any of them is cold it is named in the log;
* `[1]`/`[3]` the proven probes (lua_gettop via a fresh thread, gadget proof);
* `[2b]` a read-only scan for **existing CClosure objects**: `f` sits at `closure+0x30`,
  so the scan looks for `base+handlerRva` and validates the header type byte at
  `hit-0x28` (6 = function). This gives us real closure pointers for `UnitHealth`,
  `UnitName` and `UnitExists`;
* `[4]` `lua_pushcclosure` alone, 6 s hard timeout, terminate on wedge;
* `[4b]` **the allocation-free bridge**: it replicates the VM's own push sequence
  (`value@+0`, `tt` word `@+8`, `extra@+0x10` via `0x4718E50`, then `top += 0x18`) for a
  closure found in `[2b]`, pushes `"player"` with `lua_pushlstring`, and runs the handler
  with `lua_pcall(1,1,0)`. **No `pushcclosure`, no allocation, no collector** - which is
  the whole point, since the allocation is the one call that has never survived.

## hunt41 run (the "hunt40 payload") and the hunt42 redesign

The log the user ran was hunt41's body (its `[HDR]` line still said "hunt40" — a stale
string; fixed in hunt42). What it settled:

* `[0]` **every callee is WARM, including the factory page `0x4720000`** (which had never
  been probed) and `luaC_step` `0x472DBC0`. Cold code was never the problem.
* `[4]` `lua_pushcclosure` **wedged again — with `G+0x78` (totalbytes) forced to 0**, so
  the step was skipped and it still hung: the wedge is inside pushcclosure's own body.
* `[2b]` the CClosure scan found **0 candidates** — as it turns out, because of my bug
  (below), not because the closures are missing.
* `[4b]` never ran, and `[4]`'s `TerminateThread` dropped the client from 72 threads to
  1: **the client crash was the termination**, exactly like hunt40. hunt42 therefore
  never terminates a payload thread — a wedge means "restart the client", full stop.

### lua_pushcclosure 0x471B800, read end to end (live dump, offset == RVA)

```
0x471B806  mov r9,[rcx+0x38]        ; G
0x471B830  cmp [r9+0x78],[r9+0x70]  ; totalbytes vs threshold -> GC step (skipped)
0x471B868  cmp nup,(top-base)/24    ; api check
0x471B889  call 0x47190C0           ; getcurrenv(L)
0x471B897  call 0x4720B50           ; factory(L, nup, env)  <-- allocation
0x471B8A7  mov [rax+0x30],rdi       ; closure->f     (rdi = the C function)
0x471B9xx  ->  copy upvalues, then push the slot
```

* `0x47190C0 getcurrenv(L)`: if `L->ci != L->base_ci` return `curr_func(L)->c.env`
  (`closure+0x20`); otherwise return `L->[0x90]` — which is therefore **the globals table
  pointer (`l_gt`) at `L+0x90`**.
* `0x4720B50`: `size = 0x38 + nup*24`, `0x472FB50` = **`luaM_realloc_`**
  (`... call qword [G+0x10]` with `rcx = [G+0x18]` — i.e. **G->frealloc/G->ud**, the one
  call that can hang), then `0x472D770` = `luaC_link`, then `c.env` at `+0x20`,
  `isC` byte at `+0x12`, `nup` byte at `+0x13`.
* `luaC_link` writes `obj->next = G->rootgc` at `+0x00`, `obj+0x10 = tt` (byte),
  `obj+0x11` = the white bits. So the **GC object header is `next@0, tt@+0x10,
  marked@+0x11`** — *not* `tt@+0x08`.
* That is precisely why hunt41's `[2b]` found nothing: it validated `hit-0x28`,
  i.e. `closure+0x08`, against `{6,8}`. The test belonged at `closure+0x10`.

### The way around it: lua_getfield

The bridge does not need pushcclosure at all — the real closure is already in the
globals table:

* `0x471A370` **`lua_getfield(L, idx, k)`** — 3 args, NUL-terminated key: `index2adr`,
  `strlen`, `luaS_newlstr`, `luaV_gettable(L, t, &key, L->top)`, `top += 0x18`.
  (`0x471A500` is the same function with an explicit length: `(L, idx, s, len)`.)
* `index2adr(L, -10002)` (GLOBALSINDEX `0xFFFFD8EE`) returns `L+0x90`, the globals table.
* `0x471A370` is called by the client itself (125 call sites, including the API
  registration code), so it is a normal, hot path.

`lua_getfield` + `lua_pushlstring` (`0x471BC20`) + `lua_pcall` (`0x471B5C0`) allocate
**nothing**: the keys ("UnitHealth", "player", ...) were interned by the client at
startup, so `luaS_newlstr` just returns the existing TString. No `pushcclosure`, no
allocator, no collector.

### hunt42.ps1 — the allocation-free bridge (delivered)

* `[0]` read-only: `G->frealloc`/`G->ud`, the globals tvalue at `L+0x90`, `L->top`
  sampled 3x (is the client running Lua right now?), and the callee/protection map;
* `[1]` remote-thread proof (`lua_gettop` on a page-local fake state -> 123);
* `[2]` gadget hunt; `[3]` gadget jump proof, then the 379-byte payload;
* `[4]` **probe**: `lua_getfield` alone (both the 3-arg and the length-taking form), and
  a second name — the slot must come back `tt=6` (function);
* `[5]` **the bridge**: `UnitHealth("player") = getfield + pushlstring + pcall`;
* `[6]` a read-only snapshot through the same bridge: UnitHealth/UnitExists/UnitLevel/
  UnitClass/UnitName/UnitGUID/GetTime/GetMoney/IsLoggedIn (one thread each, strings are
  dumped out of the TString);
* `[7]` `-Diag`: two raw probes that settle the pushcclosure mystery —
  `luaS_newlstr` with a *fresh* string (forces an allocation) and the CClosure factory
  itself `(L, nup=0, env=globals)`;
* `[8]` summary, page cleanup, and `upload C:\ghidra-scripts\hunt42-out.txt`.

The payload is position-independent (it takes `rcx = &block` from the gadget and reads
L, the callee addresses and the strings out of the block, so the *code* never changes),
it is assembled with keystone and re-disassembled to verify, and it is `sub rsp,0x28`
aligned. Stage stamps: `0x4001` entered, `0x4002` getfield returned, `0x4003` before
pushlstring, `0x4004` before pcall, `0x4005` results captured, `0x4020` COMPLETE,
`0x4010/0x4011` raw calls; thread exit code `0x4200CAFE` = ran to the end.
`BridgeOne` restores `L->top` afterwards, but only when the observed top matches the
expected one (otherwise it warns instead of touching the stack).

## hunt42 run + hunt43: the client's call-site protector (the real reason pcall "wedged")

hunt42 finally isolated the blocker. Its log:

* `[0]` `G->frealloc = module+0x7614E0` (the client's own allocator), `G->ud = 0x251D774A730`;
  `L+0x90` (`l_gt`) = a table (tt=5); the Lua cluster warm; `L->top` stable over 300 ms
  (the client was idle in Lua, so the calls do not race with a running script).
* `[4]` **`lua_getfield` works from a foreign thread** - both the 3-arg form (`0x471A370`)
  and the length-taking twin (`0x471A500`) pushed `tt=6` (a real CClosure), for UnitHealth
  *and* GetTime. So the globals-table lookup and `lua_pushlstring` are fine.
* `[5]` `UnitHealth("player")` wedged at stage `0x4004` = **inside `lua_pcall`**, and the
  payload thread (tid 15124) later died with
  `ACCESS_VIOLATION at 0x7FF73B259115 = module+0x1D9115, memory at 0x0` - a *deliberate*
  null dereference (`movzx eax,[rbx]` with `rbx = 0`).

### What that address is

`0x1D9115` is inside the client's protector function at `0x1D8E80`. Every VM call entry
point hands that function its own return address:

```
0x47190A0 f_call               ... jmp  0x1D8E80   (rcx = f_call's own return address)
0x471EA50 luaD_call            ... add rsp,0x30 / pop rdi / jmp 0x1D8E80
0x471B5C0 lua_pcall            ... mov rcx,[rsp+0x58] (its caller's return address)
                                   call 0x1D8E80
0x471ECE0 luaD_pcall           ... call 0x1D8E80
0x471F920 luaD_rawrunprotected ... call 0x1D8E80
(plus 0x4719093, 0x47196D1, 0x4719BA7, 0x471BA7E, 0x471CE84, 0x472BFD9)
```

and `0x1D8E80` **validates that address**:

```
0x1D8EF7 mov eax, dword [rip+0x5e17c33]   ; base+0x5FF6B30: a descriptor
0x1D8EFF test rax,rax / jne 0x1D8F63      ; 0 -> skip the range check
0x1D8F63 mov rcx,[rax+8]                  ; (as read: {size, base} -> window [base,base+size))
0x1D8F67 cmp rdx,rcx  / jb  0x1D9090      ; X < base            -> REJECT
0x1D8F70 add rcx,[rax] / cmp rdx,rcx / jae 0x1D9090  ; X >= end -> REJECT
0x1D8F7C movzx r8d,[rdx-5] / cmp r8b,0xe8 ; a 5-byte E8 call   -> ACCEPT (typecode 0xDB)
0x1D8F91 cmp byte [rdx-7],0xff ...        ; or a FF /2 call     -> ACCEPT (other codes)
0x1D8FD3..0x1D9038                        ; classification, then reversible transforms
0x1D903C call 0x1DE920 / 0x1D9051..       ; obfuscated transform of the typecode byte
0x1D9068 mov rax,[rip+0x5e17a99]          ; base+0x5FF0B08
0x1D9107 xor dl,[rax+0x50b] / cmp dl,0x1e ; the final canary
0x1D9115 movzx eax,[rbx]                  ; rbx = 0 -> the null dereference we saw
```

The game's own callers satisfy it trivially: `luaD_call` is entered by a 5-byte `E8` from
`f_call`, `f_call` by the 7-byte `ff 94 24 ..` indirect call inside `rawrunprotected`, and
`lua_pcall` by its callers inside the module. **Our payload's `ff d0` (`call rax`) issued
from a `VirtualAllocEx` page is neither inside the module nor one of the two accepted
shapes**, so the reject path runs, the typecode byte is never set, the canary comparison
fails and the protector null-dereferences. This is also why everything else worked:
`lua_getfield`, `lua_pushlstring`, `lua_gettop` and the CClosure factory are *not* hooked -
only the call-machinery entry points are.

### hunt43: the trampoline

We cannot move our page into the module, so we make `lua_pcall` see a module return address.
`X` must satisfy `byte[X-5] == 0xE8` (the accepted direct-call shape) and `byte[X] == 0xC3`
(so control comes straight back to us). **`base+0x471A983` is exactly that**: the real `ret`
that ends `lua_gettop`, with the `0xE8` byte five before it (inside the preceding
`shr rax,0x3f`).

```
sub rsp, 8                          ; a normal call enters a function with rsp%16 == 8
lea r10, [rip+tramp_back] ; push r10
mov r10, [rbx+0x58]                 ; X = base+0x471A983
push r10
jmp lua_pcall                       ; the protector sees module X + an E8 shape -> accept
tramp_back: add rsp, 8              ; rax = the pcall status
```

hunt43 also logs the two protector globals (`dword[base+0x5FF6B30]`, the descriptor, and
`qword[base+0x5FF0B08]` plus its `+0x50B` canary byte), verifies the trampoline live and
falls back to scanning the first 1 MB of the Lua cluster for another `E8 ..` + `ret` site.
`[4]` (getfield probe), `[5]` (the bridge), `[6]` (snapshot) and `[7]` (`-Diag` allocation
probes) are otherwise unchanged from hunt42.

If `[5]` still wedges at `0x4004`, the next step is to bypass instead of satisfy the check:
patch the validator entry (`0x1D8E80`, 3 bytes -> `xor eax,eax; ret`), run the bridge, and
restore those 3 bytes immediately.

## hunt43 run + hunt44: the trampoline passes the shape check; localise the wedge by sampling RIP

hunt43 run (fresh session, `base 0x7FF73B080000`):

* `[PT] dword[base+0x5FF6B30] = 0x0` - the descriptor the range check would use is 0 in this
  build (it is also 0 in the dump, so it is not a session artefact), and
  `qword[base+0x5FF0B08] = 0x7FF913E210A0` (a heap pointer) with byte `+0x50B = 0x43`.
* `[PT] candidate 1 ACCEPTED`, `trampoline mode = 1`, `X = base+0x471A983`.
* `[A1]/[A2]/[A3]` getfield still returns `tt=6` - the lookup path is solid.
* `[B1]` `UnitHealth("player")` **wedged again at stage 0x4004** (STILL_ACTIVE after 6 s), but
  **there was no access violation this time** - the trampoline changed the protector's
  decision. The client still died afterwards, and this time *we* caused it: the script then
  freed the stub page while the wedged thread was still executing there (`[CLEAN] page
  free=True`). Fixed: once anything has wedged, hunt44 leaves the thread *and* the page alone.

### The wedge is therefore *after* the shape check, and needs measuring, not reading

Everything the protector does between the shape check and its return is either ciphertext in
the dump (unexecuted blocks) or depends on globals we read as zero, so further static reading
is a dead end. hunt44 measures instead:

* **stuck-thread RIP sampling** - `SuspendThread` + `GetThreadContext` (CONTEXT layout: `Rip`
  at `+0xF8`, `RFlags/segments` at `+0x38`, `Rax` at `+0x78`, `Rcx` at `+0x80`, `Rsp` at
  `+0x98`), three samples 250 ms apart, printing RIP (translated to `module+0x...`), RSP, six
  registers, the 16 bytes at RIP and the 24 bytes at RSP.
* **fine-grained stage stamps**: `0x4004` before the call setup, **`0x4004A` the instant before
  `jmp lua_pcall`**, **`0x4004B` in the trampoline continuation** (i.e. lua_pcall returned),
  `0x4005` results captured, `0x4020` complete.
* `[5]` the simplest possible call first: **`GetTime()`** (no arguments, no unit lookup, no
  allocation). If this wedges, the machinery is the blocker rather than the handler.
* `[7]` the **allocation probe**: `luaS_newlstr` with a brand-new string - settling whether a
  foreign thread can allocate at all (the pushcclosure wedge of hunts 39-41).
* `-Start <n>` so a rerun after a wedge can skip the sections that already wedged.

Also fixed in hunt44: every script since hunt42 wrote its log to `hunt41-out.txt` (the rename
chain never touched that string) and hunt41's header left a second `param(...)` line behind,
which PowerShell reported as "the term 'param' is not recognized" at the top of every log.

## module+0x1D8E80, read from the decrypted page: a tail call, not an entry check

The live dump has the page holding `0x1D8E80` decrypted, which settles what the routine really
is. **Correction (later the same day):** "the decoded exe does not" was a file-offset artefact
- `wowb-decoded.exe` is a normal PE (`.text` VA `0x1000`, raw pointer `0x400`, so
`offset = RVA - 0xC00`), and at the *correct* offset that page is plaintext and byte-identical
to the live dump. See the section at the end of this file. The listing below is unaffected:

```
0x1D8E80  40 53                  push rbx
0x1D8E82  48 83 EC 20            sub rsp, 0x20
0x1D8E86  48 8B D1               mov rdx, rcx          <- rcx is the checked address
0x1D8E89  0F 1F 80 00 00 00 00   nop dword ptr [rax]   <- obfuscated region starts here
...
0x1D8EF7  8B 05 33 7C E1 05      mov eax, [base+0x5FF6B30]   ; the range descriptor (0)
0x1D8EFD  33 DB                  xor ebx, ebx                ; rbx = 0 from here on
0x1D8EFF  48 85 C0               test rax, rax
0x1D8F02  75 5F                  jne 0x1D8F63               ; descriptor != 0 -> range check
...
0x1D8F7C  44 0F B6 42 FB         movzx r8d, [rdx-5]          ; the byte before the address
0x1D8F81  41 80 F8 E8            cmp r8b, 0xE8
0x1D8F87  C6 44 24 38 DB         mov byte [rsp+0x38], 0xDB   ; `call rel32` shape
0x1D8F91  80 7A F9 FF            cmp byte [rdx-7], 0xFF      ; `FF /2` shapes
0x1D8F9C  44 0F B6 4A FA         movzx r9d, [rdx-6] / and 0x38 / == 0x10 or 0x18
... also [rdx-6], [rdx-4..-3], [rdx-3..-2], [rdx-2..-1]
0x1D9033  C0 C8 02               ror al, 2
0x1D9041  E8 DA 58 00 00         call 0x1DE920               ; transform A
0x1D9063  E8 C8 58 00 00         call 0x1DE930               ; transform B
0x1D9068  48 8B 05 99 7A E1 05   mov rax, [base+0x5FF0B08]   ; the key object
0x1D9074  32 88 76 03 00 00      xor cl, [rax+0x376]
0x1D9082  0F 94 C0               sete al / add al, 0x82      ; 0x82 or 0x83
0x1D90E4  E8 27 01 00 00         call 0x1D9210               ; transform C
0x1D90F9  0A D0                  or dl, al                   ; nibble swap
0x1D9107  32 90 0B 05 00 00      xor dl, [rax+0x50B]         ; the key byte (0x43 in game)
0x1D910D  80 F2 DE               xor dl, 0xDE
0x1D9110  80 FA 1E               cmp dl, 0x1E
0x1D9113  75 03                  jne 0x1D9118               ; != 0x1E -> plain epilogue
0x1D9115  0F B6 03               movzx eax, [rbx]           ; == 0x1E -> read through NULL, with rbx = 0
0x1D9118  48 83 C4 20            add rsp, 0x20
0x1D911C  5B                     pop rbx
0x1D911D  C3                     ret
```

Facts that matter:

* The check runs on the value in **`rcx`**, and every one of the 25 `E8` call sites loads it from a
  **stack slot**, not from the stack pointer: `mov rcx, [rsp+0x48]` (0x4719AB9), `[rsp+0x58]`
  (0x471B242, 0x471B727), `[rsp+0x28]` (0x471B31E, 0x472C53F), `[rsp+0x78]` (0x471EDF0,
  0x471F8C0, 0x471F8ED), `[rsp+0x158]` (0x471F9A1), `[rsp+0x38]` (0x48A1A97). So the routine is
  handed **the caller's saved return address** - it is a return-address integrity spot check.
* The call is the **last instruction before each function's epilogue** (`mov eax, ebx` /
  `mov eax, edi` / `mov eax, esi` / `mov eax, [rsp+...]`, then `add rsp`, `pop`s, `ret`). The
  byte it computes in `eax` is **discarded by every single call site** - it is not a type tag and
  not an input to anything else.
* The fatal branch is the **`dl == 0x1E`** side, which dereferences `rbx`, and `rbx` is zeroed at
  0x1D8EFD - that is the `0x1D9115` access violation hunt42 hit (`rbx=0`). It is one arm of an
  obfuscated branch whose outcome depends on the shape byte and on the key bytes at
  `[[base+0x5FF0B08]+0x376]` and `[[base+0x5FF0B08]+0x50B]`, i.e. on runtime state - not a
  "reject the foreign call site" test.
* Since the call is a *tail* call and its result is unused, **overwriting the entry with
  `31 C0 C3`** (`xor eax,eax; ret`) is safe: the caller's stack is untouched (the prologue's
  `push rbx`/`sub rsp,0x20` never happen, so the plain `ret` pops exactly the caller's return
  address and no epilogue is needed). hunt44 exposes this as the opt-in `-PatchGuard` route: it
  patches the 8 bytes, reads them back, prints `was/now`, and restores the original bytes in
  `UnpatchGuard` before the cleanup section.

### The allocator chain (why a foreign thread may be the problem)

```
luaM_realloc_ 0x472FB50  ->  call qword [G+0x10]        ; rcx = G->ud  (the game heap object)
      frealloc = module+0x7614E0
0x7614E0  test rdx,rdx / jne ...        ; ptr == 0 -> allocate, else realloc
0x7614E5  add [base+0x7FF7B78-ish], r9  ; alloc/free byte counters
0x7614EC  jmp 0x4146F00                 ; tail jump into the game's own memory manager
0x4146F00 lea r10, [base+0xD29400]      ; 14 size-class thresholds (dwords), scanned linearly
0x4146F40 mov eax,[r11]; cmp r15,rax; jbe -> class index
0x4146F68 mov rcx,[heap + class*8]      ; bucket head from the ud object
0x4146F6C mov rax,[rcx+0x18] / mov [rbx],rax / mov [rcx+0x18],rbx   ; pop a free block
0x4147029 ... slow path -> call 0x46A6D20 (block from the OS pool), then memcpy 0x48E2B70
```

The fast path has **no lock, no `lock` prefix and no `pause`**; the only TLS uses anywhere in
`0x4146F00..0x4147200` are three `mov rax, gs:[0x58]` (the TEB TLS array) at 0x4147865, 0x4147905
and 0x4147E95. The whole Lua cluster (`0x4719000..0x4730000`) contains **zero `FF 15` IAT
calls** - it never waits on an OS object directly. Both matter for reading hunt44's output: a
wedge inside the allocator is a *logic* wedge (foreign thread / stale heap object), not a
blocked `WaitForSingleObject`.

## hunt44's two runs: there was never a wedge - the trampoline returned to address 0

Both hunt44 runs (plain and `-PatchGuard`, sent in as logs plus `client_crash_report.txt`)
behaved identically, and the crash report for the `-PatchGuard` run
(**pid 17960, tid 18376 - exactly the "wedged" `[B0]` thread**) named the fault:

```
RIP=0000000000000000  RBP=0   RCX=00007FF73F79A983 (= X)   R13=00007FF73F79B5C0 (= lua_pcall)
RBX=000001BB4C3B0600 (= our block)  R14=000001BABFAA8358 (= lua_State)  R15=L->top
RDI=0 (lua_pcall's status)  RSP=000000AC864CF718
ACCESS_VIOLATION - the instruction at 0x0 referenced memory at 0x0, "could not be executed"
```

`RDI = 0` means **lua_pcall ran and returned 0**; the fault is our return path. The payload
pushed its continuation at `[A+8]`, where `A` = rsp at lua_pcall's entry - and `[A+8]` is
lua_pcall's *shadow space*, which lua_pcall's own prologue owns:

```
0x471B5D5   mov qword ptr [rsp+0x60], rbp      ; rsp+0x60 == A+8  -> the continuation slot
0x471B708   mov rbp, qword ptr [rsp+0x60]      ; and restored from the same slot
```

so the slot was overwritten with `rbp` (0 on a fresh payload thread), the `ret` at `X`
popped 0, and the thread executed address 0. Everything that looked like a "wedge for
6-8 s" was the client's crash handler parking the faulting thread and killing the process
several seconds later - which is also why hunt42's thread "died at 0x1D9115" a few seconds
after its stage stopped, and why `GetExitCodeThread` kept reporting `STILL_ACTIVE`.

Consequences for the payload (hunt45):

* rsp is forced with `and rsp, -16` and restored from `r12` (callee-saved, spilled and
  restored by lua_pcall through `[A+0x18]`, so it survives the call).
* **mode 0** - the validator is patched out (`-PatchGuard`): an ordinary `call r13`. The
  return address it pushes lives in our own page, which only the (patched) validator would
  have objected to. ABI-correct by construction.
* **mode 1** - validator live: keep the two-hop return, but set `rbp` to the continuation
  before the call, because lua_pcall spills `rbp` into exactly the slot the `ret` at `X`
  pops. That is what keeps the slot alive.
* The script chooses the mode (`-PatchGuard` -> 0, otherwise -> 1) and prints which.

Three PowerShell bugs in hunt44 also cost it diagnostics, all fixed in hunt45: `[1]` died
on `IntPtr + [uint64]` (so the gettop proof never ran), `SuspendThread`'s failure value
`0xFFFFFFFF` came back as `-1` and threw in `if ($sr -eq [uint32]0xFFFFFFFF)` (so the RIP
samples never printed at all), and `$res.pcall = [int]($pv -band [uint64]0xFFFFFFFF)`
threw `InvalidCast` on the `-999` sentinel.

## hunt45 - RUN, ALL GREEN (the call machinery works)

`hunt45-out.txt` settles phase 3's blocking question:

```
[B0] GetTime() = 205448.515        <- mode 1 trampoline, one injected thread
[B1] UnitHealth("player") : <tt=259 0x4052400000000000>      (= 73.0)
[D1] luaS_newlstr(L,"zzq44ccdc8d9d") -> returned non-zero    (allocation from our thread WORKS)
[S1..S11] 11/11: GetTime 205448.682, UnitHealth(player) 73, UnitExists(player) true,
          UnitLevel(player) 2, GetMoney() 24, IsLoggedIn() true, UnitExists(target) false,
          UnitClass(player) "HUNTER"/"Hunter" (2 results), UnitName(player) "Zero",
          UnitGUID(player) "Player-70-00000005"
[SUM] execOk=True gadgetProof=True payloadOk=True getfieldProbe=True tramp=1 callOk=True
      snapshotOk=11/11 stuck=False      - no crash, 70 threads before and after
[7] allocation probe: "interning a NEW string allocates fine from our thread"
```

So `getfield(globals,name)` + `pushlstring(arg)` + `lua_pcall` through the mode-1 trampoline
is a working, general "call a read-only WoW API" bridge. Two facts fall out of it:

* **The type word carries a value flag.** `UnitHealth` returned `tt=0x103` while `GetTime`
  returned `tt=3`; `0x103 & 0xFF = 3` = number and the eight bytes were a double `73.0`. The
  high byte marks the value (secure/unit data) - a reader must compare `tt & 0xFF`. hunt46's
  `TTName`/`RenderCall`/`MailCmd` do that.
* **Allocation is not the blocker.** hunt40/41's `pushcclosure` wedge (stage `0x1004`, all
  callees warm, block intact) was never "a foreign thread may not allocate" - hunt45 interned
  a brand-new string from the same kind of thread. The difference that is left is the
  collector: those runs had `totalbytes >= threshold` (hunt41 had written the threshold), so
  `luaC_checkGC` inside `pushcclosure` ran `luaC_step` from a thread with no CallInfo frame.
  The bridge never does that: `lua_pcall` always hands the call a real frame. hunt46 logs the
  collector counters before and after its loop for exactly this reason.

`[1]` (the fake-state gettop proof) is the last PowerShell casualty: `[uint64]$fake` on an
`IntPtr` throws (`Cannot convert ... System.IntPtr to System.UInt64`), so the fake state was
never written. hunt46 uses `$fake.ToInt64()`.

## hunt46 - the bridge goes resident (one thread, a mailbox, many calls)

hunt45 needed one injected thread per call. The executor/reader cannot work that way, so
hunt46's payload lives at `page+0x800` and serves commands from a mailbox at `page+0x1000`:

| field | meaning |
|---|---|
| `CMD @+0xC8` | 0 = idle, 1 = run the block, 0xFF = exit |
| `SEQ @+0xD0` / `ACK @+0xD8` | per-command handshake; the payload copies SEQ to ACK only once the results are complete |
| `calls @+0xE0` / `idles @+0x118` | counters (throughput / poll rate) |
| `tickFn @+0xF8`, `sleepFn @+0x100` | `kernel32!GetTickCount64` / `kernel32!Sleep`, resolved by the driver and then verified byte-for-byte against the client's own mapping (kernel32 is a KnownDLL, so the address is shared) |
| `deadline @+0x108` | failsafe: whatever happens to the driver, the payload exits by itself |
| `spin budget @+0x110`, `sleep ms @+0x124` | fallback when no clock/sleep resolves |

Stage stamps: `0x4100` resident/waiting, `0x4101` command taken, then hunt45's
`0x4002..0x4020` inside the call, `0x40FF` exiting. The block fields `+0x00..+0xC0` are
hunt45's layout unchanged.

Why this is the right shape for the port: it is what `Executor64`/`DX11PresentHook64` need on
the C# side - one place to write a command, one place to read the result - and it removes
per-call thread creation from the hot path. The driver-side numbers printed by `[6]`/`[7]`
(latency percentiles, calls/s, and the payload thread's own CPU from `GetThreadTimes`) are
the input for the reader's poll interval.

Safety carried over from hunts 39-45: the page is never freed while a payload thread might
live in it, `TerminateThread` is never used, and the payload has an exit path from every
state (command or deadline).

### hunt46 RESULTS - and three readings that look like failures but are not

```
[M] tid=19864 stage=0x4100 (RESIDENT - waiting for a command)  exit=259
[M1] GetTime() = 206185.77   latency 3.19 ms
[L] 180 commands through ONE thread in 0.73 s -> 244.9 calls/s
[L] latency min=0.19 p50=1.06 avg=1.1 max=14.92 ms  failures=0  top_after mismatches=0
[L] collector: not due  (the bridge never forced a GC step)
[I] idles 825 -> 2127 = 651 polls/s; payload thread CPU 0.0156 s / 2 s = 0.78 % of one core
[F] unused failsafe thread exited by itself after 1.68 s: exit=0x4200CAFE stage=0x40FF
[X] exit command: 0 s, exit=0x4200CAFE, served 181 commands
[CLEAN] page free=True       - no crash, 71 -> 70 threads
```

* `[P1] exit=133` is a PASS. The fake state is (top = fake+0xC88, base = fake), so
  lua_gettop returns (top-base)/24 = 0xC88/24 = 133.67 -> 133. The "expect exactly 123"
  text came from hunt44 and was stale; the `.ToInt64()` fix is what made the proof run
  at all.
* `[L] payload calls counter=181 (expect 180)`: 180 from the reader loop plus the one
  from `[5]`. The payload's counter is exact - one command, one increment.
* `[L] payload thread CPU: 0 s = 0 us/command`: `GetThreadTimes` advances in ~15.6 ms
  ticks, so 180 calls in 0.73 s rounded to zero (< 87 us per call). The idle figure in
  `[7]` (one tick per 2 s) is the meaningful number.

## The 2026-10-13 crash (build 70291, pid 8276): the race, not the machinery

287 commands and 41 rounds of live reads (GetTime, health, level, class, GUID) through the
resident bridge, then one call stopped at stage `0x4004A` (the stamp set just before the
trampoline jump into `lua_pcall`) and the client died.

The crash report says more than the summary line does:

```
Exception: 0xC0000028 (invalid/unaligned stack during an unwind) at ntdll+0x12D2F
ThreadID: 9784 [Current Thread]      R10 = module+0x472AEC7   (inside the Lua cluster)
XMM1 = 00007FF73B7E8A50_00000202133A9358   (a module pointer + the lua_State we were using)
```

* `0xC0000028` is **ours**: the trampoline lives in a page with no `.pdata`, so when the
  original fault happened the client's unwinder could not walk our frame and reported a bad
  stack instead. The real fault is hidden, but the registers say where it was: inside the Lua
  cluster, holding the lua_State.
* The cause is structural: **Lua in this client runs on one thread, and that thread mutates
  the lua_State hundreds of times per second**. Our payload is a foreign thread, so a command
  that lands while the client is mid-Lua is a data race on the same state - `L->top`, the stack
  slots above it, and any `luaD_reallocstack` the client performs while we hold cached
  `TValue*` in `r13`/`r15`. hunt45 (180 calls in 0.73 s) and hunt46 (244 calls/s) were simply
  lucky: the same race existed, the window is smaller than the exposure.
* So an external bridge is safe only while the client is **not** running Lua, or while the
  client's Lua thread is stopped. hunt46 measured the game's Lua as quiescent at the moment it
  sampled (`L->top == L->base`, stable 3x) - which is why it survived - but nothing enforced it.

The managed bridge (0.2.0) now enforces it: identify the Lua thread by sampling instruction
pointers, wait for `L->top == L->base` and `L->ci` at its resting value, suspend that thread,
re-check, then run the command and resume from a `finally`. See LUA_BRIDGE_64.md.

## 0.2.0's first run: the guard refused, and that is the safe direction

`LuaBridgeProbe.exe --seconds 60 --verbose` on build 70291 (pid 19516) got as far as
`payload : 641 bytes, resident` and then stopped with

```
guard       : freezes tid  (best guess: busiest module thread) for the duration of every call
[error] InvalidOperationException: the client's Lua thread was not identified ...
```

The blank tid says `FindLuaThread()` picked no thread at all: either no thread handle could
be opened or every sample landed outside module code, and the old code then had nothing to
freeze and said nothing about why. The bridge refused to call instead of racing — the right
outcome — and 0.3.0 turns the failure into data: `ThreadReport` is a per-thread sampling
table (tid, cpu delta, context reads, module hits, lua hits, first RIPs, Win32 errors), the
pick now also has CPU time as an independent signal (the frame thread is the busiest thread
in the process, and the frame loop is what runs Lua), and `--lua-thread N` freezes a thread
picked by hand.

Also worth remembering from the crash report: build 70291 has a `WatchdogThread`. Keep every
freeze to a few milliseconds (the guard caps a command at 1 s frozen, and commands measure
~1.5 ms).

## 2026-10-13 (later): `wowb-decoded.exe` is the complete plaintext image - the "ciphertext
## there" reading was a file-offset mistake

Two same-day notes in this file say `wowb-decoded.exe` still holds ciphertext on some pages
("has ciphertext at the Lua RVAs", "that page is ciphertext there"). Both came from reading
the file **at offset == RVA**, which is only true for the live dump. The decoded exe is a
normal PE file: `.text` has `VirtualAddress 0x1000` and `PointerToRawData 0x400`, so

```
file offset = RVA - 0xC00        (.text;  -.rdata: -0x800,  .data: -0x2000)
```

Read at the right offset, the decoded exe is byte-faithful everywhere, and it is *strictly
better* than the live dump for offline work:

| RVA | decoded (mapped) | live dump | verdict |
|---|---|---|---|
| `0x471B5C0` lua_pcall | `4053574883ec484c8b51288d42014c2b` | same | match |
| `0x471A370` lua_getfield | `48895c24084889742410574883ec4049` | same | match |
| `0x1D63190` UnitHealth | `488bc4554157488d68a14881ecb80000` | same | match |
| `0x7614E0` frealloc | `4885d2750c4c010d8c654107e90f5a9e` | same | match |
| `0x1D8E80` protector | `40534883ec20488bd10f1f8000000000` | same | match (the note above is wrong) |
| `0x136FFF0` RunScript | `40554157488bec4883ec784533ff4889` | `00…00` | **live is NOACCESS, decoded has the code** |
| `0x1B46C60` TargetUnit | `405553488d6c24e84881ec1801000048` | `00…00` | same |
| `0x2C24040` CastSpellByName | `4055535657415541564157488dac2450` | `00…00` | same |
| `0x2CC2DB0` RunMacro | `48895c2408574883ec20488bd9e83efe` | `00…00` | same |
| `0x23C8BE0` MoveForwardStart | `40554157488bec4883ec284889742450` | `00…00` | same |

So the two artefacts answer two different questions and both are needed:

* **decoded exe = the bytes.** Whole image, including every handler the live process maps
  NOACCESS (the cold ones), so cold functions *are* analyzable offline - the earlier "cold
  pages cannot be decoded offline" is wrong. 282 `.text` pages still hold ciphertext in the
  decoded exe (hunt34's packed-vs-decoded count) - those, and only those, need the client.
* **live dump = the current protection.** A `00…00` page in the live dump means NOACCESS
  *right now*; `logs/lua_cluster.bin` (live bytes) equals the decoded exe at `0x4718000`, so
  the live dump is not doing anything the decoded exe does not.

Practical effect: point `rea` / Ghidra at **`wowb-decoded.exe`** (Ghidra maps the sections
itself, no offset arithmetic), and use `ReadProcessMemory` against the live client only to ask
"is this page decrypted now?" - which is what the `--call` cold-page gate in
`Tools/LuaBridgeProbe` does (0.4.0, see `LUA_BRIDGE_64.md`).

## 0.3.0 and 0.4.0 normal runs: the refusal had a second cause - `Start()` never searched

Two more runs of the probe (0.3.0 and 0.4.0, same pid 19516, same minute as a `--check` that
had found tid 13524 cleanly) printed the old blank `guard : freezes tid` and refused:

```
bridge page : 0x21D31750000   gadget 0x7FF73BC515E3
payload     : 641 bytes, resident, waiting for commands
guard       : freezes tid  (best guess: busiest module thread) for the duration of every call
[error] InvalidOperationException: the client's Lua thread was not identified ...
```

Not a sampling problem this time - a **call-site** problem. After the 0.3.0 rewrite of the
search, `FindLuaThread()` was only reachable from `Verify()`, and `Verify()` is what `--check`
calls. `Start()` went from the gadget check straight to `VirtualAllocEx` +
`CreateRemoteThread`, so a normal run reached its first `Call()` with `_guardThreads` empty
*by construction*, and every such run must refuse regardless of how good the sampling is. The
missing table in those logs is the tell: `_threadReport` was empty, i.e. the search had never
run in that process. (It also means 0.2.0's "Start() finds the thread first" note was wrong
for anything after the rewrite.)

0.5.0: `Start()` calls `EnsureGuardThread()` before it writes anything and throws with the
sampling table + the three fixes if the pick is still empty; the guard calls the same helper as
a backstop for direct users of the class; `Verify()` searches once instead of twice (the second
search could have emptied a good pick between printing `tid` and `Start()`). The escape hatch
that needed no rebuild, and the one to reach for first next time: `--lua-thread 13524` with the
tid `--check` printed - `UseLuaThread(tid)` opens the handle and the guard freezes it.

## 0.5.0 first run: THE GUARD IS PROVEN - commands execute, nothing crashes (798 calls)

Two runs, one after the other, same client (pid 19516), both green. This is the first time a
command has executed with the Lua thread frozen, and it closes the open question from the
2026-10-13 crash (was the race the hazard? yes - and freezing the thread beats it):

| run | calls | rate | frozen per call | waits | retries | crash | client |
|---|---|---|---|---|---|---|---|
| 0.4.0, `--lua-thread 13524` (forced) | 791 | 13.1/s | 1.236 ms | 5 | 4 | none | alive, 70 threads |
| 0.5.0, automatic pick (tid 13524) | 798 | 13.2/s | 1.139 ms | 12 | 11 | none | alive, 70 threads |

```
guard       : freezes tid 13524 (seen executing Lua when sampled) for the duration of every call
sampled 70 threads for 2000 ms in 857 rounds; all thread handles opened
            13524         312     857     112      5   0x7FF9B3C61444 ...
            -> the Lua thread was SEEN (RIP inside module+0x4700000..0x4740000); freezing: 13524
[ 114] GetTime=211529.467    Dw    level 2   hp 72    target=True  Player-70-00000008  class "ROGUE"/"Rogue"
[stats]     114 rounds, 798 calls in 60.5 s  ->  13.2 calls/s
guard       : 798 freezes, 12 waits for the VM to go quiet, 11 retries, 1.139 ms frozen per call
[stop]      the resident thread exited on command - no TerminateThread was used
[alive]     pid 19516 is still running (70 threads)
```

What the numbers say:

* **freezes == calls (798/798)** - every single call ran against a frozen VM; none slipped
  through unguarded. There is no "sometimes it races"; the invariant holds.
* **1.139 ms frozen per call** - the game loop (tid 13524 is the frame thread: 312 ms of CPU in
  the 2 s window, 112 module RIPs) is paused ~1.1 ms per call. At this call rate that is ~1.5%
  of one thread's time, and the guard's own 1 s cap was never approached.
* **12 waits + 11 retries** - the guard did hit moments where the VM was mid-Lua
  (`L->top != L->base`) or moved during the suspend sequence, and it *retried* instead of
  calling. Those are exactly the moments that killed the client on 2026-10-13 (287 calls,
  unguarded). Zero crashes over ~1,600 guarded calls.
* **13.2 calls/s is the probe's rate, not the bridge's ceiling** - the probe sleeps 500 ms
  between rounds by default; a round is 7 calls in ~24 ms (p50), ~3.3 ms per call wall clock of
  which 1.1 ms is the freeze. An `--interval 0` run measures the ceiling.
* tids are **session-stable, not build-stable**: 13524 was the Lua thread in both runs, but a
  restarted client gets new tids - which is why the automatic pick matters and `--lua-thread N`
  is only an override, not the mechanism.
* the forced-tid path (`--lua-thread 13524`, 0.4.0) worked exactly as designed: it was the
  escape hatch from the blank-tid bug and it carried a full 791-call run on its own.

Next milestone: **the first write** - `/run print(1)` in the game to decrypt the `RunScript`
page, then `LuaBridgeProbe.exe --call RunScript --arg "print('bbuddy: the bridge can write')"`
(0.4.0's cold-page gate refuses `RunScript` until that page is warm - a safety property, not an
obstacle). Numeric arguments are still unsupported in the push path (`lua_pushnumber` was never
located), but most C handlers use `luaL_checknumber`, which converts a *string* argument to a
number - so `--arg 1.5` may already work; worth one test on a warm numeric API
(e.g. `--call GetActionCooldown --arg 1`).

## 2026-10-13: THE FIRST WRITE - our Lua ran inside the client and the client printed it

`RunScript` was refused by the cold-page gate (it is one of the four handlers hunt37/hunt38
found encrypted). The user typed `/run print(1)` in the game - which makes the *client* execute
that handler, decrypting its page - and the next call went through:

```
handler     : module+0x136FFF0   readable, first bytes 40554157488bec4883ec784533ff4889 -> WARM
                                     [cold in the hunt37/38 survey - the game has since decrypted it]
[call]      RunScript("print('bbuddy: the bridge can write')") = nil
[call]      5.48 ms wall, 0.81 ms while frozen, type word 0x0 -> Nil
[guard]     1 freezes, 0 waits for the VM to go quiet, 0 retries, 2.169 ms frozen per call
payload     : served 1 command(s) on one resident thread
[stop]      the resident thread exited on command - no TerminateThread was used
[alive]     pid 19516 is still running (70 threads)
```

and the client's chat frame showed `bbuddy: the bridge can write`.

That is the whole execution story closed: **read** (hunt45/46: 11/11 API reads; 0.5.0: 798
guarded calls), **write** (RunScript changed the client's own state and it echoed it back), and
the concurrency hazard handled (the Lua thread frozen for 798/798 calls, zero faults). Nil is
the correct return for a statement-only chunk.

Reading the two timing numbers, because they look inconsistent and are not:

* `0.81 ms while frozen` is `LastCallMilliseconds` - the ack-poll loop only, from writing
  `cmd = 1` to seeing `ack == seq`,
* `2.169 ms frozen per call` is the guard's own measure - suspend → resume, which also contains
  the control-block writes that happen *after* the freeze and *before* the ack poll,
* `5.48 ms wall` is the whole `Call()`: guard entry (which includes its deliberate 2 ms sleep
  quantum and the quiet re-checks), the writes, the ack.

So the true cost of one command is ~2 ms frozen + ~2 ms of the guard's quiet check. The 2 ms
sleep is the biggest single item and is deliberate (a quiet reading must survive one scheduling
quantum before we trust it); if a botbase ever needs more than ~300 commands/s that is the knob
to revisit first - measured, not guessed.

Also settled by the same run: the cold-page rule is *per client session*, and it is not a wall -
the game warms a handler by using it. A bot in a real session warms most of what it needs simply
by playing; `/run` is only needed for functions the UI never calls.

### The action primitive: `--seq`

`--call` is one command per process, and a process costs ~4 s (attach + the 2 s thread
sampling + page + payload). An action that has to be undone - walk for a moment, turn, cast,
loot - needs its steps milliseconds apart in ONE session, so the probe grew `--seq`:

```
LuaBridgeProbe.exe --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
```

Steps are `Name` or `Name=arg`, separated by `;`, with `sleep=MS` between them. Every step goes
through the same cold-page gate (a refusal stops the sequence *before* the action is left half
done), each line prints its result and timing, and the payload thread is started once and
stopped once. This is the shape the bot's action queue will take: an ordered list of guarded
calls with explicit pauses, executed on one resident thread.

## 2026-10-13: the movement attempt - the gate refused a COLD MoveForwardStart (and was right)

```
LuaBridgeProbe.exe --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
handler     : module+0x23C8BE0   UNREADABLE - still encrypted (cold)
[seq 1]     REFUSED at step 1 - nothing after it was sent
```

`RunScript --call` still worked in the same session (its page stayed warm from the earlier
`/run print(1)`), which is the point: **warmth is per-page, per-session state, not a property of
the build or of the mechanism.** The gate reads the page, so a warm handler is callable and a
cold one is refused, in the same process, seconds apart.

Two corrections that came out of this run:

* the earlier note "hunt37 found MoveForwardStart warm, so it is callable today" was wrong for
  this session. hunt37's *protection* sample said warm; hunt38's live dump read those pages as
  zeros (NOACCESS at dump time) and this run says cold. The map is a sample of one moment.
* **a bound key does not warm a Lua handler.** The player had moved (level 1 -> 2, walked
  around) and `module+0x23C8BE0` was still encrypted: the W key's binding path and the Lua API
  handler live on different pages. Only the client executing *that function* decrypts it, which
  means `/run MoveForwardStart()` (from the client's own Lua) is the warming step, not walking.

The refusal message printed `RunScript`'s warming line because it was hard-coded - fixed in
0.6.1, which prints the line for the function that was actually refused, and adds a companion
`/run MoveForwardStop()` for any `*Start` so a successful warm does not leave the character
walking. 0.6.1 also makes `--seq` continue past a *Lua-level* refusal (the client rejecting a
call: the command was served, the payload is healthy, so a movement pair should report both
halves) while still aborting on a cold-page refusal or a timeout.

## 2026-10-13: the movement call KILLED the client - and the crash report explains the whole design

The gate let it through (the user had warmed the page with `/run MoveForwardStart()`), the call
went in, and 1 s later our payload had not returned. The client then terminated itself and
wrote its own error report (build 70291, pid 16108):

```
Error:    ASSERT(FrameScript_IsLuaThread())
File:     Engine/Source/FrameScript/FrameScriptEvents.cpp, Line 208
ThreadID: 13572            <- OUR payload thread, not the client's Lua thread (6184)
stack (resolved against base 0x7FF73B080000):
  module+0x471A983   our trampoline return site X
  module+0x471B700   lua_pcall
  module+0x471ED31   luaD_pcall
  module+0x47190B0 / 0x471EAD6 / 0x471F3DB / 0x471F98D   the payload's own call path
  module+0x23C8C0C   MoveForwardStart (handler is 0x23C8BE0, so 0x2C bytes in)
  module+0x23C550D   0x23DD429   0x23DBE47
  module+0x1DA4596   the FrameScript event dispatcher
  -> assert FAILED, client terminated itself
```

So `MoveForwardStart` is not a getter: it raises a FrameScript event, and this client requires
events to be raised **from its own Lua thread**. From any other thread it does not ignore the
call and it does not return an error - it asserts and dies. That also explains our side of it:
stage `0x4004A` (the payload's stage right before `lua_pcall`) never advanced because the
client was already busy writing that report.

Two consequences, both important:

* **The freeze was never the problem.** Nothing about the guard, the trampoline, the gadget or
  the payload is at fault; a foreign thread calling an event-raising function is refused by
  design. This is a *stronger* wall than the protector, and it is deterministic.
* **The reads and RunScript are still fine** because they raise no events. The bridge's
  `ServerThread` mode remains correct for everything that does not dispatch.

### The fix: run the command ON the client's Lua thread (hijack mode, 0.7.0)

`FrameScript_IsLuaThread()` asks one question - "is the current thread the Lua thread?" - so the
answer is to make it true. `LuaExecutionMode.LuaThreadHijack` suspends the client's Lua thread
at a quiet moment (`L->top == L->base`, `L->ci` at rest, double-checked, as the guard already
did), saves its **full CONTEXT**, then sets RIP = our payload entry, RCX = the control block and
RSP = a private 64 KB stack whose top slot holds a `pause; jmp $` stub - and resumes it. The
payload now executes on the VM's own thread, so:

* events are raised from the Lua thread, which is what the assert wants;
* the `lua_State` is only ever touched by its owner, so the cross-thread race is gone outright
  (no freeze needed at all);
* the payload's final `ret` lands on the stub, never in game code, so the thread is parked at a
  place we chose;
* we then suspend it, `SetThreadContext` the saved CONTEXT back, and resume: the game continues
  exactly where it was.

Cost per command is the same order as before (~2-5 ms of the client's time), and every earlier
invariant holds: patch by offset + read-back, no TerminateThread, never free the page under a
live payload, never resume a thread that a wedged call left inside our code (a wedge now leaves
the client frozen and the page untouched, and the probe says exactly that instead of advising a
retry).

This is also the correct shape for everything that *dispatches*: `TargetUnit` (UNIT_TARGET),
`CastSpellByName`, `RunMacro`, `JumpOrAscendStart`, and movement. Anything that raises an event
must go through `--on-lua-thread`; the injected-thread mode can only ever do non-dispatching
work.

## 2026-10-13 (later): the hijack machinery bug, `FORCETAINT_STRONG`, and the movement-set RVAs

### The first `--on-lua-thread` run never got to the client

```
[seq 1]     MoveForwardStart() -> the client's Lua refused it: SetThreadContext failed: 0
```

Nothing was called. The mode failed inside its own setup, and the message was wrong twice over:

| what it said | what was true |
|---|---|
| "the client's Lua refused it" | the bridge's own `SetThreadContext` call failed; the client was never asked anything |
| "failed: 0" | not an error code - 0.7.0 read `Marshal.GetLastWin32Error()` *after* `ResumeThread`, so it printed what that call left behind |

Root cause: `private const uint ThreadSetContext = 0x0040`. `0x0040` is `THREAD_QUERY_INFORMATION`;
`THREAD_SET_CONTEXT` is **0x0010**. Because the mask was legal but wrong, `OpenThread` **succeeded**
(0x4A = SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION) and returned a handle that may read a
context but never write one. Everything else in the run was healthy: the search found the Lua
threads and printed the sampling table (`freezing: 3368, 19380`, both with Lua RIP hits), the thread
suspended, `GetThreadContext` returned - only the write was refused, and the refusal was reported as
a Lua problem.

Fixed in 0.8.0: the correct mask plus a `THREAD_ALL_ACCESS` fallback; errors captured immediately;
the raw `NtSetContextThread` NTSTATUS printed when Win32 refuses; a **CONTEXT round-trip self-test
per candidate at setup** (suspend -> read -> write the same context back -> read -> resume, also
available as `--check --on-lua-thread`); **read-back verification of every write**, retried with
`CONTROL|INTEGER` (plain `CONTROL` does not include RCX, which carries the control block);
`LuaTakeoverException` so the probe says `[takeover]` instead of blaming the client; and
`HijackReport`/`HijackHow`/`HijackAttempts` in the header so a log carries the whole story.

Same lesson as the call-site bug earlier that day: when our message blames the client, check it
against the client's own logs and against what our own calls returned. Both times the bridge was at
fault and the wording pointed the wrong way.

### `/run MoveForwardStart()` is blocked by the client's own protection

```
*** FORCETAINT_STRONG*** has been blocked from an action only available to the Blizzard UI
```

`MoveForwardStart` is **forbidden** to script code in this client (not merely protected): addons,
macros and `/run` can never call it. Two consequences:

1. `/run Name()` still **warms** the handler - the gate reports `module+0x23C8BE0 -> WARM` in that
   same session, so the blocked call executed the first instruction and decrypted the page.
   Warming and acting are different questions, and only the second one is closed.
2. For the port, the Lua API is not an execution path for movement/target/cast at all. The route is
   a C-level call from the client's own Lua thread (`--on-lua-thread`), where the subject of a taint
   check - a tainted script context - does not exist, while `FrameScript_IsLuaThread()` (a thread
   identity check) is satisfied by construction. Whether the forbidden/taint gate also inspects a
   C-level call is the open question; the next run answers it with a `[seq]` line.

### The movement set has verified RVAs (use them for the gate, not for calls)

hunt31 walked the live registration tables and the static dump agreed, so the movement pair set is
evidence-backed: `MoveForwardStart 0x23C8BE0`, `MoveForwardStop 0x23C9000`,
`MoveBackwardStart 0x23C9410`, `MoveBackwardStop 0x23C9830`, `TurnLeftStart 0x23C9C40`,
`TurnLeftStop 0x23CA060`, `TurnRightStart 0x23CA470`, `TurnRightStop 0x23CA890`,
`StrafeLeftStart 0x23CACA0`, `StrafeLeftStop 0x23CB0D0`, `StrafeRightStart 0x23CB500`,
`StrafeRightStop 0x23CB930`, `PitchUpStart 0x23CBD60`, `PitchUpStop 0x23CC180`,
`PitchDownStart 0x23CC590`, `PitchDownStop 0x23CC9B0`, `JumpOrAscendStart 0x23C5560`,
`AscendStop 0x23C6840`, `DescendStop 0x23C7160`, `ToggleRun 0x23C76C0`,
`ToggleAutoRun 0x23C7F50`, `StartAutoRun 0x23C83A0`, `StopAutoRun 0x23C87C0`,
`MouselookStart 0x23D1110`, `MouselookStop 0x23D1580`, `IsMouselooking 0x23D1A40`,
`SetMoveEnabled 0x23D9340`, `SetTurnEnabled 0x23D97E0` (movement table base `0x58CF2B0`,
58 entries, `MoveForwardStart` at `+0x70`). They are in the probe's table so the cold-page gate can
decide, and they are the map for the reverse-engineering step below.

`ToggleAutoRun` / `StartAutoRun` deserve a test of their own: autorun is *movement without a held
key*, i.e. the cheapest forward primitive if the client accepts it from a C call.

### Next: read the decrypted movement path (the wall here is code, not warmth)

`--dump` (new in 0.8.0, read-only, `.txt` manifest, md5) copies out pages that are decrypted in the
live client and still ciphertext offline. Take these while the session that ran
`/run MoveForwardStart()` is still warm:

```
LuaBridgeProbe.exe --dump 0x23C5500:0x400,0x23C8BE0:0x400,0x23C9000:0x400,0x23DBE00:0x400,0x23DD400:0x400,0x1DA4500:0x400,0x58CF2B0:0x3A0 --out movement.bin
```

What to look for in those bytes: (a) what `MoveForwardStart`'s body does with the helper at
`0x23C550D` (the crash stack entered it 0x2C bytes into the handler); (b) where the thread-identity
and `FORCETAINT_STRONG` checks sit in `0x23C550D -> 0x23DD429 -> 0x23DBE47 -> 0x1DA4596`, which
decides whether the hijacked C call passes them; (c) the engine-level movement entry underneath the
Lua layer - the function the client's own key handler uses, with no script context and no per-frame
Lua cost, which is what the bot actually wants.

## 2026-10-13 (later still): what the client actually allows, and where CTM comes in

### The in-game verdict: the Lua API is closed for every movement action

Four chat-box probes, in the session pid 14952:

| probe | result | reading |
|---|---|---|
| `/run MoveForwardStart()` | `FORCETAINT_STRONG` - "blocked from an action only available to the Blizzard UI" | forbidden to script code (0.7.0 crash section) |
| `/run ToggleAutoRun()` | `FORCETAINT_STRONG` | autorun is forbidden too - no "move without holding a key" shortcut through Lua |
| `type(ClickToMove)` | `nil` | the CTM function is not exposed to Lua at all in this build |
| `type(RunBinding), type(ToggleAutoRun), type(StartAutoRun)` | `function function function` | the names exist; existence is not permission |
| `GetCVar("enableClickToMove")` | `nil` | that CVar name is not registered here - the option lives under another name |

So: **no Lua path to movement exists in this client.** Not one that a bot may use. The only two
routes left are (a) the C-level call from the client's own Lua thread (the queued
`--on-lua-thread --seq "MoveForwardStart ; sleep=400 ; MoveForwardStop"`, which bypasses the
script-context check by construction and is still unproven) and (b) the engine functions
underneath the script layer, which is where click-to-move leads.

### Click-to-move works - confirmed behaviourally, and it is the bot's core mover

With "Click to Move" enabled, right-clicking the ground ~100 yards ahead makes the character walk
there (it will walk into obstacles rather than path around them - the obstacle avoidance was always
the bot's job, not the client's). And the legacy bot is built on it: `WoWMovement.ClickToMove` is
called from Gatherbuddy, DungeonBuddy, DiscoBot, BGBuddy, Meshnavigator, Flightor, the avoidance
helpers and both movers - plus `ClickToMoveMover`/`KeyboardMover` as `Navigator.PlayerMover`
implementations. Whatever else the port does, `WoWMovement.ClickToMove(point)` has to work.

### The legacy primitives, and what has to be found in 70291

`Styx/WoWInternals/WoWMovement.cs` and `Styx/Logic/Pathing/Interop/KeyboardMover.cs` name the exact
3.3.5a machinery, which is the shopping list for the x64 hunt:

| legacy (3.3.5a) | what it is | needed in 70291 |
|---|---|---|
| `CGPlayer_C__ClickToMove` = `0x727400` | writes the CTM info struct and starts the move | the CTM entry point: `(type, guid, x, y, z, facing)` |
| `CGPlayer_C__ClickToMoveStop` = `0x72B3A0` | stops a CTM move | the CTM stop |
| `ClickToMove_Base` = `0xCA11D8` | `ClickToMoveInfoStruct`: Type (+0), TurnSpeed (+4), destination, GUID, ... | the CTM info struct (the differential-scan target) |
| `CGInputControl_ToggleControlBit` = `0x5FBE10` | `__thiscall(this, flag, start, timestamp, 0)` - HB used it for every direction except jump | the engine-level movement-flag function *under* `MoveForwardStart` |
| `ActiveInputControl_Ptr` = `0xC24954` | global holding the active `CGInputControl` | the input-control global (the flags live there) |
| `GetActivePlayerObject` = `0x4038F0` | the local player object | already have the modern object model (`OM 0x7B7B738`) |
| `me.SetFacing(radians)` | `KeyboardMover`'s turn primitive: a *write* to the player's rotation | the modern rotation field (a write, no call - the cheapest primitive of all) |

Two consequences for the plan:

1. **`MoveForwardStart` (module+0x23C8BE0) is a wrapper, not the engine.** Its crash stack went
   handler -> `0x23C550D` -> `0x23DD429` -> `0x23DBE47` -> the FrameScript event dispatcher
   (`0x1DA4596`) -> the thread assert: the script layer raises an event *around* the movement. The
   engine-level call the dispatcher (or the key handler) eventually makes is the modern
   `ToggleControlBit`. That is the function the bot wants: no script context, no event, no per-frame
   Lua cost.
2. **`ClickToMove` needs float arguments**, and the resident payload has no `lua_pushnumber` (and
   would not want one: this call is not a Lua call). It needs a *native* call stage - args in
   RCX/RDX/R8/R9/XMM0-3 from the control block, a `call [target]`, results back in the block. That
   is a payload change (new bytes, keystone-assembled and capstone-verified like the current 641),
   and it is the same stage the rest of Phase 4 needs. It also answers a thread question: the CTM
   code is the *input* path, so it very likely does not raise a FrameScript event and can run on the
   injected resident thread - to be proven, not assumed.

### Next RE step: the CTM info struct, found by differential scan (hunt47)

The chain to follow, all read-only:

1. find the string of the CVar that controls click-to-move (the name is still unknown -
   `enableClickToMove` is not it in this build; `/run print(GetCVar("clickToMove"), GetCVar("autoInteract"))`
   is the cheap way to settle it) and, from its CVar struct, the value pointer;
2. find the code that *reads* that value (RIP-relative references into the string/struct in the
   module's **readable** pages - the client has run the input path, so those pages are decrypted);
3. near it, the click handler that converts the cursor pick into a CTM order;
4. the CTM info struct: the differential-scan target - stand still, snapshot a window, right-click
   100 yards away, snapshot again; the struct changes to `{type=4 (Move), destination x/y/z, GUID}`;
5. the function that reads the struct (xref scan) is `CGPlayer_C__ClickToMove`'s modern twin, and
   the function that reads the *input control* flags is the `ToggleControlBit` twin.

Until that exists, the queued `--on-lua-thread` movement test decides whether route (a) is enough
for the bot's keyboard-style movement (`KeyboardMover` needs only `SetFacing` + forward), with CTM
as the follow-up for path following.

## 0.8.2 — the parked-thread crash, decoded (2026-10-13)

Both hijack runs (pid 14952 `--call GetTime`, pid 14472 `--seq MoveForwardStart`) died the same way:

| field | crash 14952 | reading |
|---|---|---|
| exception | `ACCESS_VIOLATION write` at `0x7FF8E17731B0` | the store target |
| RIP | `0x1BEBD0A0807` | bridge page `0x1BEBD0A0000` + 0x807 = payload + 0x807 = entry + 7 |
| RSP | `0x1BEBD14FFD0` | our private hijack stack — CONTROL was applied |
| RBX = RCX = R10 | `0x7FF8E17730D0` | the CLIENT's register, same value tid 18484 was sampled at |
| target | `[rbx + 0xE0]` | the payload's call counter at ctrl + 0xE0 |

Entry is `mov rbx,rcx; sub rsp,0x28` (7 bytes) and the next instruction stores the counter, so the
payload was entered with the client's stale `RCX` while our `RIP`/`RSP` had landed: on a thread
parked in a kernel wait the kernel applies CONTROL but not the INTEGER file of a written context.
The self-test could not see this (rights + alignment are all a same-context round-trip proves).

Rules from here on:
1. hijack only a moment with RIP **inside the game module** (parked-wait moments are skipped);
2. verify the read-back of RIP **and** RSP **and** RCX, not RIP alone;
3. a `--force` style override must never be added for moments outside the module.

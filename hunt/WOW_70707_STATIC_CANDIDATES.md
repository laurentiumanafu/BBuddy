# Build 70707: static offset candidates

These are **research candidates**, not a usable or validated offset table. They
were derived without executing the client, changing game memory, or invoking
Lua. A 70707 attachment now uses the candidate globals in its reader and
prints a read-only candidate summary; this is not a completed
port. A decoded file need not match
the bytes and protection state of the running process; static matches also do
not establish live pointer validity, object identity, or safe actions.

## Inputs and method

- 70707 `Wow-decoded.exe`, SHA-256
  `2cfec8708b648efd8b78db4cd52da78ede44de39ce229a0175a572b749c81a89`,
  preferred image base `0x140000000`, SizeOfImage `0x73F9000`.
- Previously investigated 70291 `wowb-decoded.exe`, SHA-256
  `8f864e73e73ad67319765063a7c491a420dbe2d0714ce427106e39cd9f109192`.
- PE RVAs were compared using x86-64 disassembly. At 70291 references to known
  globals, RIP-relative displacements and relative call/jump displacements
  were masked, then surrounding instruction bytes were searched in the 70707
  `.text` section. Unique matches are evidence, not proof: bytes can match by
  coincidence or represent refactored code. Some references did not match and
  some produced conflicting targets.
- Independently, 70707 registration sequences and `{name, function}` static
  tables identify handler addresses. These are reverse-engineering anchors;
  this analysis does not establish a safe calling convention or context.

All addresses below are **image-relative virtual addresses (RVAs)**, not raw
file offsets or fixed process addresses. Actual runtime base can change.

## Candidate globals / structure fields

| Meaning inferred from the corresponding 70291 code | 70291 RVA/offset | 70707 candidate | Static evidence and limitations |
|---|---:|---:|---|
| Player GUID global | `0x61C72F0` | `0x5110D50` | Three uniquely matched code references in the sampled set; one additional sample produced a conflicting `0x5245730` and is not trusted. |
| Inline player name | `0x61C7300` | `0x5110D60` | One unique matching reference; located 16 bytes after the GUID candidate as in 70291. Not verified as a string in a live process. |
| Current target GUID | `0x7BDFF38` | `0x6B1DFB8` | 37 unique-reference matches in the first 80 sampled xrefs. Several may belong to the same logical path; this is not 37 independent validations. |
| Object-manager global | `0x7B7B738` | `0x6D20538` | Two nearby matches in a shared reset path and one separate match. Pointer semantics and pool relationship remain unverified. |
| Handle-pool table root | `0x582D150` | `0x481CBE0` | Two matched references from the first 80 sampled xrefs. One other match pointed elsewhere; treat as a lower-confidence lead. |
| Map-root global | `0x7B7B4F8` | `0x6D204B8` | Four matched references in the first 80 sampled xrefs; one conflicting match. |
| Map-root field candidate | `+0x28` | `+0x20` | Corresponding instructions at 70291 `0x205452` and 70707 `0x21BCD9` read these fields after loading the corresponding roots. Diagnostic-only: semantic identity and live behavior are unverified. |
| Camera world-frame global | `0x5FF99F0` | `0x4F48610` | 70707 `GetCameraZoom` registration points to `0x2835700`; that code reads this global. |
| Camera pointer within frame | `+0x490` | `+0x480` | Directly visible in the 70707 `GetCameraZoom` handler. This is a structure offset, **not** a module RVA. |
| Zoom field within camera object | `+0x274` | `+0x274` | Directly visible in that handler. Other camera fields are not established by this observation. |

Concrete corresponding sequences include 70291 `0x24287C1` versus 70707
`0x1D5F051` for player-global candidate; 70291 `0x1FC1A1A/0x1FC1A2F` versus
70707 `0x19F91E5/0x19F91FA` for object-manager candidate; and 70291
`0x1EB684` versus 70707 `0x1F21C4` for target candidate. The last pair calls
70291 `0x1FC37F0` and 70707 `0x19FAE20` respectively, making the latter a
candidate counterpart of the existing GUID-to-unit helper, not a proven ABI.

## 2026-11-02 mouseover lead

The 70707 gather log showed a pointer-shaped value at the **70291** mouseover RVA `0x6283898`; the gather loop was incorrectly using that address on both builds. Static comparison of the two decoded images gives candidate 70707 RVA **`0x51C7A78`**. In 70291, `movups` at `0x2994647` stores a 16-byte value to `0x6283898`; its matching 70707 sequence at `0x21E4727` stores to `0x51C7A78`. The 70291 GUID resolver call sequence at `0x2A906FD` has a corresponding 70707 sequence at `0x22B9B6A`, and both load those respective addresses. These are related static references, not independent live proof of mouseover semantics. The runtime hover search still requires an away transition, an exact full-GUID match against a fresh eligible nearby node, a final reread, and STOP/combat/cursor checks before posting right-button down. No node click, cast, or harvest on 70707 has yet been validated.

## Registered 70707 handler anchors

The registration instructions directly associate the names below with these
code RVAs in the decoded file. They are **not** player-state offsets or proof
that invoking them is safe; the bot's Lua bridge remains retired.

| Handler name | 70707 RVA | Evidence |
|---|---:|---|
| `UnitHealth` | `0x17F7390` | Registration near `0x1828759` |
| `UnitHealthMax` | `0x17F5AE0` | Registration near `0x182870B` |
| `UnitGUID` | `0x17EFE70` | Registration near `0x1828585` |
| `UnitLevel` | `0x180CE10` | Registration near `0x1828D8B` |
| `UnitName` | `0x180D2E0` | Registration near `0x1828DA5` |
| `UnitReaction` | `0x1819F90` | Registration near `0x1829097` |
| `UnitCanAttack` | `0x17DFAF0` | Registration near `0x18282C7` |
| `UnitPower` / `UnitPowerMax` | `0x1813440` / `0x1814E90` | Registration near `0x1828F2B` / `0x1828F93` |
| `TargetUnit` | `0x1678790` | Registration near `0x1678F9C` |
| `MoveForwardStart` / `MoveForwardStop` | `0x1D25740` / `0x1D25B30` | Name/function table entries at `0x48BE0C0/0x48BE0D0` |
| `GetCameraZoom` | `0x2835700` | Name/function table entry at `0x4947240` |
| `GetInventoryItemID` | `0x26CAA90` | Name/function table entry at `0x492EDC0`; says nothing by itself about bag layout |

## Follow-up observations and faction lead

One live attach showed matching record/unit GUIDs and back-pointers for a local
player with vtable RVA `0x41DA390`, and one selected monster with vtable RVA
`0x41EE168`. Their `record+0x60` transforms were plausible, about 4.8 yards
apart. These two observations do not validate every player or unit, establish
the transform's semantics, or authorize movement/combat. The diagnostic
snapshot recognizes these two vtables with identity and stability checks;
`0x41DC3A8` remains an unverified candidate and is not accepted.

The 70707 `UnitReaction` handler at `0x1819F90` calls `0x20807B0`, in the
same position as 70291 `0x1D89A00` calls `0x27EDBC0`. The routines have closely
corresponding paths: 70291 reads unit `+0x1E5C` (previously traced to
FactionTemplate), while 70707 reads `+0x1E54` (e.g. `0x2080BE2`). A nearby
70707 helper at `0x2080D38` also reads `+0x1E54` after a table lookup, mirroring
70291 `0x27EE199`. The adjacent flag test shifts from `+0x1E60` to `+0x1E58`.
This is a **static candidate**, not a proven key or reaction. Attach diagnostics
print 16 uninterpreted dwords from unit `+0x1E20` through `+0x1E5C` for the
local and selected records when readable and structurally matched. The separate
single-target experimental trial uses the candidate key with explicit selection
and an allowlist; it does not establish hostility. The 70291 filter is unchanged.

A later attach contained `+0x1E28` qwords 148 for the player and 55 for the
selected Rockjaw Trogg, matching the user's reported current HP. `+0x1E30`
had the same respective values; maximum HP was not independently reported, so
the sample does not distinguish current from maximum. The `+0x1E38` dwords
were 4 and 2, which the user confirmed matched the displayed levels. The
`+0x1E54` dwords were 3 and 36; neither value alone proves reaction or attackability.
A subsequent selection-free scan reported candidate 31 for 6 units, 32 for 27,
36 for 33, and 57 for 1, with 0 unreadable/changed among the scanned candidates.
The selected Trogg was in the 36 group. These are observations, not verified
relations or exhaustive counts. In that later attach the raw player health pair
was 118/118, but no corresponding displayed health was supplied.

Static evidence: `UnitHealth` at `0x17F7390` calls `0x288FF50`, whose unit
path tail-jumps to `0x20AAE60`; at `0x20AAF97` one path reads a signed qword
at `+0x1E28`. `UnitHealthMax` at `0x17F5AE0` calls `0x28900A0`, which
can tail-jump to `0x20AAFF0`; `0x20AB0F9` reads `+0x1E30`. Both paths can
use other sources or multiply by a client-computed scale, so the raw fields
are not guaranteed to equal displayed health in every context. A level-related
helper reached from `UnitLevel` also reads `+0x1E38`, but may choose alternate
values. These are candidates, not validated gameplay stats or safe call APIs.

The user later reported a mesh research query at candidate map 0: the nearest
polygons to the player and selected unit were respectively 0.18 and 0.31 yd
from the candidate positions. This supports the location/map lead at that spot,
but does not prove the map on transitions, an actual route, movement, or attackability.

Additional static tracing found a stored-action-array candidate at RVA
`0x6D3EC60`. The 70291 reference at `0xDB87F1` and corresponding 70707 reference
at `0xA538FD` both access an indexed qword array, and 35 unique-reference
matches in the sampled set point to the same new RVA (not necessarily
independent). The old 70291 array RVA `0x7DFF4E0` is outside the 70707 image.
The first 12 qwords are logged on attach only if two reads and player GUID
checks agree; ordering, slot mapping and packing are still unknown.

The `UnitPower` path in 70707 can use `[unit+index*4+0x1BC0]` at `0x2890305`;
`UnitPowerMax` can use `[unit+index*4+0x20F0]` at `0x289040D`. Both obtain
`index` through `0x20AC3B0` and have special cases or other sources. It is not
safe to assume index zero is current mana or reuse 70291's contiguous max-power
field. The attach log prints the first eight raw dwords from each candidate
array and the raw `+0x1E21` byte only when the local unit identity and repeated
samples are stable. A later 171/171-mana sample matched index zero and action
slots 1–3 matched 6603/403/331, but this does not prove dynamic behavior or
actual bindings. In a subsequent supervised single-target trial, the log recorded
three Lightning Bolt health changes 55→40→25→10 and mana observations
156→141→126 after starting at 171. The target then disappeared; the log cannot
distinguish death from losing the target. The user reported that the trial
looked fine. This supports the tested damage and mana paths, but not healing,
melee, a route, or general combat. See `WOW_70707_EXPERIMENTAL_ATTACH.md` for
trial restrictions.

The stopped-bot **Combat Settings → Factions → Scan nearby** button now shows
70707 candidate `+0x1E54` words without selection. It rechecks identity and
stable values, but uses unverified vtables and transforms and does not verify
code signatures. The scan output is explicitly research-only and also written to
the log; it does not by itself authorize a target for the separate trial.

## Still unknown

The new record and unit-object layout, generality of the observed vtables,
position/facing fields, health/level/faction fields, map ID chain, cursor and
movement controls, bag capacity/contents, and reaction semantics have **not**
been established for 70707. Old 70291 member offsets are not safe to carry over
just because one related field or instruction remains the same. The candidate
map is only exposed to the authorized experimental Grind thread; camera
position remains unavailable. Existing 70291 faction signatures and identity
checks have not been bypassed.

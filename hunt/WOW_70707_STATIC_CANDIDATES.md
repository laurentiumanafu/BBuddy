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
local and selected records when readable and structurally matched; these are
never used for targeting. The 70291 faction filter remains unchanged.

## Still unknown

The new record and unit-object layout, generality of the observed vtables,
position/facing fields, health/level/faction fields, map ID chain, cursor and
movement controls, bag capacity/contents, and reaction semantics have **not**
been established for 70707. Old 70291 member offsets are not safe to carry over
just because one related field or instruction remains the same. The candidate
roots are wired into the 70707 reader; its map ID and camera position remain
unavailable until their member fields are established. Existing
70291 faction signatures and identity checks have not been bypassed.

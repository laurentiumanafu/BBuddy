# 70291 Ghidra trace — 2026-10-28

Sources: user trace `1IDWsqnOfXsMTNIw0UzwF8RuhBSHoUec7`, headless log
`1PbGMDzF2T0skPibgqFeUru8DkWoU-eB1`, earlier audit
`1vo4BCS9hjJmyg7SUf9OM8MhK3TmCzVk-`. The trace ran read-only on the existing
`wowb-decoded.exe` project, image base 0x140000000. The headless log shows all
13 seed sections executed and the trace saved. The single UnitHealth 16-byte
signature matched the earlier sample. That is not full binary identity proof.

**Important limitation:** the connector returned a truncated trace: the
beginning, end, and selected excerpts (obtained with different read limits)
were examined, not every line. Pseudo-disassembly can walk into junk and
mid-instruction bytes. Every item below is an unverified static lead, not an
identified field, usable native ABI, safe call, or live validation. Never call
one from the bot on this basis.

* Health handler RVA `0x1D63190`: instruction at RVA `0x1D63283` calls
  `0x31A0800`. The subsequent `CVTSI2SD XMM0,RAX` at `0x1D63297` converts
  the returned integer to a double in a path labeled UnitHealth. This gives a
  high-value function to inspect, but does not yet link it to unit data +1E30.
* UnitReaction RVA `0x1D89A00`: call sites `0x1D89B40 → 0x27EDBC0` and
  `0x1D89BD5 → 0x29B7D60` are leads on separate branches. The latter is also
  visible in UnitIsEnemy at `0x1D6D425`. The paths likely perform more than a
  simple field read; no hostility offset or enum semantics are established.
* Movement trace: `0x23C5508` encodes a call to `0x23DD400`, followed by
  `0x23C550D MOV RBX,[RSP+0x40]` and function epilogue. This corroborates that
  the old `0x23C550D` crash-stack address is a return/callsite location, **not**
  an established function entry. `0x23DD400` is a movement-path lead, not CTM.
* Earlier xref at `0x7A8941` decodes as
  `MOV qword ptr [0x147BDFF40],RSI` (RVA of destination `0x7BDFF40`, high
  half of candidate target GUID). It is an instruction-level writer lead,
  not proof of a public setter, complete 128-bit write, context, or safe call.
* No inferred click-to-move structure/function; search absence is not proof
  that functionality does not exist. User confirmed manual CTM is functional.

`WowBFocusedTrace70291.java` narrows the next batch to four direct-call targets
with instruction and branch bounds, avoiding the earlier enormous output. It
compiled and ran on the user's Ghidra installation; see the results below.
Use rea, Gemini CLI Flash 3.5 Lite, and Ghidra, then independent read-only live
observations before promotion. 3.3.5a is unchanged.


## Focused trace result (15:39, user machine)

Full report Drive `1Z-5iwhNJdTT-ZqixhGXif-r4IOv3qBMR` and headless log
`1_m4L7YXqsEmMEbhIEgIVte-kQUOrqu4D`. The script compiled, ran on the saved
project and ended with COMPLETE (only bounded static exploration, not a
completeness proof). The full 13,198-byte focused report was received.

### Health / maximum-health leads

* `0x31A0800` calls `0x1FC37F0`; after a high-part shift/type comparison it
  takes a special branch through `0x2712720`, checks pointer guards, then at
  `0x31A0859` loads a **qword** from the resolved `RAX + 0x268`. On the
  non-special branch it may tail-jump at `0x31A08BC` to `0x281E7D0`, after
  passing the object previously returned by `0x1FC37F0` in RCX. The resulting
  fields and the precise meaning of DL are unverified. A guard failure path
  loads `100` at `0x31A0870`, so outputs must not be interpreted as universally
  raw memory reads.
* Separately, the earlier 229,700-byte trace had a UnitHealthMax-associated
  direct-callee section at `0x31A0950`: similar conditional path and a qword
  read `[RAX + 0x270]` at `0x31A0999`. The connector returned only excerpts of
  that large report, so the exact UnitHealthMax callsite was not inspected.
* **Neither `+0x268` nor `+0x270` is proven to apply to the snapshot's
  `record+0xD8` unit data** or ordinary units. They cannot be substituted for
  existing candidates `+0x1E30/+0x1E38`. Object identity, special branch,
  field width, semantics and non-special path require independent checks.

### Reaction

* The `0x27EDBC0` function compares pairs of qwords at object offsets `+0x10`
  and `+0x18` against pairs at `+0x1DA0/+0x1DA8`, and selects between
  `+0x1D70` and `+0x1D90` in one branch. These resemble relationship/GUID
  checks, but object identity and actual field semantics are unknown.
* The alternate `0x29B7D60` starts by loading a **signed dword** at
  `[RCX+0x2C4]`, conditionally indexes a table, and another path reads a dword at
  `[RCX+0x2A0]`. RCX is a resolved object of unconfirmed type; neither number
  is established as a unit, faction, or hostility offset. Output capped at
  50 instructions with three blocks still pending.

### Movement / CTM

`0x23DD400` conditionally calls `0x23DB9D0` or `0x23DC450` based on R8D and
then inspects bit masks in EDI and fields `[RBX+0x04]`/`[RBX+0x24]`. This is
consistent with a control/input path, not proof of CTM. Both children are
bounded next leads, and CTM destination/stop handling and ABI remain unknown.
The target GUID writer `0x7A8941` remains a single instruction-level lead.

The subsequent `WowBNextTrace70291.java` run traced the health tail callee,
max-health helper and both movement children using 50 instructions each; see
the next section. Its outputs require independent read-only live validation.


## Next trace result (15:43, user machine)

Trace Drive `1SsxtlPe6oCWOPV1C6oTAd1edLBW7cQaC`; headless log
`1erFFNTgcEPXR6zBHRtTLG8Wx1Um3rpHN`. Both were read in full; the script
compiled and completed. All results below are bounded static leads and can
include wrong instruction paths; no runtime behavior has been established.

* Health non-special target `0x281E7D0` loads qwords from the resolved object's
  `+0x1DD0` and `+0x1DD8`, shifts the latter by `0x3A`, then uses module
  globals `0x58FCD88`/`0x58FCD80`, arithmetic and a call to `0x2ACB5B0`.
  The trace ended at `0x281E89C` before the lookup result or return. These two
  qwords are not established as health values or as unit-data fields; the
  resolved object has not been equated to `record+0xD8`.
* Maximum-health-associated `0x31A0950` has a non-special tail jump at
  `0x31A09BE` to `0x281E940` when its resolved pointer is non-null; it has a
  guarded special path reading qword +0x270 and a fallback path. No universal
  max-health field or ABI is established.
* Movement children `0x23DB9D0` / `0x23DC450` compare a byte at context +0xA8,
  access dwords +0x04 and +0xB4, and access `[module+0x5FF99F0]` then +0x490.
  At the previously captured callsite `0x23C54F7`, an LEA passes
  `module+0x58CF650` in RCX to `0x23DD400` at `0x23C5508`. This is a
  static input/control context lead, **not** a validated CTM structure,
  position destination, or safe action entrypoint. It is not a pointer read.
* First-class CTM remains unresolved; no 70291 CTM offset has been discovered.

Next: `WowBHealthTrace70291.java` traces the normal health and max-health
paths with higher instruction budgets, plus bounded exact xrefs to the static
control context. These are separate analyses, not game calls. Its syntax was
checked locally but it has not been compiled/run here. No gameplay source or
3.3.5a behavior changed.


## Health and movement-context trace (15:47, user machine)

Trace Drive `1mWPOFJmXiUF9V_jBMD0h884tsY0zUwVO`; headless log
`1gqo5S73aQ__SYhwdmBREV16PRihIR4fj`. Both read in full, script compiled and
completed. The following is **static evidence**, not runtime identity proof:

* In helper `0x281E7D0`, a failed/unavailable GUID-like lookup reaches a branch
  that calls `0x26E1CB0`, converts its single-precision return to double, and
  on the `DIL == 0` branch executes
  `0x281E8EC CVTSI2SD XMM0,qword ptr [RBX + 0x1E30]` followed by
  `MULSD XMM0,XMM1`. This instruction treats an eight-byte operand as a signed
  integer for that conversion. The alternative `DIL != 0` path reads +0x1BC0.
* In helper `0x281E940`, the analogous lookup-fallback branch calls `0x26E1CB0`
  and executes `0x281EA32 CVTSI2SD XMM2,qword ptr [RBX + 0x1E38]`, then
  multiplies by the converted floating-point result. Conversions/rounding
  follow. This establishes operand width and signed conversion **for these
  static paths**, not that the values read are always the Lua API result.
* The earlier lookup-success paths instead load signed dwords at offset +0x30
  and +0x34 from `lookupResult + 0x18` (equivalently +0x48/+0x4C from the
  non-null node). Those are a different structure and route. A failed lookup,
  non-unit/alternate entity, scale not equal to one, or races may change what
  the API returns. The 90-instruction cap ends before all current-health
  branches are exhausted, although the max-health path reached RET.
* `RBX` in both helpers is a resolved object from `0x1FC37F0`, not yet shown
  to equal the snapshot's `*(record+0xD8)` data pointer. Thus the existing raw
  qword candidate offsets have stronger static support, but current/max health
  are not promoted to validated general gameplay properties. The observed
  low32=72 vs the user's UI remains one local sample; the raw high halves and
  independent max-health observation were not supplied. Compare complete raw
  qwords, scaling, lookup behavior and resolved object identity across samples.
* Context `module+0x58CF650` has many exact static references, including
  0x23FDCCA, 0x23FDDEA and 0x23FDF94; no containing functions were identified
  by Ghidra for these sites. They are input/movement context leads, not evidence
  of CTM. Absence of additional references doesn't rule out indirect users.

The read-only snapshot already preserves both raw qwords in the candidate
inspector. The older attach line displays only low 32-bit diagnostics and does
not implement the lookup or scaling; it must not be interpreted as the exact
API result. No code was changed in response to the static evidence.

Next: `WowBScaleAndInput70291.java` inspects the scale helper and three of the
context-reference sites. The latter are **instruction sites, not function
entries**; decoding can follow misleading paths. The script is read-only,
uses the previously successful pseudo-disassembly framework, and was syntax
parsed but not compiled/run in this sandbox. Use rea, Gemini CLI Flash 3.5
Lite and Ghidra to analyze findings; user-machine runs are necessary here.

## Scale and input site trace (15:52, user machine)

Report Drive `1EhOaOnzRbFe9H3ztdojVaLsRh5ZQKe7e`, MD5
`2bcb0410cdc4d64abf1bff0908f88c1a`; headless log
`1SJE_SX6mnBAi7osb0AGS3RmNQ64ixjqz`, MD5
`d409a8c8c98d64f7fe1156f0becbec97`. Both read fully; script compiled and
completed on the read-only project. The byte signature matched again, but is
not proof of the full image or runtime state.

* Scale helper `0x26E1CB0` tests its input, calls `0x269B450` and `0x2813130`,
  and on that conditional path reads `[RDI+0x1E54]` at `0x26E1D66`. The trace
  hit its 48-instruction limit immediately thereafter. A null/failed-check path
  branches to `0x26E1E8A` but the limit prevented decoding it. Neither the
  scale result nor a default factor has been established; +0x1E54 is not a
  validated gameplay field.
* Site `0x23FDCCA` leads to a conditional call of `0x23DD400` at `0x23FDD30`
  with RDX=0x2000000, R8D derived from CL and RCX=module+0x58CF650. Site
  `0x23FDF94` tests bit `0x2000000` at context+4 and checks two other globals.
  The relationship and exact semantics are unproven; this is a movement/control
  bit lead, not a CTM destination, operation, or callable ABI.
* Site `0x23FDDEA` tests low bits of context+4 and context+0x90, consults other
  global bytes, and calls `0x23FDF60`, `0x23FDEA0`, `0x23DE140`. Their role is
  unknown. Static pseudo-disassembly can follow misleading or overlapping paths.
* No CTM address was found, no action was performed, and no health value was
  promoted. Context address and sites are not necessarily function entries.

Next read-only script `WowBScaleMouse70291.java` follows both bounded scale
continuations, two direct-call movement leads and the registered
`SimulateMouseClick` handler `0x155FBB0`. The latter is a UI registration lead,
not proof it routes to world clicks or CTM. No native/UI calls are executed.
Each seed has an 80-instruction cap; continuation-local branches before a seed
are outside the window. Script syntax parsed locally; it has not been compiled
or run locally. The required toolchain remains rea + Gemini CLI Flash 3.5 Lite
+ Ghidra; no rea/Gemini analysis has been run in this sandbox.

## Scale and mouse trace (15:55, user machine)

Report `1kGLH1MZ-OJbwVBHtphvE1cuL7n8Z0AH2`, MD5
`dad8a4fc843ba0036207ffe70beabcad`; log `1XGW3nFf7uBZnVV8wPd-h6ILnlR_3hFXO`,
MD5 `8dc4acb5d3ffe6eafaa328e56d40b1ab`. Read in full; script compiled and
completed on the read-only project. Findings remain bounded static evidence:

* The `0x26E1CB0` helper's first three guards converge on `0x26E1E8A`, which
  loads a single-precision value from image RVA `0x533F9EC` into XMM0 and
  returns. Its bytes were not included in this report: do not assume 1.0 or
  that a live value equals the saved image. It is a guard path, not proof of a
  default scale for every object. On the other path the helper reads dwords
  from object `+0x1E54` and `+0x1E48`, builds a stack object, and calls
  `0x26E1C50` immediately before its epilogue, without overwriting XMM0 on the
  visible path afterward. The called function's result and data semantics
  remain unknown. Pseudodisassembly does not establish function correctness.
* Input helper `0x23FDEA0` conditionally calls/tail-jumps to `0x23FB150` with
  CL values 4 and 5 and other arguments; `0x23FDF60` computes a boolean from
  multiple global/context conditions including the previously noted flag.
  Nothing in this establishes what the numeric values mean, CTM or ABI.
* The registered `SimulateMouseClick` handler at `0x155FBB0` enters severe
  suspicious/overlapping instructions around `0x155FC20` and reaches the cap;
  no credible world-click or CTM path was demonstrated. Do not follow junk
  branch targets as if they were verified executable functions.
* Snapshot-pointer identity, health API scaling, CTM and gameplay support all
  remain unvalidated. No gameplay code/offsets changed.

Next read-only `WowBScaleControl70291.java` traces direct targets `0x26E1C50`,
`0x269BFA0`, and `0x23FB150`, plus the registered UnitCanAttack handler; it
prints image bytes at the guard constant and bounded exact refs to the three
input state bytes. These are static leads only. Syntax parsed locally; not yet
compiled/run. Use rea + Gemini CLI Flash 3.5 Lite + Ghidra when available;
these tools and the decoded image are not available in this sandbox.

## Scale, input, and attackability trace (16:16, user machine)

Report `1b3EuqAg4LoLEIx37iMm2HGrJIwZ7IDrN`, MD5
`e6badf20631d1b8e23d0f078a6b48f47`; log `1RDapMJu1o7_yEX8Kqg0knPttbR_XyJvz`,
MD5 `c457a14b186a00ba694fe5efb06c0228`. Both read in full. Script compiled
and completed read-only with its usual bounds. Static leads, not runtime proof:

* At image RVA `0x533F9EC`, saved bytes `00 00 80 3F` decode as little-endian
  IEEE-754 float **1.0**. On the previously traced failed-guard path,
  `0x26E1CB0` loads that value into XMM0 and returns. The image bytes do not
  establish live memory contents or that a given unit takes this path.
* On the other path `0x26E1CB0` calls `0x26E1C50`, which calls `0x26E17F0`,
  preserves its XMM0, and then multiplies it either by the same image constant
  if `0x269B450` returns null, or by the XMM0 returned by `0x2876980`. It is
  not established as universally 1.0; computed factors and pointer identity remain unverified.
* Lookup helper `0x269BFA0` reads dword `[RDI+0x1E44]` and, if zero,
  `[RDI+0x1E40]` on one traced path, but RDI identity and those fields' meanings
  are unproven. Instruction cap reached with branches still pending.
* `0x23FB150` uses CL as an index/bit shift and tests an input mask at
  `module+0x61B3F18`, also accesses a byte table at `module+0x7DE95B8`.
  It is a static input lead, not a proven CTM destination, stop handler or API;
  it hit the 70-instruction cap with 15 branches pending. Prior CL=4/5 values
  do not establish key or mouse-button identities.
* Registered UnitCanAttack handler `0x1D48E20` calls `0x1FC37F0` twice, with
  distinct 16-byte stack slots as arguments, and checks the first result. Trace
  stops at `0x1D48F4B`, before the attackability decision. It does not prove a
  faction/attackable field, pointer identity or safe callable ABI.
* State-byte xrefs include `0x23C59B7` inside a Ghidra-labeled function, but
  the label itself is not independent proof of flow or CTM. Other xrefs often
  lack a containing function; indirect references may be absent.

Next `WowBAttackControl70291.java` follows the UnitCanAttack and input sites,
reads bounded exact xrefs to the input mask/table, and traces the two scale
factor call targets. Continuation seeds are sites, **not function entries**;
negative/backward branches outside the per-seed window are omitted. Read-only,
syntax parsed, not compiled or run here. Preserve GUID128/x64 safety and
legacy 3.3.5a. rea + Gemini CLI Flash 3.5 Lite + Ghidra are requested; none
is available here. No gameplay changes or promotions were made.

## Attackability and input continuation trace (16:19, user machine)

Report `1I_gJQ7HcJr_5Ey6qPm0pwtn8xZ1NTZp4`, MD5
`abcec1d8679c254644e111db501bfc7c`; log `1cks5AQLKeLwMB6yvWmQV0i04p46Rronr`,
MD5 `9a7c718740f4b480417ba9e311fb91b6`. The log was read in full and shows
successful compilation and completion. The connector marked the report as
truncated in transit (middle missing), despite its extraction metadata saying
otherwise. Only visible sections were inspected; do not infer omitted lines.
All results are bounded pseudo-disassembly, not live validation.

* UnitCanAttack, after the two `0x1FC37F0` calls, checks both pointers for null.
  If non-null, `0x1D48F5B` calls `0x27EF450` with the first resolved object
  in RCX, the second in RDX and R8D=0. It tests AL and stores a derived boolean
  at stack `[RBP+0x6F]`; null or false paths clear AL. Later visible bytes
  enter suspicious/overlapping instructions, so the entire Lua result path is
  not established. `0x27EF450` is a direct-call predicate lead, not a proven
  ABI or a field offset and must not be invoked by gameplay code.
* Input continuation `0x23FB220` conditionally calls `0x215A80`, clears bits
  in `module+0x61B3F18`, reads a 32-byte-indexed table based at
  `module+0x7DE95B8`, and calls `0x214E30` on a traced path. A later block
  aggregates nonzero table bytes as bits in `module+0x61B3CD8`. This is an
  input-state lead, not demonstrated click-to-move or destination data. Exact
  xrefs to the mask show only sites in this helper; xrefs may be incomplete.
* Visible guard branch `0x2876D7C` returns the saved-image 1.0 float. The
  helper also dispatches through an indirect jump table, which this trace does
  not follow. Some `0x26E17F0` output is omitted by the connector; its switch
  and return paths cannot be reconstructed from the received excerpts. Do not
  assume the computed scale is always 1.0.
* No game state or offsets promoted; no gameplay source changed. Script
  `WowBAttackInput70291.java` prepares a bounded trace of predicate `0x27EF450`,
  context xref instruction site `0x23C59B7` (not an entry), and input event
  call target `0x214E30`. There are no game calls or writes; syntax parsed,
  but script has not been compiled/run locally. Analyze with the requested rea,
  Gemini CLI Flash 3.5 Lite and Ghidra when available; this sandbox lacks them.

## Attack predicate and movement context trace (16:22, user machine)

Report `15m7nLxPUIiHiyPe_qbUe__un-dGPKuX_`, MD5
`f97628d6d0644a6fea4c896f0d9da0ca`; log `1uAXxbNRdlyq3g129S4QhYDrZg8JalrEY`,
MD5 `fc32e9110c9a08587664670e7028900a`. Both read fully. Script compiled and
completed, with instruction caps and unverified pseudo-disassembly paths.

* UnitCanAttack-associated `0x27EF450` saves RCX and RDX as RDI and RBX.
  It checks object type bytes +0x24 against a table and, conditionally, flags
  at object +0x26D0. At `0x27EF4F6` it reads a **dword from the second resolved
  object +0x1E60**, testing bit 1, bits 20/16/25 and the low-byte sign bit;
  these can branch to `0x27EF6FE`. It also reads second object +0x94, tests
  +0x1E68 bit 8 and +0x1ABE bit 7, then reads first object +0x1E60. These are
  static predicate checks, not a complete attackability implementation or named
  flag mapping. `+0x1E60` has stronger evidence as a flags-containing dword
  in this predicate, but neither object identity with snapshot `record+0xD8`
  nor flag semantics, general validity or the final return are proven.
* `0x23C59B7` tests global byte RVA `0x617DFC8` and calls `0x23FDDE0` and
  `0x23FDCC0` under guards, then calls `0x23C5460`; subsequent branch decoding
  includes suspicious instructions and is truncated. Ghidra identifies its
  containing function as entry `0x23C5560`, but neither label nor call graph
  demonstrates click-to-move. No destination or safe movement ABI was found.
* Input target `0x214E30` consumes a 32-byte block from its RDX argument,
  manipulates an input mask and branches/calls; its trace hits the 65-instruction
  cap. The block is not established to be a world coordinate or CTM request.

Next read-only `WowBAttackMove70291.java` traces the attack-predicate
continuation and common branch target (both **sites, not function entries**),
and the Ghidra-labeled movement-function lead `0x23C5560`. It does not invoke
any game code and may miss backward/indirect paths. Syntax parsed; not locally
compiled/run. Use rea + Gemini CLI Flash 3.5 Lite + Ghidra when available;
these and the decoded image are unavailable in this sandbox. No gameplay
source or offset constants were changed.

## Attack and movement trace (16:27, user machine)

Report `1DHkwqpuR-pQhDcYjYehlPu1E7Cw6bJ1Y`, MD5
`aa4ea60cd2b114a835be0d01aaa0ff9c`; log `1K_PkClDiFwBvnvfj153sNPmbDdDOy1bO`,
MD5 `b9ed2da8995d7578249dede0ec3167c2`. Read in full; script compiled and
completed on read-only project. Findings remain bounded static leads:

* The common branch target `0x27EF6FE` clears AL then returns. Earlier flagged
  tests branch there; this is evidence of a false result on those paths, not
  sufficient to implement the whole UnitCanAttack predicate. At `0x27EF5A7`
  it also tests second object dword `+0x1E64` bit 28; related conditional paths
  check bit 16 on both objects, call `0x27EEAC0`, `0x2816070` and `0x28C39E0`.
  Predicate continuation hit its 65-instruction limit with branches pending.
  Neither field meaning nor object identity with the snapshot is established.
* The Ghidra-labeled movement lead `0x23C5560` calls `0x1FC37F0` then jumps
  to previously inspected site `0x23C59B7` if its result is nonzero. Another
  path runs into suspicious overlapping instructions, so this neither proves
  CTM nor offers a safe callable entry.
* To avoid circling the same input-mask path, next script
  `WowBClickEvidence70291.java` traces the attack continuation site `0x27EF673`
  and performs a bounded, case-sensitive ASCII/UTF-16LE search for several
  click/move/right-click substrings and nearby existing references. Searches
  include at most four hits per string/encoding and xrefs to preceding 96
  bytes; those refs may point to unrelated neighboring data. Search absence is
  not evidence that CTM is absent. Decryption, xref resolution, string starts,
  context, object identity, ABI, thread and live behavior remain unproven.
  Read-only; syntax parsed, not compiled/run locally. It may take longer than
  instruction-only probes. Use rea + Gemini CLI Flash 3.5 Lite + Ghidra where
  available; these and the decoded image are unavailable here. No gameplay code
  or offsets were changed.

## Click-evidence scan and attack/reaction connection (16:31–16:32)

Report `1jbIi2V8rTtsAIv8kdS1-bE7hoogyhiJe`, MD5
`778bbb84107f5f6dbb3ec925515b1726`; log `1cjquPWoImm3BSYy7iH4hV3O1HdbbdsmM`,
MD5 `c80c517f29300d2448b78d1695f0e718`. Both fully read; script compiled and
completed. The scan is limited to specified case-sensitive substrings and
initialized readable blocks, not a proof of complete binary coverage.

* The final `0x27EF450` continuation tests bit 3 of both objects' `+0x1E60`
  and calls earlier UnitReaction-associated helper `0x27EDBC0` in both object
  orders on some paths. Return values are compared against 1 or 4 depending on
  the branch; paths lead to AL=1 at `0x27EF6A5` or AL=0 at `0x27EF6FE`.
  Alternate path calls `0x27EF720` with a pointer to a stack byte, potentially
  uses that byte as the result. This is a static connection between handlers,
  not evidence for named reaction values, a complete predicate, snapshot object
  identity or a safe API. Earlier gating paths remain part of the decision.
* No matches for specified ClickToMove/clickToMove/CLICK_TO_MOVE or spaced and
  hyphenated variants. ASCII `autoInteract` at `0x516ED88` had no existing
  xrefs at hit or previous 96 bytes. `RightClick` substrings matched RVAs
  `0x4F4C917`, `0x5074997`, `0x507588D`, `0x5076DC8`; no xrefs within that
  window and the four-hit cap was reached. The first belongs to a string
  ending `RightClickDismiss`; the others' full string identities are unknown.
  No string is established to control CTM and no hit/absence proves behavior.
* Next read-only `WowBRawClickRefs70291.java` inspects bounded printable starts
  of the autoInteract hit and the latter three RightClick hits and searches
  initialized readable blocks for little-endian preferred-image VA64 and
  RVA32 patterns for both inferred starts and substring addresses. Raw matches
  may be coincidental, relocation metadata or unrelated data; lack of matches
  misses RIP-relative, indirect or encrypted references. It also traces the
  attack branch call target `0x27EF720`. Per-pattern hits and xrefs are capped.
  Syntax parsed locally, not compiled/run here; may take longer. No program
  modification or gameplay calls. CTM function/ABI remains unresolved.

## Raw click-pointer scan (16:36–16:37, user machine)

Report `1jHt88nRUlNUS03oykjHh1yE6v6uj6itB`, MD5
`b1cb499e4b9771aee4134c764d321b95`; log `1gbIJ3XnFS1R8rogG1miKi9CejnIYFXlF`,
MD5 `1301c8435c8ce389a8b96f23ca8c3a1e`. Both read fully; script compiled and
completed on the read-only project.

* `autoInteract` RVA `0x516ED88` is followed by the ASCII text **"Toggles
  auto-move to interact target"** at RVA `0x516ED98`. This is stronger evidence
  for an auto-move-to-interact configuration/string lead, but not proof that it
  controls ground-click movement, how it is registered, or a callable CTM API.
  No preferred-image VA64 or RVA32 byte pattern for its start was found in the
  scanned readable blocks; the search does not cover RIP-relative references.
* The `0x5074997` and `0x507588D` substrings are inside
  `GamePadTouchTapRightClick` and `GamePadCursorRightClick`, respectively.
  No VA64/RVA32 hits were found for their inferred starts or substrings. These
  are gamepad input labels, not established CTM leads.
* `RightClick` starts at `0x5076DC8`; raw preferred VA64 bytes occurred in
  `.rdata` at `0x4B74F10`. Ghidra had no exact xrefs to that raw match. This
  might be a table pointer or a coincidence, not proof of execution or CTM.
* `0x27EF720` initializes the supplied output byte to zero, checks bit 3 of
  each object's +0x1E60, and calls `0x27EDBC0` in both directions on one path.
  The 45-instruction cap leaves pending branches; no ABI or relationship enum
  names have been validated.

Next `WowBRipClickRefs70291.java` scans executable initialized readable blocks
for a bounded subset of raw seven-byte REX.W RIP-relative LEA/MOV patterns
whose resolved target is the autoInteract/name/description, the gamepad labels,
the RightClick string or its raw pointer candidate. Each hit may start in the
middle of an instruction or be junk; the 64-hit cap, incomplete blocks, other
encodings, indirect flow and encrypted code limit negative results. It prints
per-block coverage/errors and pseudo-decodes matches without changing the image.
Syntax parsed locally; a synthetic signed-displacement/chunk-boundary model
passed, but the Java script has not been compiled/run here. No gameplay changes
or promoted offsets. CTM and object identity remain unresolved.

## RIP-relative click-reference scan (16:42, user machine)

Report `1b8Am22qgwbiLpkrYAYp56qxJPQO2vMnF`, MD5
`4e06ac91b3344431d9fe568c3c21ab1a`; log `1ZJL0RiusFeWNfIPjkMShDEK7qLK8IzzC`,
MD5 `6f389835e43e08c6d40a576cc1abcb8f`. Both fully read; script compiled and
completed. The 7-byte REX.W LEA/MOV subset scanned 76,822,010 possible starts
in executable `.text`, with no read failures or hit-cap truncation. Other
encodings, other data targets, indirect refs, encrypted bytes and reachability
are not covered. Every match is a raw candidate and may be mid-instruction.

* `0x1A4575`: `48 8D 15 0C A8 FC 04` pseudo-decodes `LEA RDX,[image+0x516ED88]`,
  the `autoInteract` string. At `0x1A4597`, `48 8D 05 FA A7 FC 04`
  pseudo-decodes `LEA RAX,[image+0x516ED98]`, its adjacent description
  `Toggles auto-move to interact target`. They are 0x22 bytes apart; together
  they are a useful static registration/configuration lead, not proof of a
  CVar registration, executing code, ground CTM, function entry, or ABI.
  Ghidra reports no containing function at either site.
* `0x1549A7` addresses `GamePadTouchTapRightClick`; `0x23FCC18` and
  `0x23FD5AE` address `GamePadCursorRightClick`. Only the last has a Ghidra
  containing function (`FUN_1423fd340`); names and boundaries are metadata,
  and none is proven to be CTM. The selected pattern subset found no candidate
  to the `RightClick` string or raw pointer-table location.
* Next `WowBAutoInteractTrace70291.java` starts bounded pseudo-disassembly at
  `0x1A4575`, `0x23FCC18`, `0x23FD5AE` (all **instruction candidate sites,
  not entrypoints**), and dumps unaligned bytes preceding/around `0x1A4575`.
  It may miss backward and indirect flow or decode overlapping junk. It is
  read-only, syntax parsed, but not compiled/run here. This is not rea; use the
  requested rea + Gemini CLI Flash 3.5 Lite + Ghidra when those tools and image
  are available. No code/offsets promoted; ground CTM remains unresolved.

## AutoInteract reference trace (16:45, user machine)

Report `11BTayRo7-b6gG0oRlhiQpgpnIR6dsWxF`, MD5
`017b1b4c5c5734084bf5671041b86c4a`; log `1Sdh8-ZS6_jAiVUNIcIc0tO6Dnu9Uw3Av`,
MD5 `b39082833fc75bb07654a952958c673a`. Both fully read; script compiled and
completed. Static pseudo-disassembly remains unverified.

* At site `0x1A4575`, `RDX` is loaded with `autoInteract`, `RCX` with address
  `module+0x59492E8`, and a pointer to the nearby description is placed on the
  stack before direct call `0x1A45AE → 0x1E4000`. The preceding byte dump has
  `CC 48 83 EC 58` at `0x1A455F`–`0x1A4563`, a plausible boundary/prologue at
  `0x1A4560`, **not a confirmed function entry**. This resembles configuration
  initialization but the callee, object type, lifetime and runtime behavior
  are unknown. `module+0x59492E8` is an address passed by LEA, not an observed
  pointer dereference or validated CVar data offset. Ground CTM is not proven.
* At `0x23FCC18`, the `GamePadCursorRightClick` string participates in a
  case-folded comparison, followed by `0x23FDF60` on one conditional path.
  At `0x23FD5AE`, the same string is passed to `0x4759B30`, alongside writes
  to neighboring globals including `module+0x7DE9610`; earlier trace `0x23FDEA0`
  reads that global. This suggests a gamepad-input configuration connection,
  but does not prove CTM or infer native argument semantics. Both traces capped.
* Next `WowBAutoInteractRefs70291.java` traces candidate initializer
  `0x1A4560` and direct-call target `0x1E4000`; scans the REX.W seven-byte
  RIP-relative LEA/MOV subset for `0x59492E8` and nearby static addresses;
  prints static data/default-string bytes. Nearby addresses may be separate
  globals, raw matches can be false or mid-instruction, and cap/encoding
  limitations apply. No process attach, program mutation or game calls.
  Syntax parsed but not compiled/run here. Requested rea/Gemini/Ghidra tools
  and decoded image are unavailable in sandbox. No offset promoted.

## AutoInteract object-reference scan (16:49, user machine)

Report `1N76oLrYC6NL7O_riI3Mie9pXGiuPz9P7`, MD5
`b18c128e4b17d05bb45f58d626e36461`; log `1aa8QdQwgjoGhS7JakkZF9__pvn39u37Q`,
MD5 `4905963a74684742d0ed8f810ac67626`. Both fully read; script compiled and
completed. Static results only; snapshot code and native APIs unchanged.

* **Correction:** the previous probe labeled RVA `0x3068B70` a "possible
  default string". Its bytes begin `48 89 5C 24 08 57 48 83 EC 20 ...`,
  consistent with executable x64 code, not a demonstrated string. The
  initializer loads its address at `0x1A4569` and places it on the caller
  stack at `[RSP+0x30]` before `0x1E4000`; this makes it a code-pointer/callback
  *candidate*, but its role and calling convention are not yet proven.
* `0x1E4000` saves RCX as RBX and RDX as RDI, derives a case-folded hash of
  the string in RDX on one path, then stores a value at `[RBX]` and the RDX
  string pointer at `[RBX+0x08]`. It hits the 50-instruction cap before the
  later argument handling is fully observed. These statements concern the
  traced branch and object passed by the initializer, not a complete layout,
  runtime value, or ground-CTM proof.
* Raw REX.W RIP-relative scan finds candidate references to address
  `module+0x59492D0` at `0x3068E8C`, `0x312E604`, and `0x492AF00`, plus
  initializer sites. This is 0x18 bytes before the `0x59492E8` address passed
  to the helper; proximity alone does not prove the objects are the same or a
  fixed field relation. `0x492AF00` is also the initializer's post-construction
  RCX passed to an external tail-jump; its role is unproven. Candidates can
  be misaligned/junk, and non-REX or indirect references were not scanned.
* The displayed zero-filled static image data around these addresses is not a
  live CVar state: static constructors and runtime configuration can change it.
  No click-to-move destination/function or gameplay-safe offset established.

Next `WowBAutoInteractCallback70291.java` traces the passed code-pointer
candidate at `0x3068B70`, the raw instruction reference sites `0x3068E8C` and
`0x312E604`, and the `0x1E40D7` callee continuation. The latter three are
**sites, not function entries**. Control flow/ABI, object identity, runtime
state and CTM relevance remain unverified. Read-only script syntax parsed, not
compiled/run here. Requested rea + Gemini CLI Flash 3.5 Lite + Ghidra and the
decoded image are unavailable in this sandbox.

## AutoInteract callback and movement-state lead (16:54, user machine)

Report `1QdYU9bp6Vs79xEn0DgYvbG_oTYti_4Iz`, MD5
`4c6e1a9f51d35b32bfd61d36f86d1f12`; log `1Tm2dwru5aP2953NbvVF5sgjxD43oPV90`,
MD5 `38a77f1f7d0d8f19d919fe09ca4d83b4`. Both fully read; script compiled and
completed with bounded static traces. Nothing below is a live-state or safe
native-call claim.

* The initializer passes `0x3068B70` in the caller's stack argument slot
  `[RSP+0x30]` to `0x1E4000`. In that callee's visible prologue/stack layout,
  `0x1E40EC` loads `[RSP+0x110]`, corresponding to the caller's slot after
  the return address, five pushes and `SUB RSP,0xB0`. The continuation tests
  the loaded pointer and takes an allocation/setup path, but the cap ends
  before a definitive callback registration/invocation is visible.
* At candidate `0x3068B70`, after null checks, the decoded path loads a pointer
  from `[module+0x5FF99F0]`, then follows `+0x490`. It examines the string in
  R8, calls `0x46EBC24`, tests EAX, and sets or clears bit 0 of the resulting
  object's dword `+0xAC`; AL=1 is returned on the visible set path and on the
  null path. The other branch's final instructions were capped. This is a
  strong *static relationship* between the initializer's code-pointer lead and
  a movement-related pointer chain. Earlier movement-path helpers used that
  same global and `+0x490`, but pointer identity, lifetime, field meaning,
  callback ABI, and runtime execution are unverified. Never write it as CTM.
* At `0x312E604` a separate trace passes `module+0x59492D0` to `0x1E8F90`,
  tests AL and sets/clears bit 0 at `[RBX+0xAC]`. That supports another static
  relationship, but RBX identity with the object above is not proven. Site
  `0x3068E8C` passes the same static address to `0x3068ED0`, which writes a
  byte at `[RCX+0xC8]`; relationship with `0x59492E8` still not established.
* No ground CTM function, target, ABI or safe write was discovered. Next
  `WowBAutoInteractBitScan70291.java` traces `0x1E418C` (continuation site,
  not entry) and `0x1E8F90` (call target lead), plus a read-only raw scan for
  select byte/dword TEST instructions using displacement `+0xAC` and masks
  including bit 0. It scans executable blocks with bounded output, reports
  coverage/counts, and prioritizes a broad RVA range **without semantic proof**.
  Other objects, encodings, SIB forms, instruction prefixes, mid-instruction
  matches and encrypted code can mislead or hide uses. Syntax parsed and a
  synthetic boundary/SIB scanner model passed; Java not compiled/run here.

## AutoInteract helper / bounded bit-test scan (16:59, user machine)

Report `1SvvSsmGuiE27T2uRvxaIMB8fDIeGAdWT`, MD5
`d2e5a854879224776d987e6a9f62f474`; log `1MOv8nt3WkfGvw0ABcmINpskeZOBy037U`,
MD5 `063e9c1fc15e6ed07c18522f3a396824`. Both fully read; script compiled and
completed; trace is bounded pseudo-disassembly, not proof of runtime flow.

* Continuation `0x1E4194` stores RSI (the initializer-supplied `0x3068B70`
  on the non-null path) into the first qword of an allocated structure at RDI.
  Subsequent instructions construct/copy a wrapper and call through a wrapper
  pointer at `0x1E41DE`; this is stronger evidence that the code pointer is
  captured for a callback-like role, but does **not** establish that call is
  the candidate's invocation, nor its ABI, thread, or live behavior.
* Helper `0x1E8F90` tests `[RCX+0x10]`: if non-null, returns whether dword
  `[pointer+0x14]` is nonzero; otherwise a logging/assert-like path ultimately
  returns byte `[RCX+0xC8]`. At the earlier `0x312E604` site the address of
  `module+0x59492D0` is passed to it and the return value controls a bit update
  at `[RBX+0xAC]`. This provides a plausible value-query path, not proof of
  runtime CVar value, object identity or a ground-movement operation.
* The existing Ghidra xrefs for `0x1E8F90` include `0x23DD084`, a call site in
  the same broad movement-code region; **its RCX argument is not established**
  and it must not be assumed to reference `autoInteract`. Other refs include
  `0x25C2713`, `0x26DFA79`, `0x26DFB73`, and `0x26DFB87` (capped at six).
* Bounded raw scan covered 76,822,010 starting positions, one executable `.text`
  block, zero failed blocks. It found two selected `TEST [reg+0xAC]` mask-bit-0
  byte patterns: opcode sites `0x7847F9` (`F681AC00000001`) and `0x2256A90`
  (`F680AC00000001`); none fell in the arbitrarily prioritized RVA region.
  Raw opcode starts, prior instruction boundaries, object identities and the
  real executed paths are unproven. This scan omits SIB, many other instruction
  forms, indirect use, encrypted code, and reading first then testing register;
  it does not establish that the flag has no other readers.
* Next read-only `WowBAutoInteractUse70291.java` prints raw predecessor bytes
  and several **alternative unverified** linear predecessor decodings for the
  `0x23DD084` call site and the two raw candidate sites, then bounded forward
  traces. It also traces `0x1E4ED0`, a wrapper-manager lead. Predecessor
  selection is heuristic and has no control-flow or function-entry guarantee.
  Syntax parsed; Ghidra compilation/execution remains pending. No gameplay
  offsets promoted; ground CTM and its ABI remain unresolved.

## Helper call-site context and bit candidates (17:03, user machine)

Report `1m1k6INDmtmgY31C3LNYvqRltmEdKWPei`, MD5
`24cef77f630091274e48491ad570ec3a`; log `1IVWxE0nhSmYABxzx2NLBVAi8rzmKuzfh`,
MD5 `1bb72dfeea32fd604efe55e284cfd3df`. Both fully read; script compiled and
completed. Heuristic predecessor decodings and forward paths are not verified
boundaries/reachability, and no native ABI follows from these observations.

* The three displayed predecessor candidates all converge at `0x23DD07D`:
  `LEA RCX,[module+0x58CF1E0]`, followed by the value-helper call
  `0x23DD084 -> 0x1E8F90`. **This is not the address `0x59492D0` passed by
  the autoInteract-associated site**. The name and semantic relationship, if
  any, of the queried `0x58CF1E0` object remain unknown. One static path then
  calls `0x2803F50` with RCX=RDI, EDX=ESI and conditionally selects R8D, tests
  AL, and may call `0x23DDC80` with RCX=RBX. No destination coordinates or CTM
  ABI are demonstrated.
* Raw candidate `0x7847F9` appears on one plausible sequence following
  `0x7847D0` (preceded by INT3 padding), where RCX is an unknown object and
  its `+0xA8` float and `+0xAC` bit are read. A later branch handles another
  object's `+0xA8/+0xAC`. This does not identify either object as the
  `0x3068B70` callback's global-chain object or establish movement semantics.
* Raw candidate `0x2256A90` follows `MOV RAX,[RBX+0x18]` in the displayed
  candidate predecessor, then tests `[RAX+0xAC]` and `[RDI+0x4C]`. The
  allocation-heavy continuation and unknown objects do not establish a CTM
  connection. Raw patterns can originate in unrelated code or misalignment.
* Wrapper lead `0x1E4ED0` loads through R9 and indirectly `CALL RAX` after
  loading it from an object; the actual object, invocation ABI and runtime
  target are unproven. Do not call this or the captured code pointer.
* Next `WowBMovementHelper70291.java` traces call-target leads `0x2803F50` and
  `0x23DDC80`, and scans selected REX.W RIP-relative references to the queried
  `0x58CF1E0` plus `0x58CF1F8` (nearby hypothesis only). Scan match cap and
  exclusions are printed; static zeroes are not runtime state. Java syntax
  parsed, compilation/run pending. No gameplay changes or offsets promoted.

## Movement helper, shared global-chain lead (17:47, user machine)

Report `1r1bresvhtunHVgwJ5pU1dJLqxj4gohR2`, MD5
`4a983e7f15babcb48a93369dd65c9008`; log `19JQiq14eR-xRrtpMsdYDIAetaATMaw8p`,
MD5 `dc453de76787814ac4a0cac2719ecedf`. Both fully read; script compiled and
completed. Bounded pseudo-disassembly is static evidence only.

* `0x23DDC80` tests dword `[RCX+0x80]`; if nonzero, it clears the field, loads
  `[module+0x5FF99F0]`, follows `+0x490`, and on the non-null branch jumps to
  `0x3132340`. This is the same *pointer-chain expression* previously seen in
  the `autoInteract` code-pointer candidate `0x3068B70`. This strengthens the
  case that the paths touch related control state, but does not prove either
  function runs, the same object remains live, any field meaning, or CTM.
* `0x2803F50` uses incoming RCX as an object, reads dwords `+0x1E08/+0x1E60`,
  consults several calls/flags and reaches its 50-instruction limit. The field
  offsets resemble other unit candidates, but object identity, role and
  compatibility with snapshot pointers are not established. Do not promote.
* Raw RIP-relative search of the specified subset found 5 candidates with
  76,822,010 starts, no failed blocks and no cap: `0x153B70` loads address
  `module+0x58CF1F8`; `0x153BD7` writes RAX to `module+0x58CF1E0`; sites
  `0x2428A6`, `0x23DD07D`, `0x4903760` load address `module+0x58CF1E0`. These
  are candidate instruction sites, not established entries or complete xrefs.
  The queried object's name is not yet known; proximity alone does not establish
  layout. Static image contents are not runtime state.
* Next read-only `WowBMovementState70291.java` traces both selected reference
  sites, the branch target `0x3132340`, and `0x280401C` continuation, and
  reports printable strings from a **limited raw nearby RIP-LEA scan**. Such
  raw matches may be junk/unrelated; names must be tied to decoded instruction
  flow before interpreting them. Syntax parsed, but no local Ghidra/Java
  compilation or execution. No ground CTM destination/ABI or safe write proven;
  no gameplay offsets promoted.

## Movement-state trace and queried-object initializer (17:56, user machine)

Report `1C6h7gER2jdxCJ8DGoRldpR5_FKsKe0wq`, MD5
`5edb1c9513bd03fc11fa6999522b997e`; log `1cdkvuvmP0eUbXmkq5wYCcbZH-572kDTX`,
MD5 `92602105d36ac4bd98700aba5d48a989`. Both fully read; Ghidra ran the
script and saved the report with COMPLETE (bounds apply). Pseudo-disassembly,
raw string candidates and branch traces are static, not runtime proof.

* `0x153B70` loads RCX with `module+0x58CF1F8`, then calls the previously
  traced initializer `0x1E4000`. Nearby raw LEA candidates at `0x153B5B` and
  `0x153B67` reference the description beginning "Boolean indicating if we
  should swap turn directions when driving in reverse" and the name
  `DriveSwapReverseTurnDirection`. The last LEA ends just before the known
  site, but its register and link to this call were not yet independently
  decoded in this report. It is a **strong name lead**, not proof of the
  queried `0x58CF1E0` object's identity or ground CTM. Later `0x153BD7` stores
  a pointer at `0x58CF1E0`; the relation between the two addresses is unproven.
* `0x2428A6` passes `module+0x58CF1E0` to `0x4759D00`; nearby raw strings
  `CursorCenteredYPos` and `enableWowMouse` are from nearby instructions and
  must not be assigned to that object merely due to proximity.
* `0x3132340` subtracts one from `[RCX+0xC4]`; on the zero path calls
  `0x1FC37F0`, then under conditions calls `0x3138260` and `0x3139BE0`.
  This is downstream of the shared global-chain path and is a static control
  lead, not an established ground-destination, stop function or callable ABI.
  `0x280401C` continuation processes floating-point values and globals; their
  interpretation and the RCX object's identity remain unknown.
* Next `WowBMovementContinuation70291.java` decodes the earlier
  `0x153B5B` instruction site to clarify the name-to-initializer relationship,
  and traces `0x3138260` and `0x3139BE0`. Syntax parsed here; Ghidra normally
  compiles/runs the Java script when the provided headless command is executed.
  This newly generated script has not yet been run in Ghidra. No gameplay code,
  safe call or offsets promoted.

## Reverse-turn initializer and three-float comparison (17:59, user machine)

Report `1uJotPFh41W9QrKeIKV0HOQspDWSlJTur`, MD5
`f84c283145e38090000b58040dd28e73`; log `1WylgaVAhydHombje6s23rZZiqh77LsYQ`,
MD5 `a6ff21544c854f8cf411349ef4aa89de`. Both fully read; Ghidra ran the script
and saved a complete bounded report. Static instruction decoding is not a
runtime execution or safe-ABI claim.

* `0x153B5B` loads the reverse-turn description into RAX; `0x153B67` loads
  `DriveSwapReverseTurnDirection` into RDX. The path loads
  `module+0x58CF1F8` into RCX, stores the description in the stack arguments,
  and calls initializer `0x1E4000`. Thus the previous string lead is tied to
  this specific static initializer; it concerns reverse-turn configuration,
  not proven ground click-to-move. A pointer is later written to nearby
  `module+0x58CF1E0`; whether this is the exact queried object/configuration
  representation remains unproven. Deprioritize this configuration route as a
  CTM destination lead unless new evidence connects it to ground clicks.
* The `0x3138260` trace calls `0x285D7A0` with RDX pointing to a stack
  buffer and compares three successive float-sized values at stack offsets
  `+0x20/+0x24/+0x28` to `[RBX+0x23C/+0x240/+0x244]` on one path. These
  could be components of a vector, but their meaning, order, frame, object and
  relationship to CTM are unknown; the decoded path does not demonstrate the
  callee wrote the buffer. Trace capped before the remaining continuation.
* `0x3139BE0` updates float-sized fields `+0x328`, `+0x32C`, `+0x154` using
  XMM1 and helper calls, under a `+0xCC` check. This shows arithmetic, not a
  proven heading, position or CTM action; no offsets promoted.
* Next read-only `WowBMovementVector70291.java` traces the direct-call target
  `0x285D7A0` and two continuation **sites**, `0x313830F` and `0x3138323`.
  Syntax parsed here; run through the usual headless command, which handles
  compiling the script itself. Bounds/branch ambiguity remain. No gameplay
  changes or verified ground-CTM entry/ABI.

## Three-value producer and consumer leads (18:02, user machine)

Report `1zg3phzX2fBtPSZSNi3NVyxwvkuF0jX6_`, MD5
`4257e052f9318c3bddfdc61d8bbdf467`; log `1oD0sggf1-3dxBB0KcsP7qU9Uv5p-7vMb`,
MD5 `8661b1e6aebd097b91ec97b4a76ed521`. Both fully read; Ghidra ran the
script and saved a complete bounded report. This remains static evidence.

* At `0x285D7A0`, RDX (the caller's candidate three-float buffer) is saved to
  RBP. The visible trace performs global/pointer lookup, but hits its bound
  before showing any write through RBP. The reported branch targets
  `0x285D8CE` and `0x285D867` were not explored. Therefore the origin, value,
  width, coordinate meaning, frame and use of the three compared values remain
  unproven. Some decoded sequences may be alternate or overlapping.
* The decoded continuation at `0x3138323` passes RCX=RBX, RDX=RDI, XMM2=XMM0,
  R9D=ESI to `0x3132840` before returning. Previous context showed ESI selected
  based on comparison of three float-sized values on one branch, but the path
  and consumer's actual operation are not validated. This is not a safe ABI.
* Next read-only `WowBMovementVectorFlow70291.java` traces continuation sites
  `0x285D8CE` and `0x285D867` and direct-call target `0x3132840`, with 60
  instructions per seed. They are static leads; branch reachability, register
  values, object identity and coordinate interpretation require further proof.
  Syntax parsed here; run with the headless command, which handles the script.
  No gameplay offset or ground CTM function is established.

## Three-value source delegation and consumer (18:08, user machine)

Report `1NMkX3ebAvuVNpweb5mcpzJN5frTpqohe`, MD5
`52c6cb51affd5b02692e5e94921d4a0a`; log `1E5AB2zkJFYmzW9RvIlh_NLt1PxTH6yAr`,
MD5 `8759fd57fa20bea117949f4cb8e2ca0d`. Both fully read; Ghidra ran the
script and saved a complete bounded report. This remains static evidence.

* The `0x285D7A0` continuation reaches `0x285D8FB`, passing the caller's
  output-buffer candidate saved in RBP as RDX to `0x46C3820`; RCX is RSI and
  R8 points to a locally constructed 16-byte area. It returns RBP as RAX.
  When RBX is nonzero it calls `0x1F0070` and places its return in the local
  area; otherwise it zeroes the area. Neither the `0x46C3820` implementation
  nor its buffer writes were examined here. The three compared float-sized
  values remain unproven coordinates and no CTM relevance is established.
* `0x3132840` tests its input pointers and object fields +0x430/+0xC4, checks
  a possible configuration global around module+0x5956A90, and on one decoded
  branch performs float arithmetic using `[RBX+0x264]` and the XMM2-derived
  value. Trace caps at 60 instructions, with unexplored branches. Meanings,
  reachability and runtime object identities remain unverified.
* Next read-only `WowBMovementValueSource70291.java` traces direct-call target
  leads `0x46C3820` and `0x1F0070` and continuation **site** `0x3132936`.
  Syntax parsed here; Ghidra handles compilation when the script is run. Bounds
  and ambiguous/junk paths apply; no gameplay call, coordinate, field or CTM
  offset promoted.

## Output helper copies twelve bytes (18:12, user machine)

Report `15PBi6_AsXschEkqo63jum04m-2sLwLz5`, MD5
`6356a8128efb4e6e67448687fa9ac723`; log `1hllTh_URDDK6JJnfKa_3nfCjjDemu70p`,
MD5 `70c98ce59855e88956266a639a660a3a`. Both fully read; Ghidra ran the
script and saved a complete bounded report. Findings are static only.

* The `0x46C3820` helper saves RDX (the candidate destination buffer) in RBX,
  passes its R8 argument as RDX to `0x46C2D80`, and then copies **12 bytes**
  from the returned RAX pointer to RBX: an 8-byte `MOVSD` and a 4-byte `MOV`.
  The use of MOVSD here is a raw 8-byte copy, not evidence that it is a double.
  The caller compares three 4-byte pieces as floats on a decoded path, but the
  data origin, reference frame, meaning and relationship to CTM remain unknown.
* The `0x1F0070` helper returns null, RCX plus a nonnegative field offset, or
  follows additional lookup calls, depending on a dword in an indirect object.
  Its objects and runtime behavior are unproven.
* The `0x3132936` continuation performs float comparisons and adjusts XMM6,
  then returns it in XMM0; it is not a ground destination setter established
  by this trace. Earlier paths were bounded and can overlap.
* Next read-only `WowBMovementCopySource70291.java` traces the `0x46C2D80`
  source-pointer lead and site `0x1F015A`, another existing xref to the 12-byte
  copy helper. Its predecessor candidates are heuristic, not established
  instruction boundaries or object identity. Syntax parsed here; Ghidra
  handles compilation when the user runs the script. Ground CTM, ABI and safe
  write remain unresolved; no gameplay offsets promoted.

## Candidate three-value source and cache (18:16, user machine)

Report `1pSpUXeBHxvOs7oCO8RugvT08yqp3ssIR`, MD5
`a7f96521a5364efcf354c5ef6f90e897`; log `1pETX6vXmEfVZogN_DgdUgd-WQzndhBRK`,
MD5 `32fbb6c8e002c81adfee7db91b2fa4ad`. Both fully read; Ghidra ran the
script and saved a complete bounded report. Pseudo-disassembly is static only.

* `0x46C2D80` loads RSI from `[RDX+8]` and checks bit `0x40` at `[RSI+0x20]`.
  If clear, a decoded branch returns `RSI+0x7C`. If set, it copies 8 bytes
  from `[RSI+0x38]` to `[RSI+0x7C]` and 4 bytes from `[RSI+0x40]` to
  `[RSI+0x84]`, then follows additional conditional code; its full path was
  capped. The prior copy helper copies 12 bytes from the returned pointer to
  a buffer and one caller compares three four-byte elements as floats.
  This supports a cached three-element value *candidate*, not proof of a
  world position, frame, player, current target or click destination. In
  particular, the RSI object is not established as the snapshot record/unit,
  and `+0x38/+0x7C` must not be promoted as gameplay offsets.
* A second caller site `0x1F015A` passes an output pointer and a stack-built
  context to `0x46C3820`. Its alternative predecessor decodings contain
  inconsistent starts and are not reliable function boundaries; no independent
  semantic meaning was shown. `0x46C2D80` may transform/update the cached bytes
  on additional paths; their completeness and runtime reachability are unknown.
* Next read-only `WowBMovementPositionTransform70291.java` traces previously
  observed continuation **sites** `0x46C2E6E`, `0x46C2EE4`, `0x46C2EFA` to
  inspect possible transformations. These are not function entries or safe
  APIs. Syntax parsed here; Ghidra handles compilation when the script runs.
  No live validation, coordinate semantics, ground-CTM entry/ABI or safe write
  established. No gameplay changes or offsets promoted.

## Three-value affine-like transform (18:39, user machine)

Report `1fynHX_KLEKEECxP-jq0IHoOHa6wQPtHZ`, MD5
`c23a4df8688b39eabca7ca4727250c2a`; log `1WHyo0-TcjAwa_JiGBAmfaiYnoepRmFal`,
MD5 `befc7dfae761ad46853cd1f0029b9158`. Both fully read; Ghidra ran the
script and saved a complete bounded report. These are static paths, not proof
of execution or of a safe native ABI.

* On one decoded path, `0x46C2F85` calls `0x46C1B90` with RDX pointing to a
  stack buffer. The following instructions load float values from
  `[RSI+0x7C]`, `[RSI+0x80]`, `[RSI+0x84]`, broadcast each and multiply by
  separate 16-byte stack vectors at `+0x30`, `+0x40`, `+0x50`, add those and
  the vector at `+0x60`, then store at least the low float back to
  `[RSI+0x7C]` (`0x46C2FC8`; the trace capped there). This is **consistent
  with affine-like three-component transformation**, stronger evidence for a
  spatial/vector interpretation, but the exact matrix, coordinate frame,
  other two output stores, object identity, runtime path and CTM relation are
  not yet proven. The caller's 12-byte copy could reflect transformed data.
* Alternative paths call `0x46C6270` and proceed to return `RSI+0x7C`;
  various pointer resolution branches and their semantics remain unproven.
  Do not use `+0x7C` or the helper as a gameplay offset/API.
* Next `WowBMovementAffine70291.java` traces continuation site `0x46C2FC5`
  (not an entry), `0x46C1B90`, and `0x46C6270`. Each trace is bounded to 60
  instructions and can decode junk or miss indirect/out-of-window control
  flow. Syntax parsed here; Ghidra handles compilation when run. Ground CTM,
  target coordinates and safe invocation are still unresolved; no gameplay
  changes or offsets promoted.

## Full affine output and ground-click search pivot (18:46, user machine)

Report `1xBooytapP6fzV3ibXreRoF8nJ-D3fv3q`, MD5
`c5d66c740bbc860055e601c7de25e6fb`; log `1t_nGcRnKTVhYG1Is3IYTmvoDbR4pG_JP`,
MD5 `b6fbafb6f2935ae82d50d76aa30cc909`. Both fully read; Ghidra ran the
script and saved a complete bounded report. These are static paths only.

* `0x46C2FC5` shows the remainder of the affine-like calculation. It writes
  three scalar lanes to `[RSI+0x7C]`, `[RSI+0x80]`, `[RSI+0x84]`, clears bit
  `0x40` at `[RSI+0x20]`, then returns `RSI+0x7C`. Together with the earlier
  multiplication and translation-like addition, this strongly supports a
  cached three-dimensional transformed value on that path. It does NOT prove
  world position, axis order, owning object, ground-click destination, ABI or
  runtime execution; the image can contain overlapping/junk branches.
* `0x46C1B90` copies nine dwords from the pointer returned by `0x46C3000` into
  a padded 4x4-style buffer, writes zeros in padding and `0x3f800000` at +0x3C,
  calls `0x46C2D80` and copies its 12 bytes into +0x30/+0x38; an optional path
  calls `0x46C3260` and `0x479A840`. This is consistent with assembling a
  transform and translation, potentially nested; precise matrix convention,
  identities, and runtime recursion/conditions remain unverified. No field or
  function promoted to gameplay.
* Rather than continue deep-diving a likely general spatial-transform utility
  without evidence connecting it to manual ground CTM, pivot back to searching
  distinct ground-click and movement names. Next read-only
  `WowBGroundClickSearch70291.java` searches a limited list of case-sensitive
  ASCII and UTF-16LE substrings (including MoveTo, ClickMove, PointAndClick,
  terrain/world-click terms); it prints bounded printable context and existing
  Ghidra xrefs, then scans initialized executable bytes for a subset of raw
  seven-byte REX.W RIP-relative LEA/MOV references to hit/inferred string starts.
  Caps, partial reads and scan coverage are reported; substring starts/context
  are heuristics, bytes may be junk and absence does not disprove CTM. This may
  take longer than a trace. Syntax parsed and a synthetic signed-displacement/
  chunk-boundary model passed; the Java script has not yet run in Ghidra.
  No C# changes or CTM entry/destination/ABI established.

## Broader ground-click string search (18:49, user machine)

Report `1WAD6i2WYYb30eQLyRHxwMr_PrMYvF7RM`, MD5
`8492884cf8ef4799adfd5edd16d965f1`; log `1FaNUfeLlKzmsOrvWHwLfrqBuW0gvBsXm`,
MD5 `a0068f3e25c143d8e3846982525cc4cf`. Both fully read; Ghidra ran script,
completed searches and saved a complete bounded report.

* Case-sensitive ASCII `MoveTo` had four matches before its per-pattern cap:
  `0x4F6EC70` (exact printable `MoveTo`), `0x50BB649` (within
  `CEffect::MoveToDestroyList`), `0x50E3398` (`MoveToRel`) and `0x50E33B8`
  (`MoveToAbs`). Additional MoveTo matches may exist due to the cap. ASCII
  `MOVE_TO` at `0x5123360` also matched. These strings alone prove neither a
  gameplay movement API nor click-to-move. Other tested spellings/UTF-16LE
  yielded no match in initialized readable image blocks; absence does not
  disprove CTM or exclude unsearched variants/encodings/encrypted bytes.
* Selected raw seven-byte REX.W RIP-relative LEA/MOV scan covered 76,822,010
  starts, no failed blocks/cap: `0x1844575 -> 0x4F6EC70`, `0x17A092 ->
  0x50E3398`, `0x17A1D2 -> 0x50E33B8`, `0x2756205 -> 0x50BB640`.
  No selected raw reference to `0x5123360` was found. Raw sites can be
  misaligned/junk and indirect/different instruction references are omitted.
  Existing Ghidra xrefs were absent for all string hits/preceding windows.
* Next read-only `WowBMoveToRefs70291.java` traces the first three raw
  reference sites (NOT function entries), prints heuristic alternative
  predecessor decodings and surrounding printable image bytes, and scans for
  raw VA64/RVA32 pointer patterns for `MoveTo`, `MOVE_TO`, `MoveToRel`,
  `MoveToAbs`. Pointer hits may be incidental; caps, missing relocations,
  alternate encodings and indirect flow prevent negative claims. Syntax parsed;
  user Ghidra compiles when running. No ground CTM or safe ABI promoted.

## MoveTo names and candidate code pointers (18:54, user machine)

Report `1ebgvBImeYSeUBxF-FV4vf2C_DG5dCHIJ` MD5
`cfcb43ea5a297ab2924aade3dd895ddf`; log `1130O0AsMlN1_P2FCB11l7mdxdrE7taUi`
MD5 `1127f4e85097d76718a49e691ac53f93`. Both fully read, Ghidra completed
and saved the bounded report; image identity sample matched prior UnitHealth
16 bytes (not full-image identity). All statements below are static leads.

* Exact `MoveTo` at `0x4F6EC70` appears beside `PetOrdersMeta`, `Attack`,
  `Follow` and `PetOrders`. `MOVE_TO` at `0x5123360` appears near pet action
  text and `CGPetInfo::GetPet`. This context favors a pet-order interpretation,
  not evidence for manual player ground-click CTM; nearby strings alone do not
  prove their data structures or actual usage. Preferred VA64 match for
  `MOVE_TO` at `.rdata+RVA 0x4BB0320` had no existing exact xrefs; raw patterns
  otherwise returned none for the four names, but alternate encodings and
  indirect references remain possible.
* `MoveToRel` and `MoveToAbs` appear near `MoveSplineRel/Abs`, `IsMoving`,
  `FollowPath`, `SetSnapToGround` and `GetRemainingMoveTime`. At raw reference
  `0x17A092`, decoded instructions place the name in global `0x6280A70` and
  code pointer `0x295E290` in `0x6280A88`; later `0x295E390` is placed in
  `0x6280A98`. At `0x17A1D2`, analogous stores place `MoveToAbs` at
  `0x6280AE0`, `0x295E4D0` at `0x6280AF8`, and `0x295E5D0` at `0x6280B08`.
  Registration-like structure and pointer associations are plausible, not
  proven roles, executable flow or an ABI. These could be unrelated to CTM.
* Next read-only `hunt/WowBMoveMethods70291.java` bounds pseudo-disassembly
  of these four candidate pointers to 90 instructions/0x500 bytes and up to
  two encountered direct callees per seed to 65 instructions/0x300 bytes,
  with a shared de-duplication set; existing exact xrefs are capped. It does
  not call client code or modify the program. Syntax parsed locally; Ghidra
  compilation and execution are untested. No C# edits, gameplay functionality,
  CTM destination or safe calling convention established.

## MoveToRel/Abs pointer trace (18:58, user machine)

Log `1njIRgvU7-6kKtOnjnk0sxIIIoETyvR_O` MD5
`51a4ee5fc7b80ad13d22fbd33bdceafd` fully read; script ran and saved report.
Report `1IYEX92frKk4et1-txocbnti3C-lnBHF0` MD5
`42ae4da9fd4f2ae2fe4c4aea9a5d4348` says complete, but Drive response omitted
its middle; the following statements rely only on visible portions, not a
complete report read. Pseudo-disassembly may be wrong or unreachable.

* `0x295E290` performs TLS/guard checks, calls `0x2971B30` on a stack buffer,
  copies a large structure to global `0x8157BF0` and returns that address.
  The visible trace for `0x295E4D0` has analogous checks and copy to
  `0x8157F90`. These look initialization/metadata-like rather than direct
  evidence of movement; identities and execution unproven.
* `0x295E390` puts `0x29399C0` in R8 and `0x6280A68` in RDX, tail-jumping
  to `0x2971D10`; `0x295E5D0` puts `0x296B810` in R8 and `0x6280AD8` in
  RDX and tail-jumps to the same helper. The code addresses are new leads,
  not proven callbacks, APIs, executable paths or CTM.
* Next `WowBMoveCallbacks70291.java` traces those two R8 code-pointer leads
  and the common tail-jump target, 55 instructions/0x500 bytes and 12 blocks
  per seed; prints direct call leads but deliberately does not follow them to
  keep output small. Existing exact xrefs capped at 6. It is read-only; syntax
  parsed locally, Ghidra compilation and execution untested. Image match is
  limited to the prior UnitHealth 16-byte sample, not full identity. No
  gameplay offset, CTM destination, signature or safe native ABI established.

## MoveToRel/Abs candidate callbacks (19:01, user machine)

Report `10Y-oeGt3X7_6emmCbBmCLHBSPUkdBD17`, MD5
`ff0802a2763c97441d2db9f467718679`, and log
`1F98iX45Bm-9jgvWMbcFX0jZR6qyR7V9W`, MD5
`4d09fa7497dc197d6238c47dbf343ab2`, fully read; Ghidra ran and saved a
complete bounded report. 16-byte UnitHealth image sample matches previous
observation; not full identity proof. Static decodes may contain junk.

* `0x29399C0` tests pointer `[RCX+0x70]`; if non-null it sets `R8D=1`,
  stack byte `+0x20=1`, copies XMM2 to XMM3, then calls `0x28B46F0`.
  At `0x296B8E2` the absolute-associated path sets `R8D=1`, stack byte
  `+0x20=0`, copies XMM4 to XMM3, and also calls `0x28B46F0`; trace
  reached that block via conditional path. The flags' meaning, input object,
  argument types, actual path and safe ABI are unproven.
* In `0x296B810`, one conditional path reads three float-sized components
  from `[RDX]`, `[RDX+4]`, `[RDX+8]`, multiplies and adds 16-byte vectors
  and writes three scalar results back to the same addresses. Other branches
  differ. This supports an affine-like transformation, but does not establish
  world/relative frame, coordinate axes, client state or ground-click CTM.
* The common `0x2971D10` saves R8 in RSI, tests values via several calls,
  and the trace reaches the 55-instruction limit before fully showing what
  happens to RSI. Its ABI and dispatch semantics are unknown; some call RVAs
  are in a range previously associated with Lua but that is not sufficient to
  identify this helper. The trace can miss branches and overlapping code.
* Next `WowBMoveShared70291.java` traces shared callee `0x28B46F0`, plus
  continuation sites `0x296B8DD` and `0x2971DE4` (not entries), each bounded
  to 75 instructions/0x500 bytes and 12 blocks. Direct-call targets are listed
  but not followed. Syntax parsed locally; Ghidra compilation/run untested.
  No implementation, destination offset, engine CTM function or safe ABI
  established.

## Shared movement callee and dispatcher (21:12, user machine)

Report `1qCFpt9qXB94zgFeXANWdUp7xKjVR17Jn`, MD5
`cafad4a41c9cd1cfa66b651bf51e1693`; log `1iMrDGiCBl58V8TpMlF39RnX91v-FLj5X`,
MD5 `df1dea269a975065268543fbc6479716`. Both fully read; Ghidra completed and
saved bounded output. UnitHealth 16-byte sample matched (not full identity).

* Starting at `0x28B46F0`, shared callee saves inputs, computes a size based
  on `(R8+1)*12`, and prepares multiple dynamically sized, 16-byte-aligned
  stack regions. Trace reached instruction cap at `0x28B483D`; operations
  after setup remain unknown. Do not infer object ownership or semantics.
* Continuation `0x296B8DD` confirms transformed `[RDX+4]` store falls through
  to `0x296B8E2`, which calls shared `0x28B46F0` with stack byte 0. This
  links the previously decoded affine-like path to the shared call statically;
  reachability and actual coordinate frame remain unverified.
* On one path from `0x2971D10`, R8 is saved in RSI, prior checks are made,
  then `0x2971E3E` calls RSI with RCX from `[RSP+0x70]`, RDX pointing to a
  12-byte stack value, and XMM2 receiving converted scalar from a double.
  This supports a callback-like role for the supplied pointers, not proof of
  native ABI, type, game context, protected status, reachability or player
  click-to-move. Other branches/error paths exist.
* Next read-only `WowBMoveSharedFlow70291.java` decodes continuation site
  `0x28B4840` (not a function entry), bounded 150 instructions, 0x900-byte
  window and 12 blocks, printing direct targets but not following them. It
  also prints 112-byte raw/ASCII contexts at `0x50B91B0`, `0x50B91F0`,
  `0x50B9250`, `0x50B9290`, `0x50E1DB0` referenced by dispatcher paths;
  printable text is not proof of actual use. Syntax parsed locally; Ghidra
  compilation/execution untested. No CTM function, destination or safe ABI.

## Shared callee spatial calculation (21:15, user machine)

Report `1eO4ieWNo4g816fP6z_nruJJMbg7qgv5W`, MD5
`ae67e6fa166658ec4ed6f8c9f2995c06`; log `13XYLdXI64m205tiiyuOHI4z7EwcPHdbZ`,
MD5 `b99d650d9843a15da28584a341c88931`. Both fully read; Ghidra ran and saved
complete bounded output. Image sample matched prior 16 UnitHealth bytes.

* The decode from `0x28B4840` reads 12 bytes from `[R8+0x50/+0x58]` and
  12-byte elements from `[R12 + index*12]`, conditionally transforms a
  three-component element with 16-byte vectors `[R15+0x20..+0x50]`, then
  calculates squared component differences, takes `SQRTSS` and accumulates a
  float, appending 12-byte elements to a buffer on a decoded branch. This is
  strong static evidence of a spatial/path-length-like calculation; does NOT
  establish actual reachable flow, axis convention, object identity, player
  movement, navigation, click-to-move or a callable ABI. The 150-instruction
  cap stopped before 5 pending branches were explored.
* Dispatcher data includes `SYNTAX ERROR ... Expected ... parameters` and a
  `float` type string. Alongside the indirect call and previous associated
  names, this supports an argument-validation/dispatch interpretation; it
  does not establish that invoking it externally is safe or permitted.
* Next read-only `WowBMovePathTail70291.java` traces branch targets
  `0x28B4B7C`, `0x28B4BAB`, `0x28B4C12` (continuation sites, NOT entries),
  each bounded to 55 instructions, 0x500 bytes and 12 blocks; also prints
  byte/ASCII context at two addresses referenced in the shared callee.
  Syntax parsed locally; Ghidra compilation/run untested. If tail suggests
  model animation or a generic subsystem, pivot back to direct ground-click
  evidence rather than assume this is CTM. No gameplay offsets promoted.

## MoveTo path tail and less tedious batch workflow (21:17, user machine)

Report `18HdSG5UTT0PwkS6HozZ53_IO3EAWvevA`, MD5
`009be9565cf9e1802c52b5ac9aa9ab39`; log `1TtqspPnFmHZPA2n4XHHe9sG_cF7iQEgL`,
MD5 `3ef185f1bf51c1e57c4156e30b7240a7`. Both fully read; script completed.
Log includes an earlier partial startup as well as the successful run.

* Tail loops through point-buffer processing and later divides by the distance
  accumulator. Two continuation traces have overlapping instructions and hit
  their 55-instruction caps with pending branches, so these are not complete
  control-flow reconstructions. Data used by allocation-like calls includes
  `WowGetRawTypeName<class CEasingData>` at `0x50D80E0`; this makes an easing/
  animation or generic spatial subsystem plausible, not proof of its exact
  identity or that it can never participate in gameplay. No evidence ties it
  directly to manual ground-click CTM. Pivot to distinct ground-click leads.
* User noted previous download/run/upload loop was tedious. Next read-only
  `hunt/WowBBatchCtm70291.java` groups a bounded ASCII substring search
  (`MoveTo` cap 16; other selected terms cap 5), inferred printable context,
  raw selected REX.W RIP-relative LEA/MOV references (24 cap) and short
  pseudo-disassembly of first six raw sites (16 instructions each) in one
  timestamped report. Search scans initialized readable blocks and raw scan
  scans initialized readable executable blocks; bounds, failed blocks and caps
  are reported. No UTF-16, complete instruction set, indirect refs or runtime
  evidence; matches/decoding may be junk. First six are scan order, not ranking.
  Syntax parsed; no Ghidra compilation/execution here. Successful runs need
  only one report uploaded to existing handoff Drive folder, then user can
  say 'done' and agent searches folder; ask for headless log only if failure.
  This is not automatic Drive upload and does not eliminate human review.
  No changes to gameplay code, no validated CTM function or safe ABI.

## Batched CTM name/reference survey (report pasted by user)

User pasted the full report including `COMPLETE (bounds apply)`; no Drive report
or separate headless log was supplied/needed. Report shows image base
`0x140000000` and matching prior 16-byte UnitHealth sample, not full identity.

* ASCII `MoveTo` now has five hits, below cap 16 and with zero reported failed
  blocks: prior `MoveTo`, `CEffect::MoveToDestroyList`, `MoveToRel`,
  `MoveToAbs`, plus `PetMoveTo` at `0x5123848` (substring `0x512384B`). This
  further supports a pet or generic movement context for some matches, not
  player ground CTM. Other explicitly searched case-sensitive ASCII strings
  had no matches in initialized readable blocks; other forms, encodings,
  encrypted bytes and incomplete dump remain untested.
* Seven-byte selected raw REX.W LEA/MOV scan covered 76,822,010 starts with
  no reported failed blocks or cap, finding the same four known sites
  `0x17A092`, `0x17A1D2`, `0x1844575`, `0x2756205`. There was no raw hit to
  the PetMoveTo substring or inferred start in that limited encoding set.
  These are byte-pattern candidates, not verified executing instructions; no
  other encodings or indirect references tested. Four short traces repeated
  prior registration-like/generic/effect leads; no ground CTM identified.
* Offline triage of `hunt/api_handlers.tsv` surfaces registered
  `SimulateMouseClick` `0x155FBB0`, `SimulateMouseDown` `0x15612C0`, and
  `SimulateMouseUp` `0x1562590`; pre-existing Ghidra metadata contains
  `0x23FD340` around `GamePadCursorRightClick`. These could clarify generic
  input plumbing, not established world-click handlers, a bypass, safe native
  ABIs, or evidence they can be invoked in the game. Do not execute them.
* Prepared `hunt/WowBInputCtmBatch70291.java` as an optional later read-only
  survey of those four seeds, up to 35 instructions per seed and 24 per one
  deduplicated direct-callee lead per seed. Exact Ghidra xrefs capped at six,
  branches/indirects can be missed and instructions can be junk. Syntax parsed
  locally; Ghidra compilation/execution untested. User finds repetitive runs
  tedious: don't automatically impose more one-step runs. Seek fewer batched
  runs and a Drive-for-desktop synced-folder option if available; the latter
  needs user setup and is not an existing automatic upload. CTM destination,
  game-state and safe ABI remain unresolved.

## Input-handler batch and concrete movement blockers (21:24, user machine)

Report `1LY3JL5ayOR6WzfiBUcuT5tnyl5futmUR`, MD5
`8d0c23cef7a264cf9cd88c5f055576f0`, fully read and ends `COMPLETE (bounds
apply)`. No separate log was provided; a successful bounded report suffices.
The prior 16-byte UnitHealth sample matched; not full identity proof.

* Decoded entry paths for registered `SimulateMouseClick`, `SimulateMouseDown`,
  `SimulateMouseUp` call `0x7479B0` with small values and branch on AL; their
  other shared call `0x749800` receives a pointer to the respective name on
  some paths. What those checks mean, whether protected, what events result,
  and whether they reach world ground clicks remain unproven. Some later
  pseudo-decodes are suspicious/overlapping and trace caps leave pending flow.
  Do not invoke these functions or claim a workaround.
* Metadata seed `0x23FD340` shows repeated registration-like calls
  `0x4759B30` with code pointers, but the bounded output does not establish
  an event path to ground CTM, a movement setter, or an ABI. Input batch
  is not a gameplay implementation and no offset was promoted.
* Code review confirms `Navigator` defaults to `ClickToMoveMover`; in addition,
  `MeshNavigator` has direct `WoWMovement.ClickToMove` calls. `LocalPlayer.SetFacing`
  uses CTM Face, so the existing `KeyboardMover` is not a ready-independent
  x64 fallback. `TreeRoot.Start` intentionally refuses x64 targets. A bot with
  no validated movement route cannot operate normally; even successful CTM
  discovery alone would not resolve x64 actions/objects, SQLite, or the start
  guard. 3.3.5a constants and signatures must not be reused for 70291.
* Next priority: read-only, controlled observations of manual ground clicks
  to seek movement state correlated across multiple distinct destinations,
  same/different positions, stationary/moving/stop controls and repeated runs,
  followed by targeted Ghidra + rea/Gemini static tracing. A candidate may
  instead be cursor state, camera/world projection, player position or stale
  memory; no write/call or claimed ABI without corroboration. Design a
  low-friction batched capture before asking user for another run. Prove
  coordinate meaning, lifetime and identity, then separately establish entry,
  stop and thread/calling context; implement guarded first-class CTM only
  after reliable evidence. CTM isn't obstacle avoidance: navigator remains
  responsible for pathing and stuck handling. Other target/reaction/combat,
  quest/loot and x64 SQLite/startup work remains necessary.

## Low-friction live differential capture prepared (2026-10-28)

User asked to continue toward CTM. Added stand-alone `Tools/WowBCtmCapture/`
read-only x64 Windows/.NET 10 project. It opens WowB with query/VM_READ rights
only, validates AMD64 PE, and takes idle1/2, manually clicked A1/2 and B1/2
snapshots before analyzing offline. All six scans are partial/non-atomic. It
filters 4-byte-aligned finite float triples stable within pairs and different
between A/B; output is a uniform sample of at most 200 with complete count and
read/cap metrics. Coverage: 128 MiB total main-image writable and private pages,
limited by scan ordering and chunk boundaries. Up to ~768 MiB + overhead RAM.
Gaps: no repeated same click, independent motion control, actual click coords,
encoded/unaligned fields, some allocations, ABA or full memory coverage. A
result can be current position, cursor, model, camera, stale, or coincidence;
it is NOT an identified CTM offset or safe API. Report includes raw addresses
and sampled floats, and user should review before sharing.

Tree-sitter C# syntax parse passed; independent Python model of the documented
filter passed five basic cases (not execution of the C# code). The repository
lint was run: it returns one unrelated pre-existing false-positive at
`GreenMagic/LuaBridge64.cs:1752` for `_moduleSize`, which is actually used in
`clientRip > _base + _moduleSize`. This lint does not compile the new tool.
Sandbox lacks .NET SDK and WoW; project compile, bundled `--selftest`, Windows
P/Invoke and live capture are all untested. User-machine build and report are
needed; no gameplay code/offset was changed or claimed functional. The six
snapshots are captured before analysis to reduce movement delays; scan durations
are recorded and target identity sample is only partial. Existing x64 guard and
legacy 3.3.5a paths preserved.

## Live v1 CTM capture and targeted v2 (21:38, user machine)

Report `1laiySdax8s4p6zXSdmx8oxg72TfTNTsE`, MD5
`a576006fd21b44ce3d1a1913236c56fa`, fully read; v1 compiled and completed on
Windows. PID 12184, base `0x7FF7E0EB0000`; UnitHealth 16-byte sample matched
prior observation, not full identity. 133,525,504 of 134,217,728 planned bytes;
`scopeCapped=True`, four chunks failed at idle1, 266 completed all six scans.
Each scan took 0.61–0.85 s. Stable-A 31,109; AB-changed 139; stable-B 49,
all 49 shown (`outputCapped=False`). No selected matches were image RVAs;
all shown were private VA candidates. Most displayed triples include zeros,
ones or overlapping partial values; nothing alone identifies a ground target.
No screenshot, movement timing or exact click position is inferred from report.
Private VAs belong to this process instance and cannot be assumed persistent.
Zero direct CTM offset/function identified; caps and omitted memory preclude a
negative conclusion. Player record position wasn't recorded by v1, so report
cannot establish that the player's allocations were inside its selected scope.

Modified `Tools/WowBCtmCapture` to v2: GUID128 player lookup across pool slots;
checks occupied tag, marker, pool/array, full GUID before/after position read;
record+0x60 sampled after each phase; player unit only used as anchor if vtable,
back-pointer, GUID match. This is non-atomic and cannot eliminate ABA. It
prioritizes nearby writable private chunks from those allocations, then image
and stratified private chunks rather than only lowest-address private pages.
Same 128 MiB / six non-atomic captures / ~768 MiB budget; additional per-scope
counts and approximate distances to player sampled after A1/B1 are reported,
not used as proof or exclusion. Matching triples remain leads only, full ABI
unknown. No fixed old absolute addresses reused. Selection is still partial
and may miss CTM; no same-click repeat or independent motion control included.

v2 syntax parsed locally; independent selection-index model passed, and bundled
synthetic `--selftest` extended for selection/comparison logic but neither it
nor Windows compilation or live v2 run executed here (sandbox lacks .NET/WoW).
v1's Windows success does not prove v2 build. `python3 Tools/lint_cs_types.py`
reported only previously documented unrelated `_moduleSize` false-positive at
LuaBridge64.cs:1752; lint doesn't compile capture code. Both ZIPs tested.

## Experimental write-tracing probe (local-server authorization)

User explicitly approved invasive methods and accepts crashes. Created separate
`Tools/WowBCtmTrace/` rather than modifying the previously delivered v2 capture.
The batched tool performs the fresh v2-style six-snapshot capture and, if there
are candidates, heuristically selects up to four non-overlapping triples, one
4-byte component each. It then attaches the native Windows debugger and uses
per-thread x64 hardware data breakpoints to report write hits. It is **not a
custom injected DLL** and does not call game functions or patch game code;
`DebugBreakProcess` requests breakpoints in the target, thread debug registers
and debugger state are changed, and debug events suspend the client. Manual C,
D, K labels provide two ground-click phases and a keyboard-only control. Up to
3,000 events / 90 sec, 200 detailed distinct phase/thread/RIP/slot signatures.
Output includes RIP after write, instruction-window bytes, registers, raw stack
qwords, writer signatures and cleanup/coverage information. Return addresses in
raw stack are not a reliable unwind. Candidate choice can be irrelevant, can
omit CTM, may reuse allocations, and watchpoint use can perturb gameplay.

Important: syntax parsed; separate Python ctypes layout and DR7 bit model passed,
`git diff --check` passed. C# lint still flags the prior unrelated known false
positive in LuaBridge64.cs:1752. **The trace source has not been compiled on
Windows, its bundled selftest has not been run, and no debugger APIs have been
exercised against any Windows process or live game.** The first real run is a
prototype test, not an established finding. The tracer attempts to restore
occupied debug-register slots and detach while stopped; on abnormal exit or
cleanup uncertainty restart client. Existing debugger/anti-debugging/access
conditions may prevent attachment. Zero output doesn't refute CTM. Continue with
Ghidra, rea and Gemini using any trace signatures; do not label a writer RIP a
function entry or publish a CTM offset/ABI without corroboration.

## User debugger trace 22:02 and next injected probe

User provided capture `1IAkUKz6qrAe2NC7ERqbLpn9C4Gaeg_Q3`, MD5
`90f7de4d5edee52319c9acb8fd7f73af`, 42,945 bytes, and watch trace
`191D6ANwVdEotK-k7NVGSZVWD4RgpNgDN`, MD5
`ae5ae0f72b37c356e847f9749065d6d1`, 6,876 bytes. Capture tool output was
middle-truncated by transport, so only its visible header/tail and the complete
trace are available; do not infer omitted candidates. User said they moved
**only with the mouse**: the phase named `k` in the trace is NOT an independent
keyboard control.

Capture PID 15176, image base `0x7FF7E0EB0000`, matching 16-byte UnitHealth
sample; player anchor family 1 slot 20 record `0x27A979A1240`, validated unit
`0x27AC40BB120`; positions after scans moved from (10477.058,971.884,1319.893)
to B2 (10523.630,979.825,1316.726). Scope capped at 128 MiB, **imageBytes=0**,
319 chunks planned, 309 fully read, failures 2 at idle1, 7 at A1, 1 at A2;
2,306 B-stable candidates, uniform 200 reported, output capped. This is not
complete image coverage or full candidate output.

Complete debugger report: 71 threads armed, 274 hits, restoreFailures=0,
restored=True, detached=True. Targets 0/1 were coordinate-like heap triples:
0 `0x27BE0CA23BC` A=(10616.100,934.730,1318.719),
B=(10669.176,869.888,1319.493); 1 `0x27BE0CA227C`
A=(10601.695,1019.756,1334.954), B=(10687.312,868.962,1322.252).
These are NOT validated click destinations. The same post-write RIP
`0x7FFA4F2B30FB` (outside the main image) wrote both; exact bytes ending at RIP
`F3 41 0F 6F 44 09 F0 48 83 C0 04 F3 0F 7F 41 F0` show 16-byte SIMD load
`[rcx+r9-0x10]` and store `[rcx-0x10]`. With reported
R9 `0xFFFFFFFFBEDA0000`, this maps watched destination components
`0x27BE0CA23C0` -> source `0x27B9FA423C0`, and
`0x27BE0CA2280` -> `0x27B9FA42280`. These are historical per-process VAs,
not reusable offsets. Source-content equality was not separately recorded in
v1. The writer resembles bulk copy; module identity and CTM relevance unknown.
No independent keyboard control; no CTM function/ABI established.

User authorized and requested injection if no relevant CTM result. New
`Tools/WowBCtmTraceInject/` performs fresh capture and debugger phase, recognizes
only the exact above copy sequence, derives source addresses from live stopped
registers, and requires current 4-byte source/destination equality; if no exact
copy source appears, it targets current candidate components, or GUID/slot-
checked player position components when no candidates. It uses process name +
creation-time identity before launching native `Tools/WowBCtmInject/` loader.
Loader writes remote DLL path and config, creates remote threads for LoadLibrary
and exported setup/stop; DLL uses vectored exception handling, PAGE_GUARD and
single-step rearming for 25 seconds, and logs nearby pre-write RIP/registers.
It attempts to restore guards, but leaves the DLL, handler, TLS and config
resident until process exit to avoid use-after-unload. User MUST restart the
client after the run. Concurrent access/rearm and page reuse can miss or confuse
events; guard access can dramatically disrupt/crash client. No game calls or
code patches are issued. Native exe/DLL cross-compiled to AMD64 Windows with
Zig 0.12.0-dev.1286, -Wall -Wextra -Werror; warnings reported in Zig's MinGW
stdio dependency, not these source files. PE architecture, DLL exports/imports,
C# tree-sitter syntax, offline SIMD address model, DR layout model and
`git diff --check` verified; user-machine .NET build, Windows execution of
native components, injected cleanup and game/live trace **not tested here**.
No inference from mere injection; inspect resulting RIPs with Ghidra, rea and
Gemini and compare later controls before promoting offsets.

## 22:19 security-labelled crash; no injected guard trace

User supplied four files. Metadata:
* injected report `1mvdGysCwQl_j7buoLUqKdINq7dXIRff7`, 596 bytes,
  MD5 `af0c98ec01d3c2b97e304215b121b610`, fully read;
* client crash `1B8v7Vh7HtdfRaFaBcm_yf7UzBnd82xVR`, 128394 bytes,
  MD5 `186babe6dbe955a2c486f0423465acc4`, header plus partial output read;
* debugger trace `1-UVpz-O71g-b90QyvhdwTv_Tat-US36B`, 19551 bytes,
  MD5 `420be7fad8df27b9d363d77e224ae816`, middle omitted by transport; the
  header, detailed start, final signature summary and cleanup footer are visible;
* capture `1xzqlmQ23q026r3QNKxbKmMIT_IHMQMUr`, 40594 bytes,
  MD5 `2ffc0df66383fbf8708f091fe1be1ab9`, middle omitted by transport.

PID 13492. The crash header explicitly says `Security Crash`, exception
`0xC00000E5`, parameters `0xE1D05001`, `0x700`, RIP `0x7FFA543B41CA`
(KERNELBASE), tid 16132, timestamp 22:19:25.296; frames include unknown
modules. This label does **not** establish which safeguard or action caused it.
The debugger trace file is named 22:18:54.752, with elapsed 11.58 s: approximate
end 22:19:06.332. The injector file is named 22:19:32.712, which is 7.416 s
**after** the crash time on the presumed same, unchanged PC clock. The loader
creates this filename at its start, before the remote LoadLibrary call. It
reports `Remote DLL not found after LoadLibrary; return=0x0`,
`loaded=0 started=0 stopped=0 status=1`. Source inspection shows that this
path exits before calling ProbeStart; there are no injected guard events.
`loaded=0` is the loader's observation, not proof a DLL could never have briefly
loaded. PID/creation and WaitForSingleObject checks do not prove a client isn't
already crash-reporting. User experienced a crash during the overall clicking
experiment; the timing is inconsistent with the guard probe causing it under
an unchanged shared clock. Prior debugger activity may or may not be related;
causality remains unknown. Do not rerun invasive sequence until investigated.

Capture matches UnitHealth identity sample, selected player record, 128MiB
capped, imageBytes=0; 303 full chunks, 10 total read failures, 3629 stable B,
200 sampled (capped). Debugger: 71 threads armed, 106 hits, no arm/restore
failures, restored=True detached=True, derivedExactCopySources=0. All 106
signature counts in the visible summary are slot=0x8 (target 3), i.e. none
recorded for the most world-coordinate-like target 0. No independent control;
not evidence no CTM exists. The target 3 data includes (10.162,2.497,-5.874)
and (-8.692,0.703,-8.244), many copies and values like 0/1: plausibly generic
transform/render data, not established. Observed instructions directly before
reported post-write RIPs from bytes and registers:
* RVA `0x1F412C9`: bytes `89 47 FC`, writes `[rdi-4]` on the observed event;
  post-write RIP `0x1F412CC`.
* RVA `0x20409D6`: `0F 11 40 40` stores 16 bytes `[rax+0x40]`; post-write
  RIP `0x20409DA`. An adjacent `0F 10 43 40` loads from `[rbx+0x40]`.
* RVA `0x1F40F8B`: `0F 11 4D 00`; post-write `0x1F40F8F`.
* RVA `0x1F40EBF`: `0F 11 6D 10`; post-write `0x1F40EC3`.
These are instructions, not functions or ABI; no CTM relevance inferred.

Prepared offline Ghidra script `hunt/WowBCtmWriterAudit70291.java`, which checks
live instruction bytes against the decoded project and skips mismatches, emits
existing containing-function metadata and bounded pseudo-disassembly. Java
syntax tree parsed; it is not Ghidra-compiled/run here. It cannot establish
whether debugger attachment caused the security-labelled crash. Follow with
rea/Gemini as appropriate; do not treat static guesses as executable flow.

## Ghidra writer audit and standalone injected 10-second probe

User provided `WowBCtmWriterAudit70291-20261028-222752-398.txt`, Drive
`1qIXdLI5gUVRJDnCb7RiPT2HiXJl5ydNu`, MD5 `071502f6d6095a13e7206e652864caa0`,
10,684 bytes, fully read. Ghidra script compiled/executed, complete. All four
live store instruction byte checks matched decoded project; none had existing
containing-function metadata or exact xrefs. `0x1F412C9` writes a sequence of
scalars sourced from R12+0x5A4..+0x640, and `0x20409D6` is part of a series
of 16-byte RBX-to-RAX copies ending in an indirect virtual jump. `0x1F40F8B` /
`0x1F40EBF` show SHUFPS plus 0x30-byte output patterns, consistent with generic
vector/matrix manipulation, not evidence of CTM. Scope is pseudo-disassembly
with bounded paths; no verified function entry/flow, destination or ABI.

At the user's explicit request created `Tools/WowBCtmQuickProbe/`: a standalone
Windows x64 native EXE+DLL, no debugger/capture prompts. It validates partial
AMD64/build identity, locates and rechecks the GUID/occupied-slot local player,
injects a DLL, page-guards the player position `record+0x60`, and watches for 10s
after ACTIVE. It samples physical right-button state while WoW is foreground;
this is NOT proof of delivered/accepted game clicks. It streams up to 512
pre-write events/regs to disk during the window to help survive a crash.
Player position is NOT a verified CTM destination; one page may be noisy,
rearming races may miss events, and all candidates remain unverified.
Client may crash or report another security error. It attempts protection
restore but intentionally leaves DLL/VEH/TLS/config resident; restart client
regardless of status. This is explicitly user-authorized despite prior crash,
not evidence about the cause, and does not bypass client security.
Native binaries cross-compiled using Zig 0.12.0-dev.1286, `-Wall -Wextra
-Werror`; PE x64 and exports verified. The actual portable player lookup passed
synthetic Linux tests (valid, GUID mutation/race, slot mutation, duplicate,
empty/unoccupied, capacity and alignment). No Windows API, actual injection,
10-second timing or WoW run was tested here. Windows user acceptance is pending.

## Quick probe immediate exit: remote LoadLibrary not observed

User pasted `WOWB STANDALONE INJECTED 10-SECOND ... v1` report for PID 3560.
Partial image identity sample matched; checked player anchor family 1 slot 20,
record `0x1DDC1D841E0`; target position components selected. After 117-module
snapshot, remote allocations were written and loader attempted remote
LoadLibraryW; log reports `Remote DLL not found after LoadLibrary; return=0x0`,
`loaded=0 started=0 stopped=0 status=1`. Thus it never called ProbeStart or
started the 10-second page-guard observation; no click/write events were
collected. `loaded=0` means the loader did not locate the module, not proof of
all transient loader states; the thread exit code is only 32 bits. The log does
not provide the remote thread's `GetLastError`, so it cannot distinguish a bad
DLL/dependency, remote-function mismatch, client protection, client health or
module-snapshot failure. Do not assert a particular cause. Remote memory was
allocated before failure; restart client before further live instrumentation.

Created `Tools/WowBCtmQuickProbeDoctor/` as a low-risk diagnostic: first checks
metadata and performs normal LoadLibraryW in its own process (capturing local
Win32 error), then tests the same remote loader technique in a short-lived
helper child it creates and terminates. It checks DLL path write/readback and
module snapshot; it never opens/attaches/injects into WoW and never calls
ProbeStart. It must be placed next to the existing user probe DLL and produces
one short report, no gameplay inputs. A helper success narrows but does not
prove a game-specific cause. Windows AMD64 binary cross-compiled with Zig
-Wall -Wextra -Werror, PE/import checks passed; no Windows execution here.

## Doctor result (2026-10-28) and probe v2

User ran `WowBCtmQuickProbeDoctor.exe` (parent PID 2060, x64). Results:
`dll_file_present=true size=18432`, `metadata_map=true`, both exports present,
`local_normal_load=true`, helper remote load resolved to `KERNEL32.DLL`, path
write/readback match, remote thread completed, `helper_remote_dll_found=true`,
`module_scan_error=0`, `diagnostic_status=0`. Reported low32 `0x4BF50000` is the
truncated 32-bit exit value, not a full x64 address.

Conclusion: packaging, exports, imports, msvcrt dependency, path and the remote
LoadLibrary technique all work outside WoW. The earlier `return=0x0` +
"module not found" in wowb.exe is therefore not explained by a broken DLL, and
no `GetLastError` was captured there. Game-side cause remains unknown.

Probe v2 (`Tools/WowBCtmQuickProbe/`, ZIP Drive
`1Kcri0dYL1RvAflMPl8EubFO5NFR8PkJJ`, MD5 `cdafd34867d0dc1c3c76d855bff90439`)
replaces the bare `CreateRemoteThread(LoadLibraryW)` with a remote x64 stub that
calls LoadLibraryW, stores the full 64-bit HMODULE, and on NULL calls
GetLastError on the same thread before returning. Report line:
`diagnostic wait=.. exit=.. phase=.. fullModule=.. loadLastError=..`.
Watcher starts only if phase==2, module!=0, exit==0 and module enumeration
matches the returned base. The stub bytes are exercised on Linux x86-64 via a
ms_abi harness (`test_remote_loader.c`), success and failure paths pass. Not yet
run on Windows against WoW. No client security controls bypassed.

## Probe v2 in-game result: stub completed, never executed

Run output (pid 2176, base 0x7FF7E0EB0000, player family 1 slot 20,
record 0x1D0D7DBA200):

```
remote diagnostic code=0x1D122230000 data=0x1D122220000
LoadLibraryW=0x7FFA5682EE30 GetLastError=0x7FFA56808640
diagnostic wait=0x0 waitError=0 exitAvailable=1 exit=0x0 readback=1
phase=0 fullModule=0x0 loadLastError=0
```

Interpretation (evidence grade: reproducible observation, cause NOT established):
the remote thread was created, signalled as finished with exit code 0, and the
data page stayed readable, yet `phase` never left 0. Phase is written by the
stub's 5th instruction, before `LoadLibraryW` is even called. So the load result
was never produced - not that LoadLibrary returned NULL. Combined with the v1
run (same readback path, `return=0x0`, module not enumerated), two independent
runs now show: no memory effect from a thread that reports success. Candidate
explanations still unseparated: code page never executed (thread start
neutralised), remote write not visible to the executing thread, or the thread
terminating before its start routine. No mitigation policy data existed for
either run.

## Probe v3 (built, not yet run)

`Tools/WowBCtmQuickProbe/` - ZIP Drive `1nyZU3h-fV9gefFUtzOyHbKBIGk7o97FN`,
MD5 `b9c8c43daa9e8b53982638da23557d81`, EXE MD5 `512909c11d41c973b1be782a8233f92c`,
DLL unchanged `871eda15ba7a62c71a1faa12e91767a0`.

Changes:
- `remote_stubs.h` holds three vetted x64 stubs (minimal `phase=42`; generic
  single-arg call recording RAX; LoadLibraryW + same-thread GetLastError).
- Preflight readback of path, state struct and code bytes; `VirtualQueryEx`
  protection of the code page is logged before execution.
- Stage A runs the minimal stub via CreateRemoteThread, then via QueueUserAPC
  over every openable client thread, and picks whichever actually writes 42.
- Stage B loads the DLL via the working route; Stage C calls ProbeStart/Stop
  through the generic call stub. Watcher runs only on verified success.
- Read-only `GetProcessMitigationPolicy` dump: DEP, DynamicCode,
  SystemCallDisable, ExtensionPointDisable, ControlFlowGuard, Signature,
  ImageLoad, SystemCallFilter.

Offline checks: all three stubs exercised on Linux x86-64 through an ms_abi
harness (success + GetLastError=126 failure path), player lookup synthetic test,
cross-compiled PE32+ with `-Wall -Wextra -Werror`. Not run on Windows.
APC caveat: queued APCs can execute after the tool exits; restart the client.

## Probe v3 in-game result (pid 18504) - execution route isolated

```
mitigation DEP=queryFailed(87), all other policies 0x0
crt minimalStub: exit=0, phase=0            <- start routine NOT executed
apc: considered=74 queued=74, phase=42, 0ms  <- APC stub DOES execute
apc loaderStub: phase=1 (inside LoadLibraryW)
crt loaderStub: phase=2, error=570 (contaminated by the 74 pending APCs)
```

Evidence grades:
- CONFIRMED (4 independent runs): a CreateRemoteThread start routine in
  wowb.exe is reported created and finished with exit code 0 but produces no
  observable effect in target memory, including an 8-byte stub that only
  stores a constant.
- CONFIRMED: an APC-delivered stub executes on a client thread (phase=42).
- RULED OUT as blanket cause: process mitigation policies. DynamicCode,
  Signature, ImageLoad, CFG, ExtensionPointDisable, SystemCallDisable and
  SystemCallFilter all read 0. DEP query returns ERROR_INVALID_PARAMETER(87) on
  this build; DEP is unconditional for x64 images and cannot be disabled by a
  policy flag that is off. These queries are read-only.
- NOT VALID EVIDENCE: loader errors 87 and 570. v3 queued 74 APCs of the same
  load stub, so LoadLibraryW was re-entered concurrently; 570 is
  ERROR_NET_OPEN_FAILED, a documented internal placeholder that should never be
  returned to an application, which further suggests a borrowed/stale thread
  error rather than a real loader result. My bug, fixed in v4.

## Probe v4 (built, upload pending)

Single-shot attribution: timed kernelbase!Sleep(1500) start-routine test; one
minimal stub via CRT; one APC per candidate thread (max 4); one control
LoadLibraryW of System32\version.dll; one LoadLibraryW of the probe DLL; one
ProbeStart; 10 s observation; one ProbeStop. Every execution gets a never-reused
slot; a control APC that never runs aborts before the probe load so loads cannot
overlap. Local ZIP `WowBCtmQuickProbe-v4.zip` (MD5
`80aac08fb535d0426c66b7006bd46523`, EXE `46f513706bb169c9359c9a8822e9d30c`).
Drive `1ze7aWJUCEUkO4NzYuhzjSKIfEtzqeb8Z`.

## Probe v4 in-game result (pid 18504) - invalid run, two causes

The report's `pid=18504` and `creation=0x1DD671E94C5052C` are identical to the
v3 run, so the client was NOT restarted and v4 inherited v3's wedged threads
(148 queued APCs, at least one thread stuck inside LoadLibraryW).

Tool bugs found in v4:
- The method flag was never set to CreateRemoteThread before the CRT stage, so
  that stage executed the APC branch with thread id 0:
  `apc openThread failed=87` (ERROR_INVALID_PARAMETER) and the line labelled
  `crt minimalStub` was not a CRT test at all.
- The codeBlock line printed the protection returned by VirtualProtectEx (the
  old value, 0x4) instead of the live protection.
- APC candidates were the same wedged threads, so their APCs sat behind v3's
  stale entries: 3000 ms each with phase=0, versus 0 ms in v3.

VALID new evidence from v4:
- `sleepTest startRoutine=kernelbase!Sleep elapsed=1500ms verdict=routineExecuted`
  - CreateRemoteThread DOES execute a start routine in this client. So the
  earlier phrasing "start routines never execute" was too strong; the anomaly
  is specific to executing our anonymous stub page via that route (in v2/v3),
  not to thread creation.

Not evidence about the game: everything else in this run.

## Probe v5 (built)

ZIP `WowBCtmQuickProbe-v5.zip` (Drive `1jCeP7-0Ju-0AaGyPFCmn4X6bU7JNqory`), MD5 `74100e4dfc06b349c342e5a7713ccf24`,
EXE MD5 `b8c345952c43ae8ef78201d65b701755`, DLL unchanged
`871eda15ba7a62c71a1faa12e91767a0`.

- Restart guard: writes `pid creation` to
  `C:\ghidra-scripts\WowBCtmQuickProbe-last-target.txt`; a second run against
  the same process instance aborts before allocating anything (`--force`
  overrides).
- CRT method flag fixed; one APC per candidate thread, sequential, 12 s each.
- `phase=0` (never delivered/ran) vs `phase=1` (call entered, never returned;
  loader-lock deadlock) are now reported separately for control and probe load.
- Live code-page protection is queried after VirtualProtectEx.

## Probe v5 in-game result (pid 10720, fresh instance) - clean data

```
codeBlock protectNow=0x20 (PAGE_EXECUTE_READ)
sleepTest kernelbase!Sleep 1500ms -> routineExecuted
crt minimalStub: thread exited quickly (exit=0) but slot phase=0
apc tid=6112: 12000ms, phase=0 (never delivered)
apc tid=10636: 16ms, phase=42        <- works
controlLoad System32\version.dll: phase=2, module=0x0, error=87
probeLoad  probe DLL:             phase=2, module=0x0, error=87
```

Facts (evidence grade):
- CONFIRMED: CreateRemoteThread executes a *system* start routine
  (kernelbase!Sleep, 1500 ms) but not our anonymous stub page.
- CONFIRMED: APC delivery works, but only on alertable threads (1 of 4 here).
- CONFIRMED: LoadLibraryW *is entered and returns* (phase 1 -> 2) on that
  thread; it returns NULL with 87 both for a benign signed system DLL in
  System32 and for our probe DLL.
- Therefore: the failure is NOT specific to our DLL, its path, its imports or
  its signature. Mitigation policies read 0 (DynamicCode, Signature, ImageLoad,
  CFG, ExtensionPointDisable, SystemCallDisable, SystemCallFilter), so it is
  not a documented Windows policy either.
- OPEN: whether 87 is a genuine loader result, a hook/wrapper refusal, or a
  stale per-thread value (GetLastError semantics). 87 =
  ERROR_INVALID_PARAMETER: with a valid absolute path and a verified buffer
  this is not the documented outcome of LoadLibraryW path resolution
  (missing file would be 126).

## Probe v6 (built)

ZIP `WowBCtmQuickProbe-v6.zip` (Drive `13b99jWOnSArN46H8P9v53GgRYnwKs_N6`),
MD5 `35313ed813cd486383d140017cd62bf0`, EXE MD5
`1257503a1dea09d43c47fbfab4d5f6d4`, DLL unchanged
`871eda15ba7a62c71a1faa12e91767a0`.

New stages, each one remote execution on its own slot:
- `callVerify`: kernel32!GetCurrentProcessId through the generic call stub;
  must return the real PID or the run stops (validates the call mechanism).
- `bogusLoad`: LoadLibraryW("C:\__wowb_probe_missing__.dll") should give 126.
  Any other code means the error we read does not come from path resolution.
- `ldrLoad`: ntdll!LdrLoadDll with a hand-built UNICODE_STRING (record layout
  extended to 64 bytes; new `remote_ldr_code` stub at code page +192). Bypasses
  kernel32/kernelbase wrappers, so wrapper-level refusal and loader-level
  refusal are distinguishable.
- `crt` lines now separate threadWait / exitAvailable / exit / pollTimeout.

Offline: all four stubs exercised through the ms_abi harness, including
LdrLoadDll success (base written) and STATUS_DLL_NOT_FOUND (0xC0000135) paths.

## Probe v6 in-game result (pid 15980) - loader path is closed

```
codeBlock protectNow=0x20
sleepTest kernelbase!Sleep 1500ms -> routineExecuted
crt: thread exited (exit=0) but slot phase=0        (stub not executed)
apc tid=17096: 12000ms phase=0 (not alertable)
apc tid=1044 : 15ms   phase=42  (executes)
callVerify GetCurrentProcessId -> 15980 == pid  MATCHES
bogusLoad C:\__wowb_probe_missing__.dll -> error=87  (126 expected)
controlLoad System32\version.dll        -> error=87
probeLoad   probe DLL                   -> error=87
ldrLoad ntdll!LdrLoadDll -> status=0xC000071C
```

Conclusions (evidence grades):
- CONFIRMED: the call stub and record ABI are correct - GetCurrentProcessId
  returns the true PID through the same mechanism that reports the errors.
- CONFIRMED: LoadLibraryW never reaches path resolution: a missing path must
  yield 126, it yielded 87. Every path gives 87.
- CONFIRMED: ntdll!LdrLoadDll independently refuses with 0xC000071C =
  STATUS_INVALID_THREAD (verified against ntstatus references).
- Therefore the failure is inside the loader path itself, for calls originating
  from our injected code on that thread - not our DLL, not its imports, not a
  documented mitigation policy, not path handling.
- STILL OPEN: the exact mechanism (patched loader function, per-thread loader
  guard, or call-origin restriction). v7's codeDiff answers this read-only.

## Probe v7 (built): manual mapping, no loader

ZIP (Drive `1-53rI_Jn9war1URR6ma5o0rt7sTL3DQm`); MD5
`a59647e26ed31d1d40b061906e32381d`, EXE MD5
`22c3a5450549b326952924fd1b8a0039`, DLL MD5 `cc0876825f6e8cc3d4a75bf376ec09c1`.

- `codeDiff` (read-only): compares live bytes of kernel32!LoadLibraryW,
  kernelbase!LoadLibraryW, kernel32!LoadLibraryExW, ntdll!LdrLoadDll,
  ntdll!LdrGetProcedureAddress, and kernel32!GetCurrentProcessId against this
  process. Different bytes -> patched=true plus a hex dump of both.
- Probe DLL rebuilt WITHOUT the C runtime (`-nostdlib`, entry DllMain): its only
  import group is KERNEL32.dll with 11 symbols. Verified by parsing the PE:
  image 0x6000, 5 sections, entry 0x17D0, no relocation table (x64 code is
  position independent).
- Manual mapper: allocate at preferred base 0x180000000 when possible, else
  anywhere plus DIR64 relocations; copy headers+sections; resolve every import
  against modules already loaded in the client (kernel32); per-section
  protections; FlushInstructionCache; then call the image entry point once,
  then ProbeStart, the 10 s watch, ProbeStop.
- Static checks: all sections inside the image, all IAT slots in range, single
  kernel32 descriptor with 11 resolvable symbols, entry inside .text.

Toolchain note: the sandbox Zig install was wiped (.cache is not snapshotted).
Rebuild with `pip download ziglang -d /tmp/zdl && unzip -q /tmp/zdl/ziglang-*.whl
-d /tmp/zpkg`, then `/tmp/zpkg/ziglang/zig cc ...`. Zig 0.17 needs
`-nostdlib -I<zig>/lib/libc/include/any-windows-any ... -lkernel32`.

## Probe v7 in-game result (pid 8104) - INJECTION WORKS, anchor did not move

This is the first complete, self-consistent run:

```
codeDiff kernel32!LoadLibraryW / kernelbase!LoadLibraryW / LoadLibraryExW /
         ntdll!LdrLoadDll / LdrGetProcedureAddress / GetCurrentProcessId
         -> patched=false for all six (no byte-level hook)
sleepTest kernelbase!Sleep 1500ms -> routineExecuted
crt stub: thread exit=0, slot phase=0     (CreateRemoteThread still no effect)
apc tid=8836: 0ms, phase=42               (execution route)
callVerify GetCurrentProcessId -> 8104 == pid
bogusLoad missing path  -> 87  (126 expected; loader never reaches path resolution)
controlLoad version.dll -> 87
map: preferred base 0x180000000 free, atPreferredBase=true, NO relocations needed
map: KERNEL32.dll remote=0x7FFDC36B0000, imports resolved=11 unresolved=0
map: writes=ok protectFailures=0 entryReadable=1
mapEntry phase=2 returned=1
probeStart phase=2 result=0
10 s window: 12 right-button down/up pairs, all sampled with WoW foreground
ProbeStop status=0; overall status=0
active=0 pages=1 guardFaults=1 writeFaults=0 nearWrites=0 events=0
```

Established:
- CONFIRMED: manual mapping is a working DLL-load path in this client. The
  OS loader stays unusable (87 / STATUS_INVALID_THREAD) and is not byte-hooked,
  so the refusal is behavioural, not a patched function.
- CONFIRMED: the injected DLL can run remote code end to end (ProbeStart 0,
  10 s of guarded observation, ProbeStop 0, no rearm or restore errors).
- CONFIRMED: PAGE_GUARD was armed on exactly one page (the record page) and saw
  exactly one access in 10 s - a READ. Zero writes. So `record+0x60` was not
  written while the user clicked 12 times.
- NOT ESTABLISHED: whether the character actually moved (clicks were sampled
  physically, and the game may not have received them), and therefore whether
  `record+0x60` is the live position at all. Since not one of the three
  position words was touched, the record is a poor anchor regardless.

Next step is therefore anchor validation, not more injection:
`Tools/WowBFieldDiff/` samples the record window by read-only polling and
reports which words actually change while the character walks.

## WowBFieldDiff v1 (built)

ZIP `WowBFieldDiff-v1.zip` (Drive `1CpEtDTff2EvOTiTDygJDdvfwOtlbjUpl`),
MD5 `842780aa47dd198815810cb61bd91b4e`, EXE MD5
`54e425351cfcdb0fe06be84b694c66e0`.

Strictly read-only (`PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE`):
no WriteProcessMemory, VirtualAllocEx, VirtualProtectEx, remote thread, APC,
DLL or debugger. Polls record and record+0x1000 (0x800 bytes each) every 25 ms
for 10 s, tracks every changed 4-byte word with first/last values and float
interpretation, logs right-button transitions, and prints a summary sorted by
change count. Offline tracker tests (fd_init/fd_compare/fd_sort, clamping,
overflow accounting) pass.

## WowBFieldDiff report (2026-10-29, pid 7208) - anchor validated

User report: `WowBFieldDiff-20261029-094505.txt` (Drive
`1ReFqU14qSmPECBh2gGeJheXubtmLUjGv`, 220,326 B). record `0x1501FABA1E0`,
family 1 slot 20, 0x800-byte window, 25 ms polling, 10 s with 12 right-clicks.

CONFIRMED:
- `record+0x60/+0x64/+0x68` is a live float triple: 10589.54,958.46,1317.32 ->
  10589.38,958.60,1317.35 at t=2062 ms, i.e. 31 ms after RIGHT_UP at 2031 ms.
- `+0xA4/+0xA8/+0xAC` holds the identical values (same triple, offset +0x44).
- Therefore the v7 "zero writes" window was a run in which the character did
  not move, not a wrong anchor. Anchor question closed.

Four position-like triples exist inside the record, all in the same zone area:
| triple | mirror | start value | distance from +0x60 | changes |
|---|---|---|---|---|
| +0x060 | +0x0A4 identical | 10589.4, 958.6, 1317.4 | 0 | few (only on actual movement) |
| +0x150 | +0x194 identical | 10530.4, 1034.5, 1322.6 | 96.24 yd | 151 |
| +0x6A0 | +0x6E4 identical | 10563.9, 913.0, 1312.0 | 58.97 yd | 268 (most) |
| +0x7D0 | - | 10516.6, 896.2, 1309.0 | 96.25 yd | seen at t=531 ms |
Also changing: +0x160..+0x1B4 (152, orientation/rotation matrix terms),
+0x4C8/+0x4DC/+0x4FC/+0x510 toggling 0x80000000 every poll (frame/dirty flags),
+0x398/+0x3DC (0x428E2A4D, 1-LSB drift), +0x338..+0x344, +0x410/+0x418,
+0x7E0..+0x7FC.
Movement during the run was small (0x60 ~0.16 yd; 0x6A0 ~6.3 yd), which is
consistent with v7's empty guard window.

OPEN: which triple is the live player position and which (if any) is the click
destination. That is what WowBCtmPathWatch decides.

## WowBCtmPathWatch v1 (built)

ZIP `WowBCtmPathWatch-v1.zip` (Drive `1BzsDH1i0y87snQukPhB5w6EjrU4jwlHX`),
MD5 `1c95aa3aeedd203dd0d85f4e51028579`, EXE MD5
`ba5825aa5649ddc036b4de224cc795bc`.

Read-only staged probe over the four triples (default `60,150,6A0,7D0`):
4 s BASELINE with hands off, then 8 s CLICK stage (one right-click 40-80 yd
away, then hands off), 20 ms sampling. Per triple: change counts split by
stage, path length during the click stage, largest single-sample step with its
`(from) -> (to)` values, net displacement, mirror check at +0x44, and a verdict
line (DESTINATION candidate = one jump >= 3 yd then holds; POSITION candidate =
many small steps; static). If nothing moves it says the click never reached the
game, which is an input problem, not an offset problem.

`pathmath.h` holds the pure distance/plausibility helpers with offline tests
(58.97 yd and 96.25 yd pairs reproduced, NaN/zero/huge rejected).

## WowBCtmPathWatch report (2026-10-29, pid 7208) - no destination field found

Report `WowBCtmPathWatch-20261029-095131.txt` (Drive
`15CzU2hO1T5jBrRQ6pu_fKZPXy4jdPtbZ`), same process instance as the field diff
(creation 0x1DD677926D8C4C6), record 0x1501FABA1E0.

Readable at start (4 s baseline, then one right-click at t=5734 and release at
5813):
```
+0x060 = 10511.293, 956.254, 1317.160   mirror +0x0A4 identical
+0x7D0 = 10515.067, 892.771, 1309.663   mirror +0x814 identical  <- new: +0x44 mirror holds here too
+0x150 unreadable or implausible        <- was 10530.4,1034.5,1322.6 in the earlier run
+0x6A0 unreadable or implausible        <- was 10563.9,913.0,1312.0 earlier
```

Baseline (hands off): BOTH triples had 0 changes in 4 s. So the earlier
"+0x6A0 changed 268 times" was not idle drift; it needs a moving/active client.

Click stage results:
| triple | changes | path in stage | net | max step |
|---|---|---|---|---|
| +0x060 | 282 (0 baseline) | 44.03 yd | 43.94 yd | 0.273 yd |
| +0x7D0 | 291 (0 baseline) | 16.39 yd | 2.63 yd | 0.110 yd |

Established (CONFIRMED):
- Player position is `record+0x60` (mirrored at +0xA4). A single right-click
  started a 44-yard walk at t=5844 ms, 31 ms after RIGHT_UP, with many small
  steps (0.13-0.27 yd per 20 ms) and near-zero wobble (path 44.03 vs net 43.94).
- Right-clicks ARE reaching the game. Input delivery is not the blocker.
- No DESTINATION candidate exists among the four watched triples. A destination
  would jump once and then hold; none did.
- `+0x7D0` behaves like a cursor/terrain pick or camera-relative value: it began
  moving 78 ms BEFORE the button went down (mouse moved onto the spot), kept
  drifting with a 16-yard path but only 2.6 yards of net displacement.
- `+0x150` and `+0x6A0` are not stable player fields: they read as implausible
  (NaN/zero/out of range) in the second run on the same record.

Consequence: driving movement by writing a destination field has no evidence
behind it. The supported route is to call engine movement code, which is now
possible because a mapped/APC stub can execute and the player position can be
read from outside for verification.

Side note on the injected probe: v7 reported ONE guard fault and then silence on
a page that demonstrably receives hundreds of writes per second during movement.
That is consistent with either (a) no movement during that window, or (b) the
PAGE_GUARD single-step re-arm failing silently after the first fault (only
VirtualProtect failures are counted, not missing re-arms). Not established; do
not rely on that trace until it is re-tested with guaranteed movement.

## WowBRemoteCall v1 (built)

ZIP `WowBRemoteCall-v1.zip` (Drive `1G_4wSPvi3gAk52qi5P3WTH9oZxFCFDtb`),
MD5 `ab4b092d276bf8abf35cff3890a84324`, EXE MD5
`92f639a30616c13501d5b201699bfaef`.

- Read-only by default: dumps `--read RVA` bytes and the prologues of
  MoveForwardStart 0x23C8BE0, MoveForwardStop 0x23C9000, UnitHealth 0x1D63190,
  UnitReaction 0x1D89A00.
- `--call RVA[:a1,a2,a3,a4] --go`: one call, args in RCX/RDX/R8/R9, RAX recorded.
- `--moves --go`: MoveForwardStart, 1.5 s, MoveForwardStop, and the player
  position before/after; prints `MOVEMENT CONFIRMED|NOT OBSERVED`.
- Stub ABI fix found by the offline harness: the first cut used `sub rsp,0x28`
  after `push rbx`, leaving RSP misaligned at the call (callee would see RSP%16
  == 0 instead of 8). Corrected to 0x30; the harness now asserts alignment and
  passes, along with argument-register and result checks.

## User directive (2026-10-29): abandon MoveForwardStart/Stop

"The MoveForwardStart/MoveForwardStop thing does not work, we tried before and
they are locked. Focus on finding the memory offset for the mouse click-to-move
inside the game. Last logs were just dead ends."

Recorded as a standing decision: do not spend further runs on
`MoveForwardStart` (0x23C8BE0) / `MoveForwardStop` (0x23C9000). They are
protected and calling them is a dead end.

Also observed in the same harness run (pid 7208, a client already used by two
earlier tools): the APC probe found NO alertable thread among 4 candidates
(`tid=8144,16960,2196,16516` all phase=0). That is a tool limitation, not game
behaviour - the same probe succeeded on tid 10636/1044/8836 in earlier sessions,
so alertability is luck-of-the-thread-order and varies per session. Harness
patched to try 24 threads with one retry each; it is not the current focus.
`UnitHealth +0x1D63190` prologue read cleanly:
`48 8B C4 55 41 57 48 8D 68 A1 48 81 EC B8 00 00 00 48 89 58 E8 45 33 FF`,
which matches the 16-byte identity sample the probes have always used.

## WowBCtmFieldHunter v1 (built) - search for the CTM destination

ZIP `WowBCtmFieldHunter-v1.zip` (Drive `1gD4JK07b4r5T8YiSyqr8QahUh7skvsqF`),
MD5 `89c0a793c3cc26c993c2de43090cf0ef`, EXE MD5
`ba7148826f5ab908fb828d2ef8c5b68c`.

Read-only, no injection. Three snapshots of a 128 KiB window centred on the
player record (record-0x8000, default span 0x20000): before the click, ~250 ms
after, and after the walk completes. Every 4-byte-aligned float triple is
tested for the four properties a stored destination must have:
changed at the click; bitwise constant afterwards; equal to where the character
stopped (<= 12 yd); and ahead along the walked direction (cosine reported).

Thresholds: distance at click > 4 yd (so the player field itself is excluded)
and < 400 yd; unreadable pages are skipped via a per-page validity map;
256 KiB hard cap keeps memory use at ~1.5 MB. Output gives offsets relative to
the record so they can be reused directly. Empty result prints the next
cheapest experiments (larger span, click then brief W, flat ground).

Offline: huntmath tests use the real numbers from the measured 44-yard walk
(distance, forward cosine near 1 for the endpoint, negative behind the player,
~0 perpendicular, plausibility, degenerate zero-length walk).

## WowBCtmFieldHunter v1 run (pid 7208) - 0 candidates, and why

Run output: baseline 3000 ms then RIGHT_DOWN t=5859; position at start
(10495.393, 1025.073, 1324.479) identical at click; end (10521.236, 1080.674,
1324.661); walked 61.31 yd; snapshot0 fully readable (0x20000 of 0x20000);
testedTriples=32766; candidates=0.

Interpretation: no float triple in a 128 KiB window around the record behaves
like a stored destination at the END of the walk. Two design weaknesses, both
addressed in v2:
1. the endpoint test cannot see a destination that is consumed on arrival;
2. requiring three consecutive floats rejects an (x,y) point with z from terrain.

## Winkr-Public reference (external, different client) - affect on direction

Analysed <https://github.com/laurentiumanafu/Winkr-Public>; summary and exact
constants in `hunt/WINKR_REFERENCE.md`. Targets 1.15.9/69109 `WowClassic.exe`,
NOT 70291 `wowb.exe` (grep: no 70291/wowb/1.60 anywhere). Method transfers, no
offset does.

Three findings that matter:
- Movement keybinds are gated by ONE inline global: `cmp qword [g_taint], 0` then
  `je` (real work) or `call` blocked-action reporter. Same global the Lua secret
  gates use - matches our `*** FORCETAINT_STRONG ***`. So "locked" movement is one
  pointer, not many checks.
- **ClickToMove does not exist** on that client: absent as a string (ASCII+UTF-16),
  no CTM/pathfinding CVar among 524 registration sites. Right-click goes through
  `CameraOrSelectOrMoveStart` 0x15DDA40 / `MoveAndSteerStart` 0x15DE880. Combined
  with our own measurement (+0x7D0 moving 78 ms BEFORE the button went down,
  because the cursor moved onto the spot), the leading hypothesis is now that the
  move point is derived from the cursor ray, not stored as a destination.
- `CInputControl` (0x03A05BA0 on 68940, 0x03ABEB20 on 69109) has **no taint
  check** in its helpers SET_BIT 0x015E8080 / CLEAR_BIT 0x015E8A90 / APPLY
  0x015E78A0, flags dword at +0x04 (the same dword Lua `IsPlayerMoving` reads).
  APPLY is what emits the movement packet; writing flags alone gets rubber-banded.

## WowBCtmFieldHunter v2 (built) - tests both shapes in one run

ZIP `WowBCtmFieldHunter-v2.zip` (Drive `1QjQQuvIDxQCXKHxysPuHP6vx4VhVPb4P`),
MD5 `fb84eaa5e266ab1f4caab56f6d9e1d02`, EXE MD5
`d667d2dace3185c324da68c077c486b2`.

Four phases: baseline (hands off) -> **cursor sweep** (mouse moves, no click) ->
one right-click -> two samples 700 ms apart during the walk -> end of walk.

- Shape A (stored destination): collected process-wide with `--full` or in an
  8 MiB window, then re-read and kept only if bitwise identical across the two
  walk samples, 4-200 yd from the player, cosine >0.90 along the walked
  direction, and actually getting closer.
- Shape B (cursor-derived point): plausible triples in the window that changed
  during the sweep while the player stood still, each flagged `frozen=1` if it
  stopped following the cursor after the click - the best single indicator of a
  point being adopted as the move target.

Selection is a pure function (`fh_destination_like`) with offline tests built
from the real 44-yard walk numbers. Writing those tests caught a genuine bug: the
3-D cosine rejects a 2-D destination outright (a z error of even 50 yd dominates
the dot product), so all geometry is now horizontal, with `fh_dist2` for range and
`fh_approaching2` for the approach test.

## WowBSigScan v1 FAILED its own control - tool bug, not client

v1 report: `scannedBytes=0x3A3C000` of a `0x863C000` image,
`UNITHEALTH_PROLOGUE matches=0 rva=0x0 expected 0x1D63190 SCAN SUSPECT`,
`0 of 19 patterns matched`.

Diagnosis (certain): the identity check inside the same tool reads those same 16
bytes at +0x1D63190 and PASSED before the scan started, so the bytes are present.
Two defects in v1's scanning:
1. `if (got<CHUNK) break;` - the first PARTIAL 4 MB read aborted the whole scan.
   A 4 MB span crossing an unreadable gap is routine in a live image, which is why
   the scan stopped at 0x3A3C000 and never reached the control.
2. `limit = got - maxLen + 1` - the last (maxlen-1) bytes of every chunk were
   dropped, so any pattern straddling a chunk boundary was invisible by design.

This is exactly what the positive control is for: without it, "0 of 19 matched"
would have been read as "these signatures do not apply to our build" and the best
lead available would have been abandoned.

## WowBSigScan v2 run: control "failed" again - and taught us something real

Result: 4 of 19 matched, all UNIQUE, control `matches=9 rva=0xAF71D0 expected
0x1D63190 SCAN SUSPECT`.

Diagnosis: **the control sample was never unique.** The 16-byte UnitHealth
prologue appears 9 times in this image. Every earlier "identity check" (all the
probes, all the hunters) only compared the bytes AT 0x1D63190 - a presence check.
Uniqueness was asserted by me, never established. That is a standing correction for
every doc that called this a fingerprint.

The scan itself worked: four patterns matched uniquely in `.text`, each preceded
by `0xCC`, each starting with a real MSVC prologue. So `WORLD_INTERSECT` and
`WORLDFRAME_PROJECT_POINT` are genuine misses on 70291, not artifacts.

Caveat recorded: `.text` reported 0x36AA43C unreadable of 0x494343C, so NOT FOUND
inside that hole is not proof of absence. v3 prints a protection histogram
(readable / noaccess / guard / not-committed) per section to separate the two.

## WowBSigScan v2 (built) - section-table scan

ZIP `WowBSigScan-v2.zip` (Drive `1gyy9tZ7KD2zc5VmdRAsGVGbF3geeEXlL`), MD5
`b97e487c01c0d9840327214734d72ea4`, EXE MD5 `712d3ebcfedabca10c0cfd7f4de09095`.

- Parses the PE section table and scans each section WHOLE (one bulk read, with a
  page-by-page validity-map fallback), so there is no chunk arithmetic and no
  boundary to straddle.
- Match only reported when every covered byte is marked readable, so an
  unreadable hole cannot produce a phantom match from zero-filled memory.
- Reports per-section `unreadable=` bytes, so a `NOT FOUND` in a section that
  could not be fully read is not mistaken for evidence of absence.
- Scan core extracted to `scancore.h` and unit-tested against the exact bug class:
  pattern at the first byte and at the last byte of a buffer, match inside an
  unreadable hole, wildcard behaviour, capacity overflow, every generated pattern
  self-matching, and the control pattern at 0x1D63190.

## CLICK-TO-MOVE CONFIRMED (2026-10-29) - synthesised right-clicks work

WowBClickAim v1 run (report `WowBClickAim-20261029-132521.txt`): **8 probes, 8
movements**, every one ~6.1-6.9 yd. Click-to-move needs no destination field, no
WORLD_INTERSECT, no injection: PostMessage right-clicks at client coordinates move
the character. That closes the question this whole line of work was chasing.

But v1's MEASUREMENTS were invalid, and the cause was the tool:
- distance is constant 6.08-6.87 yd for dy = -300...-60. At ~7 yd/s that is
  0.95 s of running = exactly the fixed 0.85 s sampling window. The character was
  never allowed to stop, so each probe measured leftover motion from the previous
  click plus the start of the new one. Distance was window-saturated.
- the two dx=0 probes disagreed on heading by 15 deg (-101.9 vs -119.0). A fixed
  camera cannot do that; the discrepancy is contamination, not physics.
- consistent with a run-speed sanity check: 6.63 yd / 0.85 s = 7.8 yd/s.

v2 fixes the measurement (rest-to-rest for every click) and adds the closed loop.
Design constraint discovered by simulating the controller: a click can only reach
headings inside the camera cone, so a goal behind the camera is unreachable by
clicking - clicking at the screen edge walks the wrong way and diverges. The
walker therefore turns the camera with a horizontal right-drag and re-measures.

## Signature scan is NON-DETERMINISTIC in process mode

Two runs on the same pid, minutes apart (reports `20261029-132516` and the
earlier `20261029-121526`):
`.text` unreadable 0x36AA43C vs 0x3A0143C; control copies 9 vs 5;
HANDLE_TERRAIN_CLICK UNIQUE 0x25D6670 vs NOT FOUND.
Cause: .text is only partly resident, so parts of the code are not readable
depending on what the client has touched. A NOT FOUND from process mode proves
nothing. Also fixed: v2's protection histogram printed readable=0 because of an
absolute/relative offset bug (`off = e - off` instead of `e - base`).

v3 adds `--file`, scanning the decoded image on disk: complete, deterministic, no
races. That is the answer to trust for WORLD_INTERSECT / WORLDFRAME_PROJECT_POINT.

## CLICK-TO-MOVE WALKER CONVERGED (2026-10-29, field run)

WowBClickAim v2 run: WALK from (10358.85, 806.07) toward +y 30 yd, 14 legs,
"REACHED: remaining 1.93 yd", final (10360.66, 836.78). The bot has a working
movement primitive that needs no offsets beyond the position field.

Three defects the run exposed, all fixed in v3:
1. dy does not control distance. Every leg used the clamped dy=-20 yet moved
   0.09 to 12.35 yd (150x spread). The fitted model dist=a+b*dy produced NEGATIVE
   predictions (-14.3 and -106.6 yd) and was pure fiction. v3 removes the model.
2. Learning corrupted the model: at exit degPerPx=-0.30783 (calibrated -0.1029)
   and degPerDragPx=+0.16937 (calibrated -0.38165) - 3x off and sign-flipped,
   because a 0.09 yd leg still "measured" a heading. v3 learns only from legs
   travelling >3 yd, rejects sign flips and >3x outliers, and smooths.
3. What IS stable across the two runs: click -0.0936/-0.1029 deg/px, drag
   -0.3637/-0.3817 deg/px. Camera model reliable; distance relationship was not.

## The Desktop wowb-decoded.exe is the ENCRYPTED original, not a decoded image

Sig scan --file reported "1 unique match" and MAPPING NOT CONFIRMED. The PE header
explains both: .text virtual 76,821,564 vs raw 1,024 (ratio 0.000), .data 44 MB
virtual vs 6.9 MB raw (0.156). A decrypted image stores its code; this file does
not. All 57 control hits and the "UNIQUE" HANDLE_TERRAIN_CLICK @0x07726470 are
inside .data - bytes that look like prologues inside the compressed blob.

Consequence: no file-based signature result from that path is meaningful, and the
earlier process-mode UNIQUE 0x25D6670 is also unreliable (racy, partly unreadable
.text). WORLD_INTERSECT / WORLDFRAME_PROJECT_POINT remain unfound by signature.
Because click-to-move now works without them, this is no longer blocking.

SigScan v4 refuses to present lookalikes as results: structure check per section
(raw/virtual, entropy, prologue and int3 density), mapping candidates scored by
control-site position, and "ONLY IN DATA (lookalikes, not code)" for any hit in a
non-executable section.

## 2026-10-29 - ClickAim v3 could not plan a leg; live scan says 20.6% coverage

### ClickAim v3 field run (pid 7208): calibration OK, walk refused at leg 1

    position=(10504.635, 988.760, 1319.809)
    cal[1] centre dy=-140 -> 40.18 yd heading -35.8 (5.92 s, 6.78 yd/s)
    cal[2] dx=+300 dy=-140 -> 29.14 yd heading -59.4; sens -0.07861 deg/px
    cal[3] dy=-200 -> 72.27 yd (10.41 s, 6.95 yd/s)
    cal[4] dy=-20  -> 13.79 yd (2.27 s, 6.09 yd/s)
    WALK 30 yd: "Cannot plan a leg (goal outside the cone and no drag sensitivity)."
    model at exit: headingAtZero=0.0 degPerPx=-0.07861 degPerDragPx=0.00000

Three defects in the v3 rewrite, all visible in that last line:
1. degPerDragPx=0 - the drag calibration was deleted along with the distance model,
   so the planner had no way to rotate the camera.
2. coneHalfDeg left at 0 by memset - every goal counts as "outside the cone", so
   even a goal straight ahead is unplannable.
3. headingAtZero never assigned - the first leg would have planned against 0 deg.

v4 fixes all three and probes BOTH turn channels (200 px right-drag, then 500 ms
holds of VK_RIGHT and VK_LEFT, each followed by one measured centre click), using
whichever actually turned the camera. 200 px is deliberate: at the measured
0.37 deg/px, a 500 px drag rotates past 180 deg and the heading aliases to the
wrong sign.

New calibration observation: this session's click sensitivity is -0.07861 deg/px,
against -0.0936 and -0.1029 in the earlier runs - a 25% spread between sessions.
Calibration must run per session; a cached sensitivity is not good enough. The dy
spread this time was 13.79 to 72.27 yd for the same click offset (5.2x), which
reconfirms that dy is a position hint and nothing more.

### SigScan v4 live process scan: mapping confirmed, coverage 20.6%

    .text rva=0x00001000 size=0x0494343C unreadable=0x03A2C43C EXEC
    protections: readable=0xF17000 noaccess=0x3A2C43C unreadableRanges=1334
    UNITHEALTH_PROLOGUE copies=7  known site 0x1D63190 PRESENT
    MAPPING CONFIRMED
    4 of 19 patterns matched; 4 in executable code; 0 only inside data

Real RVAs (mapping confirmed, all in .text, all unique, all preceded by 0xCC):
    LUAL_LOADBUFFER       0x048A1A50
    LUAH_NEW              0x0472A3D0
    WOWGUID_TO_STRING     0x047B96A0
    HANDLE_TERRAIN_CLICK  0x025D6670

HANDLE_TERRAIN_CLICK at 0x025D6670 now has TWO independent process-mode scans
agreeing on it. That also settles a Winkr-side claim: their source says the
signature "has not matched since 68887", but on 70291 it matches. It remains the
Spell_C ground-target finaliser, not a movement function.

Coverage is the important number: 0x0F17000 readable of 0x0494343C executable
bytes = 20.6%, with 0x3A2C43C in PAGE_NOACCESS. The 15 unmatched patterns
(WORLD_INTERSECT, WORLDFRAME_PROJECT_POINT, SPELL_STOP_TARGETING, INTERACT_UNIT,
the Lua secret gates) are UNSEEN, not absent. SigScan v5 reports coverage per
pattern and adds --watch SECONDS, which rescans while the client runs so lazily
decrypted pages enter the scan; hits are deduplicated by RVA between passes.

## 2026-10-29 - ClickAim v4 REACHED the goal; two legs were geometrically impossible

Field run of v4 (pid 7208, record 0x1516BAEF180): 30 yd in 17 legs,
"REACHED: remaining 1.74 yd", converged=1. Calibration this session: click
-0.10165 deg/px (after -0.07861 an hour earlier), drag -0.22653 deg/px (after
-0.3637/-0.3817 in the earlier runs). Both channels need per-session calibration;
nothing about the camera model is stable across sessions except its rough order
of magnitude.

Two legs violate the triangle inequality for a fixed goal:

    leg 7: moved  5.45 yd, remaining  2.04 ->  30.83  (grew by 28.79)
    leg 8: moved  0.63 yd, remaining 30.83 -> 110.23  (grew by 79.40)
    leg 8 was preceded by "[before click] still moving after 20000 ms"

A stated destination cannot get further away by more than the distance walked, so
those position reads were not the character's position. Legs 9 onward are
consistent and the walk converged, which is why this cost time rather than the
run. Two consequences:
1. Planning must use the read the measurement starts from (v4 planned from an
   earlier sample, so a stale sample invalidated the plan silently).
2. Any leg that breaks the inequality, or any single sample implying more than
   1 yd + 12 yd/s, is not a measurement: re-plan from a fresh read, learn nothing.

The click slope also drifted -0.10165 -> -0.18174 (78% off calibration) because
every accepted leg carried the same weight regardless of length. Learning is now
weighted by travel (quality = moved/12, capped), limited to 3-22 yd legs, and
gated at 2x the calibrated slope instead of 3x.

SigScan --watch is now incremental: later passes only re-read pages that were
unreadable before (each with an 80 byte lead-in so a match straddling the boundary
is still found), and coverage is recomputed from a page map. The first version
rescanned 76 MB x 19 patterns every five seconds, which is why it looked hung.

## 2026-10-29 - ClickAim v5 converged in 6 legs; SigScan had a phantom-hit bug

### ClickAim v5 (pid 7208, record 0x15196813120)

    30 yd goal -> REACHED, remaining 1.68 yd, 6 legs (v4 needed 17)
    no IMPOSSIBLE GEOMETRY legs, drift vs calibration +3% click / +6% drag
    click -0.10983 deg/px, drag -0.22762 deg/px (third different drag value this
    session series: -0.3637, -0.3817, -0.2265)

The v5 validation and weighted learning worked: no discontinuous legs, and the
model ended within 6% of its own calibration. What is left is the END of the
walk, not the model:

    leg 3: moved 10.09 yd, remaining 4.10 -> 6.20
    leg 5: moved 10.85 yd, remaining 3.28 -> 8.08

Four legs of 7-11 yd to close a 4 yd gap, one of which moved away from the goal,
because the click sets a direction and the terrain decides the distance. v6 adds
the other half of the primitive: poll the position while running and cancel the
walk (tap W, which replaces the pathing move) once the remaining distance is
within `capture + coast`, where `coast` is the measured distance travelled between
the key press and standstill.

### SigScan v5 field report: two findings

1. **Phantom hits at rva 0 (my bug).** Every repeated match was reported twice, the
   duplicate being an all-zero slot at rva=0, so four unique functions were called
   AMBIGUOUS and the summary said "5 of 19" when 4 matched. Cause: counts advanced
   across passes while hits were written from index 0 each time. Fixed with
   sc_scan_into (append at count) and unit-tested. The real result stands: 4 of 19,
   LUAL_LOADBUFFER 0x048A1A50, LUAH_NEW 0x0472A3D0, WOWGUID_TO_STRING 0x047B96A0,
   HANDLE_TERRAIN_CLICK 0x025D6670.

2. **Coverage FALLS across passes**, contrary to the lazy-decryption assumption:

       pass 1  32.6% readable (unreadable 0x316843C)
       pass 14 30.4%          (0x32FB43C)
       pass 26 28.3%          (0x348143C)

   ~29 pages lost per pass: the client re-locks pages as they are read. So repeated
   scanning is counter-productive, the best single moment is right after the client
   has been used, and --watch can only preserve a union of earlier reads. Pass-1
   coverage also varies between runs (20.6% earlier, 32.6% here), so it is a
   property of client state, not of the build.

## 2026-10-29 — v6 field reports: click sensitivity was fitted to one walk

Two reports, both from pid 7208, client 1920x1080, window not foreground
(`PostMessage`).

**ClickAim v6** (`WowBClickAim-20261029-142201.txt`): `REACHED`, 1.63 yd, 17 legs,
`converged=1`, one leg skipped as discontinuous. It got there, and the report says
why it took 17 legs:

    cal[2] click dx=+300 -> heading -174.4 deg
          heading sensitivity = -0.36086 deg per pixel

against **-0.10983** on the same client two runs earlier. A 3.3x swing on a single
300 px sample. Every planned heading was therefore wrong by tens of degrees and
the character ran diagonally past the goal: legs 5, 8 and 13 **increased**
`remaining` (9.70 -> 18.68, 2.72 -> 21.38, 17.06 -> 20.10 yd) while moving 20-35
yd. Those are lateral misses, not evidence about the model.

Drag slope: v5 measured -0.22653/-0.22762, v6 measured +0.04202 and then learned
0.06677 (+59 % drift by the tool's own report). Single 200 px sample again.

Stop: exactly one stop line in 17 legs, `stop at 1.70 yd, coasted 0.07 yd` - the
character was already stopping, so the sample measures nothing, and it moved
`stopDistance` 0.80 -> 0.58 yd anyway.

**Conclusion: the planner was never the problem.** A closed loop that measures is
only as good as its calibration, and the calibration was one walk. v7 replaces it
with four offsets fitted by least squares plus two held-out offsets that can
falsify the fit, two opposite drags to separate slope from bias, a pass-by stop
for lateral misses, `closest=` telemetry per leg, and coast learning gated at
0.30 yd.

**SigScan v6** (`WowBSigScan-20261029-142417.txt`, `--watch 60`, 13 passes):
phantom-hit fix confirmed - all four matches `matches=1 UNIQUE`, no `rva=0`
duplicates, identical RVAs across three runs (v5 x2, v6 x1). Coverage flat at
29.1 % on every pass, 2/3/0/1 pages re-read per pass and nothing new unlocked,
confirming that `--watch` cannot beat a locked page. Control PRESENT.

Two reporting defects, both fixed in v7: the report printed "All executable bytes
were readable, so a NOT FOUND here is real evidence" immediately after the correct
"UNSEEN, not absent" paragraph (an unguarded `else` that fired whenever coverage
had not fallen), and the per-pass "5 of 19" counted the positive-control slot
while the summary correctly said 4.

Standing result, unchanged: `HANDLE_TERRAIN_CLICK 0x025D6670` is an AOE cast
finaliser (Winkr's `Spell_C` ground-target path), **not** click-to-move. CTM in
this client is immediate-mode; the movement channel is a synthesised click, which
is what WowBClickAim is.

## 2026-10-29 — v7 field reports: the camera follows, so the reference was wrong

**ClickAim v7** (`WowBClickAim-20261029-144250.txt`) **refused to walk**, correctly:
both held-out offsets missed (`+225`: measured -24.94 deg vs model -13.12;
`-225`: -5.77 vs +13.12). The tool did what it was built to do. The probe list then
explains the last three runs:

| dx | heading | turn vs start | turn vs previous walk | slope vs previous |
|---:|---:|---:|---:|---:|
| 0 | 47.0 | - | - | - |
| +150 | 34.1 | -12.90 | -12.90 | -0.08600 |
| +300 | 13.1 | -33.90 | -21.00 | -0.07000 |
| -150 | 34.4 | -12.60 | +21.30 | -0.14200 |
| -300 | 46.0 | -1.00 | +11.60 | -0.03867 |
| +225 | 22.1 | -24.90 | -23.90 | -0.10622 |
| -225 | 41.3 | -5.70 | +19.20 | -0.08533 |

Against a fixed origin, three probes have the wrong SIGN, so no line can fit.
Against each probe's predecessor they all agree: median **-0.0857 deg/px**, which is
the -0.0936/-0.1029/-0.10983 of v5 and v6. **The camera yaw at a click is the
heading of the previous walk** - WoW's follow camera swings back behind the
character while it runs. That single fact accounts for v6's -0.36086 outlier (fixed
origin, plus one 4.60 yd walk whose camera had barely re-centred, over 300), for
v7's rejection, and for the "same offset, different distance" confusion: `dy` is
not the only unmeasured thing, the camera had also rotated.

Also measured: the response **saturates** - +225 and +300 buy about the same angle
(-23.9 vs -21.0) - so no single linear slope is right across the full range, and a
median of per-probe slopes is the estimator that survives it.

**ClickAim v8** implements that: every probe referenced to the previous walk, one
slope per probe, median with 2x rejection, held-out check against the same moving
reference; `headingAtZero` in the walk loop is now the measured heading of the last
leg instead of `walkedHeading - degPerPx*clickPx`; a failed held-out check downgrades
the model to UNVERIFIED and still walks; only "fewer than two usable probes" or an
implausible span blocks a run.

**SigScan v7** (`WowBSigScan-20261029-144326.txt`, `--watch 60`, 13 passes): the
reporting fixes hold - `3 of 19` in both the per-pass and summary lines, no
"all readable" contradiction, `UNSEEN (only 20% ...)` instead of a retry that never
comes, plus "Coverage held at 19.8% across 13 passes". Only **3** unique matches
this time (`LUAL_LOADBUFFER 0x048A1A50`, `LUAH_NEW 0x0472A3D0`,
`WOWGUID_TO_STRING 0x047B96A0`): `.text` was 19.7 % readable (0xE66000 of
0x494343C) versus 29.1 % (0x1552000) in the earlier run, and **HANDLE_TERRAIN_CLICK
0x025D6670 was UNSEEN, not absent** exactly as the new wording says. Same pid 7208,
same imageBase. Readable coverage varies run to run with client state, which is why
the tool now reports what it did not look at instead of implying absence.

## 2026-10-29 — v8 field run: three defects and the real environment

`WowBClickAim` v8, pid 7208, window `0x6A70864`, PostMessage, start
(3965.979, 1758.319, 961.653). Calibration produced a usable slope
(-0.08009 deg/px, median of 3 probes, both holdouts checked, one out of tolerance ->
labelled UNVERIFIED, walked anyway) and the walk still ended:

    leg 1: drag= +532 click= +0 dy=-60 -> moved 0.00 yd in 0.25 s
    leg 2: drag= +536 click= +0 dy=-60 -> moved 29.71 yd, remaining 28.45 -> 40.27
    leg 3: IMPOSSIBLE GEOMETRY - moved 50.19 yd in 15.70 s
    leg 4-7: 0.12-0.27 yd each, dy=-60
    BLOCKED: four clicks in a row moved under 0.5 yd. Best remaining 13.79 yd.

Three of those lines are tool defects:

1. **`moved 0.00 yd in 0.25 s` is a race, not a blocked click.** REST_STABLE=4
   samples at 60 ms, armed immediately after the click, so "has not started walking
   yet" and "did not walk" were the same reading. Fixed with a 900 ms arming delay
   (CLICK_ARM_MS).
2. **`IMPOSSIBLE GEOMETRY` on 50.19 yd / 15.70 s = 3.2 yd/s is a false positive.**
   A whole leg was judged by am_plausible_step, whose 3 s dt clamp allows at most
   45 yd. New am_plausible_leg judges average speed; the only leg that was making
   progress is no longer discarded.
3. **The drag channel was a single sample again** (-0.19706 deg/px from one +200 px
   probe; the -200 px probe's walk was only 4.78 yd) and drove `drag=+532`. Now four
   drag probes with the same moving-reference median rule as the click channel, and
   per-leg drags capped at 420 px.

And one environment fact, stated by the user: **obstacles, monsters and hills are
the normal case**. Legs 4-7 answered a blocked leg with the same heading and a
slightly different click point, i.e. four pushes into the same hillside. v9 adds a
detour: after two short legs, turn 55 deg off the goal bearing, far-click, walk the
tangent, alternate sides, six attempts before calling the goal walled in.

Also added: calibration saved to `C:\ghidra-scripts\clickaim-cal.txt`, `--load`
reuses it (one click to find the camera yaw, then walking) so a 4 minute probe
sequence is not repeated per attempt.

## 2026-10-29 — v9 field runs: no turn channel, and it got saved

Two v9 runs, pid 7208. Run 1 calibrated the click slope well (4 probes,
-0.069/-0.083/-0.091/-0.071, median **-0.07861 deg/px**, residuals < 3 deg) and
then failed everything else:

    cal[3] dx=0 dy=-200 -> 0.43 yd ; cal[4] dx=0 dy=-20 -> 1.96 yd
    cal[drag] +200/-200/+300/-300 px -> 0.07 / 1.12 / 2.90 / 0.99 yd, all skipped
    cal[key] RIGHT/LEFT 500 ms -> 3.41 / 6.37 yd, both rejected as too short
    WARNING: neither turn channel moved the camera
    Cannot plan a leg: goal 90.0 deg, centre -30.5 deg, cone +/-56.6 deg

Run 2 (`--load`) read `degPerDragPx=0 degPerKeyMs=0` from the file saved by run 1,
skipped the turn probes entirely, and refused to plan. Both runs moved 0.00 yd.

Five causes, all tool-side:

1. **Calibration wandered ~230 yd** because every probe clicked at dy=-140 (20-70 yd
   walks). The character ended the sequence jammed where clicks moved it <3 yd.
   Probes now use dy=-100 (~6-10 yd) and a 6 yd minimum walk.
2. **A turn needs a clean walk, not a long one.** The 8 yd floor rejected 3.41 and
   6.37 yd walks that were perfectly measurable. Floor is now 2.5 yd
   (CAL_MIN_TURN_WALK). This alone turns "no turn channel" into a walkable model.
3. **A calibration with no turn channel was saved, then loaded.** Not saved any
   more, and `--load` treats a zero channel as unknown and probes it.
4. **A goal behind the camera broke the walker.** The camera follows the character,
   so walking at the cone edge moves the heading toward the goal: new REACH-AROUND
   legs (up to 8) do exactly that instead of exiting.
5. **"Character is not walking" was reported as a camera failure.** Two probes under
   2 yd now stop the run and name the causes (dead, rooted/in combat, inside
   geometry, click under a roof).

The dy-spread evidence clicks are now opt-in (`--dyevidence`); they cost two walks,
one of them 114 yd, before the turn probes.

Navigation note: CopilotBuddy-style bots use a Recast/Detour mesh from the client's
map data. That is build-independent (it reads data, not code) and is the right
answer for obstacle-aware routing, but CTM is still immediate-mode: a mesh can only
supply a sequence of mutually visible click points, so it needs a funnel/visibility
step. The open question is the beta client's map-data layout, not any offset.

## 2026-10-29 — v9 run: the camera model is VERIFIED; a stuck right button ran the bot away

`WowBClickAim-20261029-151813.txt` (v9, pid 7208). First verified calibration:

    click fit: median of 4 of 4 probes, -0.10709 deg/px
      residuals +1.37, -0.36, +0.62, +3.22 deg
      holdout +300: measured -39.39 vs model -32.13 (-7.26)
      holdout -300: measured +31.15 vs model +32.13 (-0.97)
      both held-out offsets are predicted: click sensitivity VERIFIED
    drag: +200 -> -0.22500, -200 -> -0.10418, +300 -> -0.22494
      drag fit: median of 3 of 3 probes, -0.18471 deg/px

`clickaim-cal.txt` saved as `degPerPx=-0.107086 degPerDragPx=-0.184707
degPerKeyMs=0.000000` (the user uploaded it separately).

Then the walk:

    leg  6: drag=-357 click=+0 dy=-60 -> moved 171.52 yd in 25.00 s, remaining 27.67 -> 174.61
      [between legs] still moving after 20000 ms; using current position
    leg  7: remaining 308.85 -> 348.88 ... leg 22: remaining 104.29 -> 93.18
    leg 23/24: 0.34 / 0.31 yd - stuck, 93 yd from a 30 yd goal
    stop: 0 of 24 legs; drift: click -0%, drag +17%

171.52 yd at 6.9 yd/s is a character RUNNING, and it ran for another 20 s after the
leg ended. Cause: the client was left with the **right mouse button down** (a held
right button means "run toward the cursor" in this client) because a
`WM_RBUTTONUP` was dropped. Not a camera or pathfinding failure - the click channel
demonstrably worked, since legs 11-22 walked 240 yd back toward the goal on plain
centre clicks.

v11 fixes: double button release in both drag directions; PANIC STOP (release x2,
cursor to centre, left click, stop key, settle); REST_TIMEOUT 20 s -> 8 s; runaway
detection at the start of a leg and after a click (leg > 40 yd), stop after two
runaways or a 3x goal-distance growth; no heading from a walk under 0.5 yd (v9
reported "heading at centre right now = 0.0 deg" from a 0.00 yd probe because
heading_of on two identical points returns 0.0); click offsets capped at 300 px
(v9 asked for -584); a skipped drag probe re-anchors the heading or has the next
probe discarded, which is what poisoned the -0.10418 drag sample.

## 2026-10-29 — CONVERGED: 30 yd in 8 legs, 1.61 yd, verified model

The walk works. `WowBClickAim` (v10 build, banner still said v9 - see below), pid 7208:

    click fit: median of 3 of 3 probes, -0.12674 deg/px
      holdout +300: measured -33.97 vs model -38.02 (+4.05)
      holdout -300: measured +36.52 vs model +38.02 (-1.50)
      both held-out offsets are predicted: click sensitivity VERIFIED
    drag fit: median of 4 of 4 probes, -0.17871 deg/px (leftover +11.4 deg)
    leg 7: click=-243 -> moved 19.29 yd, remaining 22.08 -> 5.05
      (capture stop at 1.79 yd, coasted 0.18 yd - correctly NOT learned)
    leg 8: click=-300 dy=-60 -> moved 4.43 yd, remaining 5.05 -> 1.61 (stop)
    REACHED: remaining 1.61 yd after 8 legs
    drift vs calibration: click -1%, drag +2%

Series of converged runs for the record: v5 6 legs (1.68 yd), v6 17 legs (1.63 yd),
v10 8 legs (1.61 yd). The stop mechanism finally fired on a real approach.

Two verified calibrations now exist and they disagree by 18 %:
-0.10709 deg/px (run 151813) and -0.12674 (this run); drag -0.18471 and -0.17871.
So the click slope is a *population* around -0.117, not a constant. The tool now
appends every calibration and pools them with a robust median; any future single
sample must be checked against that spread before it is believed.

Also fixed: the banner/version was typed separately and still said v9 in a v10 build;
it is now one AIM_VERSION macro. `--load` prints the path it reads. A drag over 45 deg
is split across legs, because leg 4 of this run asked for 420 px (the cap), ran
37.10 yd and ended FURTHER away (23.47 -> 38.49).

Click-to-move conclusion, for the record: there is no CTM destination in this client
(immediate-mode), `HANDLE_TERRAIN_CLICK 0x025D6670` is the AOE cast finaliser, and the
working movement channel is a synthesised click plus a measured camera model, which
is what this tool is. Remaining movement limits are the environment (obstacles,
monsters) and input-state hygiene, not the offset hunt.

## 2026-10-29 — two more converged walks, and a false panic

`--load --walk 60 0`: 5 legs, `REACHED` 0.79 yd, click drift +16 %. The best run
yet, and the first to start from a saved calibration (loaded -0.08631/-0.21166).
`--load --walk 30 90`: 14 legs, `REACHED` 0.86 yd, click +12 %, drag +17 %; that
spot was obstructed, so it circled, took one 99.35 yd walk and came back.

What the pair exposed:

* **The runaway detector fired on a planned click.** Leg 5 was a `dy=-200` nudge,
  clicked ~100 yd away, walked 99.35 yd correctly, stopped correctly, and v12's
  "moved > 40 yd" rule panicked on it. A long leg is only suspicious in relation to
  what the click asked for: dy=-60 is ~10 yd, dy=-200 is ~100 yd. New rule: panic
  when the rest wait times out (v9's real signature: `still moving after 20000 ms`),
  or when a near hop travels like a far click (>40 yd and >3x its own expected hop).
* **The nudge moved the character 100 yd** to sample different terrain. Cycle is now
  {-70,-90}, keeping the sample local.
* **`--load` printed two contradictory lines** when the file did not exist; the
  stale one is removed.
* **One saved sample is not a calibration**: the loaded -0.08631 was contradicted by
  the same run's own -0.0934 then -0.10017 (+16 %). Converged runs now append the
  model they learned, so `--load` pools a population.
* **The click response is a local slope**: 0.86 deg/px near 150 px vs 1.08 near 300
  in the fresh calibration, and the ±300 holdouts missed by -4.05/+8.76 deg while a
  "VERIFIED" verdict was still printed. Verified now means "inside tolerance", and
  plans are capped at 225 px with the curvature reported.

New EXE `2c97345f3a85bce6d407ee0ca11d8e55`; offline aimmath suite unchanged and green.

## 2026-10-29 — 40 yd in 2 legs; a false refusal; a circle at 40 yd

Three runs, in timestamp order: `--walk 100 0` (15:43, no cal file, calibrated,
24 legs, 65.21 yd left, converged=0), `--walk 25 180` (15:45, refused), and
`--walk 40 270` (15:45, **2 legs, 0.77 yd, drift -0 %/-0 %**).

* **Best execution yet:** the 40 yd run spent one click finding the camera and then
  two legs to arrive (34.72 yd, then 4.52 yd). This is what `--load` was for.
* **The refusal was a timeout artefact.** One centre click walked 56.09 yd at
  6.9 yd/s; it needed 8.1 s and v11's 8 s rest wait cut it off, so the tool said
  "never came to rest" and refused to calibrate or walk. Fix: 20 s wait plus a
  distance-based runaway test (120 yd of continuous travel), which still catches
  v9's 171.52 yd in 25 s.
* **The circle had two causes.** (1) The camera yaw was re-taken after every walk,
  including 0.63-1.35 yd ones whose headings are noise, so the planner kept
  believing the goal was outside the cone and kept issuing 45 deg drags - the drag
  cap then made every corrective turn the same size. (2) Detours only fired on legs
  under 0.5 yd, so legs of 5-13 yd that gained nothing counted as progress.
  Fixes: yaw only from walks >= 3 yd; three no-gain legs also trigger the
  alternating detour; turn legs plan the far click (>30 yd out) so the walk after
  the turn can re-measure the yaw.
* Calibration in the 100 yd run again refused to claim verification: holdouts at
  +/-300 px missed by -21.53 and +49.20 deg. Both holdout walks were short (6.20
  and 14.06 yd) - terrain, not the camera. The +/-225 probes agreed within 1.1 deg.

EXE `0147a62be6a9985796b0620080a257c1`; aimmath suite green including a new test
that a turn leg cannot plan a hop too short to re-measure the yaw.

## 2026-10-29 — drag legs were corrupting the camera yaw estimate

Two runs on pid 3772 (a new client session, so the v14 timing fixes were in):
`--walk 100 0` reached 21.20 yd in **5 clean click legs** (21.54/17.92/21.51/45.75/
23.82 yd) and then thrashed for 19 more; `--walk 25 180` never converged (24 legs,
13.42 yd left) and was almost entirely drag legs.

Root cause, found in the log rather than the terrain: **a drag leg does not walk
along the camera it turned to.** With the right button down the character runs along
its OLD facing while the camera rotates, so the leg displacement is a vector sum of
two different camera directions. The walker used that displacement heading as the
next `headingAtZero`; across the 25 yd run the yaw estimate swung 172 deg, the goal
never came back inside the +/-75 deg cone, and every following leg was another drag.
v15 advances the yaw by the model on turn legs and keeps the measured-yaw rule for
pure click legs.

Also confirmed working in these two runs: `WANDER_MAX_YD` never tripped (longest
walk 89.25 yd in 12.84 s - so 120 is not too tight), the pass-by stop fired for the
first time ("passed the goal at 4.42 yd; cancelled the walk rather than run on"),
and `HEADING_MIN_WALK` printed its "keeping N deg" note instead of poisoning the yaw.

## Profile feasibility (Ashenvale (22-24).xml, 44 hotspots)

Measured from the file itself: hotspot spacing inside a cluster is 6-97 yd
(typically 27-60), the Zoram cluster spreads 97-269 yd, quest centroids are 276-1485
yd apart, and the nearest hotspot to the 15:57 player position is 448 yd. Tool reach
is 5-45 yd per leg with `MAX_LEGS 24`, no routing and no Z handling. So: within-cluster
hops are reachable; anything profile-scale (RunTo to vessels, cross-zone portals,
LoadProfile chains) is not, and needs a waypoint router plus transport handling.

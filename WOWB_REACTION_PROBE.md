# 70291 NPC reaction: evidence and read-only probe

**Status: unresolved; Grind cannot yet reliably distinguish friendly, neutral
attackable and aggressive NPCs.** The existing target failsafe remains in place.
Do not use `+0x1E60`, a faction template ID, distance, TAB selection, level or
NPC flags as a complete friendliness or attackability test. Reputation, PvP,
at-war state, NPC phase and asymmetric reactions can affect the result.

Prior read-only static traces on `wowb-decoded.exe` link the registered
`UnitReaction` handler RVA `0x1D89A00` to helper `0x27EDBC0`, and
`UnitCanAttack` RVA `0x1D48E20` to `0x27EF450`. The latter checks multiple
fields and invokes the reaction helper in both orders on some paths. Those
traces are bounded pseudo-disassembly, not a safe-call ABI or proof of a
particular field; see `hunt/WOWB_70291_TRACE_FINDINGS.md` (the 16:19–16:36
sections). The one 16-byte UnitHealth signature used by the probe rules out
some mismatched clients but is **not** complete build verification. Nothing
in this change calls client functions or uses Lua.

## 2026-11-01: identified faction-template key, not reaction

The user's 12 labeled samples (Drive file `1JmI9h6dS0FXY7k1-DO9VOetAVivTfiQ7`,
MD5 `a6c6ccbd567117e34557e8660dacc3c6`) include five friendly NPC GUIDs,
three neutral attackable NPC GUIDs, and four aggressive NPC GUIDs. They show:

| User label | Distinct GUIDs / entry hints | Unit `+0x1E5C` values |
|---|---:|---|
| Friendly | 5 / 3 | 55, 57 |
| Neutral attackable | 3 / 1 | 189 |
| Aggressive | 4 / 3 | 24, 44, 66 |

All three neutral observations share the same NPC entry hint and the same raw
unit `+0x90` value (1125), so they do not provide evidence of three distinct
NPC types. In all 12 observations the hint matches `+0x90`; its exact semantics
still require validation. Labels and reactions are not independently confirmed
by the probe. The selected friendly units with template 57 and all
sampled aggressive units have `+0x1E60 == 0`, a direct counterexample to treating
zero flags as proof of hostility.

Read-only Capstone disassembly of the user's decoded image (Drive
`1cipD5-ylc1p_sdGxOzSirH57GbrHIURE`, MD5
`5a137c8a11e9dee6581d4244a97c2546`) gives an independent static link:

* UnitReaction's two-unit path calls `0x27EDBC0`; its normal branch at
  `0x27EE022` loads dword `[firstUnit+0x1E5C]` and calls `0x27EE160`.
  At `0x27EE199` that helper loads dword `[secondUnit+0x1E5C]`; both keys
  go to the same table lookup at `0x658990` using image RVA `0x7B43760`.
* At `0x1BF044`, the image passes metadata RVA `0x4D68AA0` and table RVA
  `0x7B43760` to the same initializer. Metadata begins with a pointer to the
  string `FactionTemplate` at RVA `0x52A0318`. These RVAs are from this image,
  not safe callable APIs or proof that all other clients share the offsets.
* The relevant lookup site at `0x27EE18D` begins with bytes
  `488D0DCC553505E8F7A7E6FD8B935C1E0000`. The probe checks these and the
  existing UnitHealth signature before collecting a limited table diagnostic;
  two partial signatures still are not full build verification.

This supports `+0x1E5C` as the **FactionTemplate key**, not a direct
friend/foe/neutral value. The routine also consults additional flags, lookup
records, player control, standing and special-case paths. A template can
change relation because of reputation or state. Do not hardcode the observed
IDs as hostile/friendly or assume all neutral units are attackable. The existing
Grind failsafe and selection behavior have not been altered by this finding.
`TryReadFactionTemplateId` and the startup census expose the key for diagnostics
only; the caller must already have validated the unit pointer, and reads are
non-atomic.

The upgraded probe additionally captures bounded raw bytes from the identified
FactionTemplate table instance, metadata pointer, storage object and storage
vtable, but only when both diagnostic signatures match. Pointers are checked,
failed reads remain absent, and table metadata/storage pointers are rechecked;
the observation remains non-atomic. This is for reconstructing the table
layout, not a row decoder or gameplay classifier. It may disclose addresses and
other process data and should only be shared privately. Older samples without
this optional `dbProbe` remain readable.

## 2026-11-01: more samples and bounded table snapshot

The four additional user-labeled neutral observations (Drive file
`16rF0iX6jkIBPE-m_vLpjzUDTXR-cq2cl`, MD5
`8b12645acc9ddb689bc21f979fb80068`) have four entry hints: 69 and 299
with template key 32, 250873 with key 7, and 251169 with key 634. They were
captured on **two other player characters**, not the original character:
those players have observed template keys 1 and 3629 respectively, while the
original player's key was 3. This is useful diversity but does **not** supply
three independent neutral types for the original player. Player faction and
other context confound pooled comparisons. The analyzer now requires one player
at a time. Reputation and other context can also change for one player.

All four new samples include the previous optional `dbProbe`; that probe did
not capture the heap-resident rows or index. In those snapshots the table root
at RVA `0x7B43760` has seven 24-byte field descriptors at `+0x10` and seven
24-byte palette descriptors at `+0x28`. Storage vtable RVA `0x4E480A8`, method
`+8` (RVA `0x678180`), reads the index pointer at storage `+0xB0` and count
at `+0xB8`, with four-byte entries. It checks key range at `+0x40/+0x44`, then
uses a 16-bit row index and, for primary rows, base pointer `+0x08`, stride
**dword `+0x38` = 7**, and count **word `+0x48` = 440**. The observed primary
row region is 3,080 bytes, not 18,040: the latter mistakenly uses the secondary
stride at `+0x3C` (41). The secondary pointer and count are zero in these four
snapshots. The map has 453 entries (1,812 bytes). These are observations, not
constants to hardcode into the classifier. Special index values, secondary
rows, external/hotfix overlay and compressed fields need separate handling.

The probe can now include bounded primary row/index bytes, field/palette
headers and bounded palette data after matching both partial signatures and the
observed storage vtable RVA. It caps every derived read, rejects unsafe
pointers and sizes, double-reads data and rechecks key headers and main regions.
Any failed or changing read leaves the affected details absent; even a successful
capture is not atomic and does not detect every change-away-and-back. There is
no client call, Lua, row decoder or gameplay classifier. These additional raw
heap bytes can contain process data: share the sample privately, not publicly.

To obtain the missing table evidence, after rebuilding select one living NPC on
the **original player character** and capture one correctly labeled sample;
share the updated JSONL privately. The UI should report "bounded rows included".
If it reports rows unavailable, share the result as-is; don't assume a decode.
This does not require recollecting all existing samples.

## 2026-11-01: nine recent friendly samples and missing array palettes

In Drive file `1CPAVuS-h3XZ8Qwvj3ohMALmFEiga5cJG` (MD5
`149cb7a9fe642814658988842f369607`), the latest sample's preceding ten-minute
window contains nine friendly observations at 15:45:36–15:46:14 UTC: nine
full NPC GUIDs and six unverified entry hints. They are from the original
player (template key 3). The older four neutral observations in the file lie
outside that window and were not treated as recent. All nine recent snapshots
captured the 3,080-byte primary rows, 1,812-byte index and 168-byte descriptor
vectors; the main regions have matching SHA-256 hashes across these samples.
This is useful consistency evidence, not an atomicity guarantee. Friendly
entries use keys 55 and 57; these keys remain labels of records, not universal
friendly/hostile classifications.

The field descriptors show seven packed fields: compression modes 1, 5, 1, 3,
1, 4 and 4. The captured scalar palette (field 3) contains 11 four-byte values.
An offline diagnostic inspector can recover scalar fields and packed array
indices for the primary captured rows. For example keys 3, 55 and 57 have
scalar values respectively `[3, 72, 3, 2, 12]`, `[47, 0, 2, 2, 4]` and
`[47, 2081, 3, 2, 12]`; these are raw fields and are **not** attackability.
The captured table is only a snapshot: special and secondary rows, hotfix or
external overlays, player-state overrides and asymmetric reactions are not
resolved by this inspector.

Further disassembly of the reaction template helper shows type-4 fields 5
and 6 use a **different** pointer vector rooted at table `+0x40`, rather than
the scalar palette vector at `+0x28`: `0x27EE4D7` and `0x27EE6F7` call the
8-byte pointer-vector accessor `0x202970`, then index eight four-byte slots,
reading the low 16 bits of each slot. Their observed descriptor data sizes are
2,976 and 6,112 bytes (93 and 191 eight-element arrays). Those arrays were not
in the previous probe, so their contents cannot be recovered from this file.
The probe now captures this second vector and bounded arrays only for the
observed type-4, eight-element format, with caps and double reads. The UI
reports whether **array palettes included**; metadata/rows may still be saved
if they are unavailable. The arrays and other dump data remain private.

After rebuilding, one correctly labeled living NPC sample is sufficient for a
new table snapshot; it need not be a different NPC. Share the appended JSONL
privately. It should say both "bounded rows included" and "array palettes
included". No combat, vendor, target failsafe or classification behavior is
changed. A full client reaction still cannot be inferred just from a table row.

Run `python Tools/WowBReactionAnalysis/inspect_table.py <private-samples> 3 55 57`
to inspect the last available table capture, without emitting a classifier.

## Collect labeled observations

1. Build and attach the x64 application. Stop Grind, GatherBuddy and routes.
   Select a **living NPC** in the game. Open **Gather & tools → NPC reaction
   samples**. Click the label you know applies: a friendly town NPC, a yellow
   monster known to be attackable, or an aggressive red monster. Capture is
   read-only and doesn't select, click, attack or move the target.
2. Capture at least 3 *different kinds* of NPC for each label, and repeat at
   least one of each after reselecting it. More examples in different areas,
   levels and circumstances are better. Only mark a yellow NPC attackable if
   independently known; yellow does not itself prove it can be attacked. The
   same NPC changing reaction or reputation is especially useful, but record
   that context separately. A sample label is the user's observation, not a
   game-computed reaction. Selection and pool are checked before and after,
   but observations are non-atomic and a change-away-and-back can be missed.
3. Share `Settings\wowb-reaction-samples.jsonl` from the application's output
   directory. It appends to the same file each time. It contains full player
   and target GUIDs, unit addresses, map IDs, raw unit bytes, and limited table
   diagnostic bytes/addresses; inspect before sharing, as it can disclose
   character/world identifiers. It is gitignored.
   Do not share publicly or include an unrelated full memory dump.

To inspect locally with Python 3, from the checkout run:

```
python Tools/WowBReactionAnalysis/analyze.py "path/to/wowb-reaction-samples.jsonl"
# If multiple players are listed, re-run with --player FULL_GUID
python -m unittest discover -s Tools/WowBReactionAnalysis -v
```

The analyzer refuses to pool different player characters, or rank with fewer
than three distinct GUIDs **and three unverified GUID entry hints** per label, excludes full GUIDs with conflicting
labels, and skips words missing from any sample. It prints dword/bit
correlations only; they can reflect identity, level, type, distance, location,
health, pointers, etc. Even perfect correlation is not an offset proof. Use
independent held-out NPCs and reaction/at-war/phase changes and statically trace
the data flow before adding any target filter. The hint `(GUID.High >> 6) &
0x7FFFFF` was observed for gameobjects, not yet validated as NPC entry ID.
The probe reads target and player windows in separate uncached 256-byte blocks;
unreadable blocks are omitted, never silently zero-filled. It logs chunk counts.

## Optional deeper static trace

`hunt/WowBRelationTrace70291.java` is a bounded read-only Ghidra script for the
existing decoded image/project. Copy it to `C:\ghidra-scripts\`, run it in
Ghidra against the already imported `wowb-decoded.exe`, and share its generated
`WowBRelationTrace70291-*.txt`. It prints signatures, raw instruction bytes,
branches and direct-call leads; output bounds, overlapping/junk instruction
paths and incomplete xrefs apply. It has not been compiled or run here. Do not
call those RVAs merely because the trace prints them. Live labeled observations
are still necessary.

## Selling limitation

The current 70291 vendor flow uses configured vendor points and recorded UI
clicks. It cannot read the merchant window or actual bag contents, and its
current nearby-NPC choice is a heuristic, not a validated vendor/friendly test.
A posted click or replayed sequence does not prove anything was sold. Do not
interpret this probe as completing automatic vendor identification or selling.
The 3.3.5a path is unchanged.

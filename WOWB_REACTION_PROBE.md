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

## 2026-11-01: complete primary arrays and template-only comparison

Drive file `1MbnoIT5cV7gNOqe5JrpdpLaDrrCV1pjz` (MD5
`5aeb3808eb70eff175ddc0a70d8b1fa6`) has 12 user-labeled friendly samples
from the original player, captured 15:54:40–15:55:19 UTC. All contain the two
previously missing array palettes (2,976 and 6,112 bytes); field descriptors,
index, primary rows and array palette payloads match across the 12 captures.
No raw dumps are committed. The new data is sufficient to inspect the *primary
captured rows*, not necessarily the values returned by the full client lookup
when secondary, external, hotfix or other overlays are active.

The offline helper `compare_templates.py` models only the normal table helper
`0x27EE3B0`, using the decoded fields, in both directions. Its observed branch
order is: first row field 4 intersects second row field 2, or a nonzero prefix
of first row field 5 matches second row field 0 => 1; then first field 3
intersects second field 2, or first field 6 matches second field 0 => 4; then
first field 2 intersects second field 3, or second field 6 matches first field
0 => 4; otherwise return 1 if bit 13 of first field 1 is set, else 3. Both
array loops stop at the first zero. These numeric results are not the complete
UnitReaction or UnitCanAttack result and the helper is never called in-game.

For the previously labeled examples on this player (template key 3), evaluating
the latest table snapshot yields target→player and player→target **(4,4)**
for friendly keys 55, 57 and 875, **(3,3)** for the previously labeled neutral
key 189, and **(1,1)** for aggressive keys 24, 44 and 66. Some unit labels were
collected at an earlier time; the current table snapshot is not evidence that
its data or player state was identical then. These are few examples, not held-out
validation and not a reason to hardcode template keys or numeric relation rules.

Static disassembly also confirms why the table helper cannot safely be used as
an attack filter: `0x27EDBC0` has owner, player, PvP and dynamic special-case
branches; `0x27EE160` consults player-derived overrides and, for eligible
factions, reputation paths. A 32-entry pair array at player-derived `+0x5EA8`
is checked before some template paths; the type of that player-derived pointer
and the semantics of every entry are not yet independently validated.
`0x2F99080`/`0x2F98C10` reference a 16-byte-stride region beginning at image
RVA `0x7E1A530` and a separate table at `0x7B43520`, but their layout,
initialization and relationship to a particular character still require proof.
`0x27EF450` additionally checks multiple unit flags and may call reaction helpers
in both directions. No gameplay or vendor filter was changed. Existing
failsafes are unchanged; vendoring and actual selling remain unverified.

For private offline inspection:

```
python Tools/WowBReactionAnalysis/compare_templates.py <private-samples.jsonl> 55 3
```

## 2026-11-01: controlled player-state comparison (diagnostic only)

The current probe has an optional `stateProbe`. In addition to the existing
UnitHealth signature, it checks 16 bytes at reputation-read RVA `0x2F98C10`
(`4883EC288BD14C8D4C2438488D0DFEA8`) and requires the local player vtable.
It double-reads 16,000 bytes from image RVA `0x7E1A530` (1,000 candidate
16-byte slots) and 256 bytes at local player-unit `+0x5EA8` (32 candidate
8-byte entries). The latter is only a *candidate*: the player-derived pointer
in the static trace is not yet proven always to equal the local unit pointer.
Object identity is checked by the existing before/after snapshot. The bytes are
optional and non-atomic; partial signatures do not prove the whole build. The
capture does not call client functions, Lua, move, target or interact, and does
not claim to compute UnitReaction or UnitCanAttack. These raw bytes may contain
sensitive game/character data, so share privately. Old sample files remain valid.
The UI reports `state diagnostic included` only if both regions were readable
and stable across the two reads.

Suggested controlled observation, using a **single character** with the bot
stopped throughout:

1. Select a living friendly NPC; capture `Friendly town NPC`. Separately note
   its name, appearance/color and faction if shown. Do not attack it.
2. If a faction's reversible **At War** checkbox can safely be toggled, keep
   that *same NPC selected*, record its initial setting, then capture it before
   and after changing the checkbox. Use `Other / changed state` for an
   observation that does not cleanly match the three specific labels, and note
   the checkbox, visible target color and any other game feedback for each
   sample. Restore the exact initial checkbox setting. Skip this entire step if
   unavailable or unsafe. Toggling a checkbox alone does not prove a target is
   attackable or that the checkbox affects that particular NPC.
3. Independently capture one yellow monster already known to be attackable and
   one monster known to attack on sight. Record names and observations. To
   validate the yellow example, if safe, manually attack **the monster** after
   capture and note whether it actually takes damage; do not attack town NPCs.
   A red color alone does not prove aggression. Stop the bot and don't perform
   these checks in an unsafe situation. The probe itself never attacks.
4. Share `Settings\wowb-reaction-samples.jsonl` privately along with the notes
   and roughly which times correspond to before/after. The appended file may
   contain prior samples; no deletion is necessary. Tell us if it says `state
   diagnostic unavailable` rather than assuming bytes were captured.

For a matched pair of lines (1-based) the offline command
`python Tools/WowBReactionAnalysis/compare_state.py <private-jsonl> <before-line> <after-line>`
reports changed slot indices only and requires the same player and target GUIDs.
Differences are leads, not proof of a particular faction, standing or attackability.
The fourth label `other_observation` is for context and is excluded from the
existing three-label correlation analysis; it does not change targeting.

## 2026-11-01: Crag Boar before and during retaliation

The latest version of Drive file `1JmI9h6dS0FXY7k1-DO9VOetAVivTfiQ7`
(MD5 `05b1ec78335ff1e8ea5bf5e94aaa8ee6`, modified 16:14 UTC) contains
five new observations: two user-labeled friendly NPCs, one user-labeled
aggressive monster, and two `neutral_attackable` samples of the same Crag
Boar, before and while the user reports it was attacking. This Drive file ID
previously held a different file version (the earlier MD5 quoted above).
All five new observations include the optional state and table diagnostics;
all candidate reputation regions, player override regions, and captured primary
table regions match across these samples. Read consistency is not atomic.

For the same character and the same boar, at 16:13:27 UTC and 16:13:39 UTC:

| Candidate field | Before | During retaliation |
|---|---:|---:|
| Boar FactionTemplate key | 189 | 189 |
| Boar health qwords (current/max) | 120 / 120 | 71 / 120 |
| Player health qwords (current/max) | 220 / 220 | 192 / 220 |
| Boar `+0x1E60` | `0x00000000` | `0x00080800` |
| Player `+0x1E60` | `0x00000008` | `0x00080808` |

The user's report and the falling boar health support attackability in this
particular encounter; no direct game `UnitCanAttack` result was sampled, and
health alone does not establish the damage source. The flags changes support
only a **combat-state candidate**; a neutral monster retaliating is not proof
that it was aggressive before the pull. Neither `0x80000` nor `0x800` proves
attackability, allegiance or attacker identity. The candidate reputation and
player override regions did not differ between these two snapshots. That does
not validate their semantics or rule out changes between reads, and there was no
At War checkbox test. The table-only helper returns (3,3) for boar template
189 vs player template 3; it returns (4,4) for friendly keys 55/57 and (1,1)
for the user's aggressive example key 44. These are consistent with the labels
in this small sample but remain neither a live reaction result nor a complete
attack predicate. Do not use them to weaken the existing failsafe or drive
vendor interactions. No gameplay or selling behavior was changed.

The offline `compare_state.py` also prints candidate health qwords and
`+0x1E60` for the same-player, same-target pair when both chunks exist; missing
chunks stay unavailable. It does not infer reaction or attackability.

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

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
   and target GUIDs, unit addresses, map IDs and raw unit bytes; inspect before
   sharing, as it can disclose character/world identifiers. It is gitignored.
   Do not share publicly or include an unrelated full memory dump.

To inspect locally with Python 3, from the checkout run:

```
python Tools/WowBReactionAnalysis/analyze.py "path/to/wowb-reaction-samples.jsonl"
python -m unittest discover -s Tools/WowBReactionAnalysis -v
```

The analyzer refuses to rank with fewer than three distinct GUIDs **and three
unverified GUID entry hints** per label, excludes full GUIDs with conflicting
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

# wowb.exe 70291: inventory investigation

Status: **research only**. No 70291 container/slot/item layout, free-slot count,
item identity, or mail eligibility has been established. Do not wire candidates
into selling or mailing. The existing corpse-interaction threshold remains the
fallback; neither a loot click nor a replayed sell plan proves items moved.
Nothing here calls Lua or runs client code.

## Why the previous hunt is inconclusive

`BagSlotHunt` compares 32-bit words from nine bounded player-object windows
between samples. It assumes a slot count changes by exactly one. Loot can fail,
produce multiple items, fill an existing stack, or occur between other changes;
a count might be stored indirectly, outside those windows, in a differently
encoded field, or not cached as a single count. A zero-result report does not
prove the player has no inventory fields. The NPC-only world snapshot deliberately
filters to structurally validated units; it is not a complete inventory scanner.
The handle-pool walker can enumerate records with GUIDs, but has not established
that all bag items appear there or how to identify them.

The 3.3.5a `BagStructure`/`WoWContainer` is a useful **conceptual** reference:
backpack and equipped containers, capacity, slot GUIDs, and item properties.
Its 32-bit pointers, GUIDs and `+1888` are not offsets for 70291. An item GUID
and an item entry/template ID are distinct: do not infer a name or item ID by
bit-slicing an unvalidated GUID. A bag can be full while a stack can still accept
more items, and specialty-bag empty slots may not accept ordinary loot.

## Static trace, no Lua

`hunt/api_map.txt` records 70291 handler addresses for `GetInventoryItemID`,
`GetInventoryItemCount`, `GetInventoryItemsForSlot`, and `GetSendMailItem`.
These are leads into the client code, **not** evidence that the handler has a
safe callable ABI or that it reads ordinary bag slots. The first three concern
inventory/equipment slots and the last concerns an outgoing mail attachment.
`hunt/api_names_runtime.txt` also contains the names `GetItemGUID` and
`GetItemIDByGUID`; names alone do not identify their handlers or their data.

`WowBInventoryTrace70291.java` is a bounded Ghidra pseudo-disassembly script
for these known entry RVAs and a few direct calls, plus optional existing xrefs
to the two name addresses. It does not change the program listing, attach to a
process, inject, call client functions, or execute Lua. Output can include
incorrect paths, missing xrefs, indirect calls and obfuscated bytes; review the
reported 16-byte build marker and decode quality before drawing conclusions.
The observed marker is not a complete build identity check. This script has not
been run against the user's image here.

To collect a static report later, copy the script into `C:\ghidra-scripts`, open
the matching 70291 program in Ghidra and run it in Script Manager. It writes
`C:\ghidra-scripts\WowBInventoryTrace70291-<timestamp>.txt`. Review the output
before sharing. No bag contents are captured by this static script.

## Validation sequence for a future read-only probe

Once static tracing identifies a plausible owner and storage, take bounded,
uncached, identity-checked snapshots while the bot is stopped. Do not follow an
arbitrary candidate pointer or interpret a missing read as an empty slot. Record
visible bag layout and repeat each observation; snapshots are non-atomic and
can miss transient changes. Compare:

1. An item moved from one known slot to another empty slot: the same identity
   should leave one location and appear at the other, while free-slot count is
   unchanged. Reverse the move and repeat.
2. A single **non-stackable** item placed in / removed from a known empty slot:
   occupancy changes by one in opposite directions. Check a stack merge
   separately: quantity changes but occupancy does not.
3. Different equipped bag capacities, backpack slots and empty/full normal
   bags; test specialty bags separately. Reject unstable, partial, duplicate,
   stale, wrong-owner or unloaded observations as **unknown**, never as empty.
4. For candidate item properties, compare different known items and stack sizes,
   including after moving the same instance. Validate full GUID ↔ entry/template
   ID ↔ quantity independently before resolving names or filters.

A future mail feature needs verified slot-to-item identities and counts, then
separate verification of recipient, mail window, binding/trade restrictions,
attachment and send result. Reading inventory alone neither proves an item is
mailable nor sends mail. Preserve the existing manual plan and STOP safeguards.

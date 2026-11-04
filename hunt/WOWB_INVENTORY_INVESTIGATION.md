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

## First static trace: 2026-11-02

The supplied bounded report was from `wowb-decoded.exe`, image base
`0x140000000`, and the 16-byte UnitHealth marker matched. This is a useful
sanity check, not complete build authentication. The two item-name strings
were present, but Ghidra reported no exact xrefs for them; absence of xrefs is
not evidence that the APIs or inventory data are absent. The 80 KB report was
truncated by the connector; the findings below use the visible portions only.

In the `GetInventoryItemID` handler (RVA `0x2F5F2B0`), one branch passes
`RDI + 0x9440` and a slot-like argument to `0x290CFC0`; if it returns non-null,
the handler reads a dword at the returned pointer `+0x90`. The provenance of
`RDI`, object type, slot numbering and meaning of the dword are **not yet
established**. A separate branch uses a `0x260` stride after a `< 0x13` guard;
it may be a different data source, so it must not be conflated with a bag list.
The visible `GetInventoryItemCount` path calls `0x290BDD0` with callback
`0x290ABC0`, which may shed light on enumeration but is not yet identified as
bag traversal. The original trace's first-callee limit spent its budget on
framework code and did not show these leads' bodies.

`WowBInventoryLookup70291.java` focuses on those three addresses and the two
preceding context/argument helpers from the same ID handler. It is another
bounded, static Ghidra trace: it does not attach to or call the client. It has
not been run locally. Do not treat any listed displacement as a bag, free-slot
or item-ID offset until static dataflow and controlled live observations agree.

## Focused static trace: 2026-11-02

The subsequent `WowBInventoryLookup70291` report showed that the
`0x290CFC0` path bounds an index using a dword at its input base, multiplies it
by 16 and adds the qword at input `+8`, before passing that address to a helper
at `0x1FC37F0`. The `0x290BDD0` path also indexes a 16-byte array and can
recurse through an indirect call. These are static dataflow observations, not
proof that the array contains item GUIDs, represents a backpack, has a known
capacity, or uses all-zero bytes for an empty slot. The initial handler applies
`+0x9440` to its resolved context object; its identity has not been proven to
be exactly the local player object used by our reader. The connector response
was truncated in the middle, so conclusions are limited to the visible parts.

### Read-only comparison probe

**Combat settings → Selling → Bag research snapshot** samples a candidate
header at the validated local player object's `+0x9440` and, if its observed
count is at most 256 and the pointer is in the bounded user-address range,
reads up to 256 16-byte entries twice. The input header and entries, player
identity, attachment and bot state are checked again; failed or changing reads
are discarded. Four short code signatures reduce accidental use on a different
client but do not prove a build or object layout. Data reads are non-atomic.
It does not call client code, use Lua, click, move items or change selling.
The raw header, process addresses, player GUID and candidate bytes go into the
bot log; review them before sharing. Do not infer emptiness, item ID, slot count
or bag fullness from the bytes. Observing a zero count or failed read is not a
full-bag or empty-bag result.

With the bot and any route stopped, press the button once, move a single known
item to an empty slot **manually**, then press it again. Keep the same character,
client and settings window open and let each capture finish before moving the
item. Press again after moving it back; repeat with a different item. The log
reports raw changed indices when the same player, array address and count were
observed; otherwise it prints a new baseline. A changed address, incomplete
snapshot or zero changes does not prove the hypothesis false. Do not delete or
send any items for this experiment. This probe was tested with synthetic reads,
not a real Windows client; no bag detection has been enabled.

## 70707 static trace: 2026-11-04

The user ran `WowBInventoryTrace70707` on `Wow-decoded.exe` with image base
`0x140000000`; four previously observed 70707 code markers matched. These
markers are a sanity check rather than a complete image authentication. The
bounded pseudo-disassembly completed; instructions and paths can still be
incomplete or misdecoded.

The `GetInventoryItemID` handler at RVA `0x26CAA90` has a branch at
`0x26CAB79` that passes a resolved context plus `0x9460` to a helper at
`0x2162D00`, using a slot-like argument; if it returns non-null the handler
reads a dword at returned pointer `+0x90`. This is structurally similar to the
previously traced 70291 `+0x9440` / `0x290CFC0` path, but the context is not
proven to be our local player, the argument is not proven to be a bag slot,
and neither `+0x9460` nor the returned value is a free-slot count. A separate
branch uses a `<0x13` bound and `0x260` stride; do not conflate it with bag
contents. There is no live inventory evidence in this static report.

`WowBInventoryLookup70707.java` follows the observed helper at `0x2162D00`
and the context/argument helpers `0x286B290` and `0x26C8DF0`. It prints bounded
pseudo-disassembly and direct-call leads only, verifies the same four static
markers, and does not execute client functions, run Lua, attach to a process,
or modify the Ghidra listing. It has not yet been run against the user's image.

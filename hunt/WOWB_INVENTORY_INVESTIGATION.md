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
or modify the Ghidra listing.

### Focused 70707 lookup and live comparison probe

The subsequent user-supplied lookup trace matched all four static markers.
At `0x2162D00`, one path bounds an index against the dword at the input base,
then uses `arrayPointer + index * 16` (pointer at input `+8`) as the argument
of a tail-call to `0x19FAE20`, previously identified as a candidate GUID
resolver. The surrounding control flow includes special index ranges. This
strengthens the structural similarity to 70291 but does not prove the entries
are GUIDs, reveal backpack or container slot ranges, or prove that the resolved
handler context is the local player object. The context helper `0x286B290`
resolves a GUID via `0x19FAE20` but the GUID's provenance remains unknown.

**Combat settings → Selling → Bag research snapshot** now also supports a
stopped 70707 attachment. It treats `localPlayer+0x9460` as a *hypothesis*,
checks local identity and partial build markers before and after the bounded,
uncached read, checks lookup/handler signatures when readable, reads the candidate
header and at most 256 16-byte entries twice, and rejects unstable or unreadable
data. A readable signature mismatch rejects; code pages inaccessible at runtime
are reported and do not waive the separate 70707 build and identity checks.
Results can still be wrong or incomplete, and do not establish the context owner,
slot meaning, empty entries, item GUIDs or bag count. The probe does not grant a
Grind session, run client code, click, move items, or affect selling.

For an initial comparison with the bot and all routes stopped: keep the same
character and attachment, take a baseline, manually move one recognizable item
from a known backpack slot to a known empty slot in the same backpack, capture,
move it back, capture, then repeat with a different item. Wait for each capture
to finish. Record the visible bag capacity, free count and source/destination
positions with each snapshot. Moving an item between empty and occupied slots
should not change total free slots; the experiment tests whether the raw entries
track its movement, not whether we can already count free slots. An array
reallocation, zero changes, a missing signature or an unreadable result is not
proof that the bag is empty. The output includes player GUID, raw candidate data
and process addresses, which can be sensitive. The 70291 probe remains separate
and retains its strict code signatures.

### 70707 controlled item-move log, 2026-11-04

The user reported moving copper ore and silverleaf; the exact screen slots and
which item corresponds to each change were not recorded. The first snapshot's
candidate header contained 145 entries and an inline 128-bit value equal to the
local player GUID in that session. The candidate array address, count and
header were unchanged across the later snapshots. During one move, entry 41
changed from a nonzero 16-byte value to zero; the following snapshot restored
the same value in entry 41. No other array entry changed in either observation.
A separate move caused the same 16-byte value to move from entry 50 to entry 49,
and the next snapshot reversed the change exactly. This is live evidence that
at least some candidate entries track moves of inventory-related data; it is not
proof of the item names, the slot numbering, that zeros always mean available
slots, or that the array contains every equipped bag's contents. The one-entry
change could reflect a different container, an item on the cursor, a partial
observation, or other effects; the log does not distinguish them.

The 145 entries must not be counted as backpack capacity: the array can include
other categories. Preserve unknown on incomplete reads and retain the existing
selling fallback.

### 70707 rough-stone cross-bag comparison

In the subsequent user-reported rough-stone move to another bag, the comparable
baseline at 10:59:39 had no changes relative to the previous snapshot. At
10:59:44 only entry 42 changed, from a nonzero 16-byte value to zero; at 10:59:47
it returned to the identical value, again with no other entry change. Both
signatures matched and the same candidate header, count and array address were
reported. This is consistent with a moved stack leaving and returning to the
candidate array, with its destination contents tracked somewhere else. The log
alone cannot exclude a cursor-held or otherwise intermediate state, a stack
merge, non-atomic observations, or other interpretations. It does not prove the
destination bag layout, capacity, free count, or the identity of the value.

`WowBContainerSearch70707.java` is a bounded, static-only follow-up: with the
same four marker checks, it searches for exact container API names and raw
little-endian pointers to the strings, then follows executable adjacent-qword
candidates with bounded pseudo-disassembly. `GetInventoryItemID` is the known
positive control. Name/pointer matches and linear decoding can be incomplete or
misleading, and absent names or references do not prove an API is missing. It
does not run client code or change the Ghidra listing. As before, findings for
70707 do not establish 70291 offsets or authorize bag-full decisions.

### 70707 container registration search result

The user's completed `WowBContainerSearch70707` report matched all four image
markers. Its `GetInventoryItemID` positive control found the expected direct
name pointer at RVA `0x492EDC0` and executable following-qword candidate
`0x26CAA90`. Exact, NUL-terminated container names were found in `.rdata`:
`GetContainerNumSlots` at RVA `0x40FDAD0`, `GetContainerNumFreeSlots` at
`0x40FDA90`, and `GetContainerItemInfo` at `0x40FDF88`. None had an exact
existing Ghidra xref or a direct preferred-VA64 pointer in initialized readable
blocks. Thus no container handler was discovered; these names may be referenced
indirectly, encoded differently, or not represent native registrations.

`WowBContainerRefs70707.java` checks the same image and exact name bytes before
printing nearby printable strings, scanning for matching little-endian RVA32
bytes, and scanning executable bytes for a limited set of RIP-relative MOV/LEA
encodings into that region. The scans are bounded and read-only. Raw RVA matches
can be coincidences, and RIP candidates may start in the middle of instructions;
none proves a callable handler or runtime behavior. Scan errors, output caps,
other reference encodings, and the absence of a hit must not be interpreted as
proof that no reference exists. This static search does not inspect live bags or
authorize bag-full detection.

### 70707 container reference follow-up result

The user's `WowBContainerRefs70707` report matched the four image markers and
all four name markers. Neighboring strings include API usage descriptions,
confirming the labels relate to `C_Container`, but not where the data lives.
No exact RVA32 patterns were found. The raw executable scan reported candidate
seven-byte LEAs to `GetContainerNumFreeSlots` at `0xD5C2E2` and `0xD65BE5`,
and to `GetContainerNumSlots` at `0xD5C82B` and `0xD65C04`. Each is accompanied
by an overlapping six-byte decode starting one byte later; these are not
independent observations and may be mid-instruction decodes. The nearby scan
reported 142 matches but displayed only 32; the exact scan reported 12 and was
not capped. The scan reported no block errors or cancellation, within its
stated instruction-encoding and chunk bounds. These are unverified code
references, not proven handler entries or layouts.

`WowBContainerCode70707.java` checks the image and site bytes, reports bounded
context, looks for containing PE `.pdata` runtime-function ranges, and performs
bounded pseudo-disassembly from unique readable containing ranges for the two
local sites. Backward paths, `.pdata` candidates and linear decoding are static
leads only and can be incomplete or misaligned; no client functions are called.
Nothing here establishes container boundaries, free slots, or 70291 offsets.

### 70707 candidate container handler and helper trace

The user's `WowBContainerCode70707` log matched the image and site markers.
The candidate registration sequence pairs `GetContainerNumFreeSlots` with a
pointer to RVA `0xD5C230`, and `GetContainerNumSlots` with `0xD5C730`, via
arguments immediately before a common registration-like call. Unique `.pdata`
ranges containing the local code start at `0xD5C242` and `0xD5C73E` respectively;
those ranges do not themselves prove function entry points. Both traced paths
hit a 200-instruction cap and include suspicious overlapping/obfuscated flows.

In the apparent capacity path, the parsed argument has special `-1` and `-2`
cases: the former reads a byte at `+0x7D04` from an object returned by
`0x1F58470`, and the latter yields `0x20`. Otherwise an unsigned comparison
against `0x16` precedes a call to `0x2567670`, followed by resolver candidate
`0x19FAE20` and, on non-null, a dword read at returned object `+0xAC0`. In the
apparent free-slots path, the argument and two output buffers are passed to
`0x2569110`. These are promising static leads, not validated bag counts: object
identity, index mappings, special bag semantics, error handling, output meaning,
and actual player/bag coverage remain unresolved. Do not call these routines
or reuse their offsets on 70291.

`WowBContainerHelpers70707.java` traces those three direct-call targets with
bounded pseudo-disassembly and checks both prior image markers and observed
call/registration bytes first. It is read-only, does not execute client code,
and may misdecode, follow invalid branches or truncate; its output cannot by
itself authorize a bag-full decision.

### 70707 candidate container fields and stopped comparison probe

The user's `WowBContainerHelpers70707` report matched all image/call markers.
The free-slot candidate at `0x2569110` completed within its 300-instruction
bound. For inputs 0..22 it obtains a candidate 16-byte identity from the table
at RVA `0x6D41CA0` (for indexes 5..22 an additional untraced gate applies),
passes it to the `0x19FAE20` resolver candidate, reads the returned object's
signed dword `+0xAC0`, then examines one qword at `+0x490 + 16*i` for each
entry, decrementing the initial value when that qword shifted right 58 is
nonzero. The two negative-index paths instead inspect the `+0x9460` array;
one conditionally examines indices `0x23` through `0x23+byte(+0x7D04)`
(exclusive) when a candidate flag is set, and the other scans `0x70..0x8F`.
The static
code's context resolver `0x1F58470` hit its 300-instruction limit amid
obfuscated/overlapping flow; its return identity is not proven. The high-six
bits are not proven to represent item presence, nor are the negative index
meanings, all equipped containers, ordinary-bag capacity or special bag rules.

The stopped-only **Bag research snapshot** additionally samples 70707 candidate
identities 0..4 from RVA `0x6D41CA0`, independent of the 70291 probe. It rejects
readable mismatches in 70707 container code markers, reports unreadable markers,
requires the separate player/build checks, a unique pool match with stable
record/object identity, and an unsigned raw
`+0xAC0` value at most 64 before sampling candidate entries twice. Identities,
headers, capacity, pool slot and data are rechecked; a final identity table,
player and attachment check guards the aggregate, though reads cannot be made
atomic. The initial probe grouped 16-byte chunks at `+0x490` and counted the
first qword's high-six bits; the 11:38 comparison below prompted a corrected
candidate grouping at `+0x488`, counting the second qword instead. These are
explicitly research observations, **not** validated capacity/free counts.
Unreadable, zero, duplicate, missing, out-of-range, or unstable candidates remain
unknown. The raw report contains GUIDs and process addresses; there are no game
function calls, Lua, automatic decisions or changes to the selling fallback.

For live comparison after building the updated version, stop the bot and routes,
keep one character and attachment, take a baseline snapshot, manually move one
whole stack from the backpack into an empty square in an equipped bag, wait for
the move to finish, snapshot, then move it back and snapshot again. Screenshots
of open bags or visible free counts can help correlate the reported candidates;
numeric slot IDs are not needed. Do not split or merge stacks during this test.
Keep the bags unchanged until each snapshot completes. No match or incomplete
reads are inconclusive, not evidence of full or empty bags.

### 70707 live container probe: 2026-11-04 11:26

The user's first snapshot reported `markers or player unavailable` for the
container portion; the message did not distinguish which guard failed. It did
capture the original `+0x9460` candidate array. In the two later snapshots,
entry 38 changed from nonzero to zero, then returned to the same value; no
other entry in that array changed. The new container probe then resolved two
nonzero identity-table entries to unique current pool records but rejected both
record `+0xD8` values as outside its pointer filter. The probe did not log the
values, so zero, misalignment, range failure and other causes cannot be
distinguished. The other three identity-table entries were zero, whose meanings
remain unknown. No container contents or free slots were validated.

The stopped-only probe now distinguishes the failed code marker from player
identity checks, logs the raw `+0xD8` value and a double-read bounded header if
it points into user space, and scans record `+0x28..+0x180` at eight-byte steps
for pointers to candidate headers containing both the record back-pointer and
full identity. Four-byte-aligned candidate addresses are eligible, not just
eight-byte-aligned ones. It rechecks the record fields and candidate header,
requires one distinct matching header and rejects unreadable fields, unreadable
candidate headers, or changes; it also retains the earlier object, identity,
capacity, snapshot and bot-state checks. The bounded scan is a heuristic and
cannot prove that its match is the client's resolver result or that zero or
multiple matches describe bag state. Logs contain raw data and process addresses.
The probe remains read-only and does not affect selling. A single stopped
snapshot is enough to diagnose the pointer rejection; item movement need not be
repeated unless the new report resolves candidates.

### 70707 live marker availability: 11:32

The user's next stopped snapshot recorded `code marker 0xD5C7FC unreadable`
and did not reach the container record scan. This differs from the 11:26 later
snapshots, where the container marker check succeeded, but the log alone does
not establish why the code page was unreadable. No new pointer values were
captured and no bag state was determined.

The stopped 70707 container research now follows the existing 70707 inventory
probe's partial-signature policy: every marker is checked and reported before
and after, any readable mismatch rejects, and unreadable markers remain explicitly
unconfirmed rather than aborting this read-only diagnostic. Separate build and
player/attachment checks remain mandatory, and all record, bounds, stability
and bot-state guards remain in force. This is not full image authentication,
and does not authorize gameplay decisions or change the stricter 70291 path.

### 70707 container candidate baseline: 11:36

The user's stopped snapshot completed the bounded record scan. Both nonzero
identity-table candidates had exactly one stable header matching the full
identity and its pool-record back-pointer, at record `+0x28`. One candidate
object was four-byte aligned but not eight-byte aligned. The candidate identities
also match, respectively, entries 30 and 31 of the previously captured
`localPlayer+0x9460` array in this session. Each object's raw `+0xAC0` was 6,
and its six 16-byte `+0x490` entries were all zero. The other three candidate
identities were zero. The handler code markers were readable and matched, while
three helper code markers were unreadable both before and after. The separate
player/build checks and record/data stability guards passed. Neither the two
identities' visible bag locations nor the meaning of the six zero entries has
been verified; the 11:36 capture provides a baseline, not a capacity or
free-slot verdict.

Next, without restarting the bot application or changing attachment, move one
whole stack from the backpack into an empty square in another equipped bag,
wait for the move to complete and take a snapshot; move it back and take another
snapshot. The prior baseline can be reused if the application remains open;
otherwise take a fresh baseline first. A screenshot of the open bags or visible
free counts can help establish which candidates correspond to which bags. No
numeric slot IDs are required. A changed value will still need interpretation
and wider validation before it could control selling.

### 70707 live cross-container move and grouping correction: 11:38

The user supplied two 11:38 snapshots. The first was a fresh application
baseline, not comparable to a prior snapshot in that application; it showed
candidate 1's `+0xAC0` value 6, with one nonzero tag out of six. The next
snapshot showed the identity that had occupied player-array entry 36 returning
there from zero, while the corresponding bytes in container candidate 1 became
zero; the candidate tag-zero count changed from 5 to 6, with candidate 0
unchanged. The exact 128-bit value matches across the player array and the
container bytes. The earlier 11:36 snapshot from the same WoW process and
identity showed both candidates' sampled words zero, but was a separate bot
application session; do not treat it as an atomic baseline across the restart.
All five container-specific code markers matched in both 11:38 snapshots.

The old report grouped 16-byte chunks from `+0x490`; in this observation the
item's low and high 64-bit halves appeared across two adjacent chunks. The
client's helper examines the high qword at `+0x490 + 16*i`, and the contiguous
identity observed here begins eight bytes earlier. The research probe now groups
candidate 16-byte entries starting at `+0x488`, counting high bits from each
entry's **second** qword. This is consistent with one live move, not proof of
all container layouts or empty-slot semantics. Synthetic tests reproduce the
observed cross-chunk case and assert it becomes one changed 16-byte candidate.

The stopped 70707 snapshot also reports the experimental special-index path:
local player candidate `+0x7D04`, the previously captured header flag at
`+0x9480`, and, only when bounded and enabled, tag-zero observations over
`+0x9460` entries starting at `0x23`. Size, array, identity, state and selected
static markers are rechecked; readable marker mismatches reject, while
unreadable markers are disclosed. The static helper's context and special index
are not proven to mean the player's backpack; no reported value is used for
selling or gameplay. If client state is incomplete, the result is unknown.

### 70707 annotated cross-container comparison: 11:44

The user supplied a screenshot of the combined backpack with a stack of 20
marked at its source and an arrow toward an empty lower-right square. The
screenshot appears to show 32 squares, 15 containing items and 17 empty, though
two squares are partially covered by the annotation; the image is not an
independent proof of bag boundaries or of exact capture timing.

Three comparable stopped snapshots establish the following observations:

| Snapshot | Player-range raw size / zero-tag | Candidate 0 size / zero-tag | Candidate 1 size / zero-tag |
|---|---:|---:|---:|
| Baseline | 20 / 5 | 6 / 6 | 6 / 6 |
| After move | 20 / 6 | 6 / 6 | 6 / 5 |
| Move back | 20 / 5 | 6 / 6 | 6 / 6 |

The exact 128-bit value moved from player-array entry 36 to one corrected
16-byte candidate entry in container 1 and back. The player and container
zero-tag changes are opposite, and `20+6+6=32` and `5+6+6=17`, matching the
apparent screenshot. All special-index and container code markers reported
matched. This is substantially stronger live evidence for this character and
move, but the screenshot alone does not prove coverage for other bag layouts,
zero identities, bank/reagent/specialty bags, stacks or in-combat changes.
Neither candidate values nor the arithmetic authorize bag-full selling.

The stopped 70707 research report now includes an **observed-candidate
subtotal** when the special player range and every nonzero identity among the
first five container candidates resolve and remain bounded/stable. The player
range is rechecked after container sampling. It reports how many zero identities
were not classified, declines to report a subtotal for unresolved nonzero or
duplicate identities, and explicitly warns that candidates can overlap or omit
bags. Reads remain non-atomic. The subtotal is never used in gameplay or selling,
and its equality to the annotated screenshot is not proof of a general total.

### 70707 user-confirmed empty slots: 11:53

A fresh application session and stopped snapshot reported candidate sizes of
`20+6+6=32` and zero-tag counts of `5+6+6=17`; the user confirmed there were
17 empty slots in total. Both nonzero container candidates resolved, and three
zero identities remained unclassified. The lookup marker matched, but the
inventory handler, all three special-index markers and all five container
markers were unreadable in this snapshot (before and after). Unreadable is not
an image-signature match. This independently confirms that the reported
research subtotal matches the user's count for this configuration; it does not
validate other bag configurations, a change in the number of occupied slots,
or completeness of the candidate set. It remains non-atomic research data and
is not used for selling or gameplay.

### 70707 new occupied slot: 11:57

After the user picked up an item that occupied a new slot, the stopped report
showed candidate sizes `20+6+6=32` and zero tags `4+6+6=16`, down from 17 in
the 11:53 report. Comparing the two separately captured 145-entry player arrays,
only entry 49 changed: its 16-byte candidate identity went from zero to nonzero.
The same two container candidates still showed six zero tags apiece; three
identity-table entries remained zero/unknown. Lookup and handler signatures,
all three special-index markers and all five container markers were matched
in this 11:57 capture. This supports the candidate interpretation for an actual
increase in occupied slots in this configuration, but neither non-atomic
cross-session comparison nor this one bag configuration validates other bags,
zero identity semantics or automatic full-bag selling.

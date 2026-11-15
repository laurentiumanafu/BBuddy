# 70938 experimental mailing

## Current experimental implementation

70938 Grind has a **default-off** experimental mail-after-selling option. It
requires a saved vendor with a same-map mailbox standing point, either a profile
with a same-map return hotspot or a profile-free pre-vendor return position, a
saved recipient containing only letters/hyphens, the prior combined-bag grid,
and separately recorded mailbox, Send Mail tab and
Send button coordinates. At the recorded standing point, the mailbox hover
candidate must match the saved full GUID. Client size, focus mode, map, player,
health, ghost, combat and STOP guards are checked. Changes to camera, UI scale,
window layout, bag configuration or client size require recalibration; the
controls are user-labelled, not independently detected. The merchant plan must
leave the calibrated combined bag open.

Before interaction, preflight reports the specific failed safety/geometry check.
Foreground and non-minimized background modes are supported experimentally;
focus mode is fixed for a mail attempt, and switching between game and another
application interrupts it. In background, mouse and character messages are
posted directly to the game window, not typed into the foreground application;
background recipient focus and client behavior are not verified. A held mouse
button gets a three-second grace period before any input; no input is sent while
unsafe. After a matching mailbox hover and posted interaction, the log reports
bag/grid/table checks and whether tab button messages were posted. A posted
click is not proof of a visible frame or tab. For every nonempty batch, including
after each observed send, the experiment clicks the tab, waits one second and
posts the recipient again; it cannot read back or clear the recipient field. It
attaches at most 12 candidate 0–3 raw slots and compares the mail record
candidate after each click. It sends once per batch, then requires a stable
zero-like candidate table and disappearance of the actually observed attached
raw IDs from observed bag candidates before attempting another batch. A single
right-click whose attachment is not observed for the full polling window may
be skipped for this trip only if all table reads stayed equal to the previous
expected set and fresh table/bag/table checks agree. Unexpected or unreadable
state, interruption or uncertain button release still holds. A skip is not
proof that the item is a quest item, ineligible or that a late attachment is
impossible; a delayed update can race later checks. The table is checked again
before subsequent actions and sending, but these checks are not atomic. The
skipped items are never clicked again during that trip, and can remain in the
bags. Each candidate scan group contains at most 12 clicks; if all fail, the
empty composition advances to another group without retyping the recipient.
This is **not** recipient field readback, a complete inventory, an
acknowledgement, or proof of server delivery. Attachment field interpretation
is based on limited traces; an unexpected mismatch holds rather than guessing. Candidate 4 remains Unknown/unverified, is never
silently treated as absent or selected, and the separate backpack is excluded.
A zero observed 0–3 count is not proof that all equipped bags are empty. Text
focus, UI state, mailable items, recipient reset between batches, merchant
sales and game/server outcomes are unverified. Wrong calibration or focus can
mail unintended items to a wrong recipient. Use a supervised disposable-item
test first.

A profile-free run latches the live position and character before starting the
town trip and meshes back there after mailing, without using the synthetic roam
placeholder. Cross-map return is not supported. The mailbox must belong to the
same sell plan the bot selects (the log names that plan); saving it for another
vendor is insufficient. Missing configuration or a mailbox-stage failure skips
mail and attempts a guarded mesh return to grind; combat, STOP, unreadable map or
identity, and unreachable routes still take priority. Failures before beginning
composition may be attempted on the next sell trip. Before clicking the tab a
durable `Settings/clickaim-mail-hold.txt` is created. It remains on partial
recipient input, uncertain attachments, failed/uncertain sends, crashes and an
empty unsent composition. On later trips it blocks automatic mail (not grind),
including across restarts, so an uncertain send is never blindly retried. Stop
the bot, inspect the mailbox and sent mail, resolve any uncertainty, clear
recipient text and attachments, and use **Clear mail safety hold after
inspection** before attempting mail again. The hold is removed automatically
only after a sent batch has a zero-like table and its observed IDs have vanished;
these observations are not proof of acceptance or delivery. The recipient
clearing/reset and UI state remain unverified. Confirmed rejection is not
independently observed, so three-confirmed-failure retry/continue behavior is
**not yet implemented**. Cross-map return remains unsupported; the experiment is
limited to 64 batches. No Lua is used.
The Windows build and live workflow have not been tested here. The historical
notes below describe the earlier disabled research stages and should not be
read as the current opt-in status.

Requested behavior: after the recorded sell plan, navigate to the recorded mailbox, open Send Mail, wait one second, type the user-editable recipient, attach at most 12 items per mail from equipped bags, send and verify those items left the bags. The user clarified that **only the separate 20-slot main backpack is excluded**; observed container candidates 0, 1, 2 and 3 must all be included. A future candidate 4 must not be silently excluded if established to be another equipped bag. After all eligible items are gone or three failed attempts, mesh back to the active profile hotspot. STOP, death, combat, player/map identity and no-Lua rules still apply. A saved sell plan can itself click any bag slot; the user must configure it not to sell things intended for mail.

## Historical research notes (before the opt-in workflow)

* At this earlier stage Selling settings had a persistent recipient field for future use; the current opt-in workflow is described above.
* `WowBMailBatchPolicy` is an isolated, unconnected policy: it includes candidate 0–4 equipped-bag observations and excludes the separate backpack, up to 12 per batch, holds on unknown state and models three *confirmed* failures. Candidate 4 is eligible only if its identity and equipped-bag meaning are established; an unknown identity is not evidence of an absent bag or complete inventory. It requires a verified complete inventory and an independently observed send acknowledgement with cleared attachments. Its synthetic C# fixture does not validate the live client.
* Stopped-only **Bag research snapshot** logs the full candidate bytes at every snapshot, including comparable observations, as well as the prior per-entry deltas. `Tools/analyze_mail_bag_snapshots.py <log>` compares candidate and player-array indexes; neither the raw bytes nor a delta independently proves item, empty-slot, mailbox or delivery semantics. Logs contain process addresses, GUIDs and potentially sensitive data.
* `TryReadMailCandidateBags` is a stopped-only 70938 research API. It reports candidates 0–4, preserves full 128-bit raw slot identities, and rechecks bags, pool records, table and player/session identity after scanning. A zero candidate identity is reported explicitly as Unknown; a nonzero but unresolved candidate, instability, duplicate identity or unreadability prevents a result. It does not read the backpack or validate mailbox state and cannot authorize input. The 70938 reads are not atomic.
* A stopped-only combined-grid geometry calibration is available in Selling; see `hunt/WOW_70938_MAIL_GRID_CALIBRATION.md`. It stores cursor markers and candidate identities, checks non-overlapping spans including the excluded backpack, and produces a research-only preview/file. It sends no clicks, does not prove the UI state or a correct user mapping, and is not connected to automatic mailing.
* Selling has a stopped-only **Mailbox hover research** probe. With the mailbox window closed and player/camera stationary, record an away hover on empty ground, then a hover on the mailbox. Both observations require foreground game focus, a stable cursor, identity, map and position, and two matching candidate reads; changed attachment/client/player/map/position invalidates the comparison. It logs raw full GUID candidates, derived entry bits, client dimensions and cursor coordinates. These observations send no input and do not prove the object is a mailbox or that its GUID is reliable. The camera and world contents are not verified. Logs contain potentially sensitive GUIDs.
* Optional **Test one right-click** is a separate, explicitly confirmed stopped-only experiment. It requires a freshly recorded empty away value and a distinct nonzero user-labelled mailbox hover in the same session. After another countdown, it checks that exact 128-bit hover twice, current foreground cursor, map/player/position/client, experimental health/combat/ghost candidates and a final read before posting one right-button down/up pair. A new STOP or closing the settings window cancels before the down; prior stopped state is not cleared. It consumes the observation even on failure, never automatically retries, and is not wired into gameplay. A posted click does not establish an opened mailbox; the user must visually report the result. The UI must be closed and camera/world still; these are not verified. It does not open Send Mail or type, attach or send anything.

## Mailbox hover observation

A user-labelled 70938 hover comparison showed empty away, then a nonzero hover candidate with entry-candidate 696264 over the mailbox, at 1920×1080. In the later `2026-11-15_2010_9852.log`, three comparisons had the same candidate with an empty away value; the first interaction failed a combined safety/hover/session check without a button-down, and the next two logged right-button messages posted. The user reported that the mailbox opened a few seconds after the test. This is encouraging user-observed behavior, not machine-verified frame state, general mailbox type/range, or reliability across sessions; the report does not identify which posted attempt caused the opening. Neither the GUID nor screen coordinate is hardcoded for gameplay.

## Supervised Send Mail tab test

With the bot stopped, the user can confirm the mailbox window is open and separately run **Test Send Mail tab**. After confirmation and a three-second countdown it posts at most one left-click at the user's live cursor, with foreground, cursor stability, STOP generation, identity/map/position, and experimental health/combat/ghost vetoes. It never moves the cursor or guesses a coordinate. The user must hover the actual tab, **not** the Send button; there is no programmatic mailbox or tab detection. The log reports the labelled coordinate and post result, not success, and the user must inspect the tab. It does not type, attach, send, or wire anything into Grind. Do not infer safety from the test or use its coordinate automatically. A user-supplied log showed left-button messages posted at a labelled point (125,551) on a 1920×1080 client; the user said done, but did not explicitly confirm the visible tab state. The coordinate is not hardcoded.

## Supervised recipient text test

The separate stopped-only test takes the current **Mail recipient** settings text (use Save separately to persist), and refuses anything except 1–64 UTF-16 letters and hyphens. The user must manually open Send Mail, ensure the recipient field is empty and focused, and focus the game again after starting the test. After a 3-second countdown plus an additional 1-second wait, the test posts one WM_CHAR per letter/hyphen, without clicks, select-all, Enter, paste, or retries. Foreground, cursor stability, STOP generation, session/player/map/position and experimental health/combat/ghost vetoes are checked before each character; focus in the actual recipient field and delivered text cannot be read back. A partial/failed attempt must be visually checked and manually cleared before a retry. Do not log the recipient name. This is not an automatic sending implementation, and a posted message does not prove its destination or correctness.

User later reported that the nine-character text appeared, and that the Send Mail tab automatically focuses the recipient field on their client, so no extra field click was necessary. This observation supports the requested tab → one-second wait → text sequence in that state; it does not prove automatic focus after every reopen, empty text, actual recipient correctness, or state of other client layouts.

## Supervised attachment test

After ensuring Send Mail and the calibrated combined bag are open, a stopped user can hover one disposable item in candidate 0–3 and explicitly start a one-right-click test. The saved research grid is revalidated against current client size, player and all five candidate identities, including unknowns, then checked for valid bounds, bag spans and overlap with the backpack. The live raw slot values and user-held cursor are checked repeatedly; an empty raw slot, backpack location, changed inventory, mismatched grid, candidate 4, STOP, focus loss or unsafe health/combat/ghost candidate withholds the click. It does not verify actual UI layout, item type, mailability or attachment, does not send or retry automatically, and the user must inspect the attachment. It sends no recipient or Send input. An unknown candidate 4 is not classified as absent or used as evidence the inventory is complete. The user reported success after the test logged one posted right-click on candidate 1, slot 8. This supports that one mapping and action, not all slots or future layouts.

A stopped, read-only **Preview next 12 candidates** button independently reads two matching snapshots and shows up to twelve nonzero raw candidate 0–3 slots with calculated coordinates from the saved grid. It never clicks or checks item eligibility and explicitly reports unknown candidate 4 and the unobserved backpack. This is a partial research preview, not proof that the inventory is complete or that any item can be mailed.

`WowBMailSequence` is an isolated, unconnected sequencing model for the requested tab → one-second delay → recipient → at-most-12 attachments → send → verified outcome loop. It requires independently supplied tab, exact recipient, exact attachment identities, complete inventory and send outcome observations; a posted message alone cannot satisfy them. Unknown send outcomes hold without retry, and only confirmed rejections with cleared attachments and restored items count toward three failures. STOP/unsafe observations interrupt the model. No live 70938 UI observer supplies these conditions yet, so the model does not enable mailing or change town routing.

## User-supplied grid calibration

The supplied research-only JSON reports a 1920×1080 client, 10 columns, a 20-cell backpack and four 12-cell candidates 0–3. Candidate 4 is Unknown. The recorded spans are contiguous in the assumed row-major layout, without overlap. This is a successful geometry validation, not independent proof of live UI layout or bag/item semantics. The file contains character/bag identifiers and should not be published.

## Why auto-mail is not connected

One user-supplied 70938 sequence now correlates four candidate indexes and individual visible slots (details below), and the backpack appears separately in the player array. It remains one character/layout and does not prove every bag, UI coordinate, empty slot, item type or all builds. The existing subtotal's zero tags and counts do not prove exact inventory contents or that an item is mailable. Both items stayed in the observed candidates while attached, and disappeared only in the post-send snapshot in this sequence; that is useful correlation, not a general server-acceptance or delivery guarantee. We do not yet have reliable 70938 mailbox object identity, open/send-tab state, input focus, recipient text readback, attachment state, server acceptance/rejection or layout-aware slot positions. Replaying blind clicks could mail the wrong items or send to the wrong person. Legacy mailbox code uses Lua/old addresses and is not suitable here.

The current town-trip workflow therefore still skips mailing and returns to the active profile after selling. No timer, posted click, candidate disappearance or saved nickname authorizes an automatic Send. Three failures can only be counted after a reliable failed send and attachment/inventory reset; unknown is not a confirmed failure and cannot safely authorize a duplicate send. Cross-map transport is not available. These safety limits take precedence over the requested no-stop behavior when the state is genuinely unknown.

## Observations supplied 2026-11-15

The user supplied eleven sequential stopped snapshots in `2026-11-15_1837_2848.log`, same player and four 12-entry candidate bags, plus their action labels. No screenshots, UI memory fields or failed send were recorded.

| Transition | User action | Observed change (zero-based entries) |
|---|---|---|
| 1→2 | Hearthstone backpack slot 5→20 | player array 35→50; full 16-byte value moved |
| 2→3 | Hearthstone backpack slot 20→19 | player array 50→49 |
| 3→4 | Large Fang candidate 0 slot 1→2 | candidate 0 entries 0→1 |
| 4→5 | Wicked Claw candidate 1 slot 1→3 | candidate 1 entries 0→2 |
| 5→6 | Soft Bushy Tail candidate 2 slot 3→1 | candidate 2 entries 2→0 |
| 6→7 | Heavy Stone candidate 3 slot 1→12 | candidate 3 entries 0→11 |
| 7→8→9→10 | Open Send Mail; attach Heavy Stone, enter recipient and attach Large Fang | no player-array or candidate changes in these snapshots |
| 10→11 | Manual Send | candidate 0 entry 1 and candidate 3 entry 11 become zero; other observed entries unchanged |

The four leading visual backpack slots were reported locked. The special player range `[0x23,0x33)` (16 entries) tracks the Hearthstone's changes; container candidate 0 is a separate equipped bag, not the backpack in this recording. The user subsequently clarified that candidate 0 should also be mailed, along with candidates 1–3. Candidate 4's identity was zero/unresolved throughout. An empty identity is not proof that no bag exists. The observed slots are promising, not proof of global layouts or delivery.

The parser previously discarded successfully read bags when a later `user category=...` line appeared; the regression test now covers actual log ordering. The player-array change parser reconstructs only complete, same-player, same-array snapshots and refuses inconsistent deltas.

## Offline 70938 mail-code search

`Tools/find_70938_mail_static.py <decoded-exe>` is a read-only, standard-library
scanner for the **exact recorded** 70938 executable SHA-256. It searches
section-backed ASCII mail API names, RIP-relative LEA-like byte patterns in
executable sections, and aligned name-pointer/next-executable-pointer pairs,
reporting candidate RVAs. It does not open the process, use Lua, or invoke
client functions. It rejects a different file instead of applying 70291
addresses to 70938. Pattern matches are leads, not established instruction
boundaries or handler semantics; missing matches do not establish absence.

The user-provided ZIP's MD5 matches the recorded 70938 archive; its decoded PE
SHA-256 matches the recorded image. Offline scan and Capstone inspection found
a contiguous name/function-pointer-shaped table at RVA `0x494DE50` onward:
`HasSendMailItem` points to `0x26B4F40`, `GetSendMailItem` to `0x26B4FD0`,
and `SendMail` to `0x26B53C0`. Separate named registration-like code at
`0x170E044`/`0x170E050` points to `0x16EDDB0` and the same `GetSendMailItem`
string; it must not be conflated with the table entry. These are static leads,
not callable APIs. No Lua or client code was executed.

Both `0x26B4F40` and `0x26B4FD0` take a converted argument, subtract one,
reject indexes >=16, multiply the index by 40, and reference an image-relative
base at `0x4E6CA90`. Both inspect the high six bits of the qword at entry +8;
the latter passes the entry address to a helper before reading additional item
fields. The instruction paths support a candidate array, not a proof of GUID
format, UI attachment identity, recipient, server acceptance or delivery. A
16-slot internal bound does not change the user's 12-item send limit.

Selling now has a **Mail attachment candidate snapshot** diagnostic, restricted
to a stopped 70938 session. It checks player/build/session and a readable exact
code marker, reads the 16×40 bytes twice uncached and logs only matching bytes;
any failed or changing read returns unknown. STOP and a new session veto the
snapshot. Reads are not atomic, logged identifiers may be sensitive, and stable
bytes do not prove completeness or attachment semantics. The diagnostic sends no
input and is not wired into automatic mail. The executable and the resulting
runtime candidate still require independent verification against controlled
visual changes, including an empty control and failed sends. The scanner was run
on the real image and synthetic scanner tests were run; neither is live proof.
No Windows build was run here.

### Stopped snapshot comparison

The user supplied `2026-11-15_2114_3612.log` after the requested empty → one
manually attached → removed experiment. The log itself has no action labels,
so the following is a sequence comparison, not independent visual confirmation.
At 21:15:04 all 16 records were 40 zero bytes. At 21:15:12 only record 1 was
nonzero; at 21:15:23 all 16 were zero again. The first 16 bytes of that record
were nonzero; the second 16 bytes matched the full raw identity of candidate
bag 1 in the earlier bag snapshot for the same character. The dword at +0x20
was 10. No matching first-16-byte value was found in that older log. This is
consistent with, but does not prove, a record containing item, bag and slot
fields. The earlier snapshot is from a different time, and the log does not
identify the chosen item or its current bag slot. No send or delivery is proven.

The stopped diagnostic now optionally compares its table against two matching
fresh candidate-bag snapshots, bracketing the table reads and rechecking the
table afterward. It reports raw +0x00/slot and +0x10/bag equality matches only;
unknown bags, missing matches, changed reads, and the excluded backpack remain
unknown. This can guide further manually labelled experiments but cannot prove
that a given entry is an attachment, that it is mailable or that the UI/send
state is safe. It remains disconnected from automation; all reads are
non-atomic. This extension has not been built or run on Windows here.

### Experimental active-read building blocks

At the user's request to proceed with experimental automation, the candidate-bag
reader now also has an explicit 70938 active-Grind-only entry point. It uses the
same bounded container checks, requires the original player, active session,
STOP and route guards, and leaves zero identities Unknown. The existing stopped
research entry point is unchanged. A separate active-only, double-read raw mail
candidate table reader requires a caller-supplied safety predicate, build,
player, code marker, and stable readable bytes. Neither reader sends input.

`WowBMailCandidateTable` is a pure correlation helper: it compares complete
40-byte records against at most 12 expected nonzero item/bag pairs, rejects
partial, unmatched and duplicate records, and accepts a zero-like record only
when all 40 bytes are zero. This is an experimental interpretation of a single
live trace, not verified attachment identity, UI state, recipient, or send
acknowledgement. Synthetic C# fixtures were added but have not been run here
(no .NET SDK). These building blocks are not connected to the town workflow.

**Still unfinished:** persistent calibration for the mailbox object and both
UI buttons, actual guarded input and mailbox interaction, integration after
selling, a live recipient/readback strategy, and outcome verification. The
existing recorded mailbox point is a standing location, not an object or a
screen coordinate, and no Send button coordinate is calibrated. No automatic
mail is enabled. Candidate 4 remains Unknown, so candidates 0–3 cannot prove
all equipped bags are exhausted. Unknown or interrupted sends must never be
blindly repeated; profile return still depends on same-map mesh availability.

## Further evidence and integration

Two user screenshots `img1backpackopen.png` and `itemsattached.png` are 1919×1079 and show the same **Combined Backpack** and Send Mail tab, one empty and one containing 12 attachments. The combined bag is one continuous grid and does not visually delineate the backpack from equipped bags; the screenshot alone does not map candidate indexes to GUI cells. The Send Mail tab, recipient field, attachment grid and Send button are visible, but screenshots do not prove their live state or focus, and screen positions may shift with resolution, UI scale, window layout, bag configuration or addons. The example recipient field is blank; the attached screenshot is not evidence of a successful send. Do not hardcode these coordinates for clicking.

We still need mailbox object/frame/Send tab, input and recipient verification, attachment and accepted/rejected send observations, plus reliable combined-bag GUI-to-slot mapping. If necessary, while stopped use a disposable item and a verified recipient to record a manually rejected send as well as a successful one; avoid valuable items. Preserve the existing sell plan carefully: it can sell an item intended for mail if the user recorded that click. Logs and screenshots can contain sensitive information.

Further implementation needs an evidence-backed 70938 UI/state reader or explicit calibration with verification, stable per-slot identity and screen mapping, safe text entry/readback, bounded batching/retry state, and synthetic and live tests before attaching or sending items automatically.

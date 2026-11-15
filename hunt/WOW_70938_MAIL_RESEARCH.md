# 70938 mailing research: not enabled

Requested behavior: after the recorded sell plan, navigate to the recorded mailbox, open Send Mail, wait one second, type the user-editable recipient, attach at most 12 items per mail from **candidate 1, 2 and 3 only**, send and verify those items left the bags. The main backpack must be left alone. After all eligible items are gone or three failed attempts, mesh back to the active profile hotspot. STOP, death, combat, player/map identity and no-Lua rules still apply. A saved sell plan can itself click any bag slot; the user must configure it not to sell things intended for mail.

## What is available now

* Selling settings has a persistent recipient field, explicitly labelled as future use. It never sends mail or changes the existing selling/return workflow.
* `WowBMailBatchPolicy` is an isolated, unconnected policy: it limits selection to candidates 1–3, up to 12 per batch, holds on unknown state and models three *confirmed* failures. It requires a verified complete inventory and an independently observed send acknowledgement with cleared attachments. Its synthetic C# fixture does not validate the live client.
* Stopped-only **Bag research snapshot** logs the full candidate bytes at every snapshot, including comparable observations, as well as the prior per-entry deltas. `Tools/analyze_mail_bag_snapshots.py <log>` compares candidate and player-array indexes; neither the raw bytes nor a delta independently proves item, empty-slot, mailbox or delivery semantics. Logs contain process addresses, GUIDs and potentially sensitive data.
* `TryReadMailCandidateBags` is a stopped-only, unconnected 70938 research API. It scans only candidates 1–3, preserves full 128-bit raw slot identities, and rechecks bags, pool records, table and player/session identity after scanning. A zero or unresolved desired candidate, instability, duplicate identity or unreadability is unknown. It does not read the backpack or validate mailbox state and cannot authorize input. The 70938 reads are not atomic.

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

The four leading visual backpack slots were reported locked. The special player range `[0x23,0x33)` (16 entries) tracks the Hearthstone's changes; container candidate 0 is a separate equipped bag, not the backpack in this recording. **The requested eligible candidates are 1–3, so candidate 0 remains excluded even though the test manually mailed its Large Fang.** Candidate 4's identity was zero/unresolved throughout. An empty identity is not proof that no bag exists. The observed slots are promising, not proof of global layouts or delivery.

The parser previously discarded successfully read bags when a later `user category=...` line appeared; the regression test now covers actual log ordering. The player-array change parser reconstructs only complete, same-player, same-array snapshots and refuses inconsistent deltas.

## Further evidence and integration

We still need mailbox object/frame/Send tab, input and recipient verification, attachment and accepted/rejected send observations, plus reliable UI-to-slot mapping. If necessary, while stopped use a disposable item and a verified recipient to record a manually rejected send as well as a successful one; avoid valuable items. Sanitized screenshots of Send Mail and all open bags at a fixed client resolution and UI scale would establish the visible layout for calibration, not prove UI state. Preserve the existing sell plan carefully: it can sell a candidate 1–3 item before mailing if the user recorded that click. Logs and screenshots can contain sensitive information.

Further implementation needs an evidence-backed 70938 UI/state reader or explicit calibration with verification, stable per-slot identity and screen mapping, safe text entry/readback, bounded batching/retry state, and synthetic and live tests before attaching or sending items automatically.

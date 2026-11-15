# 70938 mailing research: not enabled

Requested behavior: after the recorded sell plan, navigate to the recorded mailbox, open Send Mail, wait one second, type the user-editable recipient, attach at most 12 items per mail from **candidate 1, 2 and 3 only**, send and verify those items left the bags. The main backpack must be left alone. After all eligible items are gone or three failed attempts, mesh back to the active profile hotspot. STOP, death, combat, player/map identity and no-Lua rules still apply. A saved sell plan can itself click any bag slot; the user must configure it not to sell things intended for mail.

## What is available now

* Selling settings has a persistent recipient field, explicitly labelled as future use. It never sends mail or changes the existing selling/return workflow.
* `WowBMailBatchPolicy` is an isolated, unconnected policy: it limits selection to candidates 1–3, up to 12 per batch, holds on unknown state and models three *confirmed* failures. It requires a verified complete inventory and an independently observed send acknowledgement with cleared attachments. Its synthetic C# fixture does not validate the live client.
* Stopped-only **Bag research snapshot** logs the full candidate bytes at every snapshot, including comparable observations, as well as the prior per-entry deltas. `Tools/analyze_mail_bag_snapshots.py <log>` compares indexes only; neither the raw bytes nor a delta proves that an entry is an item, an empty slot, a bag, a successful send or the contents of a mailbox. Logs contain process addresses, GUIDs and potentially sensitive data.

## Why auto-mail is not connected

The candidate table indexes 0–4 are not verified user bag numbers; there is no proven mapping from candidate indexes and entries to visible bag coordinates. It is not established that the backpack is outside candidates 1–3. The existing subtotal's zero tags and counts do not prove exact inventory contents, that an item is mailable, or where it went. Attaching an item may already remove it from a bag candidate, before Send is accepted; disappearance alone does not confirm delivery. We also do not have reliable 70938 mailbox object identity, open/send-tab state, input focus, recipient text readback, attachment state, server acceptance/rejection or layout-aware slot positions. Replaying blind clicks could mail the wrong items or send to the wrong person. Legacy mailbox code uses Lua/old addresses and is not suitable here.

The current town-trip workflow therefore still skips mailing and returns to the active profile after selling. No timer, posted click, candidate disappearance or saved nickname authorizes an automatic Send. Three failures can only be counted after a reliable failed send and attachment/inventory reset; unknown is not a confirmed failure and cannot safely authorize a duplicate send. Cross-map transport is not available. These safety limits take precedence over the requested no-stop behavior when the state is genuinely unknown.

## A controlled live observation to enable future work

With the bot stopped and attached to 70938, use disposable items and a verified recipient. Record the game's interface layout (resolution, UI scale and screenshots) and keep it unchanged. Use **Selling → Bag research snapshot** after each step, waiting for it to finish before changing anything. Save the complete bot log; it may contain sensitive identifiers.

1. Place one cheap distinguishable item in each intended bag and one in the backpack. Snapshot the baseline; note each item's visible bag and slot.
2. Move only the backpack test item to another empty backpack slot and snapshot; move it back and snapshot. Repeat for a single item in each intended bag. Record exactly which visible slots changed. These observations help correlate candidates and entry order; they do not prove all bags or layouts.
3. Open the mailbox manually and snapshot. Click the Send Mail tab and snapshot. With the verified nickname typed manually, attach one disposable item by right-click and snapshot *before* sending. Send manually only after checking recipient/attachments, wait for the game to finish, then snapshot again. Record the visual success/failure, whether an attachment disappears before Send, and the timing. A deliberate failed-send trace should use a reversible, safe failure and must never contain valuable items.
4. Supply the log and sanitized screenshots plus the action sequence to compare the candidates and research mailbox-state observations. Do not send diagnostic logs publicly without reviewing them for sensitive values.

Further implementation needs an evidence-backed 70938 UI/state reader or explicit calibration with verification, stable per-slot identity and screen mapping, safe text entry/readback, bounded batching/retry state, and synthetic and live tests before attaching or sending items automatically.

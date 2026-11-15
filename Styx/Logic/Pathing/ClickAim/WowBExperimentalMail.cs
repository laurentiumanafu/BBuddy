using System;
using System.Collections.Generic;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Explicitly opted-in 70938 experiment. UI, focus, mailability and delivery are
// not independently verified. Unknown input/send outcomes hold without replay.
internal static class WowBExperimentalMail
{
    private static List<WowBMailGridLookup.Bag> Copy(List<WowBContainerResearchProbe.MailBagObservation> bags)
    {
        var result = new List<WowBMailGridLookup.Bag>();
        foreach (var b in bags) result.Add(new WowBMailGridLookup.Bag { Candidate = b.CandidateIndex,
            Identity = b.Bag, Unknown = b.Unknown, Slots = (WowBGuid[])b.Slots.Clone() });
        return result;
    }
    private static bool ReadBags(Memory memory, WowBReader reader, WowBGuid player, out List<WowBMailGridLookup.Bag>? bags)
    {
        bags = null;
        if (!WowBInventoryProbe.TryReadActiveMailCandidateBags(memory, reader, player, out var source) || source == null) return false;
        bags = Copy(source); return true;
    }
    private static bool Wait(int ms, Func<bool> safe)
    {
        int start = Environment.TickCount;
        while (unchecked(Environment.TickCount - start) < ms)
        {
            if (!safe() || ClickAimAbort.Wait(100)) return false;
        }
        return safe();
    }
    private static bool Move(IntPtr hwnd, int x, int y, int w, int h, Func<bool> safe)
        => safe() && ClickAimInput.PostMouseMoveFraction(hwnd, x / (double)(w - 1), y / (double)(h - 1)) && Wait(200, safe);
    private static bool Left(IntPtr hwnd, int x, int y, int w, int h, bool background, Func<bool> safe, out string reason)
    {
        bool down = ClickAimInput.ConfirmedLeftClick(hwnd, x, y, w, h, background, () => safe() ? null : "unsafe mail state", out reason);
        return down && reason == "left-button messages posted" && safe();
    }

    // True means no more unskipped 0..3 candidates remain for this trip, not
    // that every bag is empty, a skipped item is ineligible, or mail arrived.
    internal static bool Run(IntPtr hwnd, Memory memory, WowBReader reader, WowBGuid player,
        WowBMailUiCalibration.Result ui, string recipient, Func<string?> safetyFailure)
    {
        bool background = false, focusSelected = false;
        bool safe() => focusSelected && safetyFailure() == null &&
            (background ? GatherBackgroundFocus.Current(hwnd) == GatherBackgroundFocus.State.OtherApplication &&
                          GatherBackgroundFocus.TryClientOrigin(hwnd, out _, out _)
                        : ClickAimInput.GameForeground(hwnd)) &&
            ClickAimInput.ClientSize(hwnd, out int width, out int height) && width == ui.Width && height == ui.Height;
        try
        {
            if (WowBMailSafetyHold.IsHeld())
            { Logging.Write("[Mail] previous composition or send requires inspection; mail held while grinding continues. Clear the hold only after inspecting and clearing the mailbox."); return false; }
            if (!WowBMailUiCalibration.Valid(ui) || ui.Player != player.ToString())
            { Logging.Write("[Mail] preflight: controls invalid or for another character; no input sent."); return false; }
            if (!WowBMailRecipientText.ValidForTest(recipient))
            { Logging.Write("[Mail] preflight: recipient missing or invalid; no input sent."); return false; }
            if (!ClickAimInput.ClientSize(hwnd, out int w, out int h))
            { Logging.Write("[Mail] preflight: game client size unavailable; no input sent."); return false; }
            if (w != ui.Width || h != ui.Height)
            { Logging.Write("[Mail] preflight: client size " + w + "x" + h + " differs from controls " + ui.Width + "x" + ui.Height + "; no input sent."); return false; }
            if (!GatherBackgroundFocus.TryClientOrigin(hwnd, out _, out _))
            { Logging.Write("[Mail] preflight: game window minimized or geometry unavailable; no input sent."); return false; }
            if (ClickAimInput.GameForeground(hwnd)) background = false;
            else if (GatherBackgroundFocus.Current(hwnd) == GatherBackgroundFocus.State.OtherApplication) background = true;
            else { Logging.Write("[Mail] preflight: focus state unavailable or another game window focused; no input sent."); return false; }
            focusSelected = true;
            Logging.Write("[Mail] input mode: " + (background ? "background (experimental)" : "foreground") +
                "; switching between game and background interrupts mail. Recipient focus remains unverified.");
            // Only before any input, allow a short grace period for a held
            // physical button. No click or send is automatically retried.
            string? issue = safetyFailure();
            if (issue == "a physical mouse button is held")
            {
                Logging.Write("[Mail] preflight waiting briefly: " + issue + "; no input sent.");
                int start = Environment.TickCount;
                while (issue == "a physical mouse button is held" && unchecked(Environment.TickCount - start) < 3000)
                {
                    if (ClickAimAbort.Wait(100)) { issue = "STOP requested"; break; }
                    issue = safetyFailure();
                }
            }
            if (issue != null)
            { Logging.Write("[Mail] preflight withheld: " + issue + "; no mailbox, tab, recipient, attachment or send input sent."); return false; }
            if (!safe())
            { Logging.Write("[Mail] preflight state or client size changed before mailbox input; no input sent."); return false; }
            Logging.Write("[Mail] preflight passed; checking recorded mailbox hover. UI state is not verified.");
            if (!Move(hwnd, ui.MailboxX, ui.MailboxY, w, h, safe))
            { Logging.Write("[Mail] mailbox hover move withheld or interrupted; no mailbox click sent. Safety: " + (safetyFailure() ?? "move or size check failed")); return false; }
            if (!reader.TryReadMouseoverGuidCandidate(out WowBGuid hover) || hover.IsEmpty || hover.ToString() != ui.MailboxHover ||
                !reader.TryReadMouseoverGuidCandidate(out WowBGuid hover2) || hover2 != hover)
            { Logging.Write("[Mail] recorded mailbox hover did not match a stable live candidate; no mailbox click sent. Check camera, position and calibration."); return false; }
            if (!ClickAimInput.ConfirmedRightClick(hwnd, ui.MailboxX, ui.MailboxY, w, h,
                    () => safe() && reader.TryReadMouseoverGuidCandidate(out WowBGuid final) && final == hover, out string openReason))
            { Logging.Write("[Mail] mailbox click withheld: " + openReason + "; no mail send input sent."); return false; }
            Logging.Write("[Mail] mailbox interaction posted; frame is not verified: " + openReason);
            if (openReason != "right-button messages posted")
            { Logging.Write("[Mail] mailbox button release uncertain; no tab or send input. Inspect manually."); return false; }
            if (!Wait(1500, safe))
            { Logging.Write("[Mail] stopped or safety changed after mailbox interaction; no tab or send input. Inspect manually."); return false; }
            int batches = 0;
            var skipped = new HashSet<WowBGuid>(); // one trip only; never retry the same item in it
            while (batches < 64 && safe())
            {
                if (!ReadBags(memory, reader, player, out var bags) || bags == null)
                { Logging.Write("[Mail] active bag observations unavailable; no tab or send input in this batch."); return false; }
                if (!WowBMailGridLookup.TryLoad(WowBMailGridCalibration.DefaultPath, player.ToString(), w, h, bags, out var grid) || grid == null)
                { Logging.Write("[Mail] saved bag grid missing or incompatible with current player, window or bag identities; no tab or send input in this batch."); return false; }
                if (!WowBMailGridLookup.TryPreviewExcluding(grid, player.ToString(), w, h, bags, skipped, out var selected, out int count, out bool unknownPrimary, out bool unknownFifth))
                { Logging.Write("[Mail] bag preview invalid or inconsistent; no tab or send input in this batch."); return false; }
                if (unknownPrimary)
                { Logging.Write("[Mail] one or more bag candidates 0–3 are unknown; no tab or send input in this batch."); return false; }
                if (count == 0)
                {
                    Logging.Write("[Mail] no unskipped nonzero candidates 0–3 remain for this trip; skipped=" + skipped.Count +
                        "; candidate 4=" + (unknownFifth ? "Unknown" : "unverified") + ". Skipped items may remain; this is not proof of empty bags or mail delivery.");
                    return true;
                }
                var expected = new List<WowBMailCandidateTable.Expected>();
                if (!WowBMailCandidateReader.TryRead(memory, reader, player, safe, out byte[]? raw))
                { Logging.Write("[Mail] attachment candidate table unreadable or safety changed; no tab or send input in this batch."); return false; }
                if (!WowBMailCandidateTable.Matches(raw, expected))
                { Logging.Write("[Mail] attachment candidate is not empty-like; no tab or send input in this batch."); return false; }
                if (!WowBMailSafetyHold.TryBegin(out string holdReason))
                { Logging.Write("[Mail] cannot start composition: " + holdReason + "; mail is held, no tab or send input in this batch."); return false; }
                Logging.Write("[Mail] bag/grid and empty-like table prechecks passed; attempting recorded Send Mail tab (UI state unverified).");
                if (!Left(hwnd, ui.TabX, ui.TabY, w, h, background, safe, out string tabReason))
                { Logging.Write("[Mail] tab click withheld or uncertain: " + tabReason + "; safety: " + (safetyFailure() ?? "client geometry or button-release check") + ". No send input in this batch."); return false; }
                Logging.Write("[Mail] Send Mail tab click messages posted; tab opening and field focus are not verified.");
                if (!Wait(1000, safe))
                { Logging.Write("[Mail] safety changed during the one-second tab wait; no recipient or send input in this batch."); return false; }
                int generation = ClickAimAbort.Generation;
                foreach (char c in recipient)
                {
                    if (!(background ? ClickAimInput.BackgroundMailRecipientChar(hwnd, c, generation, safe)
                                     : ClickAimInput.DiagnosticRecipientChar(hwnd, c, generation, safe)))
                    { Logging.Write("[Mail] recipient entry interrupted or partial; inspect manually. No send attempted."); return false; }
                    if (ClickAimAbort.Wait(30)) return false;
                }
                Logging.Write("[Mail] recipient characters posted for batch " + (batches + 1) + "; field focus and text are not verified.");
                // Each scan group contains at most 12 clicks. If every item in
                // a group is skipped, scan the next group without retyping the
                // recipient into the still-open, empty composition.
                while (true)
                {
                    if (selected.Count == 0 || selected.Count > 12)
                    { Logging.Write("[Mail] invalid candidate group; no send input in this batch."); return false; }
                    foreach (var slot in selected)
                    {
                        if (!ReadBags(memory, reader, player, out var current) || current == null || !WowBMailGridLookup.Same(bags, current) ||
                            !WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw) || !WowBMailCandidateTable.Matches(raw, expected))
                        { Logging.Write("[Mail] bag or attachment observations changed before an attachment; no send input in this batch. Inspect manually."); return false; }
                        var bag = current[slot.Candidate];
                        if (bag.Candidate != slot.Candidate || bag.Unknown || slot.Slot < 0 || slot.Slot >= bag.Slots.Length || bag.Slots[slot.Slot] != slot.Item || skipped.Contains(slot.Item))
                        { Logging.Write("[Mail] selected bag slot changed; no send input in this batch. Inspect manually."); return false; }
                        int x = (int)Math.Round(slot.X), y = (int)Math.Round(slot.Y);
                        if (!Move(hwnd, x, y, w, h, safe) ||
                            !ClickAimInput.ConfirmedRightClick(hwnd, x, y, w, h, safe, out string clickReason))
                        { Logging.Write("[Mail] attachment input withheld or uncertain; inspect manually."); return false; }
                        if (clickReason != "right-button messages posted" || !safe())
                        { Logging.Write("[Mail] attachment button release or state uncertain; inspect manually."); return false; }
                        var withItem = new List<WowBMailCandidateTable.Expected>(expected) { new WowBMailCandidateTable.Expected(slot.Item, bag.Identity) };
                        var decision = new WowBMailAttachmentPolicy();
                        bool attached = false, complete = true;
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            if (!safe() || ClickAimAbort.Wait(200)) { complete = false; break; }
                            bool readable = WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw);
                            if (decision.Observe(readable, readable && WowBMailCandidateTable.Matches(raw, expected),
                                readable && WowBMailCandidateTable.Matches(raw, withItem)) == WowBMailAttachmentPolicy.Decision.Attached)
                            { attached = true; break; }
                        }
                        if (attached)
                        {
                            expected.Add(new WowBMailCandidateTable.Expected(slot.Item, bag.Identity));
                            continue;
                        }
                        // Stable unchanged table alone is not proof of failure.
                        // Bracket two unchanged bag observations with table reads;
                        // any unreadable, changed or unexpected state holds.
                        bool stable = complete && safe() &&
                            WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw) && WowBMailCandidateTable.Matches(raw, expected) &&
                            ReadBags(memory, reader, player, out var afterClick1) && afterClick1 != null && WowBMailGridLookup.Same(bags, afterClick1) &&
                            ReadBags(memory, reader, player, out var afterClick2) && afterClick2 != null && WowBMailGridLookup.Same(bags, afterClick2) &&
                            WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw) && WowBMailCandidateTable.Matches(raw, expected) && safe();
                        if (decision.Finish(complete, stable, stable) != WowBMailAttachmentPolicy.Decision.Skip)
                        { Logging.Write("[Mail] attachment outcome uncertain; no retry or send. Inspect manually."); return false; }
                        skipped.Add(slot.Item);
                        Logging.Write("[Mail] candidate " + slot.Candidate + " slot " + (slot.Slot + 1) +
                            " attachment not observed after stable checks; skipping it for this trip, not retrying. Ineligibility is unverified.");
                    }
                    if (expected.Count > 0) break;
                    if (!WowBMailGridLookup.TryPreviewExcluding(grid, player.ToString(), w, h, bags, skipped,
                        out selected, out count, out unknownPrimary, out unknownFifth) || unknownPrimary)
                    { Logging.Write("[Mail] next candidate group unavailable; no send input. Inspect manually."); return false; }
                    if (count == 0)
                    {
                        Logging.Write("[Mail] no further unskipped candidates; skipped=" + skipped.Count +
                            ". No send input for the empty composition. Recipient text may remain, so the mail safety hold stays set until manual inspection; skipped items may remain. Candidate 4 and delivery are unverified.");
                        return true;
                    }
                }
                if (expected.Count == 0 || expected.Count > 12 || !safe() ||
                    !WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw) || !WowBMailCandidateTable.Matches(raw, expected) ||
                    !ReadBags(memory, reader, player, out var beforeSend) || beforeSend == null || !WowBMailGridLookup.Same(bags, beforeSend))
                { Logging.Write("[Mail] final pre-send observations or safety changed; no send input in this batch. Inspect manually."); return false; }
                if (!Left(hwnd, ui.SendX, ui.SendY, w, h, background, safe, out string sendReason))
                { Logging.Write("[Mail] send input withheld or uncertain: " + sendReason + "; no retry. Inspect manually."); return false; }
                Logging.Write("[Mail] Send button messages posted once; recipient and server acceptance not verified. Never retry an uncertain send.");
                bool progressed = false;
                for (int attempt = 0; attempt < 60 && safe(); attempt++)
                {
                    if (ClickAimAbort.Wait(250)) break;
                    if (!WowBMailCandidateReader.TryRead(memory, reader, player, safe, out raw) ||
                        !WowBMailCandidateTable.Matches(raw, new List<WowBMailCandidateTable.Expected>()) ||
                        !ReadBags(memory, reader, player, out var after) || after == null ||
                        !WowBMailGridLookup.Validate(grid, player.ToString(), w, h, after)) continue;
                    bool found = false;
                    foreach (var b in after) foreach (var id in b.Slots)
                        foreach (var sent in expected) if (id == sent.Item) found = true;
                    if (!found) { progressed = true; break; }
                }
                if (!progressed) { Logging.Write("[Mail] send outcome uncertain; no retry. Inspect before resuming mail."); return false; }
                Logging.Write("[Mail] batch items no longer observed in candidates 0–4 and mail table is zero-like; server delivery and backpack contents remain unverified.");
                if (!safe()) { Logging.Write("[Mail] safety changed after observed progress; inspection hold retained."); return false; }
                if (!WowBMailSafetyHold.TryClear(out string clearReason))
                { Logging.Write("[Mail] could not clear hold after observed progress: " + clearReason + "; no further mail this trip."); return false; }
                batches++;
                Logging.Write("[Mail] next nonempty batch will post the recipient again; field contents are not readable.");
            }
            Logging.Write("[Mail] safety interruption or 64-batch limit; no automatic resend.");
            return false;
        }
        catch (Exception ex) { Logging.Write("[Mail] outcome uncertain; no automatic retry: " + ex.Message); return false; }
    }
}

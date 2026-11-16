using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Explicitly opted-in 70938 experiment. UI, focus, mailability and delivery are
// not independently verified. Each vendor trip allows at most one send attempt.
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

    // True means observed bag progress or no eligible observed candidates;
    // neither establishes that the recipient, UI or server delivery is correct.
    internal static bool Run(IntPtr hwnd, Memory memory, WowBReader reader, WowBGuid player,
        WowBMailUiCalibration.Result ui, string recipient, Func<string?> safetyFailure)
    {
        bool background = false, focusSelected = false;
        string lastSafetyFailure = "no safety check recorded";
        string? SafetyIssue()
        {
            if (!focusSelected) return "input focus mode not selected";
            string? issue = safetyFailure();
            if (issue != null) return issue;
            if (background)
            {
                if (GatherBackgroundFocus.Current(hwnd) != GatherBackgroundFocus.State.OtherApplication)
                    return "background focus changed or unavailable";
                if (!GatherBackgroundFocus.TryClientOrigin(hwnd, out _, out _))
                    return "background game window minimized or geometry unavailable";
            }
            else if (!ClickAimInput.GameForeground(hwnd)) return "game is no longer foreground";
            if (!ClickAimInput.ClientSize(hwnd, out int width, out int height)) return "client size unavailable";
            if (width != ui.Width || height != ui.Height) return "client size changed from calibration";
            return null;
        }
        bool safe()
        {
            string? issue = SafetyIssue();
            if (issue != null) { lastSafetyFailure = issue; return false; }
            return true;
        }
        // A click cannot change modes mid-input. During the following idle
        // delay, however, the user can safely focus or unfocus the game; choose
        // the correct guarded mode again before typing any characters.
        string? TabWaitCheck()
        {
            string? issue = safetyFailure();
            if (issue != null) return issue;
            if (!GatherBackgroundFocus.TryClientOrigin(hwnd, out _, out _)) return "game window minimized or geometry unavailable";
            bool nextBackground;
            if (ClickAimInput.GameForeground(hwnd)) nextBackground = false;
            else if (GatherBackgroundFocus.Current(hwnd) == GatherBackgroundFocus.State.OtherApplication) nextBackground = true;
            else return "focus state unavailable or another game window focused";
            if (background != nextBackground)
            {
                background = nextBackground;
                Logging.Write("[Mail] focus changed during tab wait; using " + (background ? "background" : "foreground") +
                    " mode after revalidation. Recipient focus remains unverified.");
            }
            return safe() ? null : lastSafetyFailure;
        }
        try
        {
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
                "; focus changes are rechecked during the tab wait and can interrupt other inputs. Recipient focus remains unverified.");
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
            Logging.Write("[Mail] using recorded mailbox pixel (" + ui.MailboxX + "," + ui.MailboxY +
                "); no hover identity check. Camera, object and UI state are unverified.");
            if (!Move(hwnd, ui.MailboxX, ui.MailboxY, w, h, safe) ||
                !ClickAimInput.ConfirmedRightClick(hwnd, ui.MailboxX, ui.MailboxY, w, h, safe, out string openReason))
            { Logging.Write("[Mail] recorded mailbox click withheld or interrupted; no mail send input sent."); return false; }
            Logging.Write("[Mail] mailbox interaction posted; frame is not verified: " + openReason);
            if (openReason != "right-button messages posted")
            { Logging.Write("[Mail] mailbox button release uncertain; no tab or send input. Inspect manually."); return false; }
            if (!Wait(1500, safe))
            { Logging.Write("[Mail] stopped or safety changed after mailbox interaction; no tab or send input. Inspect manually."); return false; }
            Logging.Write("[Mail] attempting recorded Send Mail tab at client pixel (" + ui.TabX + "," + ui.TabY +
                ") in " + w + "x" + h + " " + (background ? "background" : "foreground") +
                " mode. Tab and mailbox UI state are unverified.");
            if (!Left(hwnd, ui.TabX, ui.TabY, w, h, background, safe, out string tabReason))
            { Logging.Write("[Mail] tab click withheld or uncertain: " + tabReason + "; no send input on this trip."); return false; }
            Logging.Write("[Mail] Send Mail tab click messages posted; tab opening and field focus are not verified.");
            if (!WowBMailTabWait.Wait(TabWaitCheck, ClickAimAbort.Wait, () => Environment.TickCount64, out string waitReason))
            { Logging.Write("[Mail] tab wait interrupted: " + waitReason + "; no recipient or send input on this trip."); return false; }
            int generation = ClickAimAbort.Generation;
            foreach (char c in recipient)
            {
                if (!(background ? ClickAimInput.BackgroundMailRecipientChar(hwnd, c, generation, safe)
                                 : ClickAimInput.DiagnosticRecipientChar(hwnd, c, generation, safe)))
                { Logging.Write("[Mail] recipient entry interrupted or partial; no send attempted."); return false; }
                if (ClickAimAbort.Wait(30)) return false;
            }
            Logging.Write("[Mail] recipient characters posted once; field focus and text are not verified.");
            var skipped = new HashSet<WowBGuid>();
            var attempted = new HashSet<WowBGuid>(); // each item gets at most one click this trip
            {
                if (!ReadBags(memory, reader, player, out var bags) || bags == null)
                { Logging.Write("[Mail] active bag observations unavailable; no send input this trip."); return false; }
                if (!WowBMailGridLookup.TryLoad(WowBMailGridCalibration.DefaultPath, player.ToString(), w, h, bags, out var grid) || grid == null)
                { Logging.Write("[Mail] saved bag grid missing or incompatible; no send input this trip."); return false; }
                if (!WowBMailGridLookup.TryPreviewExcluding(grid, player.ToString(), w, h, bags, skipped, out var selected, out int count, out bool unknownPrimary, out bool unknownFifth))
                { Logging.Write("[Mail] bag preview invalid or inconsistent; no send input this trip."); return false; }
                if (unknownPrimary)
                { Logging.Write("[Mail] one or more bag candidates 0–3 are unknown; no send input this trip."); return false; }
                if (count == 0)
                {
                    Logging.Write("[Mail] no nonzero candidates 0–3 observed; candidate 4=" + (unknownFifth ? "Unknown" : "unverified") +
                        ". No send input this trip; this does not prove bags are empty.");
                    return true;
                }
                // Candidate IDs represent item clicks, not proof that the UI
                // attached them. Do not depend on the experimental mail table.
                var expected = new List<WowBMailCandidateTable.Expected>();
                while (selected.Count > 0 && expected.Count < 12)
                {
                    foreach (var slot in selected)
                    {
                        if (expected.Count >= 12) break;
                        if (!ReadBags(memory, reader, player, out var current) || current == null || !WowBMailGridLookup.Same(bags, current))
                        { Logging.Write("[Mail] bag observations changed before an item click; no send input this trip."); return false; }
                        var bag = current[slot.Candidate];
                        if (bag.Candidate != slot.Candidate || bag.Unknown || slot.Slot < 0 || slot.Slot >= bag.Slots.Length ||
                            bag.Slots[slot.Slot] != slot.Item || attempted.Contains(slot.Item))
                        { Logging.Write("[Mail] selected bag slot changed; no send input this trip."); return false; }
                        attempted.Add(slot.Item);
                        int x = (int)Math.Round(slot.X), y = (int)Math.Round(slot.Y);
                        if (!Move(hwnd, x, y, w, h, safe))
                        {
                            if (!safe()) return false;
                            skipped.Add(slot.Item);
                            Logging.Write("[Mail] item move not posted; skipping this item for the trip.");
                            continue;
                        }
                        if (!ClickAimInput.ConfirmedRightClick(hwnd, x, y, w, h, safe, out string clickReason))
                        {
                            if (!safe()) return false;
                            skipped.Add(slot.Item);
                            Logging.Write("[Mail] item click not posted: " + clickReason + "; skipping this item for the trip.");
                            continue;
                        }
                        if (clickReason != "right-button messages posted" || !safe())
                        { Logging.Write("[Mail] item button release or state uncertain; no send input this trip."); return false; }
                        expected.Add(new WowBMailCandidateTable.Expected(slot.Item, bag.Identity));
                    }
                    if (expected.Count >= 12) break;
                    if (!WowBMailGridLookup.TryPreviewExcluding(grid, player.ToString(), w, h, bags, attempted,
                        out selected, out count, out unknownPrimary, out unknownFifth) || unknownPrimary)
                    { Logging.Write("[Mail] next candidate group unavailable; no send input this trip."); return false; }
                }
                if (expected.Count == 0)
                { Logging.Write("[Mail] no item click was posted; skipped=" + skipped.Count + "; no send input this trip."); return false; }
                if (!safe() || !ReadBags(memory, reader, player, out var beforeSend) || beforeSend == null ||
                    !WowBMailGridLookup.Same(bags, beforeSend))
                { Logging.Write("[Mail] final bag observations or safety changed; no send input this trip."); return false; }
                if (!Left(hwnd, ui.SendX, ui.SendY, w, h, background, safe, out string sendReason))
                { Logging.Write("[Mail] send input withheld or uncertain: " + sendReason + "; no retry. Inspect manually."); return false; }
                Logging.Write("[Mail] Send button messages posted once; recipient and server acceptance are unverified. No second send this trip.");
                if (!Wait(1000, safe))
                { Logging.Write("[Mail] safety changed during the one-second post-send wait; outcome unknown. No retry this trip."); return false; }
                bool observed = false;
                for (int attempt = 0; attempt < 60 && safe(); attempt++)
                {
                    if (ClickAimAbort.Wait(250)) break;
                    if (!ReadBags(memory, reader, player, out var after) || after == null ||
                        !WowBMailGridLookup.Validate(grid, player.ToString(), w, h, after) ||
                        !ReadBags(memory, reader, player, out var afterAgain) || afterAgain == null ||
                        !WowBMailGridLookup.Same(after, afterAgain) || !safe()) continue;
                    int missing = WowBMailSendObservation.CountMissing(expected, after);
                    if (missing < 0) continue;
                    observed = true;
                    if (missing > 0)
                    {
                        Logging.Write("[Mail] " + missing + " of " + expected.Count +
                            " clicked item candidates no longer observed in candidates 0–4 after Send; backpack contents, recipient and delivery remain unverified. One send per trip.");
                        return true;
                    }
                }
                Logging.Write(observed
                    ? "[Mail] no clicked item candidates disappeared from observed bags after Send; treating this trip as failed. No retry this trip."
                    : "[Mail] bag state after Send was unreadable or unstable; outcome unknown and treated as a failed trip. No retry this trip.");
                return false;
            }
        }
        catch (Exception ex) { Logging.Write("[Mail] outcome uncertain; no automatic retry: " + ex.Message); return false; }
    }
}

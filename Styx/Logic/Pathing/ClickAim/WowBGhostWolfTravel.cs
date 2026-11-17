using System;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// 70938-only travel control for the Ghost Wolf candidate in the user's two
/// traces. Aura presence/absence and cast success remain experimental.
/// Unknown state never authorizes a potentially toggling form key.
/// </summary>
internal sealed class WowBGhostWolfTravel
{
    private const int CheckIntervalMs = 3000, RetryMs = 15000, HoldMs = 4000;
    // A key that did not produce an observed form is retried later than one that
    // did: the form can toggle off, so a reader that keeps saying "absent" would
    // otherwise be pressed against every fifteen seconds for the whole trip.
    private const int MissedFormMs = 60000;
    private const long UnavailableReportMs = 60000;
    private readonly bool _enabled;
    private readonly int _slot;
    private long _nextCheck, _nextAttempt, _nextUnavailableReport;
    private string _lastReport = "", _lastUnavailable = "";

    internal WowBGhostWolfTravel(bool enabled, int slot) { _enabled = enabled; _slot = slot; }
    internal static bool IsGhostWolfSpell(int id) => id == 2645 || id == 1238640;

    private ClickAimAction? FindSlot(WowBReader reader)
    {
        foreach (ClickAimAction action in ClickAimCombat.ReadBar(reader))
            if (GatherMovementSkill.IsSupported(action, _slot, true) && IsGhostWolfSpell(action.Id)) return action;
        return null;
    }

    internal bool Handles(WowBReader? reader)
    {
        try
        {
            if (!_enabled || reader?.IsExperimental70938 != true) return false;
            if (FindSlot(reader) != null) return true;
            Report("configured first-bar slot is not a readable Ghost Wolf spell; no form key sent");
            return false;
        }
        catch { Report("configured slot unreadable; no form key sent"); return false; }
    }

    private void Report(string state)
    {
        if (_lastReport == state) return;
        _lastReport = state;
        Logging.Write("[Travel] 70938 Ghost Wolf aura candidate: " + state + " (experimental, not proof of form or cast success)");
    }

    /* "unavailable" used to be the only thing the log said, and it covered about
     * twenty different refusals - a combat check, a ghost flag, a stale bar, an
     * unreadable aura candidate - none of which could be told apart. Say which
     * one it was, at most once a minute for the same reason. */
    private void ReportUnavailable(string reason)
    {
        long now = Environment.TickCount64;
        if (_lastUnavailable == reason && now < _nextUnavailableReport) return;
        _lastUnavailable = reason;
        _nextUnavailableReport = now + UnavailableReportMs;
        Logging.Write("[Travel] 70938 Ghost Wolf aura candidate unavailable: " + reason +
            "; no form key sent (experimental, not proof of form or cast success)");
    }

    // A fight, a ghost, a STOP or an active gather cast are the ordinary reasons
    // travel is not a good moment; they are not worth a line every minute.
    private static bool ExpectedWhileTravelling(string reason)
        => reason == "combat active" || reason == "ghost state active" || reason == "STOP requested" ||
           reason == "travel guard refused" || reason == "no active Grind session" ||
           reason == "cast cluster not idle (Mining)" || reason == "cast cluster not idle (Skinning)";

    private bool ReadState(IntPtr hwnd, Func<bool> safe, out Memory? memory, out WowBReader? reader,
                           out WowBObservedUnit player, out uint map, out bool active, out uint mana, out string reason)
    {
        memory = ObjectManager.Wow; reader = ObjectManager.WowB;
        player = default; map = mana = 0; active = false; reason = "not evaluated";
        try
        {
            if (!_enabled) { reason = "travel form disabled"; return false; }
            if (memory == null || reader == null || !reader.IsExperimental70938) { reason = "reader unavailable"; return false; }
            if (!reader.ExperimentalGrindActive) { reason = "no active Grind session"; return false; }
            if (hwnd == IntPtr.Zero || memory.WindowHandle != hwnd) { reason = "game window handle changed"; return false; }
            if (ClickAimAbort.Requested || RouteRunner.StopRequested) { reason = "STOP requested"; return false; }
            if (!safe()) { reason = "travel guard refused"; return false; }
            if (!WowBGrindFactions.Experimental70707BarSafe(reader)) { reason = "action bar guard unavailable"; return false; }
            if (FindSlot(reader) == null) { reason = "no readable Ghost Wolf slot"; return false; }
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out map)) { reason = "map unreadable"; return false; }
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null ||
                !snap.TryGetLocalPlayer(out player) || player.Guid.IsEmpty) { reason = "player unreadable"; return false; }
            if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) { reason = "player identity unstable"; return false; }
            if (!reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.Health == 0 || fields.MaxHealth == 0 || fields.Health > fields.MaxHealth)
            { reason = "player health unreadable"; return false; }
            if (fields.PlayerPowerTypeCandidate != 0) { reason = "power type is not mana"; return false; }
            if (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost))
            { reason = "ghost state unreadable"; return false; }
            if (ghost) { reason = "ghost state active"; return false; }
            if (!GrindRunner.TryReadGatherCombat(out bool fighting)) { reason = "combat state unreadable"; return false; }
            if (fighting) { reason = "combat active"; return false; }
            WowBGatherCastResearch.Observation cast = WowBGatherCastResearch.Observe(memory, reader, player);
            if (cast != WowBGatherCastResearch.Observation.Idle) { reason = "cast cluster not idle (" + cast + ")"; return false; }
            WowBTotemAuraCandidate? first = WowBTotemAuraCandidate.CaptureForGhostWolf(memory, reader, player, out string captureReason);
            WowBTotemAuraCandidate? second = WowBTotemAuraCandidate.CaptureForGhostWolf(memory, reader, player, out captureReason);
            // A form that can toggle off is never judged from one unreadable or
            // single sample: both reads must agree, and the reason is reported.
            if (first == null || second == null) { reason = "aura candidate unreadable: " + captureReason; return false; }
            if (first.GhostWolf70938Active() != second.GhostWolf70938Active()) { reason = "aura candidate unstable"; return false; }
            if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) { reason = "player identity unstable"; return false; }
            if (!reader.TryReadMapId(out uint mapAfter) || mapAfter != map) { reason = "map changed during read"; return false; }
            if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) { reason = fighting ? "combat active" : "combat state unreadable"; return false; }
            if (WowBGatherCastResearch.Observe(memory, reader, player) != WowBGatherCastResearch.Observation.Idle)
            { reason = "cast cluster not idle"; return false; }
            if (!safe()) { reason = "travel guard refused"; return false; }
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader || memory.WindowHandle != hwnd)
            { reason = "attachment or window changed during read"; return false; }
            if (!WowBGrindFactions.Experimental70707BarSafe(reader)) { reason = "action bar guard unavailable"; return false; }
            mana = fields.Power; active = second.GhostWolf70938Active(); reason = "";
            return true;
        }
        catch (Exception ex) { reason = "read failed: " + ex.Message; return false; }
    }

    // Called only while actually travelling. No key is sent here. If a stable
    // absent candidate and sufficient mana persist, yield to the outer loop.
    internal bool ShouldPauseTravel(IntPtr hwnd, Func<bool> safe)
    {
        long now = Environment.TickCount64;
        if (!_enabled || now < _nextCheck || now < _nextAttempt) return false;
        _nextCheck = now + CheckIntervalMs;
        if (!ReadState(hwnd, safe, out _, out _, out _, out _, out bool active, out uint mana, out string reason))
        {
            if (!ExpectedWhileTravelling(reason)) ReportUnavailable(reason);
            return false;
        }
        if (active) { Report("present"); return false; }
        return mana >= 100;
    }

    // Returns true only after posting an attempt; caller rechecks all priorities.
    internal bool TryEnsure(IntPtr hwnd, ClickAimMover mover, Func<bool> safe)
    {
        if (!_enabled || Environment.TickCount64 < _nextAttempt) return false;
        if (!ReadState(hwnd, safe, out Memory? memory, out WowBReader? reader,
                       out WowBObservedUnit player, out uint map, out bool active, out uint mana, out string reason))
        { ReportUnavailable(reason); return false; }
        if (active) { Report("present"); return false; }
        if (mana < 100) { Report("absent; below estimated 100 mana"); return false; }
        if (memory == null || reader == null) return false;
        ClickAimAction? action = FindSlot(reader);
        if (action == null) return false;
        ClickAimKeySteer.ReleaseHeld(); ClickAimKeySteer.ReleaseArrows(hwnd);
        try { mover.MoveStop(); } catch { return false; }
        if (ClickAimAbort.Wait(200)) return false;
        if (!ReadState(hwnd, safe, out Memory? checkedMemory, out WowBReader? checkedReader,
                       out WowBObservedUnit checkedPlayer, out uint checkedMap, out bool activeAfter, out uint manaAfter, out _) ||
            checkedMemory != memory || checkedReader != reader || checkedMap != map ||
            checkedPlayer.Guid != player.Guid || checkedPlayer.UnitAddress != player.UnitAddress ||
            checkedPlayer.RecordAddress != player.RecordAddress || activeAfter || manaAfter < 100) return false;
        ClickAimAction? again = FindSlot(reader);
        if (again == null || again.Id != action.Id || again.Raw != action.Raw || again.KeyVk != action.KeyVk ||
            !safe() || ClickAimAbort.Requested || RouteRunner.StopRequested || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        long attemptedAt = Environment.TickCount64;
        _nextAttempt = attemptedAt + RetryMs;
        _nextCheck = _nextAttempt;
        ClickAimInput.TapKey(hwnd, action.KeyVk, 40);
        Logging.Write("[Travel] attempted Ghost Wolf in slot " + action.Slot + "; cast and form are not yet confirmed");
        for (int waited = 0; waited < HoldMs; waited += 250)
        {
            if (ClickAimAbort.Wait(250) || !safe() || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !reader.ExperimentalGrindActive || !WowBGrindFactions.Experimental70707BarSafe(reader) ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting) return true;
        }
        if (ReadState(hwnd, safe, out _, out _, out WowBObservedUnit after, out uint afterMap, out bool nowActive, out _, out _) &&
            after.Guid == player.Guid && after.UnitAddress == player.UnitAddress && after.RecordAddress == player.RecordAddress && afterMap == map)
        {
            Report(nowActive ? "present after key attempt" : "not observed after key attempt; next try in 60 seconds");
            // A key that did not produce an observed form is the only case that can
            // toggle an active form off, so it waits longer before trying again.
            if (!nowActive) { _nextAttempt = attemptedAt + MissedFormMs; _nextCheck = _nextAttempt; }
        }
        else Report("unavailable after key attempt; no immediate retry");
        return true;
    }
}

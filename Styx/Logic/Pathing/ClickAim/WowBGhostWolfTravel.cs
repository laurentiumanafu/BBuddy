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
    private readonly bool _enabled;
    private readonly int _slot;
    private long _nextCheck, _nextAttempt;
    private string _lastReport = "";

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

    private bool ReadState(IntPtr hwnd, Func<bool> safe, out Memory? memory, out WowBReader? reader,
                           out WowBObservedUnit player, out uint map, out bool active, out uint mana)
    {
        memory = ObjectManager.Wow; reader = ObjectManager.WowB;
        player = default; map = mana = 0; active = false;
        try
        {
            if (!_enabled || memory == null || reader == null || !reader.IsExperimental70938 ||
                !reader.ExperimentalGrindActive || hwnd == IntPtr.Zero || memory.WindowHandle != hwnd ||
                ClickAimAbort.Requested || RouteRunner.StopRequested || !safe() ||
                !WowBGrindFactions.Experimental70707BarSafe(reader) || FindSlot(reader) == null) return false;
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out map) || !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) ||
                snap == null || !snap.TryGetLocalPlayer(out player) || player.Guid.IsEmpty ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.Health == 0 || fields.MaxHealth == 0 || fields.Health > fields.MaxHealth ||
                fields.PlayerPowerTypeCandidate != 0 ||
                !WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost) || ghost ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting ||
                WowBGatherCastResearch.Observe(memory, reader, player) != WowBGatherCastResearch.Observation.Idle) return false;
            WowBTotemAuraCandidate? first = WowBTotemAuraCandidate.Capture(memory, reader, player);
            WowBTotemAuraCandidate? second = WowBTotemAuraCandidate.Capture(memory, reader, player);
            if (first == null || second == null || first.GhostWolf70938Active() != second.GhostWolf70938Active() ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map ||
                !GrindRunner.TryReadGatherCombat(out fighting) || fighting ||
                WowBGatherCastResearch.Observe(memory, reader, player) != WowBGatherCastResearch.Observation.Idle || !safe() ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader || memory.WindowHandle != hwnd ||
                !WowBGrindFactions.Experimental70707BarSafe(reader)) return false;
            mana = fields.Power; active = second.GhostWolf70938Active();
            return true;
        }
        catch { return false; }
    }

    // Called only while actually travelling. No key is sent here. If a stable
    // absent candidate and sufficient mana persist, yield to the outer loop.
    internal bool ShouldPauseTravel(IntPtr hwnd, Func<bool> safe)
    {
        long now = Environment.TickCount64;
        if (!_enabled || now < _nextCheck || now < _nextAttempt) return false;
        _nextCheck = now + CheckIntervalMs;
        if (!ReadState(hwnd, safe, out _, out _, out _, out _, out bool active, out uint mana)) return false;
        if (active) { Report("present"); return false; }
        return mana >= 100;
    }

    // Returns true only after posting an attempt; caller rechecks all priorities.
    internal bool TryEnsure(IntPtr hwnd, ClickAimMover mover, Func<bool> safe)
    {
        if (!_enabled || Environment.TickCount64 < _nextAttempt) return false;
        if (!ReadState(hwnd, safe, out Memory? memory, out WowBReader? reader,
                       out WowBObservedUnit player, out uint map, out bool active, out uint mana))
        { Report("unavailable; no form key sent"); return false; }
        if (active) { Report("present"); return false; }
        if (mana < 100) { Report("absent; below estimated 100 mana"); return false; }
        if (memory == null || reader == null) return false;
        ClickAimAction? action = FindSlot(reader);
        if (action == null) return false;
        ClickAimKeySteer.ReleaseHeld(); ClickAimKeySteer.ReleaseArrows(hwnd);
        try { mover.MoveStop(); } catch { return false; }
        if (ClickAimAbort.Wait(200)) return false;
        if (!ReadState(hwnd, safe, out Memory? checkedMemory, out WowBReader? checkedReader,
                       out WowBObservedUnit checkedPlayer, out uint checkedMap, out bool activeAfter, out uint manaAfter) ||
            checkedMemory != memory || checkedReader != reader || checkedMap != map ||
            checkedPlayer.Guid != player.Guid || checkedPlayer.UnitAddress != player.UnitAddress ||
            checkedPlayer.RecordAddress != player.RecordAddress || activeAfter || manaAfter < 100) return false;
        ClickAimAction? again = FindSlot(reader);
        if (again == null || again.Id != action.Id || again.Raw != action.Raw || again.KeyVk != action.KeyVk ||
            !safe() || ClickAimAbort.Requested || RouteRunner.StopRequested || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        _nextAttempt = Environment.TickCount64 + RetryMs;
        _nextCheck = _nextAttempt;
        ClickAimInput.TapKey(hwnd, action.KeyVk, 40);
        Logging.Write("[Travel] attempted Ghost Wolf in slot " + action.Slot + "; cast and form are not yet confirmed");
        for (int waited = 0; waited < HoldMs; waited += 250)
        {
            if (ClickAimAbort.Wait(250) || !safe() || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !reader.ExperimentalGrindActive || !WowBGrindFactions.Experimental70707BarSafe(reader) ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting) return true;
        }
        if (ReadState(hwnd, safe, out _, out _, out WowBObservedUnit after, out uint afterMap, out bool nowActive, out _) &&
            after.Guid == player.Guid && after.UnitAddress == player.UnitAddress && after.RecordAddress == player.RecordAddress && afterMap == map)
            Report(nowActive ? "present after key attempt" : "not observed after key attempt; retry limited to 15 seconds");
        else Report("unavailable after key attempt; no immediate retry");
        return true;
    }
}

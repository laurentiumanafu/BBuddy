using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Hotbar attempts only: this client cannot yet read aura presence, duration,
// cast success, or authoritative spell cost. Never runs during combat/gathering.
internal sealed class ClickAimBuffController
{
    private const long ScanIntervalMs = 5000;
    private readonly ClickAimBuffSchedule _schedule = new ClickAimBuffSchedule();
    private readonly Func<long> _clock;
    private long _nextScan;

    internal ClickAimBuffController() : this(() => Environment.TickCount64) { }
    internal ClickAimBuffController(Func<long> clock) { _clock = clock; }

    // New run or confirmed resurrection: the next quiet opportunity starts fresh.
    internal void Reset() { _schedule.Reset(); _nextScan = 0; }

    private static bool Allowed(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped)
        => !stopped() && !ClickAimAbort.Requested && !RouteRunner.StopRequested && ClickAimSettings.UseBuffs &&
           ObjectManager.Wow == memory && ObjectManager.WowB == reader && memory.WindowHandle == hwnd &&
           (!reader.IsExperimental70707 || WowBGrindFactions.Experimental70707BarSafe(reader));

    private static bool TryQuietPlayer(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped,
                                       out WowBObservedUnit player, out uint map)
    {
        player = default; map = 0;
        try
        {
            if (!Allowed(memory, reader, hwnd, stopped)) return false;
            using var uncached = memory.TemporaryCacheState(false);
            // 70707 only permits verified mana-type players here. Defer at zero
            // mana rather than start a 30-minute timer on likely failed casts;
            // 70291's active power type and exact spell costs are not established.
            if (!reader.TryReadMapId(out map) || !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) ||
                snapshot == null || !snapshot.TryGetLocalPlayer(out player) || player.Guid.IsEmpty ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.MaxHealth == 0 || fields.Health == 0 || fields.Health > fields.MaxHealth ||
                (reader.IsExperimental70707 && fields.Power == 0) ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting ||
                (reader.IsExperimental70707 && (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost) || ghost)) ||
                !reader.TryReadMapId(out uint mapAfter) || map != mapAfter) return false;
            return Allowed(memory, reader, hwnd, stopped);
        }
        catch { return false; }
    }

    private static bool SameQuietPlayer(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped,
                                        WowBObservedUnit expected, uint expectedMap)
        => TryQuietPlayer(memory, reader, hwnd, stopped, out WowBObservedUnit current, out uint map) &&
           map == expectedMap && current.Guid == expected.Guid && current.UnitAddress == expected.UnitAddress &&
           current.RecordAddress == expected.RecordAddress;

    private static bool Eligible(ClickAimAction action)
    {
        if (action.Id <= 0 || action.KeyVk == 0 || !action.IsBuff) return false;
        string name = ClickAimCombat.NameOf(action.Id);
        // Forms can toggle off when pressed again. Do not schedule them by
        // default, even when class auto-detection falls back to Generic.
        // An explicit Buff role remains the user's own choice.
        if (ClickAimCombat.RoleOverrideOf(action.Id) == ClickAimSpellRole.Unknown &&
            (name == "Cat Form" || name == "Bear Form")) return false;
        // These are passive racials, not actions the character can cast.
        if ((action.Id == 20550 || action.Id == 20583) && ClickAimCombat.RoleOverrideOf(action.Id) == ClickAimSpellRole.Unknown) return false;
        return true;
    }

    private static bool SameSlot(ClickAimAction action, List<ClickAimAction> bar)
    {
        if (!Eligible(action)) return false;
        foreach (ClickAimAction now in bar)
            if (now.Slot == action.Slot && now.Id == action.Id && now.Raw == action.Raw && now.KeyVk == action.KeyVk &&
                Eligible(now)) return true;
        return false;
    }

    // Returns true only if at least one buff key was attempted. Selecting self
    // and restoring/retargeting are never used for attack; the ordinary target
    // acquisition loop will pick the next mob afterwards.
    internal bool TryApply(IntPtr hwnd, Func<bool> stopped, Action stopMovement, Action? onAttempt = null)
    {
        long now = _clock();
        if (stopped() || ClickAimAbort.Requested || RouteRunner.StopRequested || !ClickAimSettings.UseBuffs || now < _nextScan) return false;
        _nextScan = now + ScanIntervalMs;
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || hwnd == IntPtr.Zero ||
            !TryQuietPlayer(memory, reader, hwnd, stopped, out WowBObservedUnit player, out uint map)) return false;
        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        var due = new List<ClickAimAction>();
        var seen = new HashSet<int>();
        foreach (ClickAimAction action in bar)
            if (Eligible(action) && seen.Add(action.Id) && _schedule.Due(action.Id, now)) due.Add(action);
        if (due.Count == 0) return false;

        try { stopMovement(); }
        catch { Logging.Write("[Buff] could not stop movement; buff keys withheld"); return false; }
        if (ClickAimAbort.Wait(250) || !SameQuietPlayer(memory, reader, hwnd, stopped, player, map)) return false;
        using (memory.TemporaryCacheState(false))
        {
            WowBGuid selected = reader.ReadTargetGuid();
            if (selected != player.Guid)
            {
                if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map) || reader.ReadTargetGuid() != selected ||
                    !SameSlot(due[0], ClickAimCombat.ReadBar(reader)) || !Allowed(memory, reader, hwnd, stopped)) return false;
                Logging.Write("[Buff] selecting self for scheduled buffs (not a heal)");
                ClickAimInput.TapKey(hwnd, 0x70, 40); // default F1 self-target binding
                if (ClickAimAbort.Wait(120) || !SameQuietPlayer(memory, reader, hwnd, stopped, player, map)) return false;
                if (reader.ReadTargetGuid() != player.Guid)
                {
                    _nextScan = _clock() + 30000;
                    Logging.Write("[Buff] F1 did not confirm self-target; buff keys withheld and retry delayed");
                    return false;
                }
            }
        }

        bool attempted = false;
        foreach (ClickAimAction action in due)
        {
            using (memory.TemporaryCacheState(false))
            {
                if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map) || reader.ReadTargetGuid() != player.Guid ||
                    !SameSlot(action, ClickAimCombat.ReadBar(reader)) || !Allowed(memory, reader, hwnd, stopped)) break;
                ClickAimInput.TapKey(hwnd, action.KeyVk, 40);
                _schedule.RecordAttempt(action.Id, _clock());
                onAttempt?.Invoke();
                attempted = true;
                Logging.Write("[Buff] attempted " + action.Name + " (slot " + action.Slot + "); application and duration are not verified");
            }
            // Give the global cooldown time without hiding STOP or combat for 1.8 s.
            for (int waited = 0; waited < ClickAimCombat.CastSettleMs; waited += 150)
            {
                if (ClickAimAbort.Wait(Math.Min(150, ClickAimCombat.CastSettleMs - waited)) ||
                    !Allowed(memory, reader, hwnd, stopped) || !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting)
                    return attempted;
            }
        }
        return attempted;
    }
}

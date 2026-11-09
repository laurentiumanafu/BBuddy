using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Hotbar attempts; optional 70707 aura candidates are experimental and cannot
// prove casts or costs. Normal attempts are outside combat/gathering; the
// separately guarded combat path permits only supported self-only buffs.
internal sealed class ClickAimBuffController
{
    private const long ScanIntervalMs = 5000;
    private const long CombatRetryMs = 10_000;
    private const long TotemRetryMs = 10_000;
    // Observed net mana drops were 45 (Shield) and 60 (Armor). Actual costs
    // and modifiers are unreadable; thresholds are not proof of affordability.
    private static uint CombatManaEstimate(int spellId) => spellId == 324 ? 60U : spellId == 168 ? 80U :
        spellId == 8160 ? 65U : spellId == 5394 ? 40U : spellId == 5675 ? 40U : 0U;
    private readonly ClickAimBuffSchedule _schedule = new ClickAimBuffSchedule();
    private readonly Func<long> _clock;
    private readonly Dictionary<int, string> _reportedAuraState = new Dictionary<int, string>();
    private long _nextScan, _earthReservedUntil, _macroRetryAt, _macroNextPressAt;
    private int _macroRemaining;

    internal ClickAimBuffController() : this(() => Environment.TickCount64) { }
    internal ClickAimBuffController(Func<long> clock) { _clock = clock; }

    // New run or confirmed resurrection: the next quiet opportunity starts fresh.
    internal void Reset() { _schedule.Reset(); _reportedAuraState.Clear(); _nextScan = 0; _earthReservedUntil = 0; _macroRetryAt = 0; _macroNextPressAt = 0; _macroRemaining = 0; }

    private void ReportAura(ClickAimAction action, int? remaining)
    {
        string state = !remaining.HasValue ? "unavailable; using 30-minute fallback" :
            remaining.Value < 0 ? "missing in bounded candidate slots" :
            remaining.Value <= ClickAimBuffSchedule.RefreshBeforeMs ? "near expiry" : "active";
        if (_reportedAuraState.TryGetValue(action.Id, out string? old) && old == state) return;
        _reportedAuraState[action.Id] = state;
        string timing = remaining.HasValue && remaining.Value >= 0 ? "; approximately " + (remaining.Value / 1000) + " seconds remain" : "";
        Logging.Write("[Buff] 70707 experimental aura candidate " + action.Name + ": " + state + timing + " (not verified)");
    }

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

    // A Stoneclaw input attempt reserves the shared Earth slot. Actual cast
    // success/lifetime is unverified; this is a conservative local hold.
    internal void ReserveEarthAfterStoneclaw() => _earthReservedUntil = _clock() + 20_000;
    private bool EarthReserved(ClickAimAction action) => action.Id == 8160 && _clock() < _earthReservedUntil;

    private static bool Eligible(ClickAimAction action)
    {
        if (action.Id <= 0 || action.KeyVk == 0 || !action.IsBuff || WowBStoneclawPolicy.ManaEstimate(action.Id) != 0) return false;
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

    private static bool TotemSupported(WowBReader reader, ClickAimAction action)
        => reader.IsExperimental70862 && WowBTotemAuraCandidate.EffectForCast(action.Id) != 0;

    private bool TotemDue(ClickAimAction action, WowBTotemAuraCandidate? aura, long now)
        => aura?.Active(action.Id) == false && _schedule.PastAttemptBackoff(action.Id, now, TotemRetryMs);

    private static bool TotemManaEnough(WowBReader reader, WowBObservedUnit player, ClickAimAction action)
    {
        try
        {
            return reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) &&
                   fields.PlayerPowerTypeCandidate == 0 && fields.Power >= CombatManaEstimate(action.Id);
        }
        catch { return false; }
    }

    private void ReportTotem(ClickAimAction action, WowBTotemAuraCandidate? aura)
    {
        bool? active = aura?.Active(action.Id);
        string state = !active.HasValue ? "unavailable; cast withheld" : active.Value ? "active" : "absent; retry limited to 10 seconds";
        if (_reportedAuraState.TryGetValue(action.Id, out string? old) && old == state) return;
        _reportedAuraState[action.Id] = state;
        Logging.Write("[Buff] 70862 experimental totem effect " + action.Name + ": " + state + " (player effect, not totem existence)");
    }

    private static bool SameSlot(ClickAimAction action, List<ClickAimAction> bar)
    {
        if (!Eligible(action)) return false;
        foreach (ClickAimAction now in bar)
            if (now.Slot == action.Slot && now.Id == action.Id && now.Raw == action.Raw && now.KeyVk == action.KeyVk &&
                Eligible(now)) return true;
        return false;
    }

    // Auras are only candidates; a successful check is not a cast result.
    // Only supported self-only Shield/Armor or 70862 ground totems may be
    // pressed on an enemy selection; do not send F1/TAB or retarget.
    private static bool CombatPlayerSafe(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped,
                                         WowBObservedUnit expected, out uint map, out uint mana)
    {
        map = 0; mana = 0;
        try
        {
            if (!reader.IsExperimental70707 || !ClickAimSettings.UseBuffsInCombat || !Allowed(memory, reader, hwnd, stopped)) return false;
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out map) || !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit me) || me.Guid != expected.Guid || me.Guid.IsEmpty ||
                me.UnitAddress != expected.UnitAddress || me.RecordAddress != expected.RecordAddress ||
                !reader.TryGetUnitFields(me.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.MaxHealth == 0 || fields.Health == 0 || fields.Health > fields.MaxHealth ||
                !WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, me, out bool ghost) || ghost ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || !fighting ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map) return false;
            mana = fields.Power;
            return ClickAimSettings.UseBuffsInCombat && Allowed(memory, reader, hwnd, stopped);
        }
        catch { return false; }
    }

    // The macro is opaque. Each key is individually guarded; a burst is
    // non-blocking, so healing, STOP, combat and fight safety keep priority.
    private static int MacroMask()
    {
        if (!ClickAimSettings.ShamanTotemMacroValid()) return 0;
        return (ClickAimSettings.ShamanTotemEarth ? 1 : 0) |
               (ClickAimSettings.ShamanTotemHealing ? 2 : 0) |
               (ClickAimSettings.ShamanTotemMana ? 4 : 0);
    }
    private static bool MacroMissing(WowBTotemAuraCandidate aura, int mask)
        => ((mask & 1) != 0 && aura.Active(8160) == false) ||
           ((mask & 2) != 0 && aura.Active(5394) == false) ||
           ((mask & 4) != 0 && aura.Active(5675) == false);
    private static bool MacroHandles(int mask, int id)
        => (id == 8160 && (mask & 1) != 0) ||
           ((id == 5394 || id == 5675) && (mask & 6) != 0);
    private static bool ConflictingWater(List<ClickAimAction> bar)
        => bar.Exists(action => action.Id == 5394 && Eligible(action)) && bar.Exists(action => action.Id == 5675 && Eligible(action));

    private bool TryTotemMacro(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped,
                               WowBObservedUnit player, Func<bool> targetSafe, Action? onAttempt)
    {
        int mask = MacroMask(), key = ClickAimSettings.ShamanTotemMacroKey, count = ClickAimSettings.ShamanTotemMacroPresses;
        long now = _clock();
        if (!reader.IsExperimental70862 || mask == 0 || now < _earthReservedUntil)
        { _macroRemaining = 0; return false; }
        // A long interruption ends the incomplete burst rather than carrying it
        // indefinitely into a later fight. The retry delay starts at the last key.
        if (_macroRemaining > 0 && now >= _macroRetryAt) _macroRemaining = 0;
        bool starting = _macroRemaining == 0;
        if (starting ? now < _macroRetryAt : now < _macroNextPressAt) return false;
        uint minimumMana = (mask & 1) != 0 ? 65U : 40U; // most expensive configured rank; next step is unknown
        if (!CombatPlayerSafe(memory, reader, hwnd, stopped, player, out uint map, out uint mana) || mana < minimumMana ||
            !targetSafe() || !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
            fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana ||
            !reader.TryReadExperimentalActionSnapshot(out ulong[] bar) || bar.Length != 12 || bar[key - 1] == 0) return false;
        WowBTotemAuraCandidate? aura = WowBTotemAuraCandidate.Capture(memory, reader, player);
        if (aura == null || (starting && !MacroMissing(aura, mask))) return false;
        if (!CombatPlayerSafe(memory, reader, hwnd, stopped, player, out uint afterMap, out mana) || map != afterMap || mana < minimumMana ||
            !reader.TryGetUnitFields(player.UnitAddress, out fields) || fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana ||
            !targetSafe() || !reader.TryReadExperimentalActionSnapshot(out ulong[] afterBar) || afterBar.Length != 12 || afterBar[key - 1] != bar[key - 1]) return false;
        aura = WowBTotemAuraCandidate.Capture(memory, reader, player);
        if (aura == null || (starting && !MacroMissing(aura, mask)) ||
            !CombatPlayerSafe(memory, reader, hwnd, stopped, player, out afterMap, out mana) || map != afterMap || mana < minimumMana ||
            !reader.TryGetUnitFields(player.UnitAddress, out fields) || fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana ||
            !targetSafe() || !Allowed(memory, reader, hwnd, stopped) || !ClickAimSettings.UseBuffsInCombat ||
            mask != MacroMask() || key != ClickAimSettings.ShamanTotemMacroKey || count != ClickAimSettings.ShamanTotemMacroPresses ||
            _clock() < _earthReservedUntil) return false;
        // Record before input: delivery, macro contents and actual cast cannot be verified.
        now = _clock();
        if (starting) _macroRemaining = count;
        _macroRemaining--;
        _macroNextPressAt = now + ClickAimCombat.CastSettleMs;
        _macroRetryAt = now + TotemRetryMs;
        ClickAimInput.TapKey(hwnd, 0x30 + key, 40);
        onAttempt?.Invoke();
        Logging.Write("[Buff] attempted Shaman totem macro key " + key + "; remaining in burst " + _macroRemaining +
            "; macro content, binding and cast are unverified");
        return true;
    }

    internal bool TryApplyInCombat(IntPtr hwnd, Func<bool> stopped, WowBObservedUnit expectedPlayer, Func<bool> targetSafe, Action? onAttempt = null)
    {
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || hwnd == IntPtr.Zero || !reader.IsExperimental70707 ||
            !ClickAimSettings.UseBuffsInCombat || !Allowed(memory, reader, hwnd, stopped)) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (TryTotemMacro(memory, reader, hwnd, stopped, expectedPlayer, targetSafe, onAttempt)) return true;
            List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
            int macroMask = reader.IsExperimental70862 ? MacroMask() : 0;
            bool conflictWater = reader.IsExperimental70862 && ConflictingWater(bar) && (macroMask & 6) == 0;
            if (!bar.Exists(action => CombatManaEstimate(action.Id) > 0 && Eligible(action) &&
                (WowBTotemAuraCandidate.EffectForCast(action.Id) == 0 || TotemSupported(reader, action)))) return false;
            if (!CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out uint map, out uint mana) || !targetSafe()) return false;
            WowBInlineAuraCandidate? aura = WowBInlineAuraCandidate.Capture(memory, expectedPlayer);
            WowBTotemAuraCandidate? totemAura = reader.IsExperimental70862 && bar.Exists(action => Eligible(action) && TotemSupported(reader, action))
                ? WowBTotemAuraCandidate.Capture(memory, reader, expectedPlayer) : null;
            ClickAimAction? buff = null;
            foreach (ClickAimAction action in bar)
            {
                uint minimum = CombatManaEstimate(action.Id);
                if (minimum == 0 || mana < minimum || !Eligible(action) || EarthReserved(action) ||
                    MacroHandles(macroMask, action.Id) || (conflictWater && (action.Id == 5394 || action.Id == 5675))) continue;
                if (TotemSupported(reader, action))
                {
                    ReportTotem(action, totemAura);
                    if (TotemManaEnough(reader, expectedPlayer, action) && TotemDue(action, totemAura, _clock())) { buff = action; break; }
                    continue;
                }
                int? remainingCandidate = aura?.Remaining(action.Id);
                if (WowBTotemAuraCandidate.EffectForCast(action.Id) == 0 && remainingCandidate.HasValue && remainingCandidate.Value <= 0 &&
                    _schedule.PastAttemptBackoff(action.Id, _clock(), CombatRetryMs)) { buff = action; break; }
            }
            if (buff == null) return false;
            uint minimumMana = CombatManaEstimate(buff.Id);
            // Recheck identity, aura, spell slot, target, combat and mana after
            // the non-atomic reads and again before the key. Unknown is not absent.
            if (!CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out uint afterMap, out mana) || map != afterMap || mana < minimumMana ||
                !SameSlot(buff, ClickAimCombat.ReadBar(reader)) || !targetSafe()) return false;
            bool totem = TotemSupported(reader, buff);
            int? remaining = totem ? null : WowBInlineAuraCandidate.Capture(memory, expectedPlayer)?.Remaining(buff.Id);
            bool stillDue = totem ? TotemManaEnough(reader, expectedPlayer, buff) && TotemDue(buff, WowBTotemAuraCandidate.Capture(memory, reader, expectedPlayer), _clock())
                                  : remaining.HasValue && remaining.Value <= 0;
            if (!stillDue || EarthReserved(buff) ||
                !CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out afterMap, out mana) || map != afterMap || mana < minimumMana ||
                !SameSlot(buff, ClickAimCombat.ReadBar(reader)) || (totem && !TotemManaEnough(reader, expectedPlayer, buff)) || !targetSafe() || !ClickAimSettings.UseBuffsInCombat ||
                !Allowed(memory, reader, hwnd, stopped) || !_schedule.PastAttemptBackoff(buff.Id, _clock(), CombatRetryMs)) return false;
            ClickAimInput.TapKey(hwnd, buff.KeyVk, 40);
            _schedule.RecordAttempt(buff.Id, _clock());
            onAttempt?.Invoke();
            Logging.Write("[Buff] attempted " + buff.Name + " in combat; candidate expired/missing, mana gate is an estimate; success unverified");
            return true;
        }
        catch { return false; }
    }

    // Returns true only if at least one buff key was attempted. Leave the
    // existing selection alone and rely on the client's self-cast behavior.
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
        bool hasSupportedAura = reader.IsExperimental70707 && bar.Exists(action => Eligible(action) && WowBInlineAuraCandidate.Supported(action.Id));
        WowBInlineAuraCandidate? aura = hasSupportedAura ? WowBInlineAuraCandidate.Capture(memory, player) : null;
        foreach (ClickAimAction action in bar)
        {
            if (!Eligible(action) || !seen.Add(action.Id)) continue;
            // These exact totem spells are combat-only, even when their
            // role was manually set to Buff. Never fall back to the quiet timer.
            if (WowBTotemAuraCandidate.EffectForCast(action.Id) != 0) continue;
            int? remaining = aura?.Remaining(action.Id);
            if (reader.IsExperimental70707 && WowBInlineAuraCandidate.Supported(action.Id)) ReportAura(action, remaining);
            if (_schedule.Due(action.Id, now, remaining)) due.Add(action);
        }
        if (due.Count == 0 || !Allowed(memory, reader, hwnd, stopped)) return false;

        try { stopMovement(); }
        catch { Logging.Write("[Buff] could not stop movement; buff keys withheld"); return false; }
        if (ClickAimAbort.Wait(250) || !SameQuietPlayer(memory, reader, hwnd, stopped, player, map)) return false;
        WowBGuid selected;
        using (memory.TemporaryCacheState(false))
        {
            if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map)) return false;
            selected = reader.ReadTargetGuid();
        }

        bool attempted = false;
        foreach (ClickAimAction action in due)
        {
            using (memory.TemporaryCacheState(false))
            {
                if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map) || reader.ReadTargetGuid() != selected ||
                    !SameSlot(action, ClickAimCombat.ReadBar(reader)) || !Allowed(memory, reader, hwnd, stopped)) break;
                // Re-read between keys; unknown inline candidates retain their
                // existing timer fallback. Totems remain combat-only.
                if (WowBTotemAuraCandidate.EffectForCast(action.Id) != 0) continue;
                aura = reader.IsExperimental70707 && WowBInlineAuraCandidate.Supported(action.Id)
                    ? WowBInlineAuraCandidate.Capture(memory, player) : null;
                if (!_schedule.Due(action.Id, _clock(), aura?.Remaining(action.Id))) continue;
                if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map) || reader.ReadTargetGuid() != selected ||
                    !SameSlot(action, ClickAimCombat.ReadBar(reader)) || !Allowed(memory, reader, hwnd, stopped)) break;
                ClickAimInput.TapKey(hwnd, action.KeyVk, 40);
                _schedule.RecordAttempt(action.Id, _clock());
                onAttempt?.Invoke();
                attempted = true;
                Logging.Write("[Buff] attempted " + action.Name + " (slot " + action.Slot + ") without changing target; self-cast, application and duration are not verified");
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

// 70707 research candidate, bounded to inline layouts observed for one
// Mage and one Shaman. No dynamic pointer dereference, Lua, calls into the client or writes.
// Unknown layouts, unstable fields and unsupported IDs retain the old timer.
internal sealed class WowBInlineAuraCandidate
{
    private const int Header = 0x5F0, DataSize = 0x1624; // ends after player+0x1C10
    private const int DataPointer = 0x608, FirstId = 0x6F0, Stride = 0x128, Slots = 12;
    private const int ActiveCount = 0x1A60, ClientClock = 0x1C10;
    private readonly uint _clock;
    private readonly uint[] _id, _duration, _expiry;

    private WowBInlineAuraCandidate(uint clock, uint[] id, uint[] duration, uint[] expiry)
    { _clock = clock; _id = id; _duration = duration; _expiry = expiry; }

    private static uint Word(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset - Header);
    private static ulong Qword(byte[] bytes, int offset) => BitConverter.ToUInt64(bytes, offset - Header);

    // Only the exact action/aura IDs observed in the traces. Other ranks and
    // weapon enchants use the timer, not an assumed matching player aura ID.
    internal static bool Supported(int id) => id == 168 || id == 1459 || id == 324;

    internal static WowBInlineAuraCandidate? Capture(Memory memory, WowBObservedUnit player)
    {
        try
        {
            if (player.Guid.IsEmpty || player.UnitAddress < 0x10000 ||
                player.UnitAddress > 0x00007FFFFFFFFFFFUL - (ulong)(Header + DataSize)) return null;
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? first = memory.ReadBytes64(player.UnitAddress + Header, DataSize);
            byte[]? second = memory.ReadBytes64(player.UnitAddress + Header, DataSize);
            if (first == null || second == null || first.Length != DataSize || second.Length != DataSize) return null;
            // The inline pointer, 16-tagged capacity word and lengths 11/12
            // were observed in two sessions. The length's meaning is not
            // proven. Scan only that bounded range and require active-count
            // agreement. External/grown arrays are never chased.
            ulong inline = player.UnitAddress + DataPointer;
            ulong length = Qword(first, 0x5F8);
            if (Qword(first, 0x5F0) != inline || Qword(second, 0x5F0) != inline ||
                (length != 11 && length != Slots) || Qword(second, 0x5F8) != length ||
                Qword(first, 0x600) != 0x8000000000000010UL || Qword(second, 0x600) != 0x8000000000000010UL) return null;
            uint clock1 = Word(first, ClientClock), clock2 = Word(second, ClientClock);
            if (clock1 == 0 || clock2 == 0 || unchecked(clock2 - clock1) > 5000) return null;
            int slotCount = (int)length;
            uint[] id = new uint[slotCount], duration = new uint[slotCount], expiry = new uint[slotCount];
            var seen = new HashSet<uint>();
            int occupied = 0;
            for (int i = 0; i < slotCount; i++)
            {
                int offset = FirstId + i * Stride;
                id[i] = Word(second, offset); duration[i] = Word(second, offset + 12); expiry[i] = Word(second, offset + 16);
                if (id[i] != Word(first, offset) || duration[i] != Word(first, offset + 12) || expiry[i] != Word(first, offset + 16)) return null;
                if (id[i] != 0) { occupied++; if (!seen.Add(id[i])) return null; }
            }
            if (Word(first, ActiveCount) != (uint)occupied || Word(second, ActiveCount) != (uint)occupied) return null;
            return new WowBInlineAuraCandidate(clock2, id, duration, expiry);
        }
        catch { return null; }
    }

    // Null means unknown; -1 means missing in this bounded candidate layout.
    // The exact ID relation, clock and capacity remain experimental. A bad or
    // indefinite timing word is unknown, not expired.
    internal int? Remaining(int spellId)
    {
        if (!Supported(spellId)) return null;
        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] != (uint)spellId) continue;
            if (_duration[i] == 0 || _duration[i] > 24U * 60 * 60 * 1000) return null;
            int remaining = unchecked((int)(_expiry[i] - _clock));
            if (remaining < -5000 || (long)remaining > (long)_duration[i] + 5000) return null;
            return remaining;
        }
        return -1;
    }
}

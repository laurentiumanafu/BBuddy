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
    private static uint CombatManaEstimate(int spellId) => spellId == 168 ? 80U :
        spellId == 8160 ? 65U : spellId == 5394 ? 40U : spellId == 5675 ? 40U :
        WowBSealOfTheCrusader.ManaEstimate(spellId) != 0 ? WowBSealOfTheCrusader.ManaEstimate(spellId) :
        WowBWindfuryTotem.ManaEstimate(spellId) != 0 ? WowBWindfuryTotem.ManaEstimate(spellId) :
        WowBLightningShield.ManaEstimate(spellId);
    private readonly ClickAimBuffSchedule _schedule = new ClickAimBuffSchedule();
    private readonly Func<long> _clock;
    private readonly Dictionary<int, string> _reportedAuraState = new Dictionary<int, string>();
    private long _nextScan, _earthReservedUntil, _macroRetryAt, _macroNextPressAt;
    private int _macroRemaining;
    private string? _macroReportedState;
    private long _macroReportAt;

    internal ClickAimBuffController() : this(() => Environment.TickCount64) { }
    internal ClickAimBuffController(Func<long> clock) { _clock = clock; }

    // New run or confirmed resurrection: the next quiet opportunity starts fresh.
    internal void Reset() { _schedule.Reset(); _reportedAuraState.Clear(); _nextScan = 0; _earthReservedUntil = 0; _macroRetryAt = 0; _macroNextPressAt = 0; _macroRemaining = 0; _macroReportedState = null; _macroReportAt = 0; }

    internal static void ReportMacroSettings()
    {
        Logging.Write("[Buff] Shaman totem macro settings: enabled=" + ClickAimSettings.UseShamanTotemMacro +
            "; buffs=" + ClickAimSettings.UseBuffs + "; combat buffs=" + ClickAimSettings.UseBuffsInCombat +
            "; key=" + ClickAimSettings.ShamanTotemMacroKey + "; presses=" + ClickAimSettings.ShamanTotemMacroPresses +
            "; Earth=" + ClickAimSettings.ShamanTotemEarth + "; Healing Stream=" + ClickAimSettings.ShamanTotemHealing +
            "; Mana Spring=" + ClickAimSettings.ShamanTotemMana + "; Windfury=" + ClickAimSettings.ShamanTotemWindfury +
            "; key 1 attack=" + ClickAimSettings.UseKeyOneStartAttackMacro +
            "; valid=" + ClickAimSettings.ShamanTotemMacroValid() + " (binding and macro contents unverified)");
    }

    // Coalesce rapidly changing guards; repeat a persistent state occasionally
    // so a short captured log still has a reason without logging every tick.
    internal void ReportMacroState(string state)
    {
        if (!ClickAimSettings.UseShamanTotemMacro) return;
        long now = _clock();
        if (_macroReportedState != null && now < _macroReportAt + (state == _macroReportedState ? 15000 : 5000)) return;
        _macroReportedState = state; _macroReportAt = now;
        Logging.Write("[Buff] Shaman totem macro waiting: " + state);
    }

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

    private static bool ShieldSupported(WowBReader reader, int id)
        => reader.Uses70862Features && WowBLightningShield.Rank(id) != 0;

    // If several ranks are on the bar, choose the highest eligible one rather
    // than casting multiple shields or accidentally replacing it with a lower rank.
    private static int PreferredShield(List<ClickAimAction> bar)
    {
        int best = 0;
        foreach (ClickAimAction action in bar)
            if (Eligible(action) && WowBLightningShield.Rank(action.Id) > WowBLightningShield.Rank(best)) best = action.Id;
        return best;
    }

    // Several Seal of the Crusader ranks on the bar would otherwise be cast in
    // bar order, so a lower rank could replace a higher one already active.
    private static int PreferredSeal(List<ClickAimAction> bar)
    {
        int best = 0;
        foreach (ClickAimAction action in bar)
            if (Eligible(action) && WowBSealOfTheCrusader.Rank(action.Id) > WowBSealOfTheCrusader.Rank(best)) best = action.Id;
        return best;
    }

    // Three Windfury Totem ranks are mapped. Choose the highest eligible one
    // so a lower rank on the bar cannot replace a higher-rank effect.
    private static int PreferredWindfury(List<ClickAimAction> bar)
    {
        int best = 0;
        foreach (ClickAimAction action in bar)
            if (Eligible(action) && WowBWindfuryTotem.Rank(action.Id) > WowBWindfuryTotem.Rank(best)) best = action.Id;
        return best;
    }

    // The client keeps only one Seal on a Paladin. Casting a Crusader seal
    // silently cancels any other Seal, which would fight a second managed
    // seal on the bar, so only replace a seal this controller does not manage.
    private static bool SealReplacementSafe(List<ClickAimAction> bar, HashSet<uint>? activeIds)
    {
        if (activeIds == null) return true;
        foreach (uint id in activeIds)
        {
            if (id == 0 || id > int.MaxValue) continue;
            int other = (int)id;
            if (WowBSealOfTheCrusader.Rank(other) != 0 || !WowBSealOfTheCrusader.OtherSeal(other)) continue;
            if (bar.Exists(action => Eligible(action) && action.Id == other)) return false;
        }
        return true;
    }

    private static HashSet<uint> IdsOf(WowBInlineAuraCandidate inline)
    {
        var ids = new HashSet<uint>();
        foreach (uint id in inline.CopyIds()) if (id != 0) ids.Add(id);
        return ids;
    }

    // Aura slots change while effects are applied or expire, and in combat a
    // single strict snapshot can be rejected as unstable. Retry a bounded
    // number of times before falling back to the grown array; the same header,
    // count, uniqueness and stability checks apply to every attempt.
    private const int AuraCaptureAttempts = 3;

    // Seals can sit in the inline layout or in the grown array the totem
    // reader validates; both keep the ID at the same place inside a slot. A
    // null result means unknown, which never authorizes a cast.
    private static HashSet<uint>? ActiveAuraIds(Memory memory, WowBReader reader, WowBObservedUnit player,
        out string reason, out WowBInlineAuraCandidate? inline)
    {
        inline = null;
        string inlineReason = "inline candidate unreadable";
        for (int attempt = 0; attempt < AuraCaptureAttempts; attempt++)
        {
            inline = WowBInlineAuraCandidate.Capture(memory, player, out inlineReason);
            if (inline != null) { reason = inlineReason; return IdsOf(inline); }
        }
        reason = inlineReason;
        if (!reader.Uses70862Features) return null;
        WowBTotemAuraCandidate? external = WowBTotemAuraCandidate.Capture(memory, reader, player, out string externalReason);
        reason = inlineReason + "; " + externalReason;
        return external?.CopyIds();
    }

    // The inline slot carries a duration/expiry word; the grown array only
    // gives presence, so a present seal reports an unknown-but-active time.
    private static int? SealRemaining(WowBInlineAuraCandidate? inline, HashSet<uint>? activeIds, int spellId)
    {
        if (inline != null) return inline.Remaining(spellId);
        if (activeIds == null) return null;
        foreach (uint id in activeIds)
            if (id != 0 && id <= int.MaxValue && WowBSealOfTheCrusader.Rank((int)id) != 0)
                return ClickAimBuffSchedule.RefreshBeforeMs + 1;
        return -1;
    }

    private void ReportSeal(ClickAimAction action, int? remaining, bool blocked, string reason)
    {
        string state = !remaining.HasValue ? "unavailable; combat cast withheld (" + reason + ")" :
            (remaining.Value < 0 ? "absent" : "active") + " (" + reason + ")";
        if (blocked) state += "; another managed seal is active, replacement withheld";
        if (_reportedAuraState.TryGetValue(action.Id, out string? old) && old == state) return;
        _reportedAuraState[action.Id] = state;
        string timing = remaining.HasValue && remaining.Value >= 0 ? "; approximately " + (remaining.Value / 1000) + " seconds remain" : "";
        Logging.Write("[Buff] 70707 experimental Seal of the Crusader candidate: " + state + timing +
            " (rank " + WowBSealOfTheCrusader.Rank(action.Id) + "; not verified)");
    }

    // Presence only; no claim about remaining charges or duration. Unknown
    // never authorizes an in-combat attempt. The inline and grown layouts are
    // mutually exclusive and each reader checks its own header/count/stability.
    private static int? ShieldRemaining(WowBInlineAuraCandidate? inline, WowBTotemAuraCandidate? external)
    {
        if (inline != null) return inline.LightningShieldActive() ? ClickAimBuffSchedule.RefreshBeforeMs + 1 : -1;
        return external == null ? (int?)null : external.LightningShieldActive() ? ClickAimBuffSchedule.RefreshBeforeMs + 1 : -1;
    }

    private static int? ReadShield(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        WowBInlineAuraCandidate? inline = WowBInlineAuraCandidate.Capture(memory, player);
        return ShieldRemaining(inline, inline == null ? WowBTotemAuraCandidate.Capture(memory, reader, player) : null);
    }

    private void ReportShield(ClickAimAction action, int? state)
    {
        string text = !state.HasValue ? "unavailable; combat cast withheld" : state.Value < 0 ? "absent" : "present";
        if (_reportedAuraState.TryGetValue(action.Id, out string? old) && old == text) return;
        _reportedAuraState[action.Id] = text;
        Logging.Write("[Buff] 70862/70888/70938/70959 experimental Lightning Shield candidate: " + text + " (rank " + WowBLightningShield.Rank(action.Id) + "; not verified)");
    }

    private static bool TotemSupported(WowBReader reader, ClickAimAction action)
        => reader.Uses70862Features && WowBTotemAuraCandidate.EffectForCast(action.Id) != 0;

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
        Logging.Write("[Buff] 70862/70888/70938/70959 experimental totem effect " + action.Name + ": " + state + " (player effect, not totem existence)");
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
               (ClickAimSettings.ShamanTotemMana ? 4 : 0) |
               (ClickAimSettings.ShamanTotemWindfury ? 8 : 0);
    }
    private static bool MacroMissing(WowBTotemAuraCandidate aura, int mask)
        => ((mask & 1) != 0 && aura.Active(8160) == false) ||
           ((mask & 2) != 0 && aura.Active(5394) == false) ||
           ((mask & 4) != 0 && !aura.ManaSpringActive()) ||
           ((mask & 8) != 0 && !aura.WindfuryActive());
    private static bool MacroHandles(int mask, int id)
        => (id == 8160 && (mask & 1) != 0) ||
           ((id == 5394 || id == 5675) && (mask & 6) != 0) ||
           (WowBWindfuryTotem.Rank(id) != 0 && (mask & 8) != 0);
    private static bool ConflictingWater(List<ClickAimAction> bar)
        => bar.Exists(action => action.Id == 5394 && Eligible(action)) && bar.Exists(action => action.Id == 5675 && Eligible(action));

    private bool TryTotemMacro(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> stopped,
                               WowBObservedUnit player, Func<bool> targetSafe, Action? onAttempt, Action? beforeAttempt)
    {
        int mask = MacroMask(), key = ClickAimSettings.ShamanTotemMacroKey, count = ClickAimSettings.ShamanTotemMacroPresses;
        long now = _clock();
        if (!reader.Uses70862Features || mask == 0 || now < _earthReservedUntil)
        {
            _macroRemaining = 0;
            ReportMacroState(!reader.Uses70862Features ? "unsupported client" : mask == 0 ? "disabled or invalid macro settings" : "Stoneclaw Earth reservation");
            return false;
        }
        // A long interruption ends the incomplete burst rather than carrying it
        // indefinitely into a later fight. The retry delay starts at the last key.
        if (_macroRemaining > 0 && now >= _macroRetryAt) _macroRemaining = 0;
        bool starting = _macroRemaining == 0;
        if (starting ? now < _macroRetryAt : now < _macroNextPressAt)
        { ReportMacroState(starting ? "10-second retry delay" : "burst spacing"); return false; }
        // The macro's rank and next step are opaque. Mana Spring's known
        // Forever ranks cost up to 100 and Windfury's up to 250; use the
        // highest published cost of the selected group, as before.
        uint minimumMana = (mask & 8) != 0 ? 250U : (mask & 4) != 0 ? 100U : (mask & 1) != 0 ? 65U : 40U;
        if (!CombatPlayerSafe(memory, reader, hwnd, stopped, player, out uint map, out uint mana))
        { ReportMacroState("player/combat/health/ghost/map or session guard not confirmed"); return false; }
        if (mana < minimumMana)
        { ReportMacroState("mana below estimated minimum " + minimumMana); return false; }
        if (!targetSafe()) { ReportMacroState("combat target safety not confirmed"); return false; }
        if (!reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields))
        { ReportMacroState("player power fields unreadable"); return false; }
        if (fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana)
        { ReportMacroState("mana type or estimated minimum not confirmed"); return false; }
        if (!reader.TryReadExperimentalActionSnapshot(out ulong[] bar) || bar.Length != 12)
        { ReportMacroState("action bar unreadable or unstable"); return false; }
        if (bar[key - 1] == 0) { ReportMacroState("configured action slot is empty"); return false; }
        WowBTotemAuraCandidate? aura = WowBTotemAuraCandidate.Capture(memory, reader, player, out string auraReason);
        if (aura == null) { ReportMacroState("aura unknown: " + auraReason); return false; }
        if (starting && !MacroMissing(aura, mask))
        { ReportMacroState("all selected effects present in " + auraReason + " (unverified)"); return false; }
        // Stop any held chase movement only after an attempt is actually due;
        // revalidate combat, target, bar and aura after releasing the key.
        beforeAttempt?.Invoke();
        if (!CombatPlayerSafe(memory, reader, hwnd, stopped, player, out uint afterMap, out mana) || map != afterMap || mana < minimumMana ||
            !reader.TryGetUnitFields(player.UnitAddress, out fields) || fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana ||
            !targetSafe() || !reader.TryReadExperimentalActionSnapshot(out ulong[] afterBar) || afterBar.Length != 12 || afterBar[key - 1] != bar[key - 1])
        { ReportMacroState("player, mana, target, map or action slot failed revalidation"); return false; }
        aura = WowBTotemAuraCandidate.Capture(memory, reader, player);
        if (aura == null || (starting && !MacroMissing(aura, mask)) ||
            !CombatPlayerSafe(memory, reader, hwnd, stopped, player, out afterMap, out mana) || map != afterMap || mana < minimumMana ||
            !reader.TryGetUnitFields(player.UnitAddress, out fields) || fields.PlayerPowerTypeCandidate != 0 || fields.Power < minimumMana ||
            !targetSafe() || !Allowed(memory, reader, hwnd, stopped) || !ClickAimSettings.UseBuffsInCombat ||
            mask != MacroMask() || key != ClickAimSettings.ShamanTotemMacroKey || count != ClickAimSettings.ShamanTotemMacroPresses ||
            _clock() < _earthReservedUntil)
        { ReportMacroState("aura, player, target, settings or Earth reservation failed final revalidation"); return false; }
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

    internal bool TryApplyInCombat(IntPtr hwnd, Func<bool> stopped, WowBObservedUnit expectedPlayer, Func<bool> targetSafe, Action? onAttempt = null, Action? beforeAttempt = null)
    {
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || hwnd == IntPtr.Zero || !reader.IsExperimental70707 ||
            !ClickAimSettings.UseBuffsInCombat || !Allowed(memory, reader, hwnd, stopped))
        { ReportMacroState("combat buffs, session, action bar or STOP guard not confirmed"); return false; }
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (TryTotemMacro(memory, reader, hwnd, stopped, expectedPlayer, targetSafe, onAttempt, beforeAttempt)) return true;
            List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
            int macroMask = reader.Uses70862Features ? MacroMask() : 0;
            bool conflictWater = reader.Uses70862Features && ConflictingWater(bar) && (macroMask & 6) == 0;
            int preferredShield = reader.Uses70862Features ? PreferredShield(bar) : 0;
            int preferredSeal = PreferredSeal(bar);
            int preferredWindfury = reader.Uses70862Features ? PreferredWindfury(bar) : 0;
            if (!bar.Exists(action => CombatManaEstimate(action.Id) > 0 && Eligible(action) &&
                (WowBTotemAuraCandidate.EffectForCast(action.Id) == 0 || TotemSupported(reader, action)))) return false;
            if (!CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out uint map, out uint mana) || !targetSafe()) return false;
            WowBInlineAuraCandidate? aura = WowBInlineAuraCandidate.Capture(memory, expectedPlayer);
            // A Seal is watched through whichever validated layout is readable;
            // the inline snapshot keeps its timing when it is available.
            HashSet<uint>? sealIds = null;
            WowBInlineAuraCandidate? sealInline = aura;
            string sealReason = "";
            if (bar.Exists(action => Eligible(action) && WowBSealOfTheCrusader.Rank(action.Id) != 0))
            {
                if (aura != null) { sealIds = IdsOf(aura); sealReason = "inline candidate"; }
                else sealIds = ActiveAuraIds(memory, reader, expectedPlayer, out sealReason, out sealInline);
            }
            WowBTotemAuraCandidate? totemAura = reader.Uses70862Features && bar.Exists(action => Eligible(action) &&
                (TotemSupported(reader, action) || ShieldSupported(reader, action.Id)))
                ? WowBTotemAuraCandidate.Capture(memory, reader, expectedPlayer) : null;
            ClickAimAction? buff = null;
            foreach (ClickAimAction action in bar)
            {
                uint minimum = CombatManaEstimate(action.Id);
                if (minimum == 0 || mana < minimum || !Eligible(action) || EarthReserved(action) ||
                    MacroHandles(macroMask, action.Id) || (conflictWater && (action.Id == 5394 || action.Id == 5675))) continue;
                if (TotemSupported(reader, action))
                {
                    // Only the highest mapped Windfury rank is attempted, so a
                    // lower rank on the bar cannot replace a stronger effect.
                    if (WowBWindfuryTotem.Rank(action.Id) != 0 && action.Id != preferredWindfury) continue;
                    ReportTotem(action, totemAura);
                    if (TotemManaEnough(reader, expectedPlayer, action) && TotemDue(action, totemAura, _clock())) { buff = action; break; }
                    continue;
                }
                if (ShieldSupported(reader, action.Id))
                {
                    if (action.Id != preferredShield) continue;
                    int? shield = ShieldRemaining(aura, totemAura);
                    ReportShield(action, shield);
                    if (shield.HasValue && shield.Value < 0 && TotemManaEnough(reader, expectedPlayer, action) &&
                        _schedule.PastAttemptBackoff(action.Id, _clock(), CombatRetryMs)) { buff = action; break; }
                    continue;
                }
                if (WowBSealOfTheCrusader.Rank(action.Id) != 0)
                {
                    if (action.Id != preferredSeal) continue;
                    // A Seal is combat-only: it lasts 30 seconds and any other
                    // Seal cancels it, so it never uses the quiet 30-minute timer.
                    bool replace = SealReplacementSafe(bar, sealIds);
                    int? sealRemaining = SealRemaining(sealInline, sealIds, action.Id);
                    ReportSeal(action, sealRemaining, !replace, sealReason);
                    if (replace && sealRemaining.HasValue && sealRemaining.Value <= 0 &&
                        TotemManaEnough(reader, expectedPlayer, action) &&
                        _schedule.PastAttemptBackoff(action.Id, _clock(), CombatRetryMs)) { buff = action; break; }
                    continue;
                }
                int? remainingCandidate = aura?.Remaining(action.Id);
                if (WowBTotemAuraCandidate.EffectForCast(action.Id) == 0 && remainingCandidate.HasValue && remainingCandidate.Value <= 0 &&
                    _schedule.PastAttemptBackoff(action.Id, _clock(), CombatRetryMs)) { buff = action; break; }
            }
            if (buff == null) return false;
            uint minimumMana = CombatManaEstimate(buff.Id);
            beforeAttempt?.Invoke(); // release held chase movement before key/GCD wait
            // Recheck identity, aura, spell slot, target, combat and mana after
            // the non-atomic reads and again before the key. Unknown is not absent.
            if (!CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out uint afterMap, out mana) || map != afterMap || mana < minimumMana ||
                !SameSlot(buff, ClickAimCombat.ReadBar(reader)) || !targetSafe()) return false;
            bool totem = TotemSupported(reader, buff);
            bool shieldAction = ShieldSupported(reader, buff.Id);
            bool sealAction = WowBSealOfTheCrusader.Rank(buff.Id) != 0;
            WowBInlineAuraCandidate? inlineNow = sealAction || (!totem && !shieldAction)
                ? WowBInlineAuraCandidate.Capture(memory, expectedPlayer) : null;
            HashSet<uint>? sealIdsNow = null;
            if (sealAction)
            {
                if (inlineNow != null) sealIdsNow = IdsOf(inlineNow);
                else sealIdsNow = ActiveAuraIds(memory, reader, expectedPlayer, out _, out inlineNow);
            }
            int? remaining = totem ? null : shieldAction ? ReadShield(memory, reader, expectedPlayer) :
                sealAction ? SealRemaining(inlineNow, sealIdsNow, buff.Id) : inlineNow?.Remaining(buff.Id);
            bool stillDue = totem ? TotemManaEnough(reader, expectedPlayer, buff) && TotemDue(buff, WowBTotemAuraCandidate.Capture(memory, reader, expectedPlayer), _clock())
                                  : remaining.HasValue && remaining.Value <= 0;
            if (!stillDue || EarthReserved(buff) ||
                !CombatPlayerSafe(memory, reader, hwnd, stopped, expectedPlayer, out afterMap, out mana) || map != afterMap || mana < minimumMana ||
                !SameSlot(buff, ClickAimCombat.ReadBar(reader)) || (shieldAction && PreferredShield(ClickAimCombat.ReadBar(reader)) != buff.Id) ||
                (sealAction && (PreferredSeal(ClickAimCombat.ReadBar(reader)) != buff.Id ||
                    !SealReplacementSafe(ClickAimCombat.ReadBar(reader), sealIdsNow))) ||
                (totem && WowBWindfuryTotem.Rank(buff.Id) != 0 &&
                    PreferredWindfury(ClickAimCombat.ReadBar(reader)) != buff.Id) ||
                ((totem || shieldAction) && !TotemManaEnough(reader, expectedPlayer, buff)) || !targetSafe() || !ClickAimSettings.UseBuffsInCombat ||
                !Allowed(memory, reader, hwnd, stopped) || !_schedule.PastAttemptBackoff(buff.Id, _clock(), CombatRetryMs)) return false;
            ClickAimInput.TapKey(hwnd, buff.KeyVk, 40);
            _schedule.RecordAttempt(buff.Id, _clock());
            onAttempt?.Invoke();
            Logging.Write("[Buff] attempted " + buff.Name + " in combat; candidate expired/missing, mana gate is an estimate; success unverified");
            return true;
        }
        catch { ReportMacroState("combat buff check failed unexpectedly"); return false; }
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
        int preferredShield = reader.Uses70862Features ? PreferredShield(bar) : 0;
        bool hasSupportedAura = reader.IsExperimental70707 && bar.Exists(action => Eligible(action) && WowBInlineAuraCandidate.Supported(action.Id));
        WowBInlineAuraCandidate? aura = hasSupportedAura ? WowBInlineAuraCandidate.Capture(memory, player) : null;
        foreach (ClickAimAction action in bar)
        {
            if (!Eligible(action) || !seen.Add(action.Id)) continue;
            // Seals are combat-only, like the observed ground totems: a
            // 30-second buff refreshed by melee strikes has no meaning on the
            // quiet 30-minute timer outside a fight.
            if (WowBSealOfTheCrusader.Rank(action.Id) != 0) continue;
            // On 70938 this toggle-like form belongs exclusively to the
            // movement checkbox, never the unrelated 30-minute buff timer.
            if (reader.Uses70938Features && (action.Id == 2645 || action.Id == 1238640)) continue;
            // These exact totem spells are combat-only, even when their
            // role was manually set to Buff. Never fall back to the quiet timer.
            if (WowBTotemAuraCandidate.EffectForCast(action.Id) != 0) continue;
            bool shield = ShieldSupported(reader, action.Id);
            if (shield && action.Id != preferredShield) continue;
            int? remaining = shield ? ReadShield(memory, reader, player) : aura?.Remaining(action.Id);
            if (shield) ReportShield(action, remaining);
            else if (reader.IsExperimental70707 && WowBInlineAuraCandidate.Supported(action.Id)) ReportAura(action, remaining);
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
                if (reader.Uses70938Features && (action.Id == 2645 || action.Id == 1238640)) continue;
                aura = reader.IsExperimental70707 && WowBInlineAuraCandidate.Supported(action.Id)
                    ? WowBInlineAuraCandidate.Capture(memory, player) : null;
                int? remaining = ShieldSupported(reader, action.Id) ? ReadShield(memory, reader, player) : aura?.Remaining(action.Id);
                if (!_schedule.Due(action.Id, _clock(), remaining)) continue;
                if (!SameQuietPlayer(memory, reader, hwnd, stopped, player, map) || reader.ReadTargetGuid() != selected ||
                    !SameSlot(action, ClickAimCombat.ReadBar(reader)) ||
                    (ShieldSupported(reader, action.Id) && PreferredShield(ClickAimCombat.ReadBar(reader)) != action.Id) ||
                    !Allowed(memory, reader, hwnd, stopped)) break;
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
// Mage, one Shaman and one Paladin. No dynamic pointer dereference, Lua,
// calls into the client or writes. Unknown layouts, unstable fields and
// unsupported IDs retain the old timer.
internal sealed class WowBInlineAuraCandidate
{
    private const int Header = 0x5F0, DataSize = 0x1624; // ends after player+0x1C10
    private const int DataPointer = 0x608, FirstId = 0x6F0, Stride = 0x128, InlineMaxSlots = 16;
    private const int ActiveCount = 0x1A60, ClientClock = 0x1C10;
    private readonly uint _clock;
    private readonly uint[] _id, _duration, _expiry;

    private WowBInlineAuraCandidate(uint clock, uint[] id, uint[] duration, uint[] expiry)
    { _clock = clock; _id = id; _duration = duration; _expiry = expiry; }

    private static uint Word(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset - Header);
    private static ulong Qword(byte[] bytes, int offset) => BitConverter.ToUInt64(bytes, offset - Header);

    // Only the exact action/aura IDs observed in the traces. Other ranks and
    // weapon enchants use the timer, not an assumed matching player aura ID.
    internal static bool Supported(int id) => id == 168 || id == 1459 || id == 324 || WowBSealOfTheCrusader.Rank(id) != 0;

    internal static WowBInlineAuraCandidate? Capture(Memory memory, WowBObservedUnit player)
        => Capture(memory, player, out _);

    internal static WowBInlineAuraCandidate? Capture(Memory memory, WowBObservedUnit player, out string reason)
    {
        reason = "player identity invalid";
        try
        {
            if (player.Guid.IsEmpty || player.UnitAddress < 0x10000 ||
                player.UnitAddress > 0x00007FFFFFFFFFFFUL - (ulong)(Header + DataSize)) return null;
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? first = memory.ReadBytes64(player.UnitAddress + Header, DataSize);
            byte[]? second = memory.ReadBytes64(player.UnitAddress + Header, DataSize);
            reason = "inline window unreadable";
            if (first == null || second == null || first.Length != DataSize || second.Length != DataSize) return null;
            // The inline pointer, 16-tagged capacity word and lengths 11/12
            // were observed for a Mage and a Shaman; a Paladin trace showed
            // 9, 14 and 15 with the same pointer, capacity word and
            // active-count agreement. The length's meaning is still not
            // proven, so the scan stays inside the tagged capacity and keeps
            // requiring active-count agreement. External/grown arrays are
            // never chased.
            ulong inline = player.UnitAddress + DataPointer;
            ulong length = Qword(first, 0x5F8), capacity = Qword(first, 0x600);
            if (Qword(first, 0x5F0) != Qword(second, 0x5F0) || Qword(second, 0x5F8) != length || Qword(second, 0x600) != capacity)
            { reason = "inline header changed during read"; return null; }
            if (Qword(first, 0x5F0) != inline || length < 1 || length > InlineMaxSlots || capacity != 0x8000000000000010UL)
            {
                // No raw pointer is logged; these words help distinguish an
                // unsupported inline shape from a grown-array header.
                reason = "inline header rejected (pointer=" + (Qword(first, 0x5F0) == inline ? "inline" : "other") +
                    ", length=" + length + ", capacity=0x" + capacity.ToString("X") + ")";
                return null;
            }
            uint clock1 = Word(first, ClientClock), clock2 = Word(second, ClientClock);
            reason = "inline clock unavailable or unstable";
            if (clock1 == 0 || clock2 == 0 || unchecked(clock2 - clock1) > 5000) return null;
            int slotCount = (int)length;
            uint[] id = new uint[slotCount], duration = new uint[slotCount], expiry = new uint[slotCount];
            var seen = new HashSet<uint>();
            int occupied = 0;
            for (int i = 0; i < slotCount; i++)
            {
                int offset = FirstId + i * Stride;
                id[i] = Word(second, offset); duration[i] = Word(second, offset + 12); expiry[i] = Word(second, offset + 16);
                if (id[i] != Word(first, offset) || duration[i] != Word(first, offset + 12) || expiry[i] != Word(first, offset + 16))
                { reason = "inline slots changed during read"; return null; }
                if (id[i] != 0) { occupied++; if (!seen.Add(id[i])) { reason = "inline duplicate IDs"; return null; } }
            }
            if (Word(first, ActiveCount) != (uint)occupied || Word(second, ActiveCount) != (uint)occupied)
            { reason = "inline count mismatch (count=" + Word(first, ActiveCount) + "/" + Word(second, ActiveCount) + ", occupied=" + occupied + ")"; return null; }
            reason = "inline candidate";
            return new WowBInlineAuraCandidate(clock2, id, duration, expiry);
        }
        catch { reason = "inline read failed"; return null; }
    }

    internal bool LightningShieldActive()
    {
        foreach (uint id in _id) if (id <= int.MaxValue && WowBLightningShield.Rank((int)id) != 0) return true;
        return false;
    }

    // IDs were already validated as unique with stable occupied-count/header.
    // A defensive copy keeps callers from mutating this observation.
    internal uint[] CopyIds() => (uint[])_id.Clone();

    // Null means unknown; -1 means missing in this bounded candidate layout.
    // The exact ID relation, clock and capacity remain experimental. A bad or
    // indefinite timing word is unknown, not expired.
    internal int? Remaining(int spellId)
    {
        // Only one Seal can be active on a Paladin, so any Crusader rank
        // counts as present and a different rank on the bar does not fight
        // the active one.
        if (WowBSealOfTheCrusader.Rank(spellId) != 0)
        {
            for (int i = 0; i < _id.Length; i++)
                if (_id[i] != 0 && _id[i] <= int.MaxValue && WowBSealOfTheCrusader.Rank((int)_id[i]) != 0) return Timing(i);
            return -1;
        }
        if (!Supported(spellId)) return null;
        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] != (uint)spellId) continue;
            return Timing(i);
        }
        return -1;
    }

    private int? Timing(int slot)
    {
        if (_duration[slot] == 0 || _duration[slot] > 24U * 60 * 60 * 1000) return null;
        int remaining = unchecked((int)(_expiry[slot] - _clock));
        if (remaining < -5000 || (long)remaining > (long)_duration[slot] + 5000) return null;
        return remaining;
    }
}

// WoW Forever Paladin Seals. Only Seal of the Crusader ranks are managed; the
// 30-second self buff was observed in a 70938 trace as aura ID 21082 at
// player+0x6F0 + slot * 0x128. Ranks and costs come from published Forever
// trainer data, not from the client, so costs stay an estimate. The other
// Seal IDs are only a guard: their presence is never read as a duration.
internal static class WowBSealOfTheCrusader
{
    internal static int Rank(int id) => id switch
    {
        21082 => 1, 20162 => 2, 20305 => 3, 20306 => 4, 20307 => 5, 20308 => 6, _ => 0
    };
    internal static uint ManaEstimate(int id) => Rank(id) switch
    {
        1 => 25U, 2 => 40U, 3 => 65U, 4 => 90U, 5 => 125U, 6 => 160U, _ => 0U
    };
    internal static bool OtherSeal(int id) => Rank(id) == 0 && id switch
    {
        // Seal of Righteousness
        20154 or 20287 or 20288 or 20289 or 20290 or 20291 or 20292 or 20293 or 21084 => true,
        // Seal of Justice, Seal of Light and Seal of Wisdom
        20164 or 20165 or 20347 or 20348 or 20349 or 20166 or 20356 or 20357 => true,
        // Seal of Command and Seal of Martyrdom
        20375 or 20915 or 20918 or 20919 or 20920 or 407798 => true,
        _ => false
    };
}

// Known 1.60 shield ranks. The user's traces observed IDs 324 and 905;
// other aura IDs are inferred from the cast IDs, not independently verified.
internal static class WowBLightningShield
{
    internal static int Rank(int id) => id switch
    {
        324 => 1, 325 => 2, 905 => 3, 945 => 4, 8134 => 5, 10431 => 6, 10432 => 7, _ => 0
    };
    internal static uint ManaEstimate(int id) => id switch
    {
        324 => 60U, 325 => 80U, 905 => 125U, 945 => 180U, 8134 => 240U, 10431 => 305U, 10432 => 370U, _ => 0U
    };
}

// Windfury Totem cast ranks from the published WoW Forever tables. Only the
// rank 2 player effect (10609) was observed, in a single 70938 trace; the
// other cast ranks, their costs and their effect IDs are unverified here.
internal static class WowBWindfuryTotem
{
    internal static int Rank(int id) => id switch { 8512 => 1, 10613 => 2, 10614 => 3, _ => 0 };
    internal static uint ManaEstimate(int id) => Rank(id) switch { 1 => 115U, 2 => 175U, 3 => 250U, _ => 0U };
}

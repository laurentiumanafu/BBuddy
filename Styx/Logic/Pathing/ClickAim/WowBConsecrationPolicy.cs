using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Consecration is cast in combat only when two NPCs from the active Grind
// profile factions are close to the character, mirroring the Stoneclaw totem
// proximity heuristic. It does NOT prove hostility, aggression, or that either
// nearby NPC attacks the player; "attacking" cannot be read reliably, so the
// proximity pair is the authorization. The user explicitly accepted this
// heuristic for Stoneclaw and asked for the same behavior here.
internal static class WowBConsecrationPolicy
{
    internal const double RadiusYd = 10.0;
    // Consecration's cooldown is 8 s; the retry reserves that window so a
    // failed or on-cooldown press is not spammed. Slightly above the cooldown
    // so the next attempt lands after the spell is usable again.
    internal const long RetryMs = 8_500;
    // Public Forever rank costs; candidate power and actual costs are unverified.
    internal static uint ManaEstimate(int id) => id switch
    {
        26573 => 25U, 20116 => 30U, 20117 => 45U, 20118 => 60U, 20119 => 75U,
        20120 => 90U, 20121 => 105U, 20122 => 120U, 20123 => 140U, 20124 => 160U,
        20125 => 180U, 20126 => 200U, 20127 => 220U, 20128 => 240U, 20129 => 260U,
        20130 => 280U, 20131 => 300U, 20132 => 320U, 20133 => 340U, 20134 => 360U,
        20135 => 380U, 20136 => 400U, 20137 => 420U, 20138 => 440U, 20139 => 460U,
        20140 => 480U, 20141 => 500U, 20142 => 520U, 20143 => 540U, 20144 => 560U,
        20145 => 580U, 20146 => 600U, 20147 => 620U, 20148 => 640U, 20149 => 660U,
        20150 => 680U, 20151 => 700U, 20152 => 720U, 20153 => 740U, 20154 => 760U,
        20155 => 780U, 20156 => 800U, 20157 => 820U, 20158 => 840U, 20159 => 860U,
        20160 => 880U, 20161 => 900U, _ => 0U
    };

    internal static bool Eligible(ClickAimAction action)
    {
        if (action.Kind != ClickAimActionKind.Spell || action.KeyVk == 0 || ManaEstimate(action.Id) == 0) return false;
        ClickAimSpellRole role = ClickAimCombat.RoleOverrideOf(action.Id);
        return role == ClickAimSpellRole.Unknown || role == ClickAimSpellRole.Damage || role == ClickAimSpellRole.Utility;
    }

    internal static bool SameIdentity(WowBObservedUnit a, WowBObservedUnit b)
        => !a.Guid.IsEmpty && a.Guid == b.Guid && a.UnitAddress == b.UnitAddress && a.RecordAddress == b.RecordAddress;

    private static bool Candidate(WowBReader reader, WowBObservedUnit player, WowBObservedUnit unit, GrindFactionRules factions)
    {
        if (unit.IsLocalPlayer || unit.HasPlayerVtable || unit.Guid.IsEmpty || unit.Guid == player.Guid ||
            unit.UnitAddress == 0 || unit.RecordAddress == 0) return false;
        double dx = unit.Location.X - player.Location.X, dy = unit.Location.Y - player.Location.Y, dz = unit.Location.Z - player.Location.Z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!double.IsFinite(d) || d >= RadiusYd || !reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields)) return false;
        // The key is an experimental faction-template candidate, not evidence
        // that this NPC is hostile or attacking. Unknown/unstable keys do not count.
        return fields.Health > 0 && fields.MaxHealth > 0 && fields.Health <= fields.MaxHealth &&
            (fields.UnitFlags & (0x2U | 0x100U | 0x200U)) == 0 &&
            WowBGrindFactions.TryReadBoundKey(reader, unit, out uint key) && factions.Allows(true, key);
    }

    internal static bool FindPair(WowBReader reader, WowBWorldSnapshot snap, WowBObservedUnit player,
                                  GrindFactionRules factions, out WowBObservedUnit first, out WowBObservedUnit second)
    {
        first = default; second = default;
        if (factions.Mode != GrindFactionMode.Allow) return false;
        var seen = new HashSet<WowBGuid>();
        bool found = false;
        foreach (WowBObservedUnit unit in snap.Units)
        {
            if (!seen.Add(unit.Guid) || !Candidate(reader, player, unit, factions)) continue;
            if (!found) { first = unit; found = true; }
            else { second = unit; return true; }
        }
        return false;
    }

    internal static bool SamePairStillNear(WowBReader reader, WowBWorldSnapshot snap, WowBObservedUnit player,
                                           WowBObservedUnit first, WowBObservedUnit second, GrindFactionRules factions)
        => factions.Mode == GrindFactionMode.Allow && first.Guid != second.Guid && snap.TryGetUnit(first.Guid, out WowBObservedUnit a) && SameIdentity(first, a) && Candidate(reader, player, a, factions) &&
           snap.TryGetUnit(second.Guid, out WowBObservedUnit b) && SameIdentity(second, b) && Candidate(reader, player, b, factions);
}

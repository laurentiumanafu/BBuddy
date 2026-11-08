using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// The user explicitly accepted this 70862 proximity heuristic. It does NOT
// prove hostility, aggression, or that either nearby NPC attacks the player.
internal static class WowBStoneclawPolicy
{
    internal const double RadiusYd = 8.0;
    internal const long RetryMs = 30_000;
    // Public Forever rank costs; candidate power and actual costs are unverified.
    internal static uint ManaEstimate(int id) => id switch
    {
        5730 => 15U, 6390 => 30U, 6391 => 55U, 6392 => 75U,
        10427 => 105U, 10428 => 140U, _ => 0U
    };

    internal static bool Eligible(ClickAimAction action)
    {
        if (action.Kind != ClickAimActionKind.Spell || action.KeyVk == 0 || ManaEstimate(action.Id) == 0) return false;
        ClickAimSpellRole role = ClickAimCombat.RoleOverrideOf(action.Id);
        return role == ClickAimSpellRole.Unknown || role == ClickAimSpellRole.Utility || role == ClickAimSpellRole.Buff;
    }

    internal static bool SameIdentity(WowBObservedUnit a, WowBObservedUnit b)
        => !a.Guid.IsEmpty && a.Guid == b.Guid && a.UnitAddress == b.UnitAddress && a.RecordAddress == b.RecordAddress;

    private static bool Candidate(WowBReader reader, WowBObservedUnit player, WowBObservedUnit unit)
    {
        if (unit.IsLocalPlayer || unit.HasPlayerVtable || unit.Guid.IsEmpty || unit.Guid == player.Guid ||
            unit.UnitAddress == 0 || unit.RecordAddress == 0) return false;
        double dx = unit.Location.X - player.Location.X, dy = unit.Location.Y - player.Location.Y, dz = unit.Location.Z - player.Location.Z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!double.IsFinite(d) || d > RadiusYd || !reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields)) return false;
        // Five-health summoned totems were present in prior snapshots; exclude
        // very low max-health units as a rough safeguard, not a type check.
        return fields.Health > 0 && fields.MaxHealth > 10 && fields.Health <= fields.MaxHealth && (fields.UnitFlags & (0x2U | 0x100U | 0x200U)) == 0;
    }

    internal static bool FindPair(WowBReader reader, WowBWorldSnapshot snap, WowBObservedUnit player,
                                  out WowBObservedUnit first, out WowBObservedUnit second)
    {
        first = default; second = default;
        var seen = new HashSet<WowBGuid>();
        bool found = false;
        foreach (WowBObservedUnit unit in snap.Units)
        {
            if (!seen.Add(unit.Guid) || !Candidate(reader, player, unit)) continue;
            if (!found) { first = unit; found = true; }
            else { second = unit; return true; }
        }
        return false;
    }

    internal static bool SamePairStillNear(WowBReader reader, WowBWorldSnapshot snap, WowBObservedUnit player,
                                           WowBObservedUnit first, WowBObservedUnit second)
        => first.Guid != second.Guid && snap.TryGetUnit(first.Guid, out WowBObservedUnit a) && SameIdentity(first, a) && Candidate(reader, player, a) &&
           snap.TryGetUnit(second.Guid, out WowBObservedUnit b) && SameIdentity(second, b) && Candidate(reader, player, b);
}

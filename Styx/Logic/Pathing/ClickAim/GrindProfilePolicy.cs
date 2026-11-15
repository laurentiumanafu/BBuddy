using System;

namespace Styx.Logic.Pathing;

// Profile IDs, levels and coordinates are configuration, not proof of hostility.
internal static class GrindProfilePolicy
{
    internal const double HotspotRadiusYd = 100.0;

    internal static bool ValidLevels(ProfileGrindArea area)
    {
        if (area.InvalidProfileData || area.Hotspots.Count == 0 || area.TargetMinLevel < 1 || area.TargetMaxLevel < area.TargetMinLevel || area.TargetMaxLevel > 255) return false;
        foreach (RoutePoint point in area.Hotspots)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) return false;
        bool noPlayerBand = area.PlayerMinLevel == 0 && area.PlayerMaxLevel == 0;
        return noPlayerBand || (area.PlayerMinLevel >= 1 && area.PlayerMaxLevel >= area.PlayerMinLevel && area.PlayerMaxLevel <= 255);
    }

    internal static bool PlayerInBand(ProfileGrindArea area, int level)
    {
        int min = area.PlayerMinLevel > 0 ? area.PlayerMinLevel : area.TargetMinLevel;
        int max = area.PlayerMaxLevel > 0 ? area.PlayerMaxLevel : area.TargetMaxLevel;
        return min > 0 && level >= min && (max <= 0 || level <= max);
    }

    // An explicit target band can opt into monsters more than six levels
    // below the player. This does not authorize a pull by itself: callers must
    // still validate map, faction, hotspot, unit identity, health and flags.
    // The existing upper-level safety limit is deliberately not relaxed.
    internal static bool AllowsLowerTarget(ProfileGrindArea? area, uint level, int playerLevel)
        => area != null && area.HasExplicitTargetMinLevel && playerLevel > 0 && level > 0 && level < (uint)playerLevel &&
           ValidLevels(area) && PlayerInBand(area, playerLevel) &&
           level >= (uint)area.TargetMinLevel && level <= (uint)area.TargetMaxLevel;

    internal static bool AllowsTarget(ProfileGrindArea area, double x, double y, uint level)
    {
        if (!ValidLevels(area) || !double.IsFinite(x) || !double.IsFinite(y) ||
            level < (uint)area.TargetMinLevel || level > (uint)area.TargetMaxLevel) return false;
        foreach (RoutePoint point in area.Hotspots)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            double dx = x - point.X, dy = y - point.Y;
            if (dx * dx + dy * dy <= HotspotRadiusYd * HotspotRadiusYd) return true;
        }
        return false;
    }
}

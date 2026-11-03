using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Geometric heuristic only. The object pool can omit NPCs, and none of the
// observed records proves hostility, aggro range or that a location is safe.
internal static class WowBCorpseSpotPolicy
{
    internal const double ArrivalRadius = 35.0, MaxClickRadius = 38.0, MaxVertical = 15.0;
    internal const double MinNpcClearance = 30.0, PreferCurrentClearance = 35.0;
    internal static readonly double[] Rings = { 32.0, 24.0, 16.0, 8.0 };

    internal static double Distance(WoWPoint a, WoWPoint b)
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static bool InRange(WoWPoint spot, WoWPoint corpse, double radius)
        => float.IsFinite(spot.X) && float.IsFinite(spot.Y) && float.IsFinite(spot.Z) &&
           float.IsFinite(corpse.X) && float.IsFinite(corpse.Y) && float.IsFinite(corpse.Z) &&
           double.IsFinite(radius) && radius >= 0 && Distance(spot, corpse) <= radius &&
           Math.Abs((double)spot.Z - corpse.Z) <= MaxVertical;

    internal static double Clearance(WoWPoint spot, IReadOnlyList<WoWPoint> npcs)
    {
        if (!float.IsFinite(spot.X) || !float.IsFinite(spot.Y) || npcs == null) return double.NaN;
        double nearest = double.PositiveInfinity;
        foreach (WoWPoint npc in npcs)
        {
            if (!float.IsFinite(npc.X) || !float.IsFinite(npc.Y)) return double.NaN;
            nearest = Math.Min(nearest, Distance(spot, npc));
        }
        return nearest;
    }

    internal static bool ClearEnough(WoWPoint spot, IReadOnlyList<WoWPoint> npcs, double minimum)
    {
        double clearance = Clearance(spot, npcs);
        return !double.IsNaN(clearance) && double.IsFinite(minimum) && minimum >= 0 && clearance >= minimum;
    }

    internal static bool GroundArea(byte area) => area == 0 || area == 1 || area == 4;

    internal static List<WoWPoint> RankRing(WoWPoint corpse, WoWPoint current, IReadOnlyList<WoWPoint> npcs, double radius)
    {
        var result = new List<WoWPoint>();
        if (!InRange(corpse, corpse, 0) || !InRange(current, corpse, MaxClickRadius) || npcs == null ||
            !double.IsFinite(radius) || radius < 0 || radius > 32) return result;
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8.0;
            var point = new WoWPoint((float)(corpse.X + Math.Cos(angle) * radius),
                                     (float)(corpse.Y + Math.Sin(angle) * radius), corpse.Z);
            if (InRange(point, corpse, MaxClickRadius) && ClearEnough(point, npcs, MinNpcClearance)) result.Add(point);
        }
        result.Sort((a, b) =>
        {
            // Prefer the greatest distance from the closest observed NPC; use
            // shorter ghost travel only as a tie-breaker.
            int byClearance = Clearance(b, npcs).CompareTo(Clearance(a, npcs));
            return byClearance != 0 ? byClearance : Distance(a, current).CompareTo(Distance(b, current));
        });
        return result;
    }
}

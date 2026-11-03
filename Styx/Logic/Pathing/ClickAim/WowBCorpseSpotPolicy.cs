using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Geometric heuristic only. The object pool can omit NPCs, and none of the
// observed records proves hostility, aggro range or that a location is safe.
internal static class WowBCorpseSpotPolicy
{
    internal const double ArrivalRadius = 35.0, MaxClickRadius = 38.0, MaxVertical = 15.0;
    internal const double MinNpcClearance = 30.0, PreferCurrentClearance = 35.0;
    internal const int DirectionCount = 4;
    internal const double ScoutRadius = 32.0;

    internal readonly struct ScoutLocation
    {
        internal readonly int Direction;
        internal readonly WoWPoint Position;
        internal ScoutLocation(int direction, WoWPoint position) { Direction = direction; Position = position; }
    }

    // One location in each quadrant. Rank the four directions by observed NPC
    // clearance, but do not exclude crowded directions: the user wants the
    // ghost to try them before falling back to a less-safe resurrection.
    internal static List<ScoutLocation> ScoutLocations(WoWPoint corpse, WoWPoint current, IReadOnlyList<WoWPoint> npcs)
    {
        var result = new List<ScoutLocation>();
        if (!InRange(corpse, corpse, 0) || !InRange(current, corpse, MaxClickRadius) ||
            npcs == null || double.IsNaN(Clearance(current, npcs))) return result;
        for (int direction = 0; direction < DirectionCount; direction++)
        {
            WoWPoint best = default;
            double bestClearance = -1, bestDistance = double.PositiveInfinity;
            for (int i = 0; i < 4; i++)
            {
                double angle = (direction * 4 + i) * Math.PI / 8.0;
                var point = new WoWPoint((float)(corpse.X + Math.Cos(angle) * ScoutRadius),
                                         (float)(corpse.Y + Math.Sin(angle) * ScoutRadius), corpse.Z);
                double clearance = Clearance(point, npcs), distance = Distance(point, current);
                if (clearance > bestClearance || (clearance == bestClearance && distance < bestDistance))
                { best = point; bestClearance = clearance; bestDistance = distance; }
            }
            result.Add(new ScoutLocation(direction, best));
        }
        result.Sort((a, b) =>
        {
            int byClearance = Clearance(b.Position, npcs).CompareTo(Clearance(a.Position, npcs));
            if (byClearance != 0) return byClearance;
            int byDistance = Distance(a.Position, current).CompareTo(Distance(b.Position, current));
            return byDistance != 0 ? byDistance : a.Direction.CompareTo(b.Direction);
        });
        return result;
    }

    internal static List<WoWPoint> FallbackLocations(WoWPoint corpse, WoWPoint current)
    {
        var result = new List<WoWPoint>();
        if (!InRange(corpse, corpse, 0) || !InRange(current, corpse, MaxClickRadius)) return result;
        // Work inward if the prompt is not visible at the last scout position.
        foreach (double radius in new[] { 16.0, 8.0, 0.0 })
        {
            var ring = new List<WoWPoint>();
            int count = radius == 0 ? 1 : DirectionCount;
            for (int i = 0; i < count; i++)
            {
                double angle = i * Math.PI / 2;
                ring.Add(new WoWPoint((float)(corpse.X + Math.Cos(angle) * radius),
                                      (float)(corpse.Y + Math.Sin(angle) * radius), corpse.Z));
            }
            ring.Sort((a, b) => Distance(a, current).CompareTo(Distance(b, current)));
            result.AddRange(ring);
        }
        return result;
    }

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

}

using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Per-stuck-area budgets for one bounded ghost recovery. A later failed corpse
// leg may finish far from the first anchor; don't sample around the old anchor.
// Revisiting an area restores its original attempt budget instead of resetting it.
internal sealed class WowBCorpseRelocationRegions
{
    internal const int MaxRegions = 12;
    internal const double RegionRadiusYd = 40.0; // below MeshRelocate's 50 yd anchor radius

    internal sealed class Region
    {
        internal readonly double X, Y;
        internal readonly int Number;
        internal int Attempts;
        internal readonly List<RoutePoint> Tried = new List<RoutePoint>();
        internal Region(double x, double y, int number) { X = x; Y = y; Number = number; }
    }

    private readonly List<Region> regions = new List<Region>();
    private Region? current;

    internal bool TrySelect(double x, double y, out Region? selected, out bool changed, out bool created)
    {
        selected = null; changed = created = false;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        Region? nearest = null;
        double best = RegionRadiusYd;
        foreach (Region region in regions)
        {
            double d = ClickAimMath.Len(x - region.X, y - region.Y);
            if (double.IsFinite(d) && d <= best) { best = d; nearest = region; }
        }
        if (nearest == null)
        {
            if (regions.Count >= MaxRegions) return false;
            nearest = new Region(x, y, regions.Count + 1);
            regions.Add(nearest);
            created = true;
        }
        changed = current != nearest;
        current = nearest;
        selected = nearest;
        return true;
    }
}

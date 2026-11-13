using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

/// <summary>Bounded candidate geometry only; none of these points is permission to move.</summary>
internal static class ClickAimEscape
{
    // Each attempt begins on a different bearing. Subsequent bearings allow a
    // short escape along a narrow corridor rather than repeating a rejected
    // seven-yard perpendicular point. The caller must validate every segment.
    internal static List<WoWPoint> Candidates(double x, double y, double goalX, double goalY, float z, int attempt)
    {
        var points = new List<WoWPoint>();
        double dx = goalX - x, dy = goalY - y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(goalX) ||
            !double.IsFinite(goalY) || !float.IsFinite(z) || !double.IsFinite(length) || length < 1 || attempt < 1 || attempt > 3)
            return points;
        dx /= length; dy /= length;
        double[] angles = attempt == 1 ? new[] { 90.0, 135.0, 180.0, 45.0 }
                        : attempt == 2 ? new[] { -90.0, -135.0, 180.0, -45.0 }
                        : new[] { 180.0, 135.0, -135.0, 90.0, -90.0 };
        double[] distances = { 7.0, 4.0, 2.5 };
        foreach (double angle in angles)
        {
            double radians = angle * Math.PI / 180.0;
            double vx = dx * Math.Cos(radians) - dy * Math.Sin(radians);
            double vy = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            foreach (double distance in distances)
                points.Add(new WoWPoint((float)(x + vx * distance), (float)(y + vy * distance), z));
        }
        return points;
    }
}

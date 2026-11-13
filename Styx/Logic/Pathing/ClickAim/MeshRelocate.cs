using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// Picks somewhere else to stand when a hotspot leg failed.
///
/// The 18:11-18:13 run is the reason this exists: the character got within 16.5 yd of a
/// hotspot, every key-steered attempt stalled, every sidestep was refused because the short
/// segment hit the edge of the mesh (ray hit 0.25 / 0), and after three identical failures from
/// the same spot the gatherer stopped. Asking for the same leg from the same place a third time
/// is not information. Standing somewhere else on the mesh and approaching again from a
/// different direction is what a person does.
///
/// Every candidate is a point the mesh itself produced (Detour's random-point query, connected
/// to where the character stands), snapped back onto a polygon, in a safe area type, outside the
/// profile's blackspots, and reachable by a complete validated mesh path. Candidates are
/// ranked by separation from places already tried. Mesh validity does not
/// guarantee physical clearance from a fence or other dynamic obstruction.
/// </summary>
internal static class MeshRelocate
{
    /// <summary>How many alternate locations are tried before the leg is given up.</summary>
    internal const int Attempts = 5;

    /// <summary>How far from the character an alternate location may be.</summary>
    internal const double RadiusYd = 50.0;

    /// <summary>Closer than this is the same spot as far as being stuck is concerned.</summary>
    private const double MinDistanceYd = 5.0;

    /// <summary>A new location must be at least this far from every one already tried.</summary>
    private const double MinSeparationYd = 5.0;

    private const int SamplesPerPick = 40;
    private const int MaxCandidates = 12;
    private const int MaxPathChecks = 6;

    /// <summary>
    /// Smallest distance from (x, y) to any point in <paramref name="tried"/>, capped at
    /// <paramref name="cap"/>. With nothing tried yet every point scores the cap.
    /// </summary>
    internal static double SpreadScore(double x, double y, IList<RoutePoint> tried, double cap)
    {
        double best = cap;
        for (int i = 0; i < tried.Count; i++)
        {
            double d = ClickAimMath.Len(x - tried[i].X, y - tried[i].Y);
            if (d < best) best = d;
        }
        return best;
    }

    private static bool Finite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>
    /// Samples one alternate within <see cref="RadiusYd"/> of both the current and
    /// original stuck position. <paramref name="excluded"/> tests profile blackspots.
    /// False means this bounded search found no validated point, or was interrupted;
    /// it does not prove that no other reachable location exists.
    /// </summary>
    internal static bool TryPick(uint map, double originX, double originY, IList<RoutePoint> tried,
                                 Func<double, double, bool> excluded, Func<bool> interrupted,
                                 out RoutePoint point)
    {
        point = new RoutePoint();
        if (interrupted() || !double.IsFinite(originX) || !double.IsFinite(originY)
            || !ClickAimPositions.TryRead(out double x, out double y)) return false;
        double z = ClickAimPositions.Z();
        if (!double.IsFinite(z)) return false;

        try
        {
            var nav = Navigator.TripperNavigator;
            if (!nav.IsLoaded) return false;

            var at = new Vector3((float)x, (float)y, (float)z);
            nav.EnsureTilesAroundPosition(map, at, Navigator.LoadTilesAroundRadius);
            if (!nav.FindNearestPolyRef(map, at, 2.5f, 5f, out Tripper.Navigation.PolygonReference current, out Vector3 from)
                || !current.IsValid || !Finite(from)
                || ClickAimMath.Len(from.X - x, from.Y - y) > 2.5 || Math.Abs(from.Z - z) > 5)
            {
                Logging.Write("[Relocate] the character is not on a verified mesh polygon; cannot choose another location");
                return false;
            }

            var found = new List<(Vector3 Point, double Score)>();
            for (int i = 0; i < SamplesPerPick && found.Count < MaxCandidates; i++)
            {
                if (interrupted()) return false;
                // Try nearby escape points as well as longer detours. Every point
                // remains bounded around the original stuck position.
                float radius = i % 2 == 0 ? 12f : (float)RadiusYd;
                if (!nav.FindRandomPoint(map, from, radius, out Vector3 next) || !Finite(next)) continue;

                double distance = ClickAimMath.Len(next.X - x, next.Y - y);
                if (distance < MinDistanceYd || distance > RadiusYd) continue;

                if (!nav.FindNearestPolyRef(map, next, 0.5f, 2f, out Tripper.Navigation.PolygonReference end, out Vector3 snap)
                    || !end.IsValid || !Finite(snap)
                    || ClickAimMath.Len(snap.X - next.X, snap.Y - next.Y) > 0.5
                    || Math.Abs(snap.Z - next.Z) > 2.0
                    || (nav.GetPolyArea(map, end, out byte area) & 0x80000000U) != 0
                    || !MeshRoam.SafeDestinationArea(map, snap, area, (tx, ty) => nav.IsTileLoaded(map, tx, ty))
                    || ClickAimMath.Len(snap.X - originX, snap.Y - originY) > RadiusYd
                    || ClickAimMath.Len(snap.X - x, snap.Y - y) < MinDistanceYd
                    || ClickAimMath.Len(snap.X - x, snap.Y - y) > RadiusYd) continue;

                if (excluded(snap.X, snap.Y)) continue;

                double score = SpreadScore(snap.X, snap.Y, tried, RadiusYd);
                if (tried.Count > 0 && score < MinSeparationYd) continue;

                bool duplicate = false;
                foreach (var other in found)
                    if (ClickAimMath.Len(snap.X - other.Point.X, snap.Y - other.Point.Y) < 6.0) { duplicate = true; break; }
                if (duplicate) continue;

                found.Add((snap, score));
            }

            // Prefer separation; on the first attempt prefer a nearby point.
            // Distance separation cannot guarantee five different bearings.
            found.Sort((a, b) =>
            {
                int spread = b.Score.CompareTo(a.Score);
                if (spread != 0) return spread;
                return ClickAimMath.Len(a.Point.X - x, a.Point.Y - y)
                    .CompareTo(ClickAimMath.Len(b.Point.X - x, b.Point.Y - y));
            });

            int checks = 0;
            foreach (var candidate in found)
            {
                if (interrupted()) return false;
                if (checks++ >= MaxPathChecks) break;
                var probe = new RoutePoint
                {
                    X = candidate.Point.X, Y = candidate.Point.Y, Z = candidate.Point.Z, Kind = "Relocate"
                };
                // Same complete-path, endpoint and straight-ray validation the walk itself will use.
                if (RouteRunner.TryCombatMeshPath(x, y, probe) == null) continue;
                if (interrupted()) return false;
                point = probe;
                return true;
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Relocate] no reachable mesh location found within {0:F0} yd of ({1:F1}, {2:F1}) "
                + "({3} candidate(s) sampled, {4} already tried)", RadiusYd, x, y, found.Count, tried.Count));
            return false;
        }
        catch (Exception ex)
        {
            Logging.Write("[Relocate] mesh sampling failed: " + ex.Message);
            return false;
        }
    }

    internal static string SelfTest()
    {
        var tried = new List<RoutePoint>
        {
            new RoutePoint { X = 0, Y = 0 },
            new RoutePoint { X = 30, Y = 0 }
        };
        bool ok = SpreadScore(5, 0, new List<RoutePoint>(), 50.0) == 50.0
               && Math.Abs(SpreadScore(5, 0, tried, 50.0) - 5.0) < 1e-9
               && Math.Abs(SpreadScore(15, 40, tried, 50.0) - 42.72) < 0.01
               && SpreadScore(200, 200, tried, 50.0) == 50.0;
        return "[Relocate] spread-score fixtures: " + (ok ? "pass" : "FAIL") + " (not a live mesh test)";
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// Walks a <see cref="ProfileRoute"/> waypoint by waypoint.
///
/// Two modes, chosen from what the client actually has:
///
///  * a navmesh is loaded -&gt; each leg is routed with <c>Navigator.GeneratePath</c>
///    and the resulting polyline is followed point by point (this is what a
///    mesh-driven run does, and it is what the bot will do once a mesh exists for
///    this build);
///  * no navmesh -&gt; each profile waypoint is walked directly, because the profile's
///    own hotspot clusters are already 6-60 yd hops. Legs longer than
///    <see cref="ProfileRoute.MaxClickWalkYd"/> are refused instead of attempted,
///    since one synthesised click cannot be aimed across 600 yd of terrain.
///
/// It never calls <c>Navigator.MoveTo</c>: that needs the behaviour tree's pulse and
/// a complete object model, which the 64-bit client does not have yet.
/// </summary>
public static class RouteRunner
{
    private const int LegTimeoutMs = 90000;
    private const double StepTolerance = 2.5;

    /// <summary>
    /// Longest single click-walk step used when a baked grid turns a long leg into
    /// waypoints: field legs beyond ~45 yd start missing. 40 leaves margin.
    /// </summary>
    private const double MaxClickStepYd = 40.0;

    /// <summary>Runs the route in order. Blocks; call from a background thread.</summary>
    public static bool Run(IList<RoutePoint> route, int maxLegs)
    {
        if (route == null || route.Count == 0)
        {
            Logging.Write("[Route] nothing to walk: the route is empty");
            return false;
        }

        bool mesh = MeshAvailable();
        if (mesh)
        {
            Logging.Write("[Route] navmesh available: every leg is routed with GeneratePath, then followed");
        }
        else
        {
            Logging.Write("[Route] (the Detour native library is 32-bit and there are no mmap tiles for build 70291, "
                          + "so there is no mesh to route on - see hunt/NAVMESH_PLAN.md)");
            int cached = NavGridStore.CachedTiles;
            Logging.Write("[Route] baked navgrid tiles in " + NavGridStore.Root
                          + " (map " + NavGridStore.MapId + "): "
                          + (cached > 0 ? cached + " already cached" : "none loaded yet")
                          + "; each leg is routed on the grid when a tile is available");
        }

        var mover = new ClickAimMover();
        int walked = 0;

        for (int i = 0; i < route.Count; i++)
        {
            if (maxLegs > 0 && walked >= maxLegs)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] leg budget reached ({0}); stopping {1} waypoints short", maxLegs, route.Count - i));
                return true;
            }

            double px, py;
            if (!ClickAimPositions.TryRead(out px, out py))
            {
                Logging.Write("[Route] no player position - stopping (is the character in the world?)");
                return walked > 0;
            }

            RoutePoint target = route[i];
            double leg = ClickAimMath.Len(target.X - px, target.Y - py);

            if (leg <= StepTolerance)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] waypoint {0}/{1} {2} is already here ({3:F1} yd)", i + 1, route.Count, target, leg));
                continue;
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] waypoint {0}/{1} {2} - {3:F1} yd", i + 1, route.Count, target, leg));

            bool arrived;
            WoWPoint[]? path = mesh ? TryMeshPath(px, py, target) : null;

            if (path == null && !mesh)
            {
                System.Collections.Generic.List<WoWPoint> grid = TryGridPath(px, py, target);
                if (grid != null && grid.Count > 1)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Route] navgrid path: {0} waypoints for {1:F0} yd", grid.Count, leg));
                    if (WalkSteps(mover, grid))
                    {
                        walked++;
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Route] waypoint {0}/{1} reached via the grid", i + 1, route.Count));
                        continue;
                    }
                }
            }

            if (path != null && path.Length > 1)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] mesh path: {0} points", path.Length));
                arrived = true;
                for (int p = 1; p < path.Length; p++)
                {
                    WoWPoint step = path[p];
                    if (!ClickAimPilot.WalkTo(mover, step, StepTolerance, LegTimeoutMs, false))
                    {
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Route] gave up on mesh point {0}/{1} at ({2:F1}, {3:F1})",
                            p, path.Length - 1, step.X, step.Y));
                        arrived = false;
                        break;
                    }
                }
            }
            else if (leg > ProfileRoute.MaxClickWalkYd)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] refusing a {0:F0} yd leg to {1}: one click reaches about {2:F0} yd, "
                    + "and with no mesh there is nothing to route the middle with",
                    leg, target, ProfileRoute.MaxClickWalkYd));
                return walked > 0;
            }
            else
            {
                arrived = ClickAimPilot.WalkTo(mover, target.ToWoWPoint(), StepTolerance, LegTimeoutMs, true);
            }

            if (!arrived)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] stopping at waypoint {0}/{1}: the mover did not arrive", i + 1, route.Count));
                return walked > 0;
            }

            walked++;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] waypoint {0}/{1} reached", i + 1, route.Count));
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture, "[Route] route complete: {0} waypoints walked", walked));
        return true;
    }

    private static System.Collections.Generic.List<WoWPoint>? TryGridPath(double px, double py, RoutePoint target)
    {
        try
        {
            double z = ClickAimPositions.Z();
            if (double.IsNaN(z)) z = target.Z;
            System.Collections.Generic.List<WoWPoint> path =
                NavGridStore.FindPath(NavGridStore.MapId, px, py, z, target.X, target.Y, target.Z);
            return path.Count > 1 ? path : null;
        }
        catch (Exception ex)
        {
            Logging.Write("[Route] navgrid lookup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Walks a waypoint list with the click mover, splitting any segment longer
    /// than a comfortable click into shorter hops.
    /// </summary>
    private static bool WalkSteps(ClickAimMover mover, System.Collections.Generic.List<WoWPoint> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            WoWPoint from = points[i - 1];
            WoWPoint to = points[i];
            double length = ClickAimMath.Len(to.X - from.X, to.Y - from.Y);
            int steps = (int)Math.Ceiling(length / MaxClickStepYd);
            if (steps < 1) steps = 1;

            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                var step = new WoWPoint((float)(from.X + (to.X - from.X) * t),
                                        (float)(from.Y + (to.Y - from.Y) * t),
                                        (float)(from.Z + (to.Z - from.Z) * t));
                if (!ClickAimPilot.WalkTo(mover, step, StepTolerance, LegTimeoutMs, false))
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Route] stopped {0:F0} yd into a {1:F0} yd segment", length * t, length));
                    return false;
                }
            }
        }
        return true;
    }

    private static bool MeshAvailable()
    {
        try { return Navigator.IsNavigatorLoaded; }
        catch { return false; }
    }

    private static WoWPoint[]? TryMeshPath(double px, double py, RoutePoint target)
    {
        try
        {
            double z = ClickAimPositions.Z();
            float fromZ = double.IsNaN(z) ? (float)target.Z : (float)z;
            WoWPoint[] path = Navigator.GeneratePath(new WoWPoint((float)px, (float)py, fromZ), target.ToWoWPoint());
            return path.Length > 1 ? path : null;
        }
        catch (Exception ex)
        {
            Logging.Write("[Route] mesh path failed, falling back to the waypoint: " + ex.Message);
            return null;
        }
    }
}

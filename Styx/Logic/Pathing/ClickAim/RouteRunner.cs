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
///  * a navmesh is loaded -&gt; each leg is routed on the mmaps through
///    <c>Tripper.Navigation.Navigator.FindPath</c> and the resulting polyline is
///    followed point by point. <c>Navigator.GeneratePath</c> is not used because it
///    asks <c>ObjectManager.Me</c> for the map id and <c>Me</c> is null on x64;
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
            Logging.Write("[Route] navmesh available: every leg is routed on the mmaps (Navigator.FindPath), then followed point by point");
        }
        else
        {
            Logging.Write("[Route] no navmesh: either Navigation.dll did not load or there are no mmap tiles "
                          + "for this map yet. Build them with Tools\\NavMeshBuilder\\make-mmaps.cmd "
                          + "(see Tools/NavMeshBuilder/README.md); routing falls back to the baked grid.");
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
                double meshLength = 0.0;
                for (int q = 1; q < path.Length; q++)
                    meshLength += ClickAimMath.Len(path[q].X - path[q - 1].X, path[q].Y - path[q - 1].Y);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] mesh path: {0} points, {1:F1} yd for a {2:F1} yd leg{3}",
                    path.Length, meshLength, leg,
                    meshLength > leg + 1.0 ? " (detours around terrain)" : " (nearly straight)"));
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

    private static bool _meshProbed;
    private static bool _meshReady;

    /// <summary>
    /// True when a navmesh can be queried, loading the library once if it has not
    /// been already. Public so a single-point walk can take the same route a profile
    /// leg does instead of clicking straight at the goal.
    /// </summary>
    public static bool MeshReady() { return MeshAvailable(); }

    /// <summary>
    /// True once a navmesh can be queried. The first call tries to load the native
    /// library itself: on the 64-bit client no bot session ever starts, so nothing
    /// would call <c>LoadMeshes</c> and every leg would silently fall back to the
    /// grid even with a valid Navigation.dll and mmaps sitting next to the exe.
    /// </summary>
    private static bool MeshAvailable()
    {
        if (_meshProbed) return _meshReady;
        _meshProbed = true;
        try { _meshReady = Navigator.EnsureLoadedForDiagnostics(); }
        catch { _meshReady = false; }
        return _meshReady;
    }

    private static WoWPoint[]? TryMeshPath(double px, double py, RoutePoint target)
    {
        try
        {
            double z = ClickAimPositions.Z();
            float fromZ = double.IsNaN(z) ? (float)target.Z : (float)z;

            // Navigator.GeneratePath asks ObjectManager.Me for the map id, and on the
            // x64 client Me does not exist. Drive the Tripper navigator directly with
            // the map id we were told to use.
            Tripper.Navigation.Navigator nav = Navigator.TripperNavigator;
            if (!nav.IsLoaded) return null;

            /* The map id is a constant (1). When the character is somewhere else the
             * tile for this position belongs to a different map, every query comes
             * back empty, and it looks like the mesh is broken. Ask the position. */
            uint mapId = ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, px, py);
            var from = new System.Numerics.Vector3((float)px, (float)py, fromZ);
            var to = new System.Numerics.Vector3((float)target.X, (float)target.Y, (float)target.Z);

            nav.EnsureTilesAroundPosition(mapId, from, Navigator.LoadTilesAroundRadius);
            nav.EnsureTilesAroundPosition(mapId, to, Navigator.LoadTilesAroundRadius);

            Tripper.Navigation.PathFindResult result = nav.FindPath(mapId, from, to, true);
            if (result == null || !result.Status.Succeeded)
            {
                Logging.Write("[Route] navmesh found no path to ("
                    + string.Format(CultureInfo.InvariantCulture, "{0:F1}, {1:F1}", target.X, target.Y) + ")");
                return null;
            }
            if (result.Points == null || result.Points.Length < 2) return null;

            var path = new WoWPoint[result.Points.Length];
            for (int i = 0; i < result.Points.Length; i++)
                path[i] = new WoWPoint(result.Points[i].X, result.Points[i].Y, result.Points[i].Z);
            return path;
        }
        catch (Exception ex)
        {
            Logging.Write("[Route] mesh path failed, falling back: " + ex.Message);
            return null;
        }
    }
}

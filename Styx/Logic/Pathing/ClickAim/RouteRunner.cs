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
    /// How long one polyline point gets when the leg is mesh-routed. Shorter than
    /// <see cref="LegTimeoutMs"/> on purpose: the points are 5-20 yd apart and a
    /// point that takes 30 s is a point the character cannot reach, so waiting
    /// longer only delays the re-plan.
    /// </summary>
    private const int MeshPointTimeoutMs = 30000;

    /// <summary>
    /// How many times a leg may be re-planned after the character is stopped by
    /// something the mmaps do not know about. Each re-plan blackspots the place it
    /// got stuck, so the third one is a genuinely closed route, not a stubborn one.
    /// </summary>
    private const int MaxReplans = 3;

    /// <summary>
    /// Longest hop actually clicked along a mesh polyline. Shorter than
    /// <see cref="MaxClickStepYd"/> because a mesh corner is a place the client
    /// walks through, not a place to aim past.
    /// </summary>
    private const double FollowStepYd = 15.0;

    /// <summary>Radius marked as bad around a point the character could not pass.</summary>
    private const float BlackspotRadius = 8.0f;

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
            if (mesh)
            {
                arrived = WalkMesh(mover, target, leg);
            }
            else
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

                if (leg > ProfileRoute.MaxClickWalkYd)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Route] refusing a {0:F0} yd leg to {1}: one click reaches about {2:F0} yd, "
                        + "and with no mesh there is nothing to route the middle with",
                        leg, target, ProfileRoute.MaxClickWalkYd));
                    return walked > 0;
                }

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

    /// <summary>
    /// Walks one leg along the mesh, and plans again when the character is stopped.
    ///
    /// A click-walk is a request, not a command: the client walks in a straight line
    /// from where the character stands and stops at the first thing it meets. The
    /// mmaps know about terrain and building interiors, not about the cart, the fence
    /// post or the mob standing in the way, so even a correct polyline occasionally
    /// ends with the character pressed against something the path did not predict.
    ///
    /// The answer is what a person does: note where the thing that stopped you is,
    /// treat that ground as bad, and ask for a route that avoids it. Each re-plan
    /// blackspots the stuck spot (area 17, cost 60 in the DLL's own filter), so a
    /// second attempt is a different path rather than the same click harder.
    ///
    /// When the mesh has no route at all from here and the goal is inside one click,
    /// the straight click is still taken - that is the pre-existing behaviour for
    /// open ground, and refusing it would strand a character over a data gap.
    /// </summary>
    private static bool WalkMesh(ClickAimMover mover, RoutePoint target, double leg)
    {
        // One blind detour, then come back here and re-plan instead of spending six
        // more legs being stuck in the same place. This is the change that turns a
        // 90 s walk into a wall into a 40 s walk into a wall plus a way around it.
        mover.DetourBudget = 1;

        double lastPx = double.NaN;
        double lastPy = double.NaN;

        for (int plan = 0; plan <= MaxReplans; plan++)
        {
            double px, py;
            if (!ClickAimPositions.TryRead(out px, out py))
            {
                Logging.Write("[Route] lost the player position while re-planning");
                return false;
            }

            double remaining = ClickAimMath.Len(target.X - px, target.Y - py);
            if (remaining <= StepTolerance) return true;

            if (plan > 0)
            {
                double moved = double.IsNaN(lastPx) ? double.MaxValue
                                                     : ClickAimMath.Len(px - lastPx, py - lastPy);
                if (moved < 1.5)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Route] re-plan {0} made no progress ({1:F1} yd): the way out is closed in "
                        + "every direction Detour can see from ({2:F1}, {3:F1})", plan, moved, px, py));
                    return false;
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] re-plan {0}/{1} from ({2:F1}, {3:F1}), {4:F1} yd still to go",
                    plan, MaxReplans, px, py, remaining));
            }

            lastPx = px;
            lastPy = py;

            WoWPoint[]? path = TryMeshPath(px, py, target);
            if (path == null || path.Length < 2)
            {
                if (plan == 0 && leg <= ProfileRoute.MaxClickWalkYd)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Route] no route on the mesh from ({0:F1}, {1:F1}); the goal is {2:F0} yd "
                        + "away, inside one click, so clicking straight at it", px, py, remaining));
                    return ClickAimPilot.WalkTo(mover, target.ToWoWPoint(), StepTolerance, LegTimeoutMs, true);
                }

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] no route on the mesh from ({0:F1}, {1:F1}) to ({2:F1}, {3:F1}) after "
                    + "{4} re-plan(s)", px, py, target.X, target.Y, plan));
                return false;
            }

            double meshLength = 0.0;
            for (int q = 1; q < path.Length; q++)
                meshLength += ClickAimMath.Len(path[q].X - path[q - 1].X, path[q].Y - path[q - 1].Y);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] mesh path: {0} points, {1:F1} yd for a {2:F1} yd leg{3}",
                path.Length, meshLength, remaining,
                meshLength > remaining + 1.0 ? " (detours around terrain)" : " (nearly straight)"));

            mover.ResetBlocked();

            /* Detour's corners can be 40 yd apart on open ground, and every yard of
             * a click is a yard the aim calibration can be wrong by. Cutting the
             * polyline into <= FollowStepYd hops keeps the error small and means the
             * character is re-aimed every few seconds instead of walking a whole
             * straight segment on one click. It is also what makes the path
             * "followed" rather than "aimed at". */
            WoWPoint[] follow = Resample(path, FollowStepYd);

            int atPoint = 0;
            WoWPoint failed = default(WoWPoint);
            bool complete = true;

            for (int p = 1; p < follow.Length; p++)
            {
                if (ClickAimPilot.WalkTo(mover, follow[p], StepTolerance, MeshPointTimeoutMs, false, true))
                    continue;

                atPoint = p;
                failed = follow[p];
                complete = false;
                break;
            }

            if (complete) return true;

            double sx, sy;
            double sz = ClickAimPositions.Z();
            if (!ClickAimPositions.TryRead(out sx, out sy))
            {
                Logging.Write("[Route] lost the player position; cannot mark the stuck spot");
                return false;
            }
            if (double.IsNaN(sz)) sz = failed.Z;

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] stopped at mesh point {0}/{1} of {2} - ({3:F1}, {4:F1}) was {5:F1} yd away",
                atPoint, follow.Length - 1, plan + 1, failed.X, failed.Y,
                ClickAimMath.Len(failed.X - sx, failed.Y - sy)));

            if (MeshBlackspots.Mark(Navigator.TripperNavigator,
                    ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, sx, sy),
                    sx, sy, sz, BlackspotRadius) == 0 && plan == MaxReplans)
            {
                Logging.Write("[Route] the stuck spot is off the mesh, so re-planning cannot help");
            }
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Route] out of re-plans after {0} attempts; the character is at the waypoint's "
            + "neighbourhood but something keeps blocking it", MaxReplans + 1));
        return false;
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

    /// <summary>
    /// Splits a polyline so no hop is longer than <paramref name="maxStep"/>, keeping
    /// every original corner. A hop shorter than half a step is folded into the next
    /// one, otherwise a corner 2 yd after another produces a click that is all aim
    /// error and no travel.
    /// </summary>
    private static WoWPoint[] Resample(WoWPoint[] path, double maxStep)
    {
        if (path == null || path.Length < 2 || maxStep <= 0.0) return path;

        var outPoints = new System.Collections.Generic.List<WoWPoint>(path.Length * 2);
        outPoints.Add(path[0]);

        for (int i = 1; i < path.Length; i++)
        {
            WoWPoint from = outPoints[outPoints.Count - 1];
            WoWPoint to = path[i];
            double length = ClickAimMath.Len(to.X - from.X, to.Y - from.Y);

            if (length <= maxStep * 0.5)
            {
                // Too short to steer at; jump to the next corner instead and let the
                // hop that follows be the one that gets subdivided.
                if (i == path.Length - 1) outPoints.Add(to);
                continue;
            }

            int steps = (int)Math.Ceiling(length / maxStep);
            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                outPoints.Add(new WoWPoint((float)(from.X + (to.X - from.X) * t),
                                           (float)(from.Y + (to.Y - from.Y) * t),
                                           (float)(from.Z + (to.Z - from.Z) * t)));
            }
        }

        return outPoints.ToArray();
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

            /* Both ends are snapped to the mesh before asking for a path. Detour's own
             * start search only looks 20 yd up and down, so a Z that is a little wrong
             * - and on the x64 client the Z is whatever the reader last saw - makes
             * findNearestPoly return nothing and the request come back Failed. The
             * snap is the difference between "no mesh here" and "the number was off",
             * and MeshQuery.SnapLogged prints which one it was. */
            var snappedFrom = from;
            var snappedTo = to;
            if (!MeshQuery.SnapLogged(nav, mapId, from, "player", out snappedFrom))
                return null;
            if (!MeshQuery.SnapLogged(nav, mapId, to, "goal", out snappedTo))
                return null;
            from = snappedFrom;
            to = snappedTo;

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

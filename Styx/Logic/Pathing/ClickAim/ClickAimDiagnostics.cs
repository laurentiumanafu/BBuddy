using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// Lets the movement be driven on the 64-bit client, where the bot loop is still
/// disabled. Nothing here is part of the normal gameplay path; it exists so that
/// the ported mover can be exercised on the real client without waiting for the
/// whole object model.
///
/// Two triggers, both optional and both off unless asked for:
///
///   1. command line:  CopilotBuddy.exe --clickaim-walk 1977.5,232.0[,2.0]
///   2. a text file:   Settings\clickaim-walk.txt  containing x,y[,tolerance]
///
/// The file is deleted once it has been accepted, so the same coordinates can be
/// requested again. A request is only started when the player position is
/// readable, and it is refused while another walk is running.
/// </summary>
public static class ClickAimDiagnostics
{
    private const int PilotTimeoutMs = 240000;   // a 100 yd click walk needs ~20 s
    private const int PlayerWaitMs = 30000;

    private static int _enabled;
    private static readonly object Gate = new object();
    private static readonly Queue<double[]> Pending = new Queue<double[]>();
    private static bool _walking;

    // A profile route is one job, not a queue of points: it owns its own ordering.
    private static List<RoutePoint>? _pendingRoute;
    private static int _pendingRouteLegs;

    public static string RequestPath
    {
        get
        {
            string dir;
            try { dir = Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "."; }
            catch { dir = "."; }
            return Path.Combine(dir, "clickaim-walk.txt");
        }
    }

    /// <summary>
    /// Called once after a successful attach. Logs the self-test so the 26 checks
    /// are visible in the log even when no bot is started, then arms the triggers.
    /// </summary>
    public static void EnableAfterAttach()
    {
        if (Interlocked.Exchange(ref _enabled, 1) != 0) return;

        string report;
        try { report = ClickAimMath.SelfTest(); }
        catch (Exception ex) { report = "[ClickAim] self-test threw: " + ex.Message; }
        Logging.Write(report);

        try { Logging.Write(NavGridStore.SelfTest()); }
        catch (Exception ex) { Logging.Write("[NavGrid] self-test threw: " + ex.Message); }

        // The bot loop is disabled on x64, but the navmesh engine only needs
        // Navigation.dll (now built for x64) and the mmaps directory.
        bool mesh = false;
        try { mesh = Navigator.EnsureLoadedForDiagnostics(); }
        catch (Exception ex) { Logging.Write("[NavMesh] " + ex.Message); }

        // The DLL resolves mmaps relative to its own file, and it sits next to the
        // exe, so this is the only path that matters - there is no override API.
        string mmapsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "mmaps");
        int mmtiles = 0;
        int thisMap = 0;
        int paramsFiles = 0;
        string paramsForMap = "none";
        try
        {
            if (System.IO.Directory.Exists(mmapsDir))
            {
                string[] names = System.IO.Directory.GetFiles(mmapsDir);

                // Both naming schemes the loader understands. Counting per map
                // matters: a folder-wide number says nothing about whether the map
                // the player is standing on has tiles at all.
                uint mapId = (uint)NavGridStore.MapId;
                string classic = mapId.ToString("000", CultureInfo.InvariantCulture);
                string trinity = mapId.ToString("0000", CultureInfo.InvariantCulture) + "_";
                foreach (string path in names)
                {
                    string name = System.IO.Path.GetFileName(path);
                    if (name.EndsWith(".mmtile", StringComparison.OrdinalIgnoreCase))
                    {
                        mmtiles++;
                        if ((name.Length == 14 && name.StartsWith(classic, StringComparison.Ordinal))
                            || name.StartsWith(trinity, StringComparison.Ordinal))
                            thisMap++;
                    }
                    else if (name.EndsWith(".mmap", StringComparison.OrdinalIgnoreCase))
                    {
                        paramsFiles++;
                        if (name == classic + ".mmap" || name == trinity.TrimEnd('_') + ".mmap")
                            paramsForMap = name;
                    }
                }
            }
        }
        catch (Exception ex) { Logging.Write("[NavMesh] cannot list " + mmapsDir + ": " + ex.Message); }

        uint currentMap = (uint)NavGridStore.MapId;
        Logging.Write("[NavMesh] Navigation.dll loaded: " + (mesh ? "yes" : "no")
                      + " - mmaps read from " + mmapsDir);
        Logging.Write("[NavMesh] " + mmtiles + " tile files in the folder (" + paramsFiles
                      + " params files), " + thisMap + " for map " + currentMap
                      + "; params for this map: " + paramsForMap);
        if (!mesh)
        {
            Logging.Write("[NavMesh] without it, routing falls back to the baked navgrid tiles "
                          + "(Tools/NavGrid) and then to straight-line walking");
        }
        else if (thisMap == 0)
        {
            Logging.Write("[NavMesh] the library loaded, but no tile file in " + mmapsDir
                          + " belongs to map " + currentMap + " - FindPath will return nothing "
                          + "for this map. Run Tools\\UseExistingMmaps.cmd against your mmaps "
                          + "set, or bake this map with make-mmaps.cmd");
        }
        else
        {
            Logging.Write("[MeshProbe] asking Detour for a path from where the player stands; "
                          + "the answer follows in a few seconds");
        }

        Logging.Write("[ClickAim] pilot armed. Write \"x,y\" into " + RequestPath
                      + " to walk there, or start CopilotBuddy with --clickaim-walk x,y");
        Logging.Write("[ClickAim] position source: " + (ClickAimPositions.TryRead(out double px, out double py)
            ? string.Format(CultureInfo.InvariantCulture, "{0} at ({1:F1}, {2:F1})", ClickAimPositions.Source, px, py)
            : "not readable yet"));

        ParseCommandLine();

        var thread = new Thread(Worker);
        thread.IsBackground = true;
        thread.Name = "ClickAimPilot";
        thread.Start();

        if (mesh)
        {
            var probe = new Thread(MeshProbe);
            probe.IsBackground = true;
            probe.Name = "ClickAimMeshProbe";
            probe.Start();
        }
    }

    /// <summary>
    /// Every map id that has a params file in the mmaps folder, in ascending order.
    /// There are a few hundred at most, and it is the only list of "maps this set
    /// actually covers" available without reading the client.
    /// </summary>
    private static List<uint> MapsWithParams(string mmapsDir)
    {
        var ids = new List<uint>();
        try
        {
            if (!System.IO.Directory.Exists(mmapsDir)) return ids;
            foreach (string path in System.IO.Directory.GetFiles(mmapsDir, "*.mmap"))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                uint id;
                if (uint.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    ids.Add(id);
            }
        }
        catch { /* a listing failure is not fatal here */ }
        ids.Sort();
        return ids;
    }

    private static bool TileFileExists(string mmapsDir, uint mapId, int tileX, int tileY)
    {
        string classic = string.Format(CultureInfo.InvariantCulture, "{0:000}{1:00}{2:00}.mmtile",
                                       mapId, tileX, tileY);
        string trinity = string.Format(CultureInfo.InvariantCulture, "{0:0000}_{1:00}_{2:00}.mmtile",
                                       mapId, tileX, tileY);
        return System.IO.File.Exists(System.IO.Path.Combine(mmapsDir, classic))
            || System.IO.File.Exists(System.IO.Path.Combine(mmapsDir, trinity));
    }

    /// <summary>
    /// The map id to route on, given a position.
    ///
    /// <see cref="NavGridStore.MapId"/> is a constant that nothing sets from the
    /// client, so it is right for Kalimdor and wrong everywhere else. This returns it
    /// unchanged when its tile exists, and otherwise the first map in the mmaps
    /// folder that does have a tile for the position - which is right for a character
    /// standing in the Eastern Kingdoms while the bot still thinks "map 1".
    /// </summary>
    internal static uint ResolveMap(uint mapId, double x, double y)
    {
        try
        {
            int tileX, tileY;
            try
            {
                Tripper.Navigation.NativeMethods.WorldToTile((float)x, (float)y, out tileX, out tileY);
            }
            catch
            {
                tileX = (int)((32.0 * 533.33333 - x) / 533.33333);
                tileY = (int)((32.0 * 533.33333 - y) / 533.33333);
            }

            string mmapsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "mmaps");
            if (TileFileExists(mmapsDir, mapId, tileX, tileY)) return mapId;

            List<uint> covering = MapsCoveringTile(mmapsDir, tileX, tileY);
            return covering.Count > 0 ? covering[0] : mapId;
        }
        catch
        {
            return mapId;
        }
    }

    /// <summary>
    /// Which of the covered maps has a tile file for this position?
    ///
    /// <see cref="NavGridStore.MapId"/> defaults to 1 and nothing in the ported code
    /// sets it from the client, so on a character standing in the Eastern Kingdoms
    /// every query would fail with "no polygon" while the Kalimdor tiles sit right
    /// there - the exact shape of failure that looks like a broken mesh. Comparing
    /// the position against the files turns that into a sentence.
    /// </summary>
    private static List<uint> MapsCoveringTile(string mmapsDir, int tileX, int tileY)
    {
        var found = new List<uint>();
        foreach (uint mapId in MapsWithParams(mmapsDir))
        {
            if (TileFileExists(mmapsDir, mapId, tileX, tileY))
                found.Add(mapId);
        }
        return found;
    }

    /// <summary>
    /// Answers one question the file count cannot: does this navmesh actually route?
    ///
    /// It asks the engine for a short path and a long one, both starting where the
    /// player stands, and logs the status, the tile counts and the geometry it got
    /// back. Everything printed here is a value that was read or returned - if the
    /// probe cannot run, it says so instead of implying success.
    ///
    /// When the short path fails the likely cause is named: the tile file for this
    /// position is missing, or the mesh under the player is not walkable. On a stock
    /// mmap set that is nearly always a name/size mismatch, which the numbers below
    /// make obvious.
    /// </summary>
    /// <summary>
    /// Reports what the navmesh holds around a point: how many polygons the query
    /// returns, which areas they are typed as, and whether the point itself is on
    /// the mesh.
    ///
    /// This exists because "NO PATH (status Failed)" with the tile loaded is
    /// ambiguous. A tile can be loaded and hold nothing under the character (a hole
    /// in the geometry, a bridge the extrude step dropped), or hold polygons whose
    /// area the query filter costs out. One query separates the cases, and the
    /// difference decides whether the fix is a rebuild or a filter setting.
    /// </summary>
    private static void ProbePolys(Tripper.Navigation.Navigator nav, uint mapId, System.Numerics.Vector3 at)
    {
        try
        {
            float[] radii = { 5.0f, 25.0f };
            foreach (float r in radii)
            {
                var extents = new System.Numerics.Vector3(r, 30.0f, r);
                Tripper.Navigation.PolygonReference[] polys;
                try { polys = nav.QueryPolygons(mapId, at, extents, 256); }
                catch (Exception ex)
                {
                    Logging.Write("[MeshProbe] polygon query failed: " + ex.Message);
                    return;
                }

                if (polys == null || polys.Length == 0)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[MeshProbe] polygons within {0:F0} yd: none - the tile is loaded but there is "
                        + "no mesh here at all ({1:F1},{2:F1},{3:F1})", r, at.X, at.Y, at.Z));
                    continue;
                }

                var counts = new Dictionary<byte, int>();
                foreach (Tripper.Navigation.PolygonReference poly in polys)
                {
                    byte area;
                    try { if ((nav.GetPolyArea(mapId, poly, out area) & 0x80000000u) != 0) continue; }
                    catch { continue; }

                    int seen;
                    counts.TryGetValue(area, out seen);
                    counts[area] = seen + 1;
                }

                var parts = new List<string>();
                foreach (KeyValuePair<byte, int> kv in counts)
                {
                    string name = kv.Key == (byte)Tripper.Navigation.AreaType.Blackspot ? "blackspot"
                                : kv.Key == (byte)Tripper.Navigation.AreaType.Blocked ? "blocked"
                                : kv.Key == (byte)Tripper.Navigation.AreaType.Water ? "water"
                                : kv.Key == (byte)Tripper.Navigation.AreaType.Ground ? "ground"
                                : "area " + kv.Key.ToString(CultureInfo.InvariantCulture);
                    parts.Add(name + " x" + kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                parts.Sort(StringComparer.Ordinal);

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[MeshProbe] polygons within {0:F0} yd: {1} ({2})", r, polys.Length, string.Join(", ", parts.ToArray())));
            }

            bool onMesh;
            try { onMesh = nav.IsPointOnNavMesh(mapId, at, 3.0f); }
            catch { onMesh = false; }

            Tripper.Navigation.PolygonReference nearest;
            System.Numerics.Vector3 snapped;
            bool found;
            try { found = nav.FindNearestPolyRef(mapId, at, out nearest, out snapped); }
            catch { found = false; snapped = at; }

            if (found)
            {
                double dx = snapped.X - at.X, dy = snapped.Y - at.Y, dz = snapped.Z - at.Z;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[MeshProbe] point on mesh: {0}; nearest polygon is {1:F2} yd away at "
                    + "({2:F1},{3:F1},{4:F1})", onMesh ? "yes" : "no (within 3 yd)",
                    Math.Sqrt(dx * dx + dy * dy + dz * dz), snapped.X, snapped.Y, snapped.Z));
            }
            else
            {
                Logging.Write("[MeshProbe] point on mesh: no; no polygon within the search box - "
                              + "the character is standing where the mesh has a hole");
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[MeshProbe] polygon report failed: " + ex.Message);
        }
    }

    private static void MeshProbe()
    {
        try
        {
            // Let the client settle: attach happens before the world finishes loading.
            Thread.Sleep(3000);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                double px, py;
                if (ClickAimPositions.TryRead(out px, out py)) break;
                Thread.Sleep(2000);
            }

            double x, y;
            if (!ClickAimPositions.TryRead(out x, out y))
            {
                Logging.Write("[MeshProbe] skipped: player position is not readable");
                return;
            }

            uint mapId = (uint)NavGridStore.MapId;
            double z = ClickAimPositions.Z();
            bool zKnown = !double.IsNaN(z);
            if (!zKnown) z = 0.0;

            var from = new System.Numerics.Vector3((float)x, (float)y, (float)z);

            /* Which tile file does the engine expect for this position?
             *
             * Read it from the DLL itself rather than recomputing it here: the two
             * conventions in this codebase are transposed (NavGridFinder.TileOf
             * returns the .map-style pair, the loader names files the other way
             * round), and a log that prints a name the loader would never look for
             * sends the next person hunting a file that is not the problem.
             * Navigation::WorldToTile is what getTileName/getTileFile use. */
            int tileX, tileY;
            try
            {
                Tripper.Navigation.NativeMethods.WorldToTile((float)x, (float)y, out tileX, out tileY);
            }
            catch
            {
                tileX = (int)((32.0 * 533.33333 - x) / 533.33333);
                tileY = (int)((32.0 * 533.33333 - y) / 533.33333);
            }

            int gridX, gridY;
            NavGridFinder.TileOf(x, y, out gridX, out gridY);

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[MeshProbe] player {0:F1},{1:F1},{2:F1} map {3} -> loader tile ({4},{5}) = file "
                + "{6}{7}{8}.mmtile; .map-style pair ({9},{10}){11}", x, y, z, mapId, tileX, tileY,
                mapId.ToString("000"), tileX.ToString("00"), tileY.ToString("00"),
                gridX, gridY, zKnown ? "" : " (z unreadable, probing at 0)"));

            // Does the map the bot believes it is on actually have this tile? If some
            // other map does, that is the answer and the map id is the bug.
            string mmapsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "mmaps");
            List<uint> covering = MapsCoveringTile(mmapsDir, tileX, tileY);
            bool currentCovered = covering.Contains(mapId);
            Logging.Write("[MeshProbe] tile (" + tileX + "," + tileY + ") exists for map(s): "
                          + (covering.Count == 0 ? "none" : string.Join(", ", covering.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray())));
            if (!currentCovered && covering.Count > 0)
            {
                Logging.Write("[MeshProbe] the bot was using map " + mapId
                              + ", which has no tile here; probing map " + covering[0]
                              + " instead. NavGridStore.MapId is a constant, not a read from the "
                              + "client - this is what that costs");
                mapId = covering[0];
            }

            Tripper.Navigation.Navigator nav = Navigator.TripperNavigator;
            nav.EnsureTilesAroundPosition(mapId, from, 2);

            int loadedTiles = -1;
            try { loadedTiles = nav.GetLoadedTilesCount(mapId); }
            catch { /* older DLL without the export */ }

            // Did the file for the player's own grid square load? If the ring around
            // the position brought tiles in but this one is missing, the name the
            // engine derives and the name on disk disagree - that is the single most
            // expensive mistake in this area, so it gets its own line.
            bool tileLoaded = false;
            try { tileLoaded = nav.IsTileLoaded(mapId, tileX, tileY); }
            catch { /* older DLL without the export */ }

            Logging.Write("[MeshProbe] tiles loaded for map " + mapId + ": " + loadedTiles
                          + "; tile (" + tileX + "," + tileY + ") loaded: " + tileLoaded);

            // A loaded tile says the file is there. It does not say that anything
            // walkable exists at this spot, and "NO PATH" cannot tell those apart -
            // it is the same answer for an empty tile, a tile whose polygons the
            // query filter refuses, and a floor twenty yards below the character.
            // Ask Detour what is actually under the player.
            ProbePolys(nav, mapId, from);

            /* Put the probe on the same footing as a real route: snap the player's
             * point onto the mesh first and say how far it moved. If this line
             * reports a large correction, the NO PATH below was a Z problem, not a
             * missing tile - which is exactly the ambiguity that cost a session. */
            var snappedFrom = from;
            bool onMesh = MeshQuery.SnapLogged(nav, mapId, from, "player", out snappedFrom);
            if (onMesh) from = snappedFrom;

            // Two lengths: 40 yd stays inside a few tiles, 120 yd crosses seams and
            // exercises the on-demand loader while the search runs.
            double[] legs = { 40.0, 120.0 };
            double facing;
            if (!ClickAimPositions.TryReadFacing(out facing)) facing = 0.0;
            double dx = Math.Sin(facing), dy = Math.Cos(facing);

            foreach (double leg in legs)
            {
                var to = new System.Numerics.Vector3(
                    (float)(from.X + dx * leg), (float)(from.Y + dy * leg), from.Z);
                nav.EnsureTilesAroundPosition(mapId, to, 1);

                Tripper.Navigation.PathFindResult result;
                try { result = nav.FindPath(mapId, from, to, true); }
                catch (Exception ex)
                {
                    Logging.Write("[MeshProbe] " + leg + " yd threw: " + ex.Message);
                    continue;
                }

                if (result == null || !result.Status.Succeeded
                    || result.Points == null || result.Points.Length < 2)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[MeshProbe] {0:F0} yd: NO PATH (status {1}) - tile {2}{3}{4}.mmtile for map "
                        + "{5} is loaded, so read the polygon report above: 'none' means the mesh is "
                        + "empty here (rebuild), a large snap correction means the Z was wrong, and "
                        + "'blackspot' polygons mean an earlier walk marked this ground bad",
                        leg, result == null ? "null" : result.Status.ToString(),
                        mapId.ToString("000"), tileX.ToString("00"), tileY.ToString("00"), mapId));
                    continue;
                }

                double length = 0.0;
                for (int i = 1; i < result.Points.Length; i++)
                {
                    double ex = result.Points[i].X - result.Points[i - 1].X;
                    double ey = result.Points[i].Y - result.Points[i - 1].Y;
                    double ez = result.Points[i].Z - result.Points[i - 1].Z;
                    length += Math.Sqrt(ex * ex + ey * ey + ez * ez);
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[MeshProbe] {0:F0} yd: mesh path OK - {1} points, {2:F1} yd, {3:F1} ms, "
                    + "ends at ({4:F1},{5:F1},{6:F1})",
                    leg, result.Points.Length, length, result.Elapsed.TotalMilliseconds,
                    result.Points[result.Points.Length - 1].X,
                    result.Points[result.Points.Length - 1].Y,
                    result.Points[result.Points.Length - 1].Z));
            }

            Logging.Write("[MeshProbe] done. A path here is Detour reading the mmaps; "
                          + "walking still depends on the mover, not on this probe.");
        }
        catch (Exception ex)
        {
            Logging.Write("[MeshProbe] threw: " + ex.Message);
        }
    }

    /// <summary>Queues a walk. Safe from any thread; returns false if one is already running.</summary>
    public static bool WalkNow(double x, double y, double tolerance = 2.0)
    {
        lock (Gate)
        {
            if (_walking) return false;
            Pending.Enqueue(new[] { x, y, tolerance });
            return true;
        }
    }

    /// <summary>
    /// Walks <paramref name="yards"/> straight ahead of the character's own facing -
    /// the no-coordinates test. Client convention: facing 0 = +Y, clockwise, so the
    /// offset is (sin, cos).
    /// </summary>
    public static bool WalkRelative(double yards)
    {
        double x, y, facing;
        if (!ClickAimPositions.TryRead(out x, out y))
        {
            Logging.Write("[ClickAim] cannot walk ahead: no player position yet");
            return false;
        }
        if (!ClickAimPositions.TryReadFacing(out facing))
        {
            Logging.Write("[ClickAim] cannot walk ahead: facing is not readable");
            return false;
        }

        double targetX = x + Math.Sin(facing) * yards;
        double targetY = y + Math.Cos(facing) * yards;
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] {0:F0} yd ahead: ({1:F1}, {2:F1}) -> ({3:F1}, {4:F1}) at facing {5:F2} rad",
            yards, x, y, targetX, targetY, facing));
        if (WalkNow(targetX, targetY, 3.0)) return true;

        Logging.Write("[ClickAim] a walk is already running; the request was ignored");
        return false;
    }

    /// <summary>
    /// Loads a profile and queues its waypoint chain. Returns false when the file has
    /// no movement elements or a walk is already running.
    /// </summary>
    public static bool StartProfile(string path, int questId, int maxLegs, bool startNearest, bool wholeFile)
    {
        List<RoutePoint> route;
        try
        {
            route = ProfileRoute.Load(path, questId);
        }
        catch (Exception ex)
        {
            Logging.Write("[Route] could not load " + path + ": " + ex.Message);
            return false;
        }

        if (route.Count == 0)
        {
            Logging.Write("[Route] " + path + " has no Hotspot/RunTo waypoints"
                          + (questId != 0 ? " for quest " + questId : ""));
            return false;
        }

        Logging.Write("[Route] " + Path.GetFileName(path) + ": " + ProfileRoute.Describe(route));

        double px, py;
        bool havePosition = ClickAimPositions.TryRead(out px, out py);

        // A profile's waypoints are separate grind areas plus continent-scale RunTo
        // hops. Unless the whole file was asked for, walk the cluster we are
        // standing in - the alternative is "walk 17,705 yd to Darkshore".
        if (questId == 0 && !wholeFile && havePosition)
        {
            int clusterIndex, clusterCount;
            List<RoutePoint> cluster = ProfileRoute.NearestCluster(route, px, py, out clusterIndex, out clusterCount);
            if (clusterCount > 1 && cluster.Count > 0)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] the file has {0} separate walkable clusters; using #{1} - {2}. "
                    + "Use --profile-all to walk the file as one chain",
                    clusterCount, clusterIndex + 1, ProfileRoute.Describe(cluster)));
                route = cluster;
            }
        }

        if (havePosition)
        {
            int nearest = ProfileRoute.NearestIndex(route, px, py);
            double distance = ClickAimMath.Len(route[nearest].X - px, route[nearest].Y - py);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] nearest waypoint is {0}/{1} at {2:F0} yd; use --profile-nearest to start there",
                nearest + 1, route.Count, distance));

            if (startNearest && nearest > 0)
            {
                route = route.GetRange(nearest, route.Count - nearest);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route] starting at waypoint {0}, {1} waypoints to walk", nearest + 1, route.Count));
            }
        }

        lock (Gate)
        {
            if (_walking || _pendingRoute != null)
            {
                Logging.Write("[Route] a walk is already running; the profile request was ignored");
                return false;
            }
            _pendingRoute = route;
            _pendingRouteLegs = maxLegs;
        }
        return true;
    }

    /// <summary>
    /// Plans a route to a point and prints every waypoint, without touching the
    /// keyboard or the mouse. The question it answers is "is the mesh right?", and
    /// it answers it even while the mover is still being brought up: no clicks, no
    /// camera, no character movement, just Detour reading the tiles.
    /// </summary>
    private static void StartRouteReport(string spec)
    {
        var thread = new Thread(() => RouteReport(spec));
        thread.IsBackground = true;
        thread.Name = "ClickAimRouteReport";
        thread.Start();
    }

    private static void RouteReport(string spec)
    {
        try
        {
            double[] parts = ParseNumbers(spec);
            if (parts.Length < 2)
            {
                Logging.Write("[Route] --clickaim-route wants x,y[,z]: got \"" + spec + "\"");
                return;
            }

            double x, y;
            if (!ClickAimPositions.TryRead(out x, out y))
            {
                Logging.Write("[Route] no player position - is the character in the world?");
                return;
            }

            double z = ClickAimPositions.Z();
            if (double.IsNaN(z)) z = 0.0;

            var nav = Navigator.TripperNavigator;
            if (!nav.IsLoaded)
            {
                Logging.Write("[Route] Navigation.dll is not loaded; nothing to plan with");
                return;
            }

            uint mapId = ResolveMap((uint)NavGridStore.MapId, x, y);
            var from = new System.Numerics.Vector3((float)x, (float)y, (float)z);
            var to = new System.Numerics.Vector3((float)parts[0], (float)parts[1],
                                                 (float)(parts.Length > 2 ? parts[2] : 0.0));

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] plan only, no walking: ({0:F1}, {1:F1}, {2:F1}) -> ({3:F1}, {4:F1}{5}) on map {6}",
                from.X, from.Y, from.Z, to.X, to.Y,
                parts.Length > 2 ? ", " + to.Z.ToString("F1", CultureInfo.InvariantCulture) : ", height unknown",
                mapId));

            nav.EnsureTilesAroundPosition(mapId, from, Navigator.LoadTilesAroundRadius);
            nav.EnsureTilesAroundPosition(mapId, to, Navigator.LoadTilesAroundRadius);

            var snappedFrom = from;
            var snappedTo = to;
            if (!MeshQuery.SnapLogged(nav, mapId, from, "player", out snappedFrom)) return;
            if (!MeshQuery.SnapLogged(nav, mapId, to, "goal", out snappedTo)) return;

            Tripper.Navigation.PathFindResult result = nav.FindPath(mapId, snappedFrom, snappedTo, true);
            if (result == null || !result.Status.Succeeded || result.Points == null || result.Points.Length < 2)
            {
                Logging.Write("[Route] no path (status "
                    + (result == null ? "null" : result.Status.ToString())
                    + "); the probe's polygon report says whether the mesh is empty or the filter refuses it");
                return;
            }

            double straight = ClickAimMath.Len(snappedTo.X - snappedFrom.X, snappedTo.Y - snappedFrom.Y);
            double length = 0.0;
            for (int i = 1; i < result.Points.Length; i++)
                length += ClickAimMath.Len(result.Points[i].X - result.Points[i - 1].X,
                                           result.Points[i].Y - result.Points[i - 1].Y);

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Route] {0} points, {1:F1} yd for a {2:F1} yd straight line ({3:F1} ms)",
                result.Points.Length, length, straight,
                result.Elapsed.TotalMilliseconds));

            int shown = Math.Min(result.Points.Length, 60);
            for (int i = 0; i < shown; i++)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Route]   {0,2}: ({1,9:F1}, {2,9:F1}, {3,7:F1})",
                    i + 1, result.Points[i].X, result.Points[i].Y, result.Points[i].Z));
            }
            if (shown < result.Points.Length)
                Logging.Write("[Route]   ... " + (result.Points.Length - shown) + " more");
        }
        catch (Exception ex)
        {
            Logging.Write("[Route] plan failed: " + ex.Message);
        }
    }

    /// <summary>Every number in a string, separators and spaces ignored.</summary>
    private static double[] ParseNumbers(string text)
    {
        var values = new List<double>();
        if (text == null) return values.ToArray();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool numeric = i < text.Length &&
                (char.IsDigit(text[i]) || text[i] == '-' || text[i] == '+' || text[i] == '.');
            if (numeric && start < 0) start = i;
            if (!numeric && start >= 0)
            {
                double v;
                if (double.TryParse(text.Substring(start, i - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v))
                    values.Add(v);
                start = -1;
            }
        }
        return values.ToArray();
    }

    private static void ParseCommandLine()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();

            // Pre-pass: --clickaim-profile depends on these, and argument order
            // is the user's business, not ours.
            int questId = 0;
            int profileLegs = 0;
            bool startNearest = false;
            bool wholeProfile = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (a.Equals("--profile-quest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out questId);
                else if (a.Equals("--profile-legs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out profileLegs);
                else if (a.Equals("--profile-nearest", StringComparison.OrdinalIgnoreCase))
                    startNearest = true;
                else if (a.Equals("--profile-all", StringComparison.OrdinalIgnoreCase))
                    wholeProfile = true;
                else if (a.Equals("--navtiles", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    NavGridStore.Root = args[i + 1];
                else if (a.Equals("--navmap", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int mapId;
                    if (int.TryParse(args[i + 1], out mapId)) NavGridStore.MapId = mapId;
                }
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                string spec = null;
                string profile = null;
                if (arg.Equals("--clickaim-walk", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) spec = args[i + 1];
                }
                else if (arg.StartsWith("--clickaim-walk=", StringComparison.OrdinalIgnoreCase))
                {
                    spec = arg.Substring("--clickaim-walk=".Length);
                }
                else if (arg.Equals("--clickaim-route", StringComparison.OrdinalIgnoreCase))
                {
                    // Plan and print, never walk. This is the mesh test: the mover
                    // is not in the loop, so what comes back is Detour's opinion and
                    // nothing else.
                    if (i + 1 < args.Length) StartRouteReport(args[i + 1]);
                    continue;
                }
                else if (arg.StartsWith("--clickaim-route=", StringComparison.OrdinalIgnoreCase))
                {
                    StartRouteReport(arg.Substring("--clickaim-route=".Length));
                    continue;
                }
                else if (arg.Equals("--clickaim-profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) profile = args[i + 1];
                }
                else if (arg.StartsWith("--clickaim-profile=", StringComparison.OrdinalIgnoreCase))
                {
                    profile = arg.Substring("--clickaim-profile=".Length);
                }

                if (profile != null)
                {
                    StartProfile(profile, questId, profileLegs, startNearest, wholeProfile);
                    continue;
                }

                if (spec == null) continue;

                TryQueueSpec(spec, "command line");
            }

            Logging.Write("[NavGrid] tiles: " + NavGridStore.Root + " (map " + NavGridStore.MapId + ")");
        }
        catch (Exception ex)
        {
            Logging.Write("[ClickAim] could not read the command line: " + ex.Message);
        }
    }

    /// <summary>
    /// Accepts two forms: "x,y[,tolerance]" for a world position, or a single number
    /// for "that many yards straight ahead", which needs no map orientation.
    /// </summary>
    private static bool TryQueueSpec(string spec, string origin)
    {
        // A profile is a list of waypoints, not a point: "Settings\Ashenvale.xml"
        // or "profile:<path>" in clickaim-walk.txt loads and walks it.
        string trimmed = (spec ?? "").Trim();
        if (trimmed.Length > 4 &&
            (trimmed.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)))
        {
            string path = trimmed.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring("profile:".Length).Trim()
                : trimmed;
            if (path.Length >= 2 && path.StartsWith("\"", StringComparison.Ordinal) &&
                path.EndsWith("\"", StringComparison.Ordinal))
            {
                path = path.Substring(1, path.Length - 2);
            }
            Logging.Write("[Route] " + origin + " request: profile " + path);
            return StartProfile(path, 0, 0, false, false);
        }

        // "route:x,y[,z]" in clickaim-walk.txt plans and prints without walking,
        // which is how the mesh gets checked while the mover is still being worked on.
        if (trimmed.StartsWith("route:", StringComparison.OrdinalIgnoreCase))
        {
            Logging.Write("[Route] " + origin + " request: plan only, no walking");
            StartRouteReport(trimmed.Substring("route:".Length));
            return true;
        }

        double yards;
        if (TryParseRelative(spec, out yards))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] {0} request: {1:F0} yd ahead", origin, yards));
            return WalkRelative(yards);
        }

        double x, y, tol;
        if (!TryParseSpec(spec, out x, out y, out tol))
        {
            Logging.Write("[ClickAim] ignoring \"" + spec + "\" from the "
                          + origin + ": expected x,y[,tolerance] or a distance in yards");
            return false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] {0} request: ({1:F1}, {2:F1}), tolerance {3:F1} yd", origin, x, y, tol));
        if (WalkNow(x, y, tol)) return true;

        Logging.Write("[ClickAim] a walk is already running; the request was ignored");
        return false;
    }

    private static bool TryParseRelative(string text, out double yards)
    {
        yards = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.IndexOf(',') >= 0) return false;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out yards)) return false;
        if (yards < 5.0 || yards > 120.0) return false;
        return true;
    }

    private static bool TryParseSpec(string text, out double x, out double y, out double tolerance)
    {
        x = 0;
        y = 0;
        tolerance = 2.0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Trim().Split(',');
        if (parts.Length < 2 || parts.Length > 3) return false;

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
        if (parts.Length == 3 &&
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance))
            return false;

        if (Math.Abs(x) >= 100000.0 || Math.Abs(y) >= 100000.0) return false;
        if (tolerance <= 0.0 || tolerance > 50.0) tolerance = 2.0;
        return true;
    }

    private static void Worker()
    {
        string lastSeen = null;

        while (true)
        {
            try
            {
                CheckRequestFile(ref lastSeen);

                List<RoutePoint> route = null;
                int routeLegs = 0;
                double[] request = null;
                lock (Gate)
                {
                    if (!_walking && _pendingRoute != null)
                    {
                        route = _pendingRoute;
                        routeLegs = _pendingRouteLegs;
                        _pendingRoute = null;
                        _walking = true;
                    }
                    else if (!_walking && Pending.Count > 0)
                    {
                        request = Pending.Dequeue();
                        _walking = true;
                    }
                }

                if (route != null)
                {
                    try
                    {
                        RouteRunner.Run(route, routeLegs);
                    }
                    finally
                    {
                        lock (Gate) { _walking = false; }
                    }
                }
                else if (request != null)
                {
                    try
                    {
                        RunWalk(request[0], request[1], request[2]);
                    }
                    finally
                    {
                        lock (Gate) { _walking = false; }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Write("[ClickAim] pilot error: " + ex.Message);
                lock (Gate) { _walking = false; }
            }

            Thread.Sleep(500);
        }
    }

    private static void CheckRequestFile(ref string lastSeen)
    {
        string path = RequestPath;
        if (!File.Exists(path))
        {
            lastSeen = null;
            return;
        }

        string text;
        try { text = File.ReadAllText(path).Trim(); }
        catch (IOException) { return; }          // the editor is still writing it
        catch (UnauthorizedAccessException) { return; }

        if (string.IsNullOrEmpty(text) || text == lastSeen) return;

        lastSeen = text;
        if (!TryQueueSpec(text, "file")) return;

        try { File.Delete(path); }
        catch { /* the next request can reuse the same text */ }
    }

    private static void RunWalk(double x, double y, double tolerance)
    {
        int waited = 0;
        double startDistance = double.NaN;
        while (waited < PlayerWaitMs)
        {
            double px, py;
            if (ClickAimPositions.TryRead(out px, out py))
            {
                startDistance = ClickAimMath.Len(x - px, y - py);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] from ({0:F1}, {1:F1}) [{2}] to ({3:F1}, {4:F1}) - {5:F1} yd",
                    px, py, ClickAimPositions.Source, x, y, startDistance));
                break;
            }
            Thread.Sleep(500);
            waited += 500;
        }

        if (waited >= PlayerWaitMs)
        {
            Logging.Write("[ClickAim] no player position after " + (PlayerWaitMs / 1000)
                          + " s; is the character in the world?");
            return;
        }

        /* Route on the mmaps when they are there.
         *
         * A single click walks a straight line, and a straight line between two
         * points in Ashenvale is usually a tree, a rock or a cliff. The mesh turns
         * the same request into a polyline that goes around them - which is the
         * entire reason the mmaps are loaded. Falls back to the straight click only
         * when no mesh can be queried, and says which one it used.
         */
        bool mesh = false;
        try { mesh = RouteRunner.MeshReady(); }
        catch (Exception ex) { Logging.Write("[ClickAim] mesh probe failed: " + ex.Message); }

        if (mesh)
        {
            double z = ClickAimPositions.Z();
            if (double.IsNaN(z)) z = 0.0;

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] routing {0:F1} yd on the mesh; a straight click is only the fallback",
                startDistance));

            var route = new List<RoutePoint>
            {
                new RoutePoint { X = x, Y = y, Z = z, Kind = "WalkTo", Label = "single point" }
            };
            RouteRunner.Run(route, 0);
            return;
        }

        Logging.Write("[ClickAim] no mesh for this map - walking the straight line");
        ClickAimPilot.WalkTo(x, y, tolerance, PilotTimeoutMs, true);
    }
}

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
        try
        {
            if (System.IO.Directory.Exists(mmapsDir))
                mmtiles = System.IO.Directory.GetFiles(mmapsDir, "*.mmtile").Length;
        }
        catch (Exception ex) { Logging.Write("[NavMesh] cannot list " + mmapsDir + ": " + ex.Message); }

        Logging.Write("[NavMesh] Navigation.dll loaded: " + (mesh ? "yes" : "no")
                      + " - mmaps read from " + mmapsDir + " (map " + NavGridStore.MapId + "): "
                      + (mmtiles > 0 ? mmtiles + " tile files" : "none"));
        if (!mesh)
        {
            Logging.Write("[NavMesh] without it, routing falls back to the baked navgrid tiles "
                          + "(Tools/NavGrid) and then to straight-line walking");
        }
        else if (mmtiles == 0)
        {
            Logging.Write("[NavMesh] the library loaded but there are no mmaps for this map, so "
                          + "FindPath returns nothing - run Tools\\NavMeshBuilder\\make-mmaps.cmd "
                          + "to bake them from your ADT export");
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
        while (waited < PlayerWaitMs)
        {
            double px, py;
            if (ClickAimPositions.TryRead(out px, out py))
            {
                double start = ClickAimMath.Len(x - px, y - py);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] from ({0:F1}, {1:F1}) [{2}] to ({3:F1}, {4:F1}) - {5:F1} yd",
                    px, py, ClickAimPositions.Source, x, y, start));
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

        ClickAimPilot.WalkTo(x, y, tolerance, PilotTimeoutMs, true);
    }
}

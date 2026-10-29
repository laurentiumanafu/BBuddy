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

    private static void ParseCommandLine()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                string spec = null;
                if (arg.Equals("--clickaim-walk", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) spec = args[i + 1];
                }
                else if (arg.StartsWith("--clickaim-walk=", StringComparison.OrdinalIgnoreCase))
                {
                    spec = arg.Substring("--clickaim-walk=".Length);
                }

                if (spec == null) continue;

                double x, y, tol;
                if (TryParseSpec(spec, out x, out y, out tol))
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] command line walk to ({0:F1}, {1:F1}), tolerance {2:F1} yd", x, y, tol));
                    WalkNow(x, y, tol);
                }
                else
                {
                    Logging.Write("[ClickAim] --clickaim-walk expects x,y[,tolerance]; got \"" + spec + "\"");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[ClickAim] could not read the command line: " + ex.Message);
        }
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

                double[] request = null;
                lock (Gate)
                {
                    if (Pending.Count > 0 && !_walking)
                    {
                        request = Pending.Dequeue();
                        _walking = true;
                    }
                }

                if (request != null)
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

        double x, y, tol;
        if (!TryParseSpec(text, out x, out y, out tol))
        {
            lastSeen = text;
            Logging.Write("[ClickAim] ignoring \"" + text + "\": expected x,y[,tolerance]");
            return;
        }

        lastSeen = text;
        if (!WalkNow(x, y, tol))
        {
            Logging.Write("[ClickAim] a walk is already running; the request was ignored");
            return;
        }

        try { File.Delete(path); }
        catch { /* the next request can reuse the same text */ }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] file walk to ({0:F1}, {1:F1}), tolerance {2:F1} yd", x, y, tol));
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

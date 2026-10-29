using System;
using System.Globalization;
using System.Threading;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// Drives <see cref="ClickAimMover"/> without the behaviour tree.
///
/// Why this exists: on the 64-bit client <c>TreeRoot.Start()</c> still refuses to
/// start a bot, because the object model is not complete. But the movement only
/// needs one thing from the reader - the player's X and Y - and that is ported and
/// field-confirmed. So this lets the movement be exercised from the bot process
/// today, on the same client and by the same code path the tree will use later.
/// </summary>
public static class ClickAimPilot
{
    private static int _selfTestLogged;

    /// <summary>
    /// Walks to a world X/Y and blocks until it arrives, gives up or times out.
    /// Intended for a console/UI action, not for the bot thread.
    /// </summary>
    public static bool WalkTo(double x, double y, double tolerance = 2.0, int timeoutMs = 300000,
                              bool verbose = true)
    {
        LogSelfTestOnce();
        return WalkTo(new ClickAimMover(), new WoWPoint((float)x, (float)y, 0f), tolerance, timeoutMs, verbose);
    }

    /// <summary>
    /// Same walk with a caller-owned mover, so a route can reuse one mover (and one
    /// calibration read) for every leg instead of building one per waypoint.
    /// </summary>
    public static bool WalkTo(ClickAimMover mover, WoWPoint goal, double tolerance, int timeoutMs,
                              bool verbose)
    {
        if (mover == null) throw new ArgumentNullException(nameof(mover));

        int started = Environment.TickCount;
        int lastReport = started;

        while (unchecked(Environment.TickCount - started) < timeoutMs)
        {
            mover.MoveTowards(goal);

            if (mover.LastRemaining <= tolerance)
            {
                mover.MoveStop();
                if (verbose)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] arrived: {0:F2} yd remaining, {1} legs, {2} runaways",
                        mover.LastRemaining, mover.Legs, mover.Runaways));
                }
                return true;
            }

            if (verbose && unchecked(Environment.TickCount - lastReport) >= 15000)
            {
                lastReport = Environment.TickCount;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] {0:F1} yd to go, {1} legs, {2} runaways",
                    mover.LastRemaining, mover.Legs, mover.Runaways));
            }

            Thread.Sleep(50);
        }

        mover.MoveStop();
        if (verbose)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] gave up with {0:F1} yd remaining after {1} legs",
                mover.LastRemaining, mover.Legs));
        }
        return false;
    }

    /// <summary>The 26-check report belongs in the log once per process, not once per leg.</summary>
    public static void LogSelfTestOnce()
    {
        if (Interlocked.Exchange(ref _selfTestLogged, 1) != 0) return;
        try { Logging.Write(ClickAimMath.SelfTest()); }
        catch (Exception ex) { Logging.Write("[ClickAim] self-test threw: " + ex.Message); }
    }
}

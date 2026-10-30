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
        return WalkTo(mover, goal, tolerance, timeoutMs, verbose, false);
    }

    /// <summary>
    /// Same walk, but returns as soon as the mover has spent its detour budget.
    ///
    /// A caller that can re-plan wants this: "the character cannot get there by
    /// clicking" is information, and waiting out the timeout turns it into a minute
    /// of standing against a wall. The default overload keeps waiting, because a
    /// caller with no mesh to re-plan on has nothing better to do with the time.
    /// </summary>
    public static bool WalkTo(ClickAimMover mover, WoWPoint goal, double tolerance, int timeoutMs,
                              bool verbose, bool stopWhenBlocked)
    {
        if (mover == null) throw new ArgumentNullException(nameof(mover));

        int started = Environment.TickCount;
        int lastReport = started;

        /* A walk that stops making progress must stop itself.
         *
         * On 2026-10-29 two walks had to be killed by hand: one cycled through six
         * click heights at 16.8 yd without moving, and the log filled with "2 clicks
         * in a row moved nothing" for minutes. Nothing in that loop was ever going to
         * end - every component thought it was making its next attempt. So: remember
         * the best distance reached; if it has not improved by two yards in twenty
         * five seconds, stop and say so. That is a statement about the walk, not about
         * one click, and it is the caller's business to re-plan from there. */
        double bestRemaining = double.MaxValue;
        int lastProgressMs = started;
        const double ProgressYd = 2.0;
        const int StallMs = 25000;

        while (unchecked(Environment.TickCount - started) < timeoutMs)
        {
            mover.MoveTowards(goal);

            if (stopWhenBlocked && mover.Blocked)
            {
                /* Read the reason BEFORE MoveStop, which clears it. "Refused" and "dead
                 * clicks" are different failures: a refusal is the camera being too far
                 * off to click safely, and re-planning will not fix it. */
                bool refused = mover.AimGaveUp;
                mover.MoveStop();
                if (verbose)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] blocked with {0:F1} yd remaining, {1} legs, {2} detours spent"
                        + "{3}",
                        mover.LastRemaining, mover.Legs, mover.Detours,
                        refused
                            ? " - the aim was REFUSED (camera more than 25 deg off the point), so no "
                              + "click was made. Point the camera at the goal, or steer with the keys."
                            : ""));
                }
                return false;
            }

            if (mover.LastRemaining < bestRemaining - ProgressYd)
            {
                bestRemaining = mover.LastRemaining;
                lastProgressMs = Environment.TickCount;
            }
            else if (unchecked(Environment.TickCount - lastProgressMs) > StallMs)
            {
                mover.MoveStop();
                if (verbose)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] no progress for {0:F0} s: stuck at {1:F1} yd from the point "
                        + "(best was {2:F1}). Stopped instead of clicking at it forever.",
                        (Environment.TickCount - lastProgressMs) / 1000.0,
                        mover.LastRemaining, bestRemaining));
                }
                return false;
            }

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

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
                              bool verbose, bool stopWhenBlocked, bool keepMoving = false)
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
         * one click, and it is the caller's business to re-plan from there.
         *
         * Since the 19:07 run there is one more thing before that timer: the sidestep
         * below. Stopping is the last resort, not the first answer to an obstacle. */
        double bestRemaining = double.MaxValue;
        int lastProgressMs = started;
        int sidesteps = 0;
        const double ProgressYd = 2.0;
        const int StallMs = 25000;

        /* A click-walk goes where it is pointed and stops at the first thing it meets, and on
         * this client a click that meets something simply ends the walk. The 19:07 run walked
         * 11 yd of a 37 yd leg, stood at 25.5 yd from the point for 25 s, and the grind gave up
         * on the leg: nothing in the loop was ever going to move it, because every component
         * thought it was making its next attempt.
         *
         * What a player does there is take a step to the side and go on. So before the stall
         * timer or the detour budget ends the walk, sidestep: aim at a point a few yards off
         * the straight line, walk that, and ask for the goal again from the new position. The
         * first step goes to the right, the second to the left, so a wall on one side is not
         * tested twice. */

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

            /* The mover says it has spent its detours on this goal: that is a statement about
             * where the character is standing, not about the goal, so step aside and try again. */
            if (mover.Blocked && !stopWhenBlocked && TrySidestep(mover, goal, ref sidesteps, verbose))
            {
                lastProgressMs = Environment.TickCount;
                continue;
            }

            if (mover.LastRemaining < bestRemaining - ProgressYd)
            {
                bestRemaining = mover.LastRemaining;
                lastProgressMs = Environment.TickCount;
            }
            else if (unchecked(Environment.TickCount - lastProgressMs) > SidestepAfterMs
                     && TrySidestep(mover, goal, ref sidesteps, verbose))
            {
                lastProgressMs = Environment.TickCount;
                continue;
            }
            else if (unchecked(Environment.TickCount - lastProgressMs) > StallMs)
            {
                mover.MoveStop();
                if (verbose)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] no progress for {0:F0} s: stuck at {1:F1} yd from the point "
                        + "(best was {2:F1}) after {3} sidestep(s). Stopped instead of clicking at it "
                        + "forever.",
                        (Environment.TickCount - lastProgressMs) / 1000.0,
                        mover.LastRemaining, bestRemaining, sidesteps));
                }
                return false;
            }

            if (mover.LastRemaining <= tolerance)
            {
                /* Arriving at an INTERMEDIATE point is not a reason to stop walking.
                 * Releasing the forward key every 30 yd is what made the 13:11 walk
                 * choppy: stop, re-press, stop, re-press, for the length of the route.
                 * The caller that owns the next waypoint passes keepMoving. */
                if (!keepMoving) mover.MoveStop();
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

    /// <summary>How long without two yards of progress before a sidestep is tried.</summary>
    private const int SidestepAfterMs = 8000;

    /// <summary>Sidesteps allowed per walk: right, left, right, and then it is a wall.</summary>
    private const int MaxSidesteps = 3;

    /// <summary>How far to the side a sidestep aims, in yards.</summary>
    private const double SidestepYd = 7.0;

    /// <summary>How long one sidestep may take before the walk resumes toward the goal.</summary>
    private const int SidestepMs = 5000;

    /// <summary>Space: jump. A one-yard lip is what stops a click-walk most often.</summary>
    private const int JumpVk = 0x20;

    /// <summary>
    /// Steps around whatever is between the character and the goal: aims at a point
    /// <see cref="SidestepYd"/> yards off the straight line, on alternating sides, walks it, and
    /// leaves the mover ready to dispatch toward the real goal again. Returns false (and says so
    /// in <c>report</c>) when there is nothing to step from or the sidestep budget is spent.
    /// </summary>
    private static bool TrySidestep(ClickAimMover mover, WoWPoint goal, ref int sidesteps, bool verbose)
    {
        if (sidesteps >= MaxSidesteps) return false;
        sidesteps++;

        string report;
        bool ok = Sidestep(mover, goal, sidesteps, out report);
        if (verbose) Logging.Write("[ClickAim] " + report);
        return ok;
    }

    private static bool Sidestep(ClickAimMover mover, WoWPoint goal, int attempt, out string report)
    {
        report = "";
        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            report = "a sidestep was wanted but there is no player position to take it from";
            return false;
        }

        double dx = goal.X - px;
        double dy = goal.Y - py;
        double len = ClickAimMath.Len(dx, dy);
        if (len < 1.0)
        {
            report = "a sidestep was wanted but the goal is on top of the character";
            return false;
        }
        dx /= len;
        dy /= len;

        double side = (attempt % 2 == 1) ? 1.0 : -1.0;   // right first, then left
        var to = new WoWPoint((float)(px - dy * side * SidestepYd),
                              (float)(py + dx * side * SidestepYd),
                              goal.Z);

        /* "Cannot get there" was about the last position: forget it, or the mover will not
         * dispatch a click at all. */
        mover.ClearBlock();

        /* A jump is free and it clears the small things a click-walk trips on - a kerb, a
         * stump, the lip of a hill. It is also what a player presses when they are stuck. */
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = Styx.WoWInternals.ObjectManager.Wow.WindowHandle; } catch { }
        if (hwnd != IntPtr.Zero) ClickAimInput.TapKey(hwnd, JumpVk, 40);

        int started = Environment.TickCount;
        while (unchecked(Environment.TickCount - started) < SidestepMs)
        {
            mover.MoveTowards(to);
            if (mover.Blocked) break;
            Thread.Sleep(60);
        }

        double left = mover.LastRemaining;
        mover.ClearBlock();
        report = string.Format(CultureInfo.InvariantCulture,
            "stepped {0:F0} yd to the {1} to get around something in the way (sidestep {2}, {3:F1} yd left of it)",
            SidestepYd, side > 0 ? "right" : "left", attempt, left);
        return true;
    }

    /// <summary>The 26-check report belongs in the log once per process, not once per leg.</summary>
    public static void LogSelfTestOnce()
    {
        if (Interlocked.Exchange(ref _selfTestLogged, 1) != 0) return;
        try { Logging.Write(ClickAimMath.SelfTest()); }
        catch (Exception ex) { Logging.Write("[ClickAim] self-test threw: " + ex.Message); }
    }
}

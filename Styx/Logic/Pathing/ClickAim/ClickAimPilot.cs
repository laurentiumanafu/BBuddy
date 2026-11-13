using System;
using System.Collections.Generic;
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
                              bool verbose, bool stopWhenBlocked, bool keepMoving = false, Func<bool>? interrupt = null)
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
        var attemptedEscapes = new List<WoWPoint>();
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
            // A route-owner interruption can also occur inside a sidestep.
            if (RouteRunner.RequireMeshDuringSession && RouteRunner.LastRunInterrupted)
            {
                mover.MoveStop();
                return false;
            }
            /* A stop is immediate: let go of the movement key, stop the mover, and say the walk
             * did not arrive. Everything above this line would otherwise walk the whole leg. */
            if (ClickAimAbort.Requested)
            {
                mover.MoveStop();
                ClickAimKeySteer.ReleaseHeld();
                if (verbose) Logging.Write("[ClickAim] the walk was stopped on request");
                return false;
            }

            if (interrupt != null && interrupt())
            {
                ClickAimKeySteer.ReleaseHeld();
                mover.MoveStop();
                if (verbose) Logging.Write("[ClickAim] route interrupted during the leg");
                return false;
            }

            // The callback may have spent time scanning while Stop was pressed.
            if (ClickAimAbort.Requested)
            {
                ClickAimKeySteer.ReleaseHeld();
                mover.MoveStop();
                return false;
            }
            // A resampled mesh corner can already be within the arrival radius when
            // the previous point completes. Do not steer back toward it first: the
            // mover's steering tick may stop and turn even though it is already reached.
            if (mover.KeyMode && ClickAimPositions.TryRead(out double arrivalX, out double arrivalY))
            {
                double arrivalDistance = ClickAimMath.Len(goal.X - arrivalX, goal.Y - arrivalY);
                if (WithinArrivalRadius(arrivalDistance, tolerance))
                {
                    if (!keepMoving) mover.MoveStop();
                    if (verbose) Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] arrived before steering: {0:F2} yd remaining", arrivalDistance));
                    return true;
                }
            }
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
             * where the character is standing, not about the goal, so step aside and try again.
             * When the sidesteps are spent too, say so now instead of calling into a mover that
             * has already refused to aim for another twenty-five seconds. */
            if (mover.Blocked && !stopWhenBlocked)
            {
                if (TrySidestep(mover, goal, ref sidesteps, attemptedEscapes, verbose, interrupt))
                {
                    lastProgressMs = Environment.TickCount;
                    continue;
                }

                double left = mover.LastRemaining;
                mover.MoveStop();
                if (verbose)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] blocked with {0:F1} yd remaining and {1} sidestep(s) did not get "
                        + "around it - giving up on this leg", left, sidesteps));
                }
                return false;
            }

            if (mover.LastRemaining < bestRemaining - ProgressYd)
            {
                bestRemaining = mover.LastRemaining;
                lastProgressMs = Environment.TickCount;
            }
            else if (unchecked(Environment.TickCount - lastProgressMs) > SidestepAfterMs
                     && TrySidestep(mover, goal, ref sidesteps, attemptedEscapes, verbose, interrupt))
            {
                lastProgressMs = Environment.TickCount;
                continue;
            }
            else if (unchecked(Environment.TickCount - lastProgressMs) > StallMs ||
                     (RouteRunner.RequireMeshDuringSession && sidesteps >= MaxSidesteps &&
                      unchecked(Environment.TickCount - lastProgressMs) > SidestepAfterMs))
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

    internal static bool WithinArrivalRadius(double distance, double tolerance)
        => double.IsFinite(distance) && double.IsFinite(tolerance) && distance >= 0 && tolerance >= 0 && distance <= tolerance;

    /// <summary>How long without two yards of progress before a sidestep is tried.</summary>
    private const int SidestepAfterMs = 8000;

    /// <summary>Three bounded escape searches per walk; exhaustion does not prove a wall.</summary>
    private const int MaxSidesteps = 3;

    /// <summary>How long one sidestep may take before the walk resumes toward the goal.</summary>
    private const int SidestepMs = 5000;

    /// <summary>Space: jump. A one-yard lip is what stops a click-walk most often.</summary>
    private const int JumpVk = 0x20;

    /// <summary>
    /// Tries bounded escape candidates on alternating sides and behind the player,
    /// validating every candidate against the mesh during a mesh-owned session, and
    /// leaves the mover ready to dispatch toward the real goal again. Returns false (and says so
    /// in <c>report</c>) when there is nothing to step from or the sidestep budget is spent.
    /// </summary>
    private static bool TrySidestep(ClickAimMover mover, WoWPoint goal, ref int sidesteps,
                                    List<WoWPoint> attemptedEscapes, bool verbose, Func<bool>? interrupt)
    {
        if (sidesteps >= MaxSidesteps) return false;
        sidesteps++;

        string report;
        bool ok = Sidestep(mover, goal, sidesteps, attemptedEscapes, interrupt, out report);
        if (verbose || RouteRunner.RequireMeshDuringSession) Logging.Write("[ClickAim] " + report);
        return ok;
    }

    private static bool Sidestep(ClickAimMover mover, WoWPoint goal, int attempt,
                                 List<WoWPoint> attemptedEscapes, Func<bool>? interrupt, out string report)
    {
        report = "";
        // Stop before sampling: a held forward key must not move the player
        // while a ray for their previous position is being checked.
        mover.MoveStop();
        ClickAimKeySteer.ReleaseHeld();
        var sessionMemory = Styx.WoWInternals.ObjectManager.Wow;
        var sessionReader = Styx.WoWInternals.ObjectManager.WowB;
        bool SessionChanged() => RouteRunner.RequireMeshDuringSession &&
            (sessionMemory == null || sessionReader == null || Styx.WoWInternals.ObjectManager.Wow != sessionMemory ||
             Styx.WoWInternals.ObjectManager.WowB != sessionReader);
        if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
        { report = "sidestep interrupted before sampling"; return false; }
        if (!ClickAimPositions.TryRead(out double px, out double py))
        { report = "sidestep withheld: player position unreadable"; return false; }
        double z = ClickAimPositions.Z();
        if (!double.IsFinite(z))
        {
            if (RouteRunner.RequireMeshDuringSession || !float.IsFinite(goal.Z))
            { report = "sidestep withheld: player height unreadable"; return false; }
            z = goal.Z;
        }

        var candidates = ClickAimEscape.Candidates(px, py, goal.X, goal.Y, (float)z, attempt);
        WoWPoint to = default;
        bool found = false;
        int checkedCount = 0;
        foreach (WoWPoint candidate in candidates)
        {
            if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
            { report = "sidestep interrupted during mesh checks"; return false; }
            bool tried = false;
            foreach (WoWPoint prior in attemptedEscapes)
                if (ClickAimMath.Len(candidate.X - prior.X, candidate.Y - prior.Y) < 0.75) { tried = true; break; }
            if (tried) continue;
            checkedCount++;
            if (!RouteRunner.RequireMeshDuringSession || RouteRunner.ValidateGatherSidestep(px, py, candidate))
            { to = candidate; found = true; break; }
        }
        if (!found)
        {
            report = "sidestep " + attempt + " withheld: none of " + checkedCount +
                " bounded escape segments was validated on the current mesh";
            return false;
        }
        // The owner callback can take time. Do not use a ray from an old
        // position, an altered map/session, or a cancelled walk.
        if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()) ||
            !ClickAimPositions.TryRead(out double nowX, out double nowY) ||
            ClickAimMath.Len(nowX - px, nowY - py) > 0.5 ||
            (RouteRunner.RequireMeshDuringSession && !RouteRunner.ValidateGatherSidestep(nowX, nowY, to)))
        { report = "sidestep withheld: session, position or mesh validation changed"; return false; }
        if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
        { report = "sidestep interrupted after mesh validation"; return false; }

        attemptedEscapes.Add(to);
        mover.ClearBlock();
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = Styx.WoWInternals.ObjectManager.Wow.WindowHandle; } catch { }
        bool jumped = false;
        if (mover.KeyMode) ClickAimKeySteer.BeginMeshPoint();
        else if (hwnd != IntPtr.Zero) { ClickAimInput.TapKey(hwnd, JumpVk, 40); jumped = true; }
        double requested = ClickAimMath.Len(to.X - px, to.Y - py);
        double tolerance = Math.Max(0.6, Math.Min(1.5, requested * 0.3));
        int started = Environment.TickCount;
        bool oldRequireFacing = mover.RequireFacingForKeyMode;
        bool oldTurnInPlace = mover.TurnInPlaceForKeyMode;
        if (RouteRunner.RequireMeshDuringSession)
        {
            mover.RequireFacingForKeyMode = true;
            mover.TurnInPlaceForKeyMode = true;
        }
        try
        {
            while (unchecked(Environment.TickCount - started) < SidestepMs)
            {
                if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
                {
                    mover.MoveStop();
                    ClickAimKeySteer.ReleaseHeld();
                    report = "sidestep interrupted during movement";
                    return false;
                }
                if (!ClickAimPositions.TryRead(out double sx, out double sy))
                { mover.MoveStop(); report = "sidestep stopped: player position unreadable"; return false; }
                if (RouteRunner.RequireMeshDuringSession)
                {
                    // A validated ray is not a license to follow an arbitrary
                    // steering arc. Stop if the actual path leaves its corridor.
                    double cross = Math.Abs((sx - px) * (to.Y - py) - (sy - py) * (to.X - px)) / requested;
                    if (!double.IsFinite(cross) || cross > 0.75)
                    { report = "sidestep stopped: movement deviated from the validated segment"; return false; }
                }
                if (ClickAimMath.Len(to.X - sx, to.Y - sy) <= tolerance) break;
                mover.MoveTowards(to);
                if (mover.Blocked) break;
                // Wait until steering has actually held forward; a stationary jump
                // before the turn cannot help clear a lip in the escape direction.
                if (!jumped && mover.KeyMode && ClickAimKeySteer.KeyDown && hwnd != IntPtr.Zero)
                {
                    if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
                    { mover.MoveStop(); ClickAimKeySteer.ReleaseHeld(); report = "sidestep interrupted before jump"; return false; }
                    ClickAimInput.TapKey(hwnd, JumpVk, 40);
                    jumped = true;
                }
                Thread.Sleep(60);
            }
        }
        finally
        {
            mover.MoveStop();
            ClickAimKeySteer.ReleaseHeld();
            mover.RequireFacingForKeyMode = oldRequireFacing;
            mover.TurnInPlaceForKeyMode = oldTurnInPlace;
        }
        if (ClickAimAbort.Requested || RouteRunner.StopRequested || SessionChanged() || (interrupt != null && interrupt()))
        { report = "sidestep interrupted after movement"; return false; }
        if (!ClickAimPositions.TryRead(out double endX, out double endY))
        { report = "sidestep stopped: final position unreadable"; return false; }
        double moved = ClickAimMath.Len(endX - px, endY - py);
        mover.ClearBlock();
        report = string.Format(CultureInfo.InvariantCulture,
            "sidestep {0}: {1} candidate ({2:F1}, {3:F1}), requested {4:F1} yd, moved {5:F1} yd; physical clearance is unverified",
            attempt, RouteRunner.RequireMeshDuringSession ? "mesh-validated" : "unvalidated", to.X, to.Y, requested, moved);
        return double.IsFinite(moved) && moved >= 0.75;
    }

    /// <summary>The 26-check report belongs in the log once per process, not once per leg.</summary>
    public static void LogSelfTestOnce()
    {
        if (Interlocked.Exchange(ref _selfTestLogged, 1) != 0) return;
        try { Logging.Write(ClickAimMath.SelfTest()); }
        catch (Exception ex) { Logging.Write("[ClickAim] self-test threw: " + ex.Message); }
    }
}

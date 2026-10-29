using System;

namespace Styx.Logic.Pathing;

/// <summary>
/// The measured camera/click model, ported verbatim from the field tool
/// (Tools/WowBClickAim/aimmath.h). Pure functions: no memory, no input, no
/// state. Everything here was derived from field runs on build 70291 and the
/// numbers in the comments are measurements, not estimates.
///
/// Why a model at all: click-to-move is immediate-mode on this client. There is
/// no destination field to write, and the client reads the click position
/// relative to the camera, which nothing in this build exposes. So the bot
/// measures how far a click turns the camera, and then closes the loop on the
/// character's own position after every leg.
/// </summary>
public static class ClickAimMath
{
    // Named "Kind" because ClickAimParams.TurnKey is a bool (the key channel is
    // enabled); two meanings for one identifier is a trap for the next reader.
    public const int TurnKindNone = 0;
    public const int TurnKindDrag = 1;
    public const int TurnKindKey = 2;

    /// <summary>Wraps an angle to (-180, 180].</summary>
    public static double WrapDeg(double d)
    {
        while (d > 180.0) d -= 360.0;
        while (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>World heading of a delta: 0 = +X, 90 = +Y, matching the client.</summary>
    public static double HeadingDeg(double dx, double dy)
    {
        return Math.Atan2(dy, dx) * (180.0 / Math.PI);
    }

    public static double Len(double dx, double dy)
    {
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Half the angular width that a click can reach, from the measured
    /// sensitivity. Deliberately inside the geometric limit: a leg that
    /// under-turns costs one extra leg, a leg that aims past the screen edge
    /// costs a whole walk in the wrong direction.
    /// </summary>
    public static double ConeHalfDeg(double degPerPx, int halfWidth)
    {
        double reach = Math.Abs(degPerPx) * halfWidth * 0.75;
        if (reach > 75.0) reach = 75.0;
        if (reach < 5.0) reach = 5.0;
        return reach;
    }

    /// <summary>A physically possible step between two position reads.</summary>
    public static bool PlausibleStep(double dist, double dtSec)
    {
        if (dtSec < 0) dtSec = 0;
        if (dtSec > 3.0) dtSec = 3.0;
        return dist <= 1.0 + 12.0 * dtSec;
    }

    /// <summary>
    /// A whole leg is judged by its average speed, not the per-poll cap: a
    /// 15.70 s leg through the per-poll limit throws away a real 50.19 yd leg
    /// as "discontinuous" (field, 2026-10-29).
    /// </summary>
    public static bool PlausibleLeg(double dist, double secs)
    {
        if (secs < 0) secs = 0;
        if (secs > 120.0) secs = 120.0;
        return dist <= 15.0 * secs + 1.0;
    }

    /// <summary>
    /// For a fixed goal the character cannot finish further away than it
    /// started plus the distance it travelled. A leg that breaks this was
    /// measured against a position that is not the character's.
    /// </summary>
    public static bool LegConsistent(double remainingBefore, double remainingAfter,
                                     double moved, double slack)
    {
        return remainingAfter <= remainingBefore + moved + slack;
    }

    /// <summary>Alternating detour side: a bot that always picks the same side
    /// spends every attempt against the same rock.</summary>
    public static int DetourSide(int attempt)
    {
        return (attempt & 1) != 0 ? 1 : -1;
    }

    /// <summary>
    /// Updates a slope estimate from one observation, robustly. Small moves are
    /// ignored, estimates that disagree with the calibrated sense or magnitude
    /// by more than 2x are rejected, and what survives is weighted by how far
    /// the character actually walked.
    /// </summary>
    public static bool LearnSlope(ref double est, double reference, double measured,
                                 double driver, double minMove, double maxMove,
                                 double moved, double alpha)
    {
        if (Math.Abs(driver) < 1e-6) return false;
        if (moved < minMove) return false;
        if (maxMove > minMove && moved > maxMove) return false;
        double candidate = measured / driver;
        // NaN sneaks through every comparison below (NaN > x and NaN < x are both
        // false), and one NaN turns the slope into NaN and the next click pixel
        // into whatever (int) casts a NaN to.
        if (double.IsNaN(candidate) || double.IsInfinity(candidate)) return false;
        if (Math.Abs(candidate) < 1e-6) return false;
        double refMag = Math.Abs(reference);
        if (refMag > 1e-6)
        {
            if ((candidate > 0) != (reference > 0)) return false;
            if (Math.Abs(candidate) > refMag * 2.0 || Math.Abs(candidate) < refMag / 2.0) return false;
        }
        double q = moved / 12.0;
        if (q > 1.0) q = 1.0;
        if (q < 0.2) q = 0.2;
        double a = alpha * q;
        est = (1.0 - a) * est + a * candidate;
        return true;
    }

    /// <summary>
    /// Updates the click scale (yards walked per pixel of click height) from one
    /// measured leg.
    ///
    /// This is the channel that broke on 2026-10-29: the scale was a constant from
    /// a run with the camera pitched down, and this client's camera made a -100 px
    /// click walk 120+ yd. So the first accepted measurement replaces the guess
    /// outright - the guess is not evidence - and after that it is a weighted
    /// average, rejecting candidates more than 4x away from what is known, since a
    /// leg cut short by a wall is not a statement about the scale.
    /// </summary>
    public static bool LearnScale(ref double est, double fallback, double candidate, double moved,
                                  int samples, out double used)
    {
        used = est > 1e-6 ? est : fallback;
        if (candidate <= 1e-6 || candidate > 20.0) return false;
        if (moved < 3.0) return false;

        if (samples <= 0)
        {
            est = candidate;
            used = candidate;
            return true;
        }

        double ratio = candidate / used;
        if (ratio > 4.0 || ratio < 0.25) return false;

        double q = moved / 12.0;
        if (q > 1.0) q = 1.0;
        if (q < 0.25) q = 0.25;
        double a = 0.5 * q;
        est = (1.0 - a) * used + a * candidate;
        used = est;
        return true;
    }

    /// <summary>Does the model predict a measurement it was not fitted on?</summary>
    public static bool PredictionOk(double measured, double predicted, double relTol, double absTol)
    {
        return Math.Abs(measured - predicted) <= Math.Max(absTol, relTol * Math.Abs(predicted));
    }

    /// <summary>
    /// Slope per probe, then the median, then the mean of what survives. Needs
    /// no assumption that the response is linear, and throws away probes whose
    /// walk was cut short. Returns how many samples were kept, 0 = no fit.
    /// </summary>
    public static int MedianSlope(double[] driver, double[] turn, double rejectRatio, out double slope)
    {
        slope = 0;
        if (driver == null || turn == null) return 0;
        int n = Math.Min(driver.Length, turn.Length);
        if (n < 2) return 0;

        var s = new double[n];
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (driver[i] == 0.0) continue;
            s[m++] = turn[i] / driver[i];
        }
        if (m < 2) return 0;

        var sorted = new double[m];
        Array.Copy(s, sorted, m);
        Array.Sort(sorted);
        double med = (m % 2) != 0 ? sorted[m / 2] : 0.5 * (sorted[m / 2 - 1] + sorted[m / 2]);
        if (med == 0.0) return 0;

        double sum = 0.0;
        int kept = 0;
        for (int i = 0; i < m; i++)
        {
            if (Math.Abs(s[i] - med) > rejectRatio * Math.Abs(med)) continue;
            sum += s[i];
            kept++;
        }
        if (kept == 0) return 0;
        slope = sum / kept;
        return kept;
    }

    /// <summary>
    /// Plans one leg. Returns 1 = planned, 0 = inside tolerance, -1 = cannot
    /// plan. dy is a preference, never a distance claim: how far a click walks
    /// is decided by the terrain under the cursor, so the loop closes on
    /// measurement after every leg.
    /// </summary>
    public static int PlanLeg(ClickAimParams p, double remX, double remY, out ClickAimLeg leg)
    {
        leg = default;
        if (p == null) return -1;
        double remaining = Len(remX, remY);
        if (remaining <= p.Tolerance) return 0;

        int limit = p.HalfWidth - 40;
        if (limit < 20) return -1;

        double desired = HeadingDeg(remX, remY);
        double delta = WrapDeg(desired - p.HeadingAtZero);

        leg.Turn = TurnKindNone;
        leg.DragPx = 0;
        leg.KeyMs = 0;
        leg.ClickPx = 0;
        // How far the click lands is the least reliable part of the whole model:
        // it is set by where the ray under the cursor meets the terrain, so it
        // depends on the camera pitch, which moves with the mouse. The field
        // numbers (-60 = 5-10 yd, -140 = 20-45 yd) were measured with the camera
        // pitched down. On 2026-10-29 a -100 click walked MORE THAN 120 yd past a
        // 20 yd goal, which is why the tier table is gone: the hop is computed
        // from a scale that every click leg re-measures (ClickAimMover.FinishLeg)
        // and the result is clamped, so one bad scale costs one leg and not a
        // 120 yd sprint.
        leg.Dy = HopDy(p, remaining);

        if (Math.Abs(delta) > p.ConeHalfDeg)
        {
            // A click cannot reach this heading: the camera has to rotate first.
            if (p.TurnKey && Math.Abs(p.DegPerKeyMs) > 1e-6)
            {
                double ms = delta / p.DegPerKeyMs;
                double cap = p.MaxTurnMs > 1.0 ? p.MaxTurnMs : 1200.0;
                if (ms > cap) ms = cap;
                if (ms < -cap) ms = -cap;
                leg.Turn = TurnKindKey;
                leg.KeyMs = (int)(ms >= 0 ? ms + 0.5 : ms - 0.5);
                leg.Dy = HopDy(p, remaining);
                return 1;
            }
            if (Math.Abs(p.DegPerDragPx) > 1e-6)
            {
                double dpx = delta / p.DegPerDragPx;
                if (dpx > limit) dpx = limit;
                if (dpx < -limit) dpx = -limit;
                leg.Turn = TurnKindDrag;
                leg.DragPx = (int)(dpx >= 0 ? dpx + 0.5 : dpx - 0.5);
                // A turn leg walks along the NEW heading: with a long way to go
                // it must walk far enough for the next leg to re-measure the yaw.
                leg.Dy = HopDy(p, remaining);
                return 1;
            }
            return -1;
        }

        if (Math.Abs(p.DegPerPx) < 1e-6) return -1;
        double px = delta / p.DegPerPx;
        if (px > limit) px = limit;
        if (px < -limit) px = -limit;
        leg.ClickPx = (int)(px >= 0 ? px + 0.5 : px - 0.5);
        return 1;
    }

    /// <summary>
    /// The click height for a hop that should land about <c>HopFactor</c> times the
    /// distance left, using the measured click scale.
    ///
    /// Two clamps, and both matter more than the arithmetic. The near clamp keeps
    /// the cursor away from the middle of the screen, where a click lands on the
    /// character itself or on the sky and does nothing. The far clamp keeps a wrong
    /// scale from asking for a 400 px click that lands 300 yd away.
    /// </summary>
    public static int HopDy(ClickAimParams p, double remaining)
    {
        if (p == null) return -60;
        if (double.IsNaN(remaining) || double.IsInfinity(remaining) || remaining <= 0.0)
            return -p.MinDyPx;

        double want = remaining * p.HopFactor;
        if (want > p.MaxHopYd) want = p.MaxHopYd;
        if (want < p.MinHopYd) want = p.MinHopYd;

        double scale = p.YdPerDyPx > 1e-6 ? p.YdPerDyPx : p.AssumedYdPerDyPx;
        // Worked in the (positive) pixel offset and negated at the end, because
        // "below the middle of the screen" is a negative dy and comparing negative
        // numbers is how this clamp was wrong the first time.
        int offset = (int)(want / scale + 0.5);
        if (offset < p.MinDyPx) offset = p.MinDyPx;
        if (offset > p.MaxDyPx) offset = p.MaxDyPx;
        return -offset;
    }

    /// <summary>
    /// The same assertions the C tool's offline suite runs (test_aimmath.c), so
    /// the port can be checked on the user's machine without a separate test
    /// project: every one of these numbers comes from a field run.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0;
        int total = 0;
        var failures = new System.Text.StringBuilder();

        Action<string, bool> check = (name, ok) =>
        {
            total++;
            if (ok) passed++;
            else failures.Append(" [").Append(name).Append(']');
        };

        check("wrap190", Math.Abs(WrapDeg(190.0) + 170.0) < 1e-9);
        check("len345", Math.Abs(Len(3, 4) - 5.0) < 1e-12);
        check("cone72", Math.Abs(ConeHalfDeg(-0.10, 960) - 72.0) < 1e-9);
        check("coneCap", Math.Abs(ConeHalfDeg(-0.20, 960) - 75.0) < 1e-9);
        check("coneFloor", Math.Abs(ConeHalfDeg(0.0, 960) - 5.0) < 1e-9);
        check("stepJumpRejected", !PlausibleStep(79.4, 0.3));
        check("stepRunOk", PlausibleStep(7.0, 1.0));
        check("leg50ydOk", PlausibleLeg(50.19, 15.70));
        check("legJumpRejected", !PlausibleLeg(79.0, 0.3));
        check("triBroken", !LegConsistent(2.04, 30.83, 5.45, 0.5));
        check("triOk", LegConsistent(9.21, 21.16, 29.77, 0.5));
        check("detour1Right", DetourSide(1) == 1);
        check("detour2Left", DetourSide(2) == -1);

        // The 2026-10-29 16:0x calibration: four probes, median slope -0.08566.
        double[] drv = { 150, -150, 225, -225 };
        double[] trn = { -12.74, 12.46, -17.91, 21.97 };
        double slope;
        int kept = MedianSlope(drv, trn, 2.0, out slope);
        check("medianKept4", kept == 4);
        check("medianSlope", Math.Abs(slope + 0.08566) < 0.002);

        // A rejected outlier must not move the estimate.
        double est = -0.10;
        bool took = LearnSlope(ref est, -0.10, 12.0, 150, 6.0, 30.0, 10.0, 0.5);
        check("learnRejectsWrongSign", !took && Math.Abs(est + 0.10) < 1e-12);
        // -18 deg over 150 px is -0.12 deg/px: inside the 2x band, so it is
        // accepted and pulls the estimate from -0.10 toward it.
        took = LearnSlope(ref est, -0.10, -18.0, 150, 6.0, 30.0, 10.0, 0.5);
        check("learnAccepts", took && est < -0.10 && est > -0.115);

        // Click scale: the first measurement replaces the guess, later ones are
        // rejected when they are wildly different (a leg cut short by a wall).
        double scaleEst = 0.0;
        double usedScale;
        bool learned = LearnScale(ref scaleEst, 1.1, 0.7, 70.0, 0, out usedScale);
        check("scaleFirstReplacesGuess", learned && Math.Abs(scaleEst - 0.7) < 1e-9);
        scaleEst = 0.7;
        learned = LearnScale(ref scaleEst, 1.1, 9.0, 40.0, 3, out usedScale);
        check("scaleRejectsJump", !learned && Math.Abs(scaleEst - 0.7) < 1e-9);
        learned = LearnScale(ref scaleEst, 1.1, 0.75, 30.0, 3, out usedScale);
        check("scaleAcceptsClose", learned && scaleEst > 0.7 && scaleEst < 0.75);

        var p = new ClickAimParams();
        p.HeadingAtZero = 0.0;
        p.DegPerPx = -0.10;
        p.DegPerDragPx = -0.21;
        p.ConeHalfDeg = ConeHalfDeg(p.DegPerPx, p.HalfWidth);
        p.Tolerance = 2.0;
        p.NearGoal = 15.0;
        p.PreferredDy = -140;
        p.NearDy = -70;

        ClickAimLeg leg;
        check("insideTolerance", PlanLeg(p, 1.0, 0.0, out leg) == 0);
        check("clickStraight", PlanLeg(p, 40.0, 0.0, out leg) == 1 && leg.Turn == TurnKindNone);

        // The click height used to come from a tier table (-60/-100/-140). On
        // 2026-10-29 a -100 click walked more than 120 yd past a 20 yd goal, so it
        // now comes from a measured scale with two clamps. These checks pin the
        // clamps, because they are what bounds a wrong scale.
        check("dyNeverInsideNear", HopDy(p, 1.0) <= -p.MinDyPx);
        check("dyNeverBeyondFar", HopDy(p, 5000.0) >= -p.MaxDyPx);
        check("dyShortHopUsesNear", HopDy(p, 4.0) == -p.MinDyPx);
        p.YdPerDyPx = 0.25;                      // the documented field scale
        check("dyFieldScale", HopDy(p, 12.0) == -58);   // 12*1.2/0.25 = 57.6
        p.YdPerDyPx = 3.0;                       // a click that lands far, as seen
        check("dyFarScaleClamps", HopDy(p, 20.0) == -p.MinDyPx);
        p.YdPerDyPx = 0.0;

        check("turnNeeded", PlanLeg(p, -80.0, 0.0, out leg) == 1 && leg.Turn == TurnKindDrag);
        check("turnWalksHop", leg.Dy <= -p.MinDyPx && leg.Dy >= -p.MaxDyPx);
        check("turnClamped", Math.Abs(leg.DragPx) <= p.HalfWidth - 40);

        var dlg = default(ClickAimLeg);
        dlg.Turn = TurnKindDrag;
        dlg.DragPx = 100;
        check("yawAfterDrag", Math.Abs(YawAfterTurn(p, dlg) + 21.0) < 1e-9);

        if (failures.Length == 0) return string.Format("ClickAim self-test {0}/{1} passed", passed, total);
        return string.Format("ClickAim self-test {0}/{1} FAILED:{2}", passed, total, failures);
    }

    /// <summary>The camera yaw a turn leg leaves behind: model, not measurement.</summary>
    public static double YawAfterTurn(ClickAimParams p, ClickAimLeg leg)
    {
        double pred;
        if (leg.Turn == TurnKindKey) pred = p.DegPerKeyMs * leg.KeyMs;
        else pred = p.DegPerDragPx * leg.DragPx;
        return WrapDeg(p.HeadingAtZero + pred);
    }
}

/// <summary>Everything the planner reads. Mirrors aim_params in aimmath.h.</summary>
public sealed class ClickAimParams
{
    public double HeadingAtZero { get; set; } = double.NaN;
    public double DegPerPx { get; set; } = -0.11;
    public double DegPerDragPx { get; set; } = -0.21;
    public double DegPerKeyMs { get; set; }
    public double DegPerPxRef { get; set; }
    public double DegPerDragPxRef { get; set; }
    public double DegPerKeyMsRef { get; set; }
    public int HalfWidth { get; set; } = 960;
    public double ConeHalfDeg { get; set; } = 75.0;
    public double Tolerance { get; set; } = 2.0;
    public int PreferredDy { get; set; } = -140;

    /// <summary>
    /// Yards walked per pixel of click height (|dy|). 0 = not measured yet, in which
    /// case <see cref="AssumedYdPerDyPx"/> is used. Measured per leg by the mover.
    /// </summary>
    public double YdPerDyPx { get; set; }

    /// <summary>Starting guess for the click scale; deliberately pessimistic.</summary>
    public double AssumedYdPerDyPx { get; set; } = 1.1;

    /// <summary>How much further than the distance left a hop should land.</summary>
    public double HopFactor { get; set; } = 1.2;

    /// <summary>Longest hop asked for, in yards, whatever the scale says.</summary>
    public double MaxHopYd { get; set; } = 25.0;

    /// <summary>Shortest hop asked for; below this a click is a rounding error.</summary>
    public double MinHopYd { get; set; } = 4.0;

    /// <summary>Nearest the cursor may come to the middle of the screen, in px.</summary>
    public int MinDyPx { get; set; } = 30;

    /// <summary>Furthest below the middle of the screen a click may go, in px.</summary>
    public int MaxDyPx { get; set; } = 260;
    /// <summary>Click height for a 15-40 yd hop; -140 overshoots a path waypoint.</summary>
    public int MidDy { get; set; } = -100;
    public int NearDy { get; set; } = -60;
    /// <summary>Inside this distance the mid hop is already too long.</summary>
    public double MidGoal { get; set; } = 40.0;
    public double MinLearnMove { get; set; } = 6.0;
    public double MaxLearnMove { get; set; } = 30.0;
    public double NearGoal { get; set; } = 15.0;
    public bool TurnKey { get; set; }
    public double MaxTurnMs { get; set; } = 1500.0;
}

public struct ClickAimLeg
{
    public int ClickPx;
    public int Dy;
    public int Turn;
    public int DragPx;
    public int KeyMs;
}

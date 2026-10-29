using System;
using System.Globalization;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Measures the two numbers the click model needs, by walking four short legs and
/// measuring where the character actually went.
///
/// Why this exists: <c>Settings\clickaim-cal.txt</c> is empty on the client
/// ("calibration click 0.00000 (0 samples)"), so every walk runs on constants copied
/// from a field run with a different camera. On 2026-10-29 that produced a 120 yd
/// sprint on a 20 yd goal and, worse, clicks that aimed a few degrees wide on every
/// leg - small enough to look like bad luck, large enough that the character circled
/// the target instead of reaching it.
///
/// The model has two channels and both are pure geometry:
///
///   * a click at screen offset <c>px</c> from the centre walks <c>degPerPx * px</c>
///     away from the heading a centre click walks;
///   * a right-drag of <c>px</c> rotates the camera by <c>degPerDragPx * px</c>.
///
/// So: click the centre and measure the heading it walked, drag a known number of
/// pixels and click the centre again, then click either side of the centre by a
/// known number of pixels. Four walks of about 20 yd each, and the file stops being
/// a guess.
///
/// The result goes into the calibration file, which every <see cref="ClickAimMover"/>
/// reads when it is constructed. Run it with <c>--clickaim-calibrate</c> before any
/// walk in the session.
/// </summary>
internal static class ClickAimSelfCalibration
{
    /// <summary>Height of the probe clicks: a short hop, so a mistake stays short.</summary>
    private const int ProbeDy = -60;

    /// <summary>Camera drag for the drag channel. Large enough that the angle is unambiguous.</summary>
    private const int ProbeDragPx = 240;

    /// <summary>Click offset for the click channel, either side of the centre.</summary>
    private const int ProbeClickPx = 200;

    /// <summary>A walk shorter than this says nothing about a direction.</summary>
    private const double MinProbeYd = 4.0;

    /// <summary>How long one probe walk may take before it is called a failure.</summary>
    private const int ProbeTimeoutMs = 9000;

    public static void Run()
    {
        Logging.Write("[Calibrate] measuring the click model: four short walks, about 20 yd each");

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[Calibrate] no game window"); return; }

        if (!WaitForRest(6000))
        {
            Logging.Write("[Calibrate] the character is not standing still; calibration needs a still start");
            return;
        }

        double zero, firstDistance;
        if (!Probe("centre click", hwnd, false, 0, out zero, out firstDistance))
        {
            Logging.Write("[Calibrate] the centre click did not walk anywhere useful; nothing was written");
            return;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Calibrate] centre click: {0:F1} yd at heading {1:F1} deg - that heading is the zero",
            firstDistance, zero));

        double afterDrag, dragDistance;
        if (!Probe("drag +" + ProbeDragPx + " px", hwnd, true, ProbeDragPx, out afterDrag, out dragDistance))
        {
            Logging.Write("[Calibrate] the drag probe did not walk; a drag calibration needs that walk");
            return;
        }

        double dragDelta = ClickAimMath.WrapDeg(afterDrag - zero);
        double degPerDragPx = dragDelta / ProbeDragPx;
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Calibrate] a {0} px drag turned the walk {1:F1} deg -> {2:F5} deg/px",
            ProbeDragPx, dragDelta, degPerDragPx));

        double rightHeading, rightDistance;
        bool rightOk = Probe("click +" + ProbeClickPx + " px", hwnd, false, ProbeClickPx,
                             out rightHeading, out rightDistance);

        double leftHeading, leftDistance;
        bool leftOk = Probe("click -" + ProbeClickPx + " px", hwnd, false, -ProbeClickPx,
                            out leftHeading, out leftDistance);

        // The zero is the heading the last centre click walked, which was the drag
        // probe's. A click at +N px should walk a heading of zero + degPerPx*N, and
        // degPerPx is negative when clicking right turns right - the same sign the
        // planner uses in ClickAimMath.PlanLeg (px = delta / degPerPx).
        double fromRight = rightOk ? ClickAimMath.WrapDeg(rightHeading - afterDrag) / ProbeClickPx : 0.0;
        double fromLeft = leftOk ? ClickAimMath.WrapDeg(leftHeading - afterDrag) / -ProbeClickPx : 0.0;

        double degPerPx;
        if (rightOk && leftOk)
        {
            if ((fromRight > 0) != (fromLeft > 0))
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Calibrate] the two click probes disagree in sign ({0:F5} and {1:F5} deg/px), so one "
                    + "of them was deflected; not writing a click value", fromRight, fromLeft));
                return;
            }
            degPerPx = 0.5 * (fromRight + fromLeft);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] clicks: +{0} px gives {1:F5} deg/px, -{0} px gives {2:F5} deg/px",
                ProbeClickPx, fromRight, fromLeft));
        }
        else if (rightOk) degPerPx = fromRight;
        else if (leftOk) degPerPx = fromLeft;
        else
        {
            Logging.Write("[Calibrate] neither click probe walked; nothing was written");
            return;
        }

        if (ClickAimCalibration.Append(degPerPx, degPerDragPx, 0.0))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] wrote degPerPx={0:F6} degPerDragPx={1:F6} to {2}",
                degPerPx, degPerDragPx, ClickAimCalibration.DefaultPath));
        }
        else
        {
            Logging.Write("[Calibrate] could not write " + ClickAimCalibration.DefaultPath);
        }

        Logging.Write("[Calibrate] the file now pools to: " + ClickAimCalibration.Load());
    }

    /// <summary>
    /// One probe: a drag or a click, then a wait for the character to stop, then the
    /// heading it walked and how far.
    /// </summary>
    private static bool Probe(string what, IntPtr hwnd, bool drag, int offset,
                              out double heading, out double distance)
    {
        heading = 0.0;
        distance = 0.0;

        if (!WaitForRest(4000))
        {
            Logging.Write("[Calibrate] " + what + ": the character is still moving, skipping the probe");
            return false;
        }

        double x0, y0;
        if (!ClickAimPositions.TryRead(out x0, out y0))
        {
            Logging.Write("[Calibrate] " + what + ": no player position");
            return false;
        }

        if (drag)
        {
            ClickAimInput.Drag(hwnd, offset);
            ClickAimInput.Click(hwnd, 0, ProbeDy);
        }
        else
        {
            ClickAimInput.Click(hwnd, offset, ProbeDy);
        }

        int started = Environment.TickCount;
        bool moved = false;
        Thread.Sleep(400);
        while (unchecked(Environment.TickCount - started) < ProbeTimeoutMs)
        {
            double x, y;
            if (!ClickAimPositions.TryRead(out x, out y)) { Thread.Sleep(80); continue; }
            if (ClickAimMath.Len(x - x0, y - y0) > 1.0) moved = true;
            if (moved && WaitForRest(800)) break;
            Thread.Sleep(80);
        }

        double x1, y1;
        if (!ClickAimPositions.TryRead(out x1, out y1)) return false;

        double dx = x1 - x0, dy = y1 - y0;
        distance = ClickAimMath.Len(dx, dy);
        if (distance < MinProbeYd)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] {0}: only {1:F1} yd of movement - not enough to measure", what, distance));
            return false;
        }

        heading = ClickAimMath.HeadingDeg(dx, dy);
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Calibrate] {0}: {1:F1} yd at heading {2:F1} deg", what, distance, heading));
        return true;
    }

    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>True once the character has not moved for about half a second.</summary>
    private static bool WaitForRest(int ms)
    {
        double x, y;
        if (!ClickAimPositions.TryRead(out x, out y)) return false;

        int stillSince = Environment.TickCount;
        int limit = Environment.TickCount + ms;
        while (unchecked(Environment.TickCount - limit) < 0)
        {
            Thread.Sleep(60);
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;
            if (ClickAimMath.Len(nx - x, ny - y) > 0.2) stillSince = Environment.TickCount;
            else if (unchecked(Environment.TickCount - stillSince) > 500) return true;
            x = nx;
            y = ny;
        }
        return false;
    }
}

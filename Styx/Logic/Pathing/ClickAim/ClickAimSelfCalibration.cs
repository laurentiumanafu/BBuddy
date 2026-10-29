using System;
using System.Globalization;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Measures the three numbers the click model needs, by walking five short legs and
/// reading where the character actually went.
///
/// Why this exists: <c>Settings\clickaim-cal.txt</c> had never been written on this
/// install ("calibration click 0.00000 (0 samples)"), so every walk ran on constants
/// from a field run with a different camera. That produced a 120 yd sprint on a 20 yd
/// goal and clicks that aimed a few degrees wide on every leg.
///
/// The first version of this measured too subtly and produced nonsense: a 6-13 yd
/// probe walk has so much angular noise that a 240 px drag "turned" the walk 146 deg.
/// Three things are different now, and each of them removes a way to be wrong:
///
///   * **long probes.** Every probe clicks <see cref="ProbeDy"/> pixels down, which on
///     this client walks 20-40 yd. Ten yards of walk is a direction with tens of
///     degrees of error in it; thirty yards is a direction.
///   * **a measured centre.** The zero is taken from a plain centre click, not from
///     its own past state, and each channel is then measured against it.
///   * **drags are measured twice.** A drag rotates the camera and the rotation
///     *stays*, so the second drag's measurement is relative to where the first one
///     left the camera, and the two must agree. That disagreement is what caught the
///     146 deg nonsense.
///
/// Nothing here talks to the mover: the result goes into the calibration file, which
/// every <see cref="ClickAimMover"/> reads when it is constructed. Run it with
/// <c>--clickaim-calibrate</c> (or <c>calibrate</c> in the request file) before
/// walking.
/// </summary>
internal static class ClickAimSelfCalibration
{
    /// <summary>Height of the probe clicks. Long on purpose: see the class comment.</summary>
    private const int ProbeDy = -140;

    /// <summary>Camera drag for the drag channel.</summary>
    private const int ProbeDragPx = 200;

    /// <summary>Click offset for the click channel, either side of the centre.</summary>
    private const int ProbeClickPx = 200;

    /// <summary>A probe walk shorter than this says nothing useful about a direction.</summary>
    private const double MinProbeYd = 12.0;

    /// <summary>How long one probe walk may take before it is called a failure.</summary>
    private const int ProbeTimeoutMs = 12000;

    // What a plausible answer looks like. The field runs measured -0.107 to -0.127
    // deg/px for clicks and -0.18 to -0.22 for drags, and a camera drag cannot be
    // slower than 0.02 deg/px without the session being frozen. Outside these bands
    // the number is a measurement artefact, and writing it would be worse than
    // leaving the mover to measure the scale itself.
    private const double MinClickDeg = 0.03;
    private const double MaxClickDeg = 0.60;
    private const double MinDragDeg = 0.03;
    private const double MaxDragDeg = 1.00;

    public static void Run()
    {
        Logging.Write("[Calibrate] measuring the click model: five probe walks of 20-40 yd");

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[Calibrate] no game window"); return; }

        if (!WaitForRest(6000))
        {
            Logging.Write("[Calibrate] the character is not standing still; calibration needs a still start");
            return;
        }

        double h0, d0;
        if (!Probe("centre click", hwnd, new Effect(EffectKind.None, 0), 0, out h0, out d0))
        {
            Logging.Write("[Calibrate] the centre click did not walk anywhere useful; nothing was written");
            return;
        }

        double hR, dR;
        bool rightOk = Probe("click +" + ProbeClickPx + " px", hwnd, new Effect(EffectKind.Click, ProbeClickPx),
                             ProbeClickPx, out hR, out dR);

        double hL, dL;
        bool leftOk = Probe("click -" + ProbeClickPx + " px", hwnd, new Effect(EffectKind.Click, -ProbeClickPx),
                            -ProbeClickPx, out hL, out dL);

        double hD1, dD1;
        bool drag1Ok = Probe("drag +" + ProbeDragPx + " px", hwnd, new Effect(EffectKind.Drag, ProbeDragPx),
                             0, out hD1, out dD1);

        double hD2, dD2;
        bool drag2Ok = Probe("drag -" + ProbeDragPx + " px", hwnd, new Effect(EffectKind.Drag, -ProbeDragPx),
                             0, out hD2, out dD2);

        /* ---- the click channel ------------------------------------------------
         * Both offsets are compared with the same centre-click heading, so the
         * camera's own heading cancels and what is left is the click's share. */
        double clickEst = 0.0;
        int clickSamples = 0;
        if (rightOk)
        {
            double s = ClickAimMath.WrapDeg(hR - h0) / ProbeClickPx;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] +{0} px moved the walk {1:F2} deg from the centre heading -> {2:F5} deg/px",
                ProbeClickPx, ClickAimMath.WrapDeg(hR - h0), s));
            if (Plausible(s, MinClickDeg, MaxClickDeg)) { clickEst += s; clickSamples++; }
        }
        if (leftOk)
        {
            double s = ClickAimMath.WrapDeg(h0 - hL) / ProbeClickPx;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] -{0} px moved the walk {1:F2} deg from the centre heading -> {2:F5} deg/px",
                ProbeClickPx, ClickAimMath.WrapDeg(h0 - hL), s));
            if (Plausible(s, MinClickDeg, MaxClickDeg)) { clickEst += s; clickSamples++; }
        }
        if (clickSamples == 0)
        {
            Logging.Write("[Calibrate] neither click probe gave a plausible angle; the click channel is not "
                          + "written and the field default is used");
        }
        else
        {
            clickEst /= clickSamples;
        }

        /* ---- the drag channel -------------------------------------------------
         * A drag rotates the camera and it stays rotated, so the first drag is
         * measured against the centre heading and the second against where the first
         * one left the camera. Two independent estimates of the same number, which is
         * the check that the first version of this needed. */
        double dragEst = 0.0;
        int dragSamples = 0;
        if (drag1Ok)
        {
            double s = ClickAimMath.WrapDeg(hD1 - h0) / ProbeDragPx;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] drag +{0} px turned the walk {1:F2} deg from the centre heading -> {2:F5} deg/px",
                ProbeDragPx, ClickAimMath.WrapDeg(hD1 - h0), s));
            if (Plausible(s, MinDragDeg, MaxDragDeg)) { dragEst += s; dragSamples++; }
        }
        if (drag1Ok && drag2Ok)
        {
            double s = ClickAimMath.WrapDeg(hD1 - hD2) / ProbeDragPx;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] the -{0} px drag brought the camera {1:F2} deg back -> {2:F5} deg/px",
                ProbeDragPx, ClickAimMath.WrapDeg(hD1 - hD2), s));
            if (Plausible(s, MinDragDeg, MaxDragDeg))
            {
                if (dragSamples > 0 && Math.Abs(s - dragEst / dragSamples) > 0.5 * Math.Abs(s))
                {
                    Logging.Write("[Calibrate] the two drag measurements disagree by more than half; "
                                  + "discarding the drag channel rather than guessing");
                    dragSamples = 0;
                    dragEst = 0.0;
                }
                else { dragEst += s; dragSamples++; }
            }
        }
        if (dragSamples > 0) dragEst /= dragSamples;
        else Logging.Write("[Calibrate] no plausible drag angle; the drag channel is not written");

        /* ---- the click scale --------------------------------------------------
         * All five probes clicked the same height, so the distances they walked are
         * repeats of one measurement - and they are the numbers that say how much
         * ground a click is worth, which no calibration file has ever recorded. */
        double scaleEst = 0.0;
        int scaleSamples = 0;
        AddScale(ref scaleEst, ref scaleSamples, d0, ProbeDy);
        if (rightOk) AddScale(ref scaleEst, ref scaleSamples, dR, ProbeDy);
        if (leftOk) AddScale(ref scaleEst, ref scaleSamples, dL, ProbeDy);
        if (drag1Ok) AddScale(ref scaleEst, ref scaleSamples, dD1, ProbeDy);
        if (drag2Ok) AddScale(ref scaleEst, ref scaleSamples, dD2, ProbeDy);
        if (scaleSamples > 0)
        {
            scaleEst /= scaleSamples;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] a {0} px click is worth {1:F2} yd/px over {2} probe(s)",
                ProbeDy, scaleEst, scaleSamples));
        }

        if (clickSamples == 0 && dragSamples == 0 && scaleSamples == 0)
        {
            Logging.Write("[Calibrate] nothing was plausible; the calibration file was not touched");
            return;
        }

        double clickValue = clickSamples > 0 ? clickEst : 0.0;
        double dragValue = dragSamples > 0 ? dragEst : 0.0;
        if (ClickAimCalibration.Append(clickValue, dragValue, 0.0,
                                       scaleSamples > 0 ? scaleEst : 0.0))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] wrote degPerPx={0:F6} degPerDragPx={1:F6}{2} to {3}",
                clickValue, dragValue,
                scaleSamples > 0 ? string.Format(CultureInfo.InvariantCulture, " ydPerDyPx={0:F4}", scaleEst) : "",
                ClickAimCalibration.DefaultPath));
        }
        else
        {
            Logging.Write("[Calibrate] could not write " + ClickAimCalibration.DefaultPath);
        }

        Logging.Write("[Calibrate] the file now pools to: " + ClickAimCalibration.Load());
    }

    private static void AddScale(ref double sum, ref int count, double distance, int dy)
    {
        if (dy == 0 || distance < MinProbeYd) return;
        double s = distance / Math.Abs(dy);
        if (s <= 0.01 || s > 5.0) return;
        sum += s;
        count++;
    }

    private static bool Plausible(double value, double min, double max)
    {
        double m = Math.Abs(value);
        return m >= min && m <= max;
    }

    private enum EffectKind { None, Click, Drag }

    private struct Effect
    {
        public readonly EffectKind Kind;
        public readonly int Offset;

        public Effect(EffectKind kind, int offset)
        {
            Kind = kind;
            Offset = offset;
        }
    }

    /// <summary>
    /// One probe: wait for rest, apply the effect (a drag, then a click, or just a
    /// click) and report the heading the character walked and how far.
    ///
    /// <paramref name="clickOffset"/> is the horizontal offset of the click that ends
    /// the probe: the mover aims with that offset, so the probe has to as well.
    /// </summary>
    private static bool Probe(string what, IntPtr hwnd, Effect effect, int clickOffset,
                              out double heading, out double distance)
    {
        heading = 0.0;
        distance = 0.0;

        if (!WaitForRest(5000))
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

        if (effect.Kind == EffectKind.Drag)
        {
            ClickAimInput.Drag(hwnd, effect.Offset);
            Thread.Sleep(120);
        }
        ClickAimInput.Click(hwnd, clickOffset, ProbeDy);

        int started = Environment.TickCount;
        bool moved = false;
        Thread.Sleep(400);
        while (unchecked(Environment.TickCount - started) < ProbeTimeoutMs)
        {
            double x, y;
            if (!ClickAimPositions.TryRead(out x, out y)) { Thread.Sleep(80); continue; }
            if (ClickAimMath.Len(x - x0, y - y0) > 1.0) moved = true;
            if (moved && WaitForRest(900)) break;
            Thread.Sleep(80);
        }

        double x1, y1;
        if (!ClickAimPositions.TryRead(out x1, out y1)) return false;

        double dx = x1 - x0, dy = y1 - y0;
        distance = ClickAimMath.Len(dx, dy);
        heading = ClickAimMath.HeadingDeg(dx, dy);

        if (distance < MinProbeYd)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Calibrate] {0}: only {1:F1} yd of movement, needs {2:F0} - not used (was something in "
                + "the way, or is the camera pitched almost straight down?)",
                what, distance, MinProbeYd));
            return false;
        }

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

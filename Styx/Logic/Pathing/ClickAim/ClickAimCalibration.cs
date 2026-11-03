using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// The calibration file shared with the field tool (Tools/WowBClickAim). Format
/// is one line per calibration, exactly as the C tool writes it:
///
///     degPerPx=-0.107086 degPerDragPx=-0.184707 degPerKeyMs=0.000000
///
/// The file is a POPULATION, not a value: every run appends what it measured and
/// the loader takes a robust median per channel. Two verified field runs measured
/// -0.10709 and -0.12674 deg/px - 18 % apart on the same client - so trusting
/// whichever line was written last is what produced the early 3.3x errors.
/// </summary>
public static class ClickAimCalibration
{
    public const string FileName = "clickaim-cal.txt";

    public static string DefaultPath
    {
        get
        {
            string root;
            try { root = Logging.ApplicationPath; }
            catch { root = AppContext.BaseDirectory; }
            return Path.Combine(root, "Settings", FileName);
        }
    }

    public sealed class Result
    {
        public double DegPerPx { get; set; }
        public double DegPerDragPx { get; set; }
        public double DegPerKeyMs { get; set; }

        /// <summary>
        /// Yards walked per pixel of click height. Optional fourth channel: the field
        /// tool never wrote it, and a file without it simply leaves the mover to
        /// measure the scale at runtime.
        /// </summary>
        public double YdPerDyPx { get; set; }

        public int ClickSamples { get; set; }
        public int DragSamples { get; set; }
        public int KeySamples { get; set; }
        public int ScaleSamples { get; set; }
        public double ClickSpread { get; set; }
        public bool Ok { get { return ClickSamples > 0; } }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "click {0:F5} ({1} sample{2}), drag {3:F5} ({4}), key {5:F5} ({6}), "
                + "scale {7:F3} yd/px ({8})",
                DegPerPx, ClickSamples, ClickSamples == 1 ? "" : "s",
                DegPerDragPx, DragSamples, DegPerKeyMs, KeySamples,
                YdPerDyPx, ScaleSamples);
        }
    }

    /// <summary>Reads every line and pools the channels. Never throws.</summary>
    public static Result Load(string? path = null)
    {
        var r = new Result();
        path = path ?? DefaultPath;
        try
        {
            if (!File.Exists(path)) return r;
            var clicks = new List<double>();
            var drags = new List<double>();
            var keys = new List<double>();
            var scales = new List<double>();
            foreach (var line in File.ReadAllLines(path))
            {
                double a, b, c, d;
                if (!TryParseLine(line, out a, out b, out c, out d)) continue;
                clicks.Add(a); drags.Add(b); keys.Add(c);
                if (d > 0) scales.Add(d);
            }
            r.ClickSamples = PoolMedian(clicks, out double cp); r.DegPerPx = cp;
            r.DragSamples = PoolMedian(drags, out double dp); r.DegPerDragPx = dp;
            r.KeySamples = PoolMedian(keys, out double kp); r.DegPerKeyMs = kp;
            r.ScaleSamples = PoolMedian(scales, out double sp); r.YdPerDyPx = sp;
            if (clicks.Count > 1)
            {
                double lo = clicks[0], hi = clicks[0];
                foreach (var v in clicks) { if (v < lo) lo = v; if (v > hi) hi = v; }
                r.ClickSpread = Math.Abs(cp) > 1e-9 ? (hi - lo) / Math.Abs(cp) : 0.0;
            }
        }
        catch
        {
            // A calibration file is never worth throwing over.
        }
        return r;
    }

    /// <summary>Appends one line. Never throws; returns false on I/O failure.</summary>
    public static bool Append(double degPerPx, double degPerDragPx, double degPerKeyMs,
                              string? path = null)
    {
        return Append(degPerPx, degPerDragPx, degPerKeyMs, 0.0, path);
    }

    /// <summary>
    /// Appends one line, including the click scale when it was measured. Older
    /// readers ignore the extra field, and a line without it pools to no samples
    /// rather than to zero.
    /// </summary>
    public static bool Append(double degPerPx, double degPerDragPx, double degPerKeyMs,
                              double ydPerDyPx, string? path = null)
    {
        path = path ?? DefaultPath;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string extra = ydPerDyPx > 0 ? string.Format(CultureInfo.InvariantCulture,
                " ydPerDyPx={0:F4}", ydPerDyPx) : "";
            File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture,
                "degPerPx={0:F6} degPerDragPx={1:F6} degPerKeyMs={2:F6}{3}{4}",
                degPerPx, degPerDragPx, degPerKeyMs, extra, Environment.NewLine));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Median of the non-zero samples. A zero in this file means "that channel
    /// was never measured", never "the response is zero", and a key channel of 0
    /// on --load once stopped a whole run from planning at all.
    /// </summary>
    private static int PoolMedian(List<double> values, out double median)
    {
        median = 0;
        var keep = new List<double>();
        foreach (var v in values) if (Math.Abs(v) > 1e-9) keep.Add(v);
        if (keep.Count == 0) return 0;
        keep.Sort();
        int n = keep.Count;
        median = (n % 2) != 0 ? keep[n / 2] : 0.5 * (keep[n / 2 - 1] + keep[n / 2]);
        return n;
    }

    private static bool TryParseLine(string line, out double click, out double drag, out double key,
                                     out double scale)
    {
        click = drag = key = scale = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            foreach (var part in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                double v;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    continue;
                if (name == "degPerPx") click = v;
                else if (name == "degPerDragPx") drag = v;
                else if (name == "degPerKeyMs") key = v;
                else if (name == "ydPerDyPx") scale = v;
            }
            return Math.Abs(click) > 1e-9;
        }
        catch
        {
            return false;
        }
    }
}

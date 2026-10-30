using System;
using System.Globalization;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Movement the way BloogBot does it, which is the way that has never needed a camera.
///
/// BloogBot's whole movement layer is two calls (BloogBot/Game/Objects/LocalPlayer.cs):
///
///     public void MoveToward(Position pos)
///     {
///         Face(pos);                         // SetFacing(bearing) - write the player's heading
///         StartMovement(ControlBits.Front);  // SetControlBit(Front, 1, tick) - walk forward
///     }
///
/// and <c>Face</c> just computes <c>GetFacingForPosition(pos)</c> from two positions. No
/// camera, no pixels, no scales, no drag gain, no field of view. It supports 1.12.1,
/// 2.4.3 AND 3.3.5a with that same pair, which makes it the only movement model in this
/// project that has ever worked on more than one client.
///
/// Every mechanism tried here on 2026-10-29 aimed a MOUSE at a pixel, and all of them
/// failed for the same reason: the pixel only means a direction once you know the
/// camera's orientation, and nothing in this project can read it (the position that is
/// readable drifts +4.6 deg per tick with the character standing still).
///
/// So this does not aim anything. It writes the heading the character should have - a
/// number this project already READS every tick (<c>facing=3.59</c> in every attach log,
/// <c>WowBOffsets.RecordFacingOffset</c>) - and holds the forward key. The character then
/// walks along the heading given, and nothing has to be aimed.
///
/// The one thing that is NOT assumed is the angle convention: whether the client's facing
/// is radians clockwise from north, radians counter-clockwise from east, or something with
/// a sign flip is measured here, not guessed. <see cref="Calibrate"/> writes two headings,
/// measures the bearing actually walked on each, and derives the linear map between them.
/// If the two disagree, the field is not the heading and the run says so instead of
/// walking in circles.
/// </summary>
internal static class ClickAimKeySteer
{
    /// <summary>Forward key. BloogBot's ControlBits.Front, posted as the key the client
    /// binds it to.</summary>
    private const int ForwardVk = 0x57;      // W

    /// <summary>Headings this far apart are used for the calibration pair.</summary>
    private const double ProbeSeparationDeg = 70.0;

    /// <summary>A probe walk shorter than this measured nothing.</summary>
    private const double MinProbeYd = 2.5;

    private const int MaxProbeMs = 2500;
    private const int PollMs = 60;

    // ---- learned convention -------------------------------------------------
    private static bool _calibrated;
    private static bool _useOutgoing;
    private static double _slope = 1.0;       // d(bearing deg) / d(facing-input)
    private static double _offsetDeg = double.NaN; // bearing = slope*input + offset

    private static bool _keyDown;
    private static int _lastWriteMs = -100000;
    private static double _lastFacing = double.NaN;

    public static bool Calibrated { get { return _calibrated; } }
    public static bool KeyDown { get { return _keyDown; } }
    public static double Slope { get { return _slope; } }
    public static double OffsetDeg { get { return _offsetDeg; } }

    // ------------------------------------------------------------------ facing

    /// <summary>The player's heading, in the client's own units (radians).</summary>
    public static bool TryReadFacing(out double radians)
    {
        radians = 0;
        try
        {
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null) return false;

            WowBReader.WowBPoolObject obj;
            if (!reader.TryGetPlayerObject(out obj) || obj.IsEmpty) return false;

            float x, y, z, facing;
            if (!reader.TryGetObjectTransform(obj, out x, out y, out z, out facing)) return false;
            if (float.IsNaN(facing) || float.IsInfinity(facing)) return false;

            radians = facing;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the player's heading. This is BloogBot's <c>SetFacing</c> on Vanilla/TBC,
    /// which writes the field and then lets the client run; on WotLK BloogBot calls the
    /// client's own SetFacing function instead, which needs an address this build does
    /// not have.
    /// </summary>
    /// <summary>
    /// BloogBot's Vanilla/TBC split (chapter 18): the client keeps TWO facing values.
    /// The one at <c>FACING_OFFSET</c> is the server's own value, used for rendering and
    /// reads; the one at <c>SET_FACING_OFFSET</c> is the OUTGOING value, which the caller
    /// writes and then pushes with <c>SendMovementUpdate</c> (opcode 0xDA). On that build
    /// the write field sat 0x1C BEFORE the read field (read 0x9C4, write 0x9A8).
    ///
    /// That is why a write can look like it works and then come back: the client predicts
    /// locally, the server reconciles, and the readable value is overwritten with the
    /// server's truth. So the write is attempted on both fields, and the read-back is
    /// checked again after a delay to see whether it stuck.
    /// </summary>
    private const int OutgoingFacingDelta = -0x1C;

    public static bool TryWriteFacing(double radians, bool alsoOutgoing = false)
    {
        try
        {
            Memory? mem = StyxWoW.Memory;
            WowBReader? reader = ObjectManager.WowB;
            if (mem == null || reader == null) return false;

            WowBReader.WowBPoolObject obj;
            if (!reader.TryGetPlayerObject(out obj) || obj.IsEmpty) return false;

            byte[] bytes = BitConverter.GetBytes((float)radians);
            bool ok = mem.WriteBytes64(obj.RecordPtr + WowBOffsets.RecordFacingOffset, bytes);

            if (alsoOutgoing)
            {
                // The client's own SendMovementUpdate is not available on this build, so
                // the outgoing value is written and left to the client's next movement
                // packet, which carries whatever facing the client believes it has.
                long off = (long)WowBOffsets.RecordFacingOffset + OutgoingFacingDelta;
                if (off > 0)
                    ok |= mem.WriteBytes64(obj.RecordPtr + (ulong)off, bytes);
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------- keys

    /// <summary>
    /// Holds the forward key down, or lets it up. PostMessage rather than a control-bit
    /// call: Winkr's Movement.md records that PostMessage key input drives Classic
    /// clients from outside, and this project already stops the character with it.
    /// </summary>
    public static void SetForward(IntPtr hwnd, bool down)
    {
        if (hwnd == IntPtr.Zero) return;
        if (down == _keyDown) return;

        ClickAimInput.HoldKey(hwnd, ForwardVk, down);
        _keyDown = down;
    }

    public static void Release(IntPtr hwnd)
    {
        SetForward(hwnd, false);
    }

    // -------------------------------------------------------------- calibration

    /// <summary>
    /// Measures what a written heading does to the direction the character walks.
    ///
    /// Two probes, far enough apart to separate a slope from an offset:
    ///
    ///     write f0, hold forward 1.5 s, measure bearing b0
    ///     write f1 = f0 + 70 deg (in input units), hold forward, measure b1
    ///
    ///     slope = (b1 - b0) / 70        (1.0, -1.0, or something in between)
    ///     offset = b0 - slope * f0      (the client's zero relative to the world)
    ///
    /// A slope near +-1 means the field is the heading. A slope near 0 means the field is
    /// ignored, and the caller must not walk on it.
    /// </summary>
    public static bool Calibrate(IntPtr hwnd, out string report)
    {
        report = "";
        double f0, b0, f1, b1;

        if (!TryReadFacing(out f0)) { report = "cannot read the facing field"; return false; }
        if (!Probe(hwnd, f0, out b0, out report)) return false;

        double step = ProbeSeparationDeg * Math.PI / 180.0;
        f1 = f0 + step;
        if (!Probe(hwnd, f1, out b1, out report)) return false;

        /* Both probes walked the same bearing: the field that is being written is the
         * readable (server) one, and this client ignores it for movement. BloogBot's
         * chapter 18 says the OUTGOING field is the one that steers - so try that before
         * blaming the mechanism. */
        if (Math.Abs(ClickAimMath.WrapDeg(b1 - b0)) < 8.0)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Keys] writing the readable facing field moved nothing ({0:F0} deg then {1:F0} deg "
                + "bearing). Trying the OUTGOING field BloogBot writes (readable - 0x1C), which is the "
                + "value the client sends to the server.", b0, b1));
            _useOutgoing = true;
            if (!Probe(hwnd, f0, out b0, out report)) return false;
            if (!Probe(hwnd, f1, out b1, out report)) return false;
        }

        double dInput = step * 180.0 / Math.PI;
        double dBearing = ClickAimMath.WrapDeg(b1 - b0);
        double slope = dBearing / dInput;

        if (Math.Abs(slope) < 0.35)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "wrote {0:F0} deg then {1:F0} deg and the character walked bearing {2:F0} then "
                + "{3:F0} deg - it ignored the field (slope {4:F2}). The facing write does not "
                + "steer this client.", f0 * 180 / Math.PI, f1 * 180 / Math.PI, b0, b1, slope);
            return false;
        }

        _slope = slope;
        _offsetDeg = ClickAimMath.WrapDeg(b0 - slope * (f0 * 180.0 / Math.PI));
        _calibrated = true;

        report = string.Format(CultureInfo.InvariantCulture,
            "facing {0:F0} deg walked bearing {1:F0} deg; facing {2:F0} deg walked bearing "
            + "{3:F0} deg. So bearing = {4:F2} x facing + {5:F1} deg, and the client's "
            + "facing is {6} (writing the {7} field)",
            f0 * 180 / Math.PI, b0, f1 * 180 / Math.PI, b1, slope, _offsetDeg,
            Math.Abs(Math.Abs(slope) - 1.0) < 0.2
                ? "the player's heading, as BloogBot assumes"
                : "a heading scaled by " + slope.ToString("F2", CultureInfo.InvariantCulture),
            _useOutgoing ? "outgoing" : "readable");
        return true;
    }

    /// <summary>Writes a heading, walks forward for a moment, reports the bearing walked.</summary>
    private static bool Probe(IntPtr hwnd, double facing, out double bearingDeg, out string report)
    {
        bearingDeg = 0;
        report = "";

        double sx, sy;
        if (!ClickAimPositions.TryRead(out sx, out sy)) { report = "cannot read the position"; return false; }

        if (!TryWriteFacing(facing, _useOutgoing)) { report = "cannot write the facing field"; return false; }
        Thread.Sleep(120);

        // Did the write survive the client's own update?
        double after;
        if (!TryReadFacing(out after))
        {
            report = "lost the facing field after writing it";
            return false;
        }

        SetForward(hwnd, true);
        int waited = 0;
        while (waited < MaxProbeMs)
        {
            Thread.Sleep(PollMs);
            waited += PollMs;
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;
            if (ClickAimMath.Len(nx - sx, ny - sy) >= MinProbeYd) break;
        }

        SetForward(hwnd, false);
        Thread.Sleep(200);

        double fx, fy;
        if (!ClickAimPositions.TryRead(out fx, out fy)) { report = "lost the position"; return false; }

        double walked = ClickAimMath.Len(fx - sx, fy - sy);
        if (walked < MinProbeYd)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "wrote facing {0:F2} rad but the character moved {1:F1} yd with the forward key "
                + "held for {2} ms. Either the key does not reach the client or the character is "
                + "against something - this is not a facing problem.", facing, walked, waited);
            return false;
        }

        bearingDeg = ClickAimMath.HeadingDeg(fx - sx, fy - sy);
        report = string.Format(CultureInfo.InvariantCulture,
            "facing written {0:F2} rad (read back {1:F2}), walked {2:F1} yd on bearing {3:F0} deg",
            facing, after, walked, bearingDeg);
        return true;
    }

    // ----------------------------------------------------------------- steering

    /// <summary>
    /// One steering tick toward (tx, ty): sets the heading so the character walks at the
    /// point, and holds the forward key. Returns the distance still to go.
    /// </summary>
    public static bool Steer(double tx, double ty, IntPtr hwnd, bool forward, out double remaining)
    {
        remaining = 0;

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py)) return false;

        remaining = ClickAimMath.Len(tx - px, ty - py);

        double wantedBearing = ClickAimMath.HeadingDeg(tx - px, ty - py);
        double facing = FacingForBearing(wantedBearing);

        // Rewrite the heading often enough that the client cannot drift off it, but not
        // so often that its own update fights every write.
        int now = Environment.TickCount & int.MaxValue;
        if (double.IsNaN(_lastFacing) || Math.Abs(facing - _lastFacing) > 1e-4
            || unchecked(now - _lastWriteMs) > 120)
        {
            TryWriteFacing(facing, _useOutgoing);
            _lastFacing = facing;
            _lastWriteMs = now;
        }

        SetForward(hwnd, forward);
        return true;
    }

    /// <summary>The value to write so the character walks on <paramref name="bearingDeg"/>.</summary>
    public static double FacingForBearing(double bearingDeg)
    {
        if (!_calibrated) return bearingDeg * Math.PI / 180.0;
        double wanted = ClickAimMath.WrapDeg(bearingDeg);
        return ((wanted - _offsetDeg) / _slope) * Math.PI / 180.0;
    }

    // ---------------------------------------------------------------- diagnostic

    /// <summary>
    /// <c>--keywalk [x,y]</c>: calibrate, then walk straight at the point in 12 yd steps,
    /// re-steering every tick, and report. Deliberately does not touch the mesh - this is
    /// the mechanism test, and mixing in pathing would hide which of the two failed.
    /// </summary>
    public static void Run(string? spec)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            Memory? mem = StyxWoW.Memory;
            if (mem == null)
            {
                Logging.Write("[Keys] no memory reader attached");
                return;
            }

            hwnd = mem.WindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                Logging.Write("[Keys] no game window; cannot post keys");
                return;
            }

            double targetX, targetY;
            if (!Target(spec, out targetX, out targetY)) return;

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Keys] BloogBot-style movement: write the heading, hold the forward key. "
                + "Target ({0:F1}, {1:F1}). No camera, no pixels, nothing aimed.", targetX, targetY));

            string report;
            if (!Calibrate(hwnd, out report))
            {
                Logging.Write("[Keys] calibration failed: " + report);
                return;
            }
            Logging.Write("[Keys] " + report);

            double px, py;
            if (!ClickAimPositions.TryRead(out px, out py))
            {
                Logging.Write("[Keys] cannot read the position");
                return;
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Keys] starting at ({0:F1}, {1:F1}), {2:F1} yd to go", px, py,
                ClickAimMath.Len(targetX - px, targetY - py)));

            int start = Environment.TickCount & int.MaxValue;
            double best = double.MaxValue;
            int stalled = 0;

            while (true)
            {
                int now = Environment.TickCount & int.MaxValue;
                if (unchecked(now - start) > 90000) { Logging.Write("[Keys] 90 s is enough; stopping"); break; }

                double rem;
                double half = ClickAimMath.Len(targetX - px, targetY - py);
                // Aim at a point at most 12 yd out, on the line to the target: a long
                // walk is the same two calls repeated, and a short leg keeps the
                // heading error from turning into a wide arc.
                double tx = targetX, ty = targetY;
                if (half > 12.0)
                {
                    double k = 12.0 / half;
                    tx = px + (targetX - px) * k;
                    ty = py + (targetY - py) * k;
                }

                if (!Steer(tx, ty, hwnd, true, out rem)) break;

                if (rem <= 0.8) break;

                if (half < best - 0.3) { best = half; stalled = 0; }
                else
                {
                    stalled++;
                    if (stalled >= 40)
                    {
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Keys] no progress for {0:F0} s at {1:F1} yd from the point - the "
                            + "character is against something. Stopping, keys released.",
                            40 * 0.25, half));
                        break;
                    }
                }

                Thread.Sleep(250);
                if (!ClickAimPositions.TryRead(out px, out py)) break;

                double nowRem = ClickAimMath.Len(targetX - px, targetY - py);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Keys] at ({0:F1}, {1:F1}), {2:F1} yd to go, heading written {3:F0} deg",
                    px, py, nowRem, FacingForBearing(ClickAimMath.HeadingDeg(targetX - px, targetY - py))
                                  * 180.0 / Math.PI));
            }

            Release(hwnd);
            double ex, ey;
            if (ClickAimPositions.TryRead(out ex, out ey))
            {
                double left = ClickAimMath.Len(targetX - ex, targetY - ey);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Keys] stopped at ({0:F1}, {1:F1}); {2:F1} yd from the point ({3}). If this is "
                    + "small the mechanism works and the mesh path can drive it; if the character "
                    + "went the wrong way, the bearing line above is the mapping to read.",
                    ex, ey, left, left <= 2.0 ? "ARRIVED" : "not there"));
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[Keys] failed: " + ex.Message);
        }
        finally
        {
            if (hwnd != IntPtr.Zero) Release(hwnd);
        }
    }

    /// <summary>Accepts "x,y", or nothing at all: then it walks 20 yd along the current
    /// heading, which is enough to prove the mechanism.</summary>
    private static bool Target(string? spec, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (!string.IsNullOrWhiteSpace(spec))
        {
            string[] parts = spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return true;
        }

        double px, py, facing;
        if (!ClickAimPositions.TryRead(out px, out py) || !TryReadFacing(out facing))
        {
            Logging.Write("[Keys] no point given and the player cannot be read");
            return false;
        }

        double bearing = facing * 180.0 / Math.PI;
        x = px + 20.0 * Math.Cos(bearing * Math.PI / 180.0);
        y = py + 20.0 * Math.Sin(bearing * Math.PI / 180.0);
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Keys] no point given: walking 20 yd on the current facing ({0:F0} deg)", bearing));
        return true;
    }

    /// <summary>Checks the bearing/facing map and the wrap arithmetic, offline.</summary>
    public static string SelfTest()
    {
        int passed = 0, total = 0;
        var fails = new System.Text.StringBuilder();
        Action<string, bool> check = (name, ok) =>
        {
            total++;
            if (ok) passed++;
            else fails.Append(' ').Append(name);
        };

        // Uncalibrated: the client's facing is taken to be the bearing.
        _calibrated = false;
        check("mapIdentity", Math.Abs(FacingForBearing(90.0) - Math.PI / 2) < 1e-9);

        // BloogBot's case: slope 1, offset 0 -> same thing.
        _calibrated = true; _slope = 1.0; _offsetDeg = 0.0;
        check("mapSlope1", Math.Abs(FacingForBearing(45.0) - Math.PI / 4) < 1e-9);

        // A client whose facing zero is north and which counts the other way:
        // bearing 90 (east) needs facing -90.
        _slope = -1.0; _offsetDeg = 0.0;
        check("mapSlopeMinus1", Math.Abs(FacingForBearing(90.0) + Math.PI / 2) < 1e-9);

        // With an offset: bearing = facing + 90  ->  bearing 0 needs facing -90.
        _slope = 1.0; _offsetDeg = 90.0;
        check("mapOffset", Math.Abs(FacingForBearing(0.0) + Math.PI / 2) < 1e-9);

        // A scaled client: bearing = 2 * facing.
        _slope = 2.0; _offsetDeg = 0.0;
        check("mapScaled", Math.Abs(FacingForBearing(60.0) - Math.PI / 6) < 1e-9);

        _calibrated = false;
        _slope = 1.0;
        _offsetDeg = double.NaN;

        if (fails.Length == 0) return string.Format("KeySteer self-test {0}/{1} passed", passed, total);
        return string.Format("KeySteer self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }
}

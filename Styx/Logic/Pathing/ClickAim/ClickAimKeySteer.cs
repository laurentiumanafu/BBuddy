using System;
using System.Globalization;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Movement by turning the character with the keyboard and reading the heading back.
///
/// The starting point was BloogBot, whose whole movement layer is two calls
/// (BloogBot/Game/Objects/LocalPlayer.cs):
///
///     public void MoveToward(Position pos)
///     {
///         Face(pos);                         // SetFacing(bearing)
///         StartMovement(ControlBits.Front);  // hold forward
///     }
///
/// On 3.3.5a <c>Face</c> is a client FUNCTION. On the builds where BloogBot writes the
/// heading field instead, this project measured the write and it does not move anything:
/// on 2026-10-30 two probes wrote 81 deg and 151 deg and the character walked bearing 81
/// both times - it keeps its own facing - and the outgoing field at -0x1C behaved the
/// same. A field this client ignores is not a heading, however plausible the address.
///
/// What IS true is that the field is readable and equals the direction the character
/// actually walks. So the heading is not written, it is READ, and the turn keys are what
/// change it:
///
///     read facing -> tap arrow-left/right until facing == wanted -> hold W
///
/// No memory writes, no camera, no pixels, no drag gain, no field of view - the five
/// things every earlier attempt in this project died on. <see cref="Calibrate"/> measures
/// which arrow key turns which way and how fast, and whether the facing really is the
/// walked bearing; <see cref="Steer"/> closes the loop one tap at a time.
/// </summary>
internal static class ClickAimKeySteer
{
    /// <summary>Forward key. BloogBot's ControlBits.Front, posted as the key the client
    /// binds it to.</summary>
    private const int ForwardVk = 0x57;      // W
    private const int TurnLeftVk = 0x25;     // arrow left
    private const int TurnRightVk = 0x27;    // arrow right

    /// <summary>The key that cancels a client-side walk. Same key as forward on this
    /// client, which is why holding it also stops a click.</summary>
    private const int StopKeyVk = 0x57;

    private const int PollMs = 60;

    /// <summary>
    /// How long two position reads must agree before the character counts as standing
    /// still. ClickAimDestHunt has a constant of the same name, which is how this one
    /// came to be used here without being declared: a name that exists elsewhere in the
    /// same assembly is not an error until the compiler looks at it.
    /// </summary>
    private const int RestMs = 400;

    /// <summary>Longest wait for the character to come to rest before a probe.</summary>
    private const int RestTimeoutMs = 4000;

    // ---- learned convention -------------------------------------------------
    private static bool _calibrated;
    private static int _turnKey = TurnRightVk;
    private static double _turnKeyStepDeg = 30.0;   // signed, per 200 ms of _turnKey
    private static double _degPerTurnMs = 0.15;
    private static double _turnStepScale = 1.0;
    private static double _lastTurnError = double.NaN;
    private static bool _facingIsBearing = true;
    private static double _slope = 1.0;       // d(bearing deg) / d(facing-input)
    private static double _offsetDeg = double.NaN; // bearing = slope*input + offset

    private static bool _keyDown;

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
    /// Measures how the character turns, using the turn KEYS - and whether its facing is
    /// the direction it walks.
    ///
    /// Why not the facing write: on 2026-10-30 two runs wrote 51 and 81 degrees and the
    /// character walked bearing 51 and 81 - identity, i.e. it kept its OWN facing - and
    /// then ignored the next write completely (121 -> still 51, 151 -> still 81). Both
    /// the readable and the outgoing field behaved the same way, so the record field is
    /// not what the client moves by. It is, however, READABLE and it equals the walked
    /// bearing, so it is the feedback for a loop that turns with the keys instead:
    ///
    ///     read facing -> tap arrow-left/right until facing == wanted -> hold W
    ///
    /// Nothing is written to memory. The calibration only has to learn three things:
    /// which key increases the facing, how many degrees per millisecond, and whether the
    /// walked bearing really is the facing (the check that makes the rest valid).
    /// </summary>
    public static bool Calibrate(IntPtr hwnd, out string report)
    {
        report = "";

        string restReport;
        if (!StopAndRest(hwnd, out restReport))
        {
            report = "could not get the character to stand still: " + restReport;
            return false;
        }

        double f0;
        if (!TryReadFacing(out f0)) { report = "cannot read the facing field"; return false; }

        // ---- which way do the turn keys go ------------------------------------
        double right = TurnFor(hwnd, TurnRightVk, 200, out report);
        if (double.IsNaN(right)) return false;

        double left = TurnFor(hwnd, TurnLeftVk, 200, out report);
        if (double.IsNaN(left)) return false;

        if (Math.Abs(right) < 1.0 && Math.Abs(left) < 1.0)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "neither arrow key turned the character (right {0:F1} deg, left {1:F1} deg in 200 ms). "
                + "The facing field is readable but nothing here can change it - not a write, not the "
                + "turn keys - so the movement has to come from a client function "
                + "(BloogBot calls the game's own SetFacing + SendMovementUpdate).", right, left);
            return false;
        }

        // Keep the key that moved the facing MORE, and remember which WAY it moved it.
        // A boolean "right arrow is clockwise" is a guess; the signed step is measured,
        // and it is what Steer compares the error's sign against.
        _turnKey = Math.Abs(right) >= Math.Abs(left) ? TurnRightVk : TurnLeftVk;
        _turnKeyStepDeg = _turnKey == TurnRightVk ? right : left;

        double perMs = Math.Abs(_turnKeyStepDeg) / 200.0;
        if (perMs < 0.002 || perMs > 1.0)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "the turn keys moved the facing {0:F2} deg/ms, which is not a plausible rate; "
                + "right {1:F1} deg, left {2:F1} deg in 200 ms", perMs, right, left);
            return false;
        }
        _degPerTurnMs = perMs;

        // ---- is the walked bearing the facing? --------------------------------
        double before;
        if (!TryReadFacing(out before)) { report = "lost the facing field"; return false; }

        double sx, sy;
        if (!ClickAimPositions.TryRead(out sx, out sy)) { report = "cannot read the position"; return false; }

        SetForward(hwnd, true);
        Thread.Sleep(1200);
        SetForward(hwnd, false);
        Thread.Sleep(250);

        double fx, fy;
        if (!ClickAimPositions.TryRead(out fx, out fy)) { report = "lost the position"; return false; }

        double walked = ClickAimMath.Len(fx - sx, fy - sy);
        if (walked < 1.5)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "the turn keys work ({0:F1} deg in 200 ms) but the forward key moved the character "
                + "{1:F1} yd in 1.2 s. Something is in the way, or the key is not reaching the "
                + "client.", Math.Max(Math.Abs(right), Math.Abs(left)), walked);
            return false;
        }

        double bearing = ClickAimMath.HeadingDeg(fx - sx, fy - sy);
        double facingDeg = before * 180.0 / Math.PI;
        double gap = Math.Abs(ClickAimMath.WrapDeg(bearing - facingDeg));

        _facingIsBearing = gap < 25.0;
        _calibrated = true;
        _slope = 1.0;
        _offsetDeg = 0.0;

        report = string.Format(CultureInfo.InvariantCulture,
            "turn keys: right arrow {0:+0.0;-0.0} deg, left arrow {1:+0.0;-0.0} deg per 200 ms "
            + "({2:F3} deg/ms). Facing read {3:F0} deg, walked bearing {4:F0} deg - {5}. "
            + "Steering by turning the keys and holding forward; nothing is written to memory.",
            right, left, _degPerTurnMs, facingDeg, bearing,
            _facingIsBearing
                ? "the facing IS the walked direction, so the loop closes on it"
                : "THE FACING IS NOT THE WALKED DIRECTION (" + gap.ToString("F0", CultureInfo.InvariantCulture)
                  + " deg apart), so the loop closes on measured bearings instead");
        return true;
    }


    /// <summary>
    /// Releases everything and waits until two position reads agree to 5 cm.
    ///
    /// A click-walk or a held key both take a moment to actually stop, and a probe
    /// started in that moment measures the OLD walk - which is exactly the 51/51 result
    /// in the 12:04 log.
    /// </summary>
    public static bool StopAndRest(IntPtr hwnd, out string report)
    {
        report = "";
        Release(hwnd);

        if (hwnd != IntPtr.Zero)
        {
            ClickAimInput.TapKey(hwnd, StopKeyVk, 80);
            ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 80);
        }

        double lx = 0, ly = 0;
        bool have = false;
        int still = 0;

        for (int waited = 0; waited < RestTimeoutMs; waited += PollMs)
        {
            Thread.Sleep(PollMs);
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;

            if (!have) { lx = nx; ly = ny; have = true; continue; }

            if (ClickAimMath.Len(nx - lx, ny - ly) < 0.05) still += PollMs;
            else still = 0;

            lx = nx;
            ly = ny;

            if (still >= RestMs) return true;
        }

        report = string.Format(CultureInfo.InvariantCulture,
            "still moving {0} ms after the stop key (last two reads never agreed to 5 cm)",
            RestTimeoutMs);
        return false;
    }

    /// <summary>Turns for <paramref name="ms"/> and returns the change in facing, in degrees.</summary>
    private static double TurnFor(IntPtr hwnd, int vk, int ms, out string report)
    {
        report = "";
        double a, b;
        if (!TryReadFacing(out a)) { report = "cannot read the facing field"; return double.NaN; }
        ClickAimInput.TapKey(hwnd, vk, ms);
        Thread.Sleep(180);
        if (!TryReadFacing(out b)) { report = "lost the facing field"; return double.NaN; }
        return ClickAimMath.WrapDeg((b - a) * 180.0 / Math.PI);
    }

    /// <summary>
    /// One steering tick toward (tx, ty). Turns with the keys when the facing is off, and
    /// holds the forward key when it is not. Returns the distance still to go.
    ///
    /// This is a closed loop on the READABLE facing field: each tick reads the facing,
    /// turns for a duration proportional to the remaining error, and reads again.
    ///
    /// A proportional tap alone does NOT guarantee convergence - with the real turn rate
    /// 20x the assumed one it overshoots, and every overshoot costs a tick to undo (that
    /// combination oscillated for 60 ticks in a model of it). So the step size is adapted:
    /// if the error grew or changed sign, the tap was too long and is halved.
    /// </summary>
    public static bool Steer(double tx, double ty, IntPtr hwnd, bool forward, out double remaining)
    {
        remaining = 0;

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py)) return false;

        remaining = ClickAimMath.Len(tx - px, ty - py);
        double wantedBearing = ClickAimMath.HeadingDeg(tx - px, ty - py);

        double facingRad;
        if (!TryReadFacing(out facingRad))
        {
            // No facing: walk anyway. Wrong direction is still information, and the
            // caller measures the result from positions.
            SetForward(hwnd, forward);
            return true;
        }

        double facingDeg = facingRad * 180.0 / Math.PI;

        /* The error is measured against the facing, which is only the direction of travel
         * when the calibration said so. If it did not, the facing is still the thing the
         * client turns, so steering on it is the best available aim - but the caller is
         * told (in the calibration report) that the walk may differ from it, and the
         * caller's progress check is what decides whether the leg is working. */
        double error = ClickAimMath.WrapDeg(wantedBearing - facingDeg);

        if (Math.Abs(error) > TurnToleranceDeg)
        {
            /* Turn in place. One tap per tick, sized to the error but capped: a tap that
             * is too long overshoots, and overshoot on a turn costs a whole tick to undo.
             * The key pressed is whichever one moves the facing the way the error needs. */
            SetForward(hwnd, false);

            /* Learn the turn rate instead of trusting the calibration.
             *
             * A tap sized as `error / degPerMs` overshoots if the real rate is higher than
             * the measured one, and an overshoot costs a whole tick to undo - modelled with
             * the true rate 3x the assumed one it oscillated for 60 ticks without settling.
             * So if this tick's error is bigger than last tick's, or has changed sign, the
             * step was too long: halve it. It grows back slowly, so a first tick that was
             * too cautious does not stay that way. */
            if (!double.IsNaN(_lastTurnError))
            {
                bool overshot = Math.Sign(error) != Math.Sign(_lastTurnError)
                                || Math.Abs(error) > Math.Abs(_lastTurnError) + 0.5;
                if (overshot) _turnStepScale = Math.Max(0.03, _turnStepScale * 0.5);
                else _turnStepScale = Math.Min(1.0, _turnStepScale * 1.15);
            }
            _lastTurnError = error;

            int ms = (int)Math.Round(Math.Abs(error) / Math.Max(0.02, _degPerTurnMs) * _turnStepScale);
            if (ms < 10) ms = 10;   // the shortest tap that still registers
            if (ms > MaxTurnTapMs) ms = MaxTurnTapMs;

            // Turn with the measured key when its measured direction matches the error's
            // sign; otherwise the other arrow. Derived from the sign of the step, so it
            // works even if both arrows turn the same way (the usual client behaviour is
            // +-equal, but this does not assume it).
            bool sameWay = Math.Sign(error) == Math.Sign(_turnKeyStepDeg);
            int vk = sameWay ? _turnKey : _turnKey == TurnRightVk ? TurnLeftVk : TurnRightVk;

            ClickAimInput.TapKey(hwnd, vk, ms);
            Thread.Sleep(60);

            if (Math.Abs(error) > 45.0)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Keys] facing {0:F0} deg, want {1:F0} deg: {2:+0;-0} deg -> {3} for {4} ms",
                    facingDeg, wantedBearing, error,
                    vk == TurnRightVk ? "right arrow" : "left arrow", ms));
            }

            return true;
        }

        _lastTurnError = double.NaN;
        SetForward(hwnd, forward);
        return true;
    }

    /// <summary>Degrees of error that still counts as facing the point.</summary>
    private const double TurnToleranceDeg = 6.0;

    /// <summary>Longest single turn tap. A 180 deg turn is several taps, re-measured each.</summary>
    private const int MaxTurnTapMs = 400;

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

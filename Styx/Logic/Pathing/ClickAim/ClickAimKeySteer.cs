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
    // Measured per key, because the two arrows are not interchangeable: one turns the
    // facing up and one down, and they need not turn at the same rate.
    private static IntPtr _lastHwnd = IntPtr.Zero;  // the window the keys went to
    private static int _turnPosVk = TurnRightVk;    // increases the facing
    private static int _turnNegVk = TurnLeftVk;     // decreases it
    private static double _turnPosRate = 0.15;      // deg per ms, per key
    private static double _turnNegRate = 0.15;
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
        _lastHwnd = hwnd;                       // so a caller without one can still stop us
        if (down == _keyDown) return;

        ClickAimInput.HoldKey(hwnd, ForwardVk, down);
        _keyDown = down;
    }

    public static void Release(IntPtr hwnd)
    {
        SetForward(hwnd, false);
    }

    /// <summary>
    /// Lets the forward key up without being told which window. The route runner has no
    /// window handle, and leaving forward held turns "stop here and fight" into "run past
    /// the mob" - which is exactly what the 16:07 run did for two and a half minutes.
    /// </summary>
    public static void ReleaseHeld()
    {
        if (_lastHwnd != IntPtr.Zero) SetForward(_lastHwnd, false);
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
        return Calibrate(hwnd, double.NaN, double.NaN, out report);
    }

    /// <summary>
    /// As above, with a goal to aim the probe walk at.
    ///
    /// The probe has to walk a couple of yards to answer "is the facing the direction it
    /// walks", and the first version of this walked those yards on whatever heading the
    /// character happened to be facing - up to 20 yd of it, in a random direction, which
    /// is exactly the "walks 20 yards ahead and does useless calibration" this was
    /// supposed to replace. Given a target it turns to face it first, so the probe is the
    /// first few yards of the walk itself.
    /// </summary>
    public static bool Calibrate(IntPtr hwnd, double targetX, double targetY, out string report)
    {
        report = "";

        string restReport;
        if (!StopAndRest(hwnd, out restReport))
        {
            report = "could not get the character to stand still: " + restReport;
            return false;
        }

        // ---- which way do the turn keys go ------------------------------------
        /* Each key is measured TWICE and the bigger step is kept.
         *
         * The 13:11 run measured right arrow -18.0 deg and left arrow +36.7 deg in the
         * same 200 ms, which reads like an asymmetry between the keys and is not one:
         * the first keystroke after ReleaseAll is partly swallowed by the client (the
         * same "hardware action" gate that makes the first click after a pause do
         * nothing). Believing the halved number makes every turn tap twice as long as
         * it should be - which is a stutter, not a failure, so nothing reports it. */
        double r1 = TurnFor(hwnd, TurnRightVk, CalibrateTapMs, out report);
        if (double.IsNaN(r1)) return false;
        double r2 = TurnFor(hwnd, TurnRightVk, CalibrateTapMs, out report);
        if (double.IsNaN(r2)) return false;
        double l1 = TurnFor(hwnd, TurnLeftVk, CalibrateTapMs, out report);
        if (double.IsNaN(l1)) return false;
        double l2 = TurnFor(hwnd, TurnLeftVk, CalibrateTapMs, out report);
        if (double.IsNaN(l2)) return false;

        double right = Math.Abs(r2) >= Math.Abs(r1) ? r2 : r1;
        double left = Math.Abs(l2) >= Math.Abs(l1) ? l2 : l1;

        if (Math.Abs(right) < 1.0 && Math.Abs(left) < 1.0)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "neither arrow key turned the character (right {0:F1} deg, left {1:F1} deg in 200 ms). "
                + "The facing field is readable but nothing here can change it - not a write, not the "
                + "turn keys - so the movement has to come from a client function "
                + "(BloogBot calls the game's own SetFacing + SendMovementUpdate).", right, left);
            return false;
        }

        /* One key must turn the facing UP and the other DOWN, or there is no loop to
         * close: a tap can only be aimed in one direction if the opposite key exists. */
        if (right > 0 && left < 0)
        {
            _turnPosVk = TurnRightVk;
            _turnNegVk = TurnLeftVk;
            _turnPosRate = right / CalibrateTapMs;
            _turnNegRate = -left / CalibrateTapMs;
        }
        else if (left > 0 && right < 0)
        {
            _turnPosVk = TurnLeftVk;
            _turnNegVk = TurnRightVk;
            _turnPosRate = left / CalibrateTapMs;
            _turnNegRate = -right / CalibrateTapMs;
        }
        else
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "both arrows turn the facing the same way (right {0:+0.0;-0.0} deg, left "
                + "{1:+0.0;-0.0} deg per {2} ms), so no key can turn it back - there is no closed "
                + "loop to steer with", right, left, CalibrateTapMs);
            return false;
        }

        if (_turnPosRate < 0.002 || _turnPosRate > 1.0 || _turnNegRate < 0.002 || _turnNegRate > 1.0)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "the turn keys moved the facing {0:F3} deg/ms up and {1:F3} deg/ms down, which is "
                + "not a plausible rate; right {2:F1} deg, left {3:F1} deg per {4} ms",
                _turnPosRate, _turnNegRate, right, left, CalibrateTapMs);
            return false;
        }

        // ---- is the walked bearing the facing? --------------------------------
        double sx, sy;
        if (!ClickAimPositions.TryRead(out sx, out sy)) { report = "cannot read the position"; return false; }

        /* Face the goal before the probe walk, so its few yards are progress rather than
         * a random 8 yd step. Without a goal, the probe walks on the current facing. */
        bool aimed = false;
        if (!double.IsNaN(targetX) && !double.IsNaN(targetY))
        {
            aimed = AimAt(hwnd, targetX, targetY, sx, sy);
            if (!aimed)
            {
                Logging.Write("[Keys] could not turn onto the goal within a second; the probe walk "
                              + "will run on the current facing");
            }
        }

        double before;
        if (!TryReadFacing(out before)) { report = "lost the facing field"; return false; }

        SetForward(hwnd, true);
        Thread.Sleep(ProbeWalkMs);
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
            "turn keys: right arrow {0:+0.0;-0.0} deg, left arrow {1:+0.0;-0.0} deg per {2} ms "
            + "({3:F3} deg/ms up with VK 0x{4:X2}, {5:F3} deg/ms down with VK 0x{6:X2}). "
            + "Facing read {7:F0} deg, walked bearing {8:F0} deg - {9}. Steering by turning the keys "
            + "and holding forward; nothing is written to memory.",
            right, left, CalibrateTapMs, _turnPosRate, _turnPosVk, _turnNegRate, _turnNegVk,
            facingDeg, bearing,
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

    /// <summary>
    /// Turns in place until the character faces (tx, ty). Returns false if it could not
    /// within <see cref="AimTicks"/> taps.
    /// </summary>
    private static bool AimAt(IntPtr hwnd, double tx, double ty, double px, double py)
    {
        for (int i = 0; i < AimTicks; i++)
        {
            double facing;
            if (!TryReadFacing(out facing)) return false;

            double want = ClickAimMath.HeadingDeg(tx - px, ty - py);
            double error = ClickAimMath.WrapDeg(want - facing * 180.0 / Math.PI);
            if (Math.Abs(error) <= TurnToleranceDeg) return true;

            double rem;
            if (!Steer(tx, ty, hwnd, false, out rem)) return false;
            Thread.Sleep(80);

            if (!ClickAimPositions.TryRead(out px, out py)) return false;
        }
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
            /* One tap per tick, sized to the error but capped: a tap that is too long
             * overshoots, and overshoot on a turn costs a whole tick to undo. The key is
             * the one measured to turn the facing in the direction the error needs.
             *
             * Turn while still running when the error is small.
             *
             * Cutting forward for every tap is what made the 13:11 walk choppy: each few
             * degrees of correction stopped the character, turned, and started it again.
             * A player holds W and taps the arrow keys, and so can this: the turn rate
             * does not depend on movement, so a 20 deg correction is a 0.1 s arc of about
             * 2 yd. Only a big error (a new leg pointing back the way we came) is worth
             * standing still for. */
            if (!forward || Math.Abs(error) > TurnWhileRunningDeg) SetForward(hwnd, false);

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

            bool turnUp = error > 0;
            int vk = turnUp ? _turnPosVk : _turnNegVk;
            double rate = turnUp ? _turnPosRate : _turnNegRate;

            int ms = (int)Math.Round(Math.Abs(error) / Math.Max(0.02, rate) * _turnStepScale);
            if (ms < 10) ms = 10;   // the shortest tap that still registers
            if (ms > MaxTurnTapMs) ms = MaxTurnTapMs;

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

    /// <summary>
    /// Turns in place until the character faces (tx, ty), without taking a step.
    ///
    /// This exists because "the target is behind the camera or off the screen" killed every
    /// engage in the 16:07 run, and "you are not facing your target" killed every cast after
    /// the first one in the 16:42 run: a right-click can only select what is on screen and a
    /// spell can only be cast at something in front.
    ///
    /// The 16:42 log also showed that the calibrated rate cannot be trusted inside a fight:
    /// a 21 ms arrow tap moved the facing 23 deg, six times what the walk calibration says
    /// (0.18 deg/ms), and between two taps the facing drifted another 58 deg with nothing
    /// pressed. So this does not compute a tap length from the table and hope. It:
    ///
    ///   1. measures the rate from ITS OWN LAST TAP (deg moved / ms held), clamped to a
    ///      sane range, and re-measures every tick;
    ///   2. releases BOTH arrow keys after every tap, so a key that the client treats as
    ///      still held cannot keep spinning the character while a 3.2 s cast is waited out;
    ///   3. if a tap moves the facing the wrong way, swaps the two keys and carries on -
    ///      that is the failure that ends with the character's back to the mob.
    ///
    /// Returns true when the facing is within <paramref name="toleranceDeg"/> of the bearing
    /// to the point, or when the facing cannot be read at all (the caller has nothing better
    /// to try in that case).
    /// </summary>
    public static bool Face(double tx, double ty, IntPtr hwnd, double toleranceDeg, int maxTicks)
    {
        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py)) return false;
        if (ClickAimMath.Len(tx - px, ty - py) < 0.5) return true;      // standing on it

        double facingRad;
        if (!TryReadFacing(out facingRad)) return true;                // nothing to steer on

        double wanted = ClickAimMath.HeadingDeg(tx - px, ty - py);
        double rate = Math.Max(0.05, Math.Min(_turnPosRate, _turnNegRate));
        int lastTapMs = 0;
        double lastDelta = 0;
        bool swapped = false;      // swap the key pair at most once per call

        for (int tick = 0; tick <= maxTicks; tick++)
        {
            /* The bearing is recomputed every tick: the character may be walking while it
             * turns, and the mob it is aiming at is usually moving toward it. */
            double cx, cy;
            if (ClickAimPositions.TryRead(out cx, out cy) && ClickAimMath.Len(tx - cx, ty - cy) > 0.5)
            {
                px = cx;
                py = cy;
                wanted = ClickAimMath.HeadingDeg(tx - px, ty - py);
            }

            double error = ClickAimMath.WrapDeg(wanted - facingRad * 180.0 / Math.PI);
            if (Math.Abs(error) <= toleranceDeg)
            {
                ReleaseArrows(hwnd);
                return true;
            }

            if (lastTapMs > 0 && Math.Abs(lastDelta) > 0.5)
            {
                /* What the last tap actually did, not what the table says it should do. */
                double measured = Math.Abs(lastDelta) / lastTapMs;
                if (measured > 0.02 && measured < 5.0)
                    rate = 0.6 * measured + 0.4 * rate;
            }

            double ms = Math.Abs(error) / Math.Max(0.05, rate);
            if (ms < 25) ms = 25;                     // a shorter tap is not a turn
            if (ms > 220) ms = 220;                   // overshoot costs a whole tick to undo

            bool up = error > 0;
            int vk = up ? _turnPosVk : _turnNegVk;

            ReleaseArrows(hwnd);
            ClickAimInput.TapKey(hwnd, vk, (int)Math.Round(ms));
            ReleaseArrows(hwnd);                      // never leave a turn key down
            Thread.Sleep(70);

            double nextFacing;
            if (!TryReadFacing(out nextFacing)) return true;

            double delta = ClickAimMath.WrapDeg((nextFacing - facingRad) * 180.0 / Math.PI);
            if (!swapped && Math.Abs(delta) > 2.0 && Math.Sign(delta) != Math.Sign(error))
            {
                swapped = true;

                /* The key turned it the wrong way: swap the pair, permanently, and say so.
                 * This is the difference between turning onto a mob and turning one's back
                 * on it. */
                int swap = _turnPosVk;
                _turnPosVk = _turnNegVk;
                _turnNegVk = swap;
                double swapRate = _turnPosRate;
                _turnPosRate = -_turnNegRate;
                _turnNegRate = -swapRate;

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Keys] the turn keys were the wrong way round (a {0:F0} ms tap moved the facing "
                    + "{1:+0;-0} deg while {2:+0;-0} was wanted) - swapped them", ms, delta, error));
            }

            lastTapMs = (int)Math.Round(ms);
            lastDelta = delta;
            facingRad = nextFacing;
        }

        ReleaseArrows(hwnd);
        return false;
    }

    /// <summary>
    /// Lets both arrow keys up. Cheap, and the alternative - one of them staying logically
    /// down while the bot waits three seconds for a cast - is a character spinning on the
    /// spot, which is what "it turned its back on the monster" looked like.
    /// </summary>
    public static void ReleaseArrows(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) hwnd = _lastHwnd;
        if (hwnd == IntPtr.Zero) return;
        ClickAimInput.HoldKey(hwnd, TurnLeftVk, false);
        ClickAimInput.HoldKey(hwnd, TurnRightVk, false);
    }

    /// <summary>Degrees of error that still counts as facing the point.</summary>
    private const double TurnToleranceDeg = 6.0;

    /// <summary>Longest single turn tap. A 180 deg turn is several taps, re-measured each.</summary>
    private const int MaxTurnTapMs = 400;

    /// <summary>How long each calibration tap holds an arrow key.</summary>
    private const int CalibrateTapMs = 200;

    /// <summary>Long enough to walk 3-4 yd from a standstill, short enough not to matter.</summary>
    private const int ProbeWalkMs = 800;

    /// <summary>Turn taps allowed while aiming the probe and the first leg.</summary>
    private const int AimTicks = 12;

    /// <summary>
    /// Beyond this much error the character is stopped before turning. Up to it, the turn
    /// is made while still running, which is what stops the walk stuttering: a 20 deg
    /// correction at 184 deg/s is a 0.1 s arc of about 2 yd, so stopping for it is a
    /// worse approximation of walking than turning through it.
    /// </summary>
    private const double TurnWhileRunningDeg = 60.0;

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

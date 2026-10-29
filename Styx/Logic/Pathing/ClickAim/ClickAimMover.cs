using System;
using System.Globalization;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Pathing;

/// <summary>
/// Movement for a client whose click-to-move function is unknown (64-bit build
/// 70291). It is not a port of the 3.3.5a <c>ClickToMove</c> call - that call
/// does not exist here - it is the field-tested replacement:
///
///   * synthesise a right-click at a client-space pixel offset,
///   * measure where the character actually went after the click,
///   * learn the camera sensitivity from those measurements,
///   * plan the next click from the measurement, not from a model.
///
/// The whole thing is tick-driven: <see cref="MoveTowards"/> is called by the
/// pathing stack with "aim at this point now" semantics and never blocks for
/// more than one click, so it drops into <see cref="Navigator.PlayerMover"/>
/// without changing any caller.
///
/// Field results on which this is based (Tools/WowBClickAim, 2026-10-29):
/// a 40 yd goal reached in 2 legs at 0.77 yd remaining, 100 yd covered in 5 legs
/// with no obstruction, click sensitivity -0.107 to -0.127 deg/px depending on
/// the run, drag -0.18 to -0.22 deg/px, walk speed 5.9-6.9 yd/s.
/// </summary>
public sealed class ClickAimMover : IPlayerMover
{
    private enum Phase
    {
        Idle,
        Walking
    }

    // ---- tunables, all measured rather than guessed ------------------------
    private const double MinHeadingWalk = 3.0;      // shorter walks give noisy yaws
    private const double RestGapMs = 400.0;         // still this long = stopped
    private const int LegArmMs = 800;               // do not accept "rest" before this
    private const int MaxLegMs = 20000;             // a click can mean ~100 yd
    private const double WanderMaxYd = 120.0;       // more than this is not a click
    private const double BlockedMove = 0.5;         // a leg shorter than this = stuck
    private const double PassRadius = 6.0;          // came this close, then grew ...
    private const double PassGiveUp = 0.5;          // ... by this much = passed it
    private const double DetourDeg = 55.0;
    private const int MaxDetours = 6;
    private const int StopKeyVk = 0x57;             // W also cancels a click walk

    /// <summary>
    /// How far one click is expected to walk, from the field measurements of the hop
    /// ladder (-43 px 10.3 yd, -60 px 12.6, -71 px 8.5, -105 px 10.6). Used for the
    /// runaway bound and the brake, never to decide the click height itself.
    /// </summary>
    private const double ExpectedHopYd = 13.0;

    private readonly object _gate = new object();
    private readonly ClickAimParams _p = new ClickAimParams();
    private readonly ClickAimCalibration.Result _cal = new ClickAimCalibration.Result();

    private Phase _phase = Phase.Idle;
    private ClickAimLeg _leg;
    private int _legStartMs;
    private double _legX, _legY;            // position when the leg was clicked
    private double _lastX, _lastY;
    private int _lastMoveMs;
    private double _bestRemaining;
    private double _legWantYd;              // distance left when the leg was clicked
    private int _scaleSamples;              // accepted click-scale measurements

    // A click whose reach has not been measured yet: from where it was clicked,
    // how tall it was, and how far the character has gone since.
    private double _targetZ = double.NaN;   // height of the point being walked to
    private double _zeroAtClick;            // HeadingAtZero when the last click was posted
    private int _clickAtMs;
    private int _pendingDy;
    private double _pendingX, _pendingY;
    private double _pendingWantYd;
    private int _pendingStops;
    private bool _pendingProjected;
    private bool _legProjected;

    // Hop heights in the order they are tried. Every one of these was seen to move
    // the character on this client; the ones past -180 px were not, which is why
    // they are not on the list.
    private static readonly int[] HopLadder = { -70, -100, -50, -130, -90, -160 };
    private int _hopIndex;
    private int _hopDy = HopLadder[0];
    private int _deadClickStreak;
    private int _deadClicks;                // dead clicks in a row, ladder rotations included
    private int _projectedClicks;
    private bool _projectionWorks;
    private int _projectionRejects;
    private double _aimBest = double.MaxValue;
    private int _aimWorse;
    private double _aimGainSign = 1.0;
    private bool _projectedThisDispatch;

    /// <summary>
    /// True until two centre-and-offset clicks have measured the aim. The sign of the
    /// click channel is not knowable without an observation: on 2026-10-29 it was
    /// negative when this client needed positive, and every leg walked backwards.
    /// </summary>
    private bool _needSignProbe = !_sessionAimKnown;

    /// <summary>How far off centre the probe click goes. Big enough to resolve a sign.</summary>
    private const int SignProbePx = 170;

    /// <summary>Inside this many degrees of the target heading, click instead of turning.</summary>
    private const double AimToleranceDeg = 6.0;

    /// <summary>Largest single camera rotation, so a mistake cannot spin the character.</summary>
    private const double MaxAimDragPx = 320.0;

    /// <summary>
    /// Height of the click used by the camera-aim path. A "far click" that is known to
    /// move; it does not have to be accurate, because the next tick re-aims from
    /// wherever this one ends.
    /// </summary>
    private const int AimHopDy = -140;

    private int _probeAttempts;
    private int _stuck;
    private int _detourAttempt;
    private bool _warnedStuck;
    private bool _warnedUnsupported;

    /* What a previous walk in this process already measured. The aim channels are a
     * property of the client and the camera, not of one walk, so paying for the probe
     * on every goal would be silly - but the probe is only two legs, and it is far
     * better than aiming with a sign nobody has checked. */
    private static bool _sessionAimKnown;
    private static double _sessionDegPerPx;
    private static double _sessionZero;
    private static int _sessionHopDy = int.MinValue;

    public ClickAimMover()
    {
        _cal = ClickAimCalibration.Load();
        _p.DegPerPx = _cal.Ok ? _cal.DegPerPx : -0.115;
        // A measured click scale is worth having from the first click of a session:
        // the runtime learner needs a leg to settle before it knows anything, and
        // the assumed value is 30% out on this client.
        if (_cal.ScaleSamples > 0 && _cal.YdPerDyPx > 0.01) _p.YdPerDyPx = _cal.YdPerDyPx;
        _p.DegPerDragPx = _cal.DragSamples > 0 ? _cal.DegPerDragPx : -0.21;
        _p.DegPerKeyMs = _cal.KeySamples > 0 ? _cal.DegPerKeyMs : 0.0;
        if (_sessionAimKnown)
        {
            _p.DegPerPx = _sessionDegPerPx;
            _p.HeadingAtZero = _sessionZero;
        }
        if (_sessionHopDy != int.MinValue) _hopDy = _sessionHopDy;

        _p.DegPerPxRef = _p.DegPerPx;
        _p.DegPerDragPxRef = _p.DegPerDragPx;
        _p.ConeHalfDeg = ClickAimMath.ConeHalfDeg(_p.DegPerPx, _p.HalfWidth);
        double cx, cy, cz;
        if (ClickAimPositions.TryReadCamera(out cx, out cy, out cz))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] camera readable at ({0:F1}, {1:F1}, {2:F1}): clicks are aimed at world points "
                + "by projecting them onto the screen ({3:F0} deg field of view), not by a learned pixel model",
                cx, cy, cz, _p.CameraFovDeg));
        }
        else
        {
            Logging.Write("[ClickAim] camera position is not readable: falling back to the learned pixel model, "
                          + "which needs the first clicks of the walk to measure itself");
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] mover ready; calibration {0}{1}", _cal,
            _cal.Ok ? "" : " (none found - using field defaults)"));
    }

    public int Legs { get; private set; }

    /// <summary>Blind detours spent on the current goal; the budget is <see cref="DetourBudget"/>.</summary>
    public int Detours => _detourAttempt;
    public int Runaways { get; private set; }

    /// <summary>
    /// How many of the mover's own blind detours to spend before admitting that
    /// clicking harder is not going to work.
    ///
    /// Six is right when nothing else can plan: it is the only way out of a bad
    /// spot. On a mesh-routed leg it is wrong - each detour costs a whole leg
    /// (up to 20 s) and the answer is a new path, not another heading, so the
    /// caller drops this to 1 and re-plans on the first refusal.
    /// </summary>
    public int DetourBudget { get; set; } = MaxDetours;

    /// <summary>
    /// True once <see cref="DetourBudget"/> is spent without reaching the goal.
    /// A caller that can re-plan watches this instead of waiting out its timeout,
    /// which is what turned a stuck leg into ninety seconds of walking into a wall.
    /// </summary>
    public bool Blocked { get; private set; }

    /// <summary>Forgets the budget and the blocked flag, e.g. for a fresh goal.</summary>
    public void ResetBlocked()
    {
        Blocked = false;
        _detourAttempt = 0;
        _warnedStuck = false;
        _stuck = 0;
    }

    /// <summary>
    /// Distance to the last target that was asked for. Starts at MaxValue so a
    /// caller waiting for "arrived" cannot be satisfied before the first read.
    /// </summary>
    public double LastRemaining { get; private set; } = double.MaxValue;

    // ------------------------------------------------------------- IPlayerMover
    public void Move(WoWMovement.MovementDirection direction)
    {
        switch (direction)
        {
            case WoWMovement.MovementDirection.Forward:
                Pump();
                break;
            case WoWMovement.MovementDirection.Backwards:
            case WoWMovement.MovementDirection.StrafeLeft:
            case WoWMovement.MovementDirection.StrafeRight:
                if (!_warnedUnsupported)
                {
                    _warnedUnsupported = true;
                    Logging.Write("[ClickAim] only forward and click-to-move are synthesised on this build; "
                                  + "back/strafe requests stop the character instead");
                }
                MoveStop();
                break;
            default:
                break;
        }
    }

    public void MoveTowards(WoWPoint location)
    {
        lock (_gate)
        {
            _targetZ = location.Z;
            Tick(location.X, location.Y, Environment.TickCount & int.MaxValue);
        }
    }

    public void MoveStop()
    {
        lock (_gate)
        {
            IntPtr hwnd = Handle();
            if (hwnd != IntPtr.Zero) ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 120);
            _phase = Phase.Idle;
            _bestRemaining = double.MaxValue;
        }
    }

    // ------------------------------------------------------------------- tick
    private void Tick(double tx, double ty, int now)
    {
        double px, py;
        if (!PlayerPosition(out px, out py)) return;

        double rem = ClickAimMath.Len(tx - px, ty - py);
        LastRemaining = rem;

        double step = ClickAimMath.Len(px - _lastX, py - _lastY);
        bool moving = step > 0.08;
        if (moving) _lastMoveMs = now;
        _lastX = px;
        _lastY = py;

        if (_phase == Phase.Walking) { ContinueLeg(px, py, rem, now); return; }

        // A click that has not been measured yet is settled here: while the
        // character keeps walking it is braked if it runs far past what the click
        // was asked to do, and once it is standing still the click's real reach is
        // known and the next hop can be planned with it.
        ResolvePendingClick(px, py, moving, now);

        // Between legs: nothing is dispatched until the character is standing
        // still, or the next click lands on top of a walk that is still running.
        if (moving || (now - _lastMoveMs) < RestGapMs) return;

        if (rem <= _p.Tolerance) { return; }

        if (_stuck >= 2)
        {
            if (_detourAttempt >= DetourBudget)
            {
                Blocked = true;
                if (!_warnedStuck)
                {
                    _warnedStuck = true;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] BLOCKED: {0} detours and the legs still move under {1:F1} yd. "
                        + "Something is between the character and the target that clicks cannot get around.",
                        _detourAttempt, BlockedMove));
                }
                return;
            }
            StartDetour(px, py, now);
            return;
        }

        /* The boot click exists only for the fallback: with no camera to project
         * through, the first click has to walk once to learn which way "centre" is.
         * On 2026-10-29 this click never stopped being the click - LearnAim refused
         * the leg because the zero it was told to learn against was itself NaN, so
         * HeadingAtZero stayed NaN, so every tick posted another boot click and the
         * character walked wherever the camera happened to face. Hence: with a camera
         * the zero comes from the facing, without one LearnAim adopts the walk. */
        if (double.IsNaN(_p.HeadingAtZero))
        {
            if (ClickAimPositions.TryReadCamera(out _, out _, out _))
            {
                double facingRad;
                _p.HeadingAtZero = ClickAimPositions.TryReadFacing(out facingRad)
                    ? facingRad * 180.0 / Math.PI
                    : 0.0;
            }
            else
            {
                ClickAimLeg boot = default(ClickAimLeg);
                boot.Turn = ClickAimMath.TurnKindNone;
                boot.Dy = _hopDy;
                _legWantYd = rem;
                Dispatch(boot, px, py, now);
                return;
            }
        }

        /* The probe is for the pixel model only. With a readable camera the click is
         * aimed by projecting the world point, which needs no measured sign - and on
         * 2026-10-29 the probe fired first and spent four clicks (and the first 4 yd of
         * the walk) proving nothing. */
        // Gate on the camera, not on _projectionWorks: that flag is only known after a
        // projection has been attempted, which happens further down this method - so
        // gating on it let the probe run first, which is exactly what happened.
        if (_needSignProbe && !ClickAimPositions.TryReadCamera(out _, out _, out _))
        {
            // Two clicks buy the whole model: a centre click (the boot click, which
            // already ran) says what heading a centre click walks, and this offset
            // click says which way and how far an offset turns it. It walks ~10 yd,
            // possibly in the wrong direction, and then the aim is right for the rest
            // of the session.
            ClickAimLeg probe = default(ClickAimLeg);
            probe.Turn = ClickAimMath.TurnKindNone;
            probe.ClickPx = SignProbePx;
            probe.Dy = _hopDy;
            _probeAttempts++;
            if (_probeAttempts > 4)
            {
                _needSignProbe = false;
                Logging.Write("[ClickAim] the aim probe clicked four times without the character walking "
                              + "far enough to measure; going on with the unchecked model");
            }
            else
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] aim probe {0}/4: one {1} px click to find which way an offset turns the "
                    + "walk (the model's sign has never been checked on this client)",
                    _probeAttempts, SignProbePx));
            }
            if (_needSignProbe)
            {
                _legWantYd = rem;
                Dispatch(probe, px, py, now);
                return;
            }
        }

        /* Geometry first: click the screen position of the point we want to stand on.
         *
         * This replaces the whole learned model when the camera can be read. The point
         * is a real world position with a real height, so the click lands on the walk
         * the mmaps planned rather than somewhere down a ray, and the distance walked
         * is the distance to that point instead of whatever a pixel offset happened to
         * be worth that second. */
        // With a camera there is nothing for the probe to measure.
        if (_needSignProbe) _needSignProbe = false;

        /* The primary aim: turn the camera to face the point, then click the middle.
         * See TryAimByCamera for why this and not a pixel offset. */
        ClickAimLeg leg;
        if (TryAimByCamera(tx, ty, out leg))
        {
            _legWantYd = rem;
            Dispatch(leg, px, py, now);
            return;
        }

        if (_p.UseProjection && TryProjectedClick(tx, ty, out leg))
        {
            _legWantYd = rem;
            Dispatch(leg, px, py, now);
            return;
        }

        int rc = ClickAimMath.PlanLeg(_p, tx - px, ty - py, out leg);
        if (rc <= 0) return;

        /* One hop size for every click, not one computed from the distance left.
         *
         * The measured mapping from click height to distance is not monotone on this
         * client (2026-10-29, one walk): -43 px walked 10.3 yd, -105 px 10.6, -141 px
         * 8.2, -183 px 28.6, then -171 px 0.5, 0.6, 0.5, 0.0, 0.0 - the last four did
         * nothing at all. Anything derived from that curve is a guess, and a wrong
         * guess at a goal 40 yd away meant -253 px, which never moves. So the height
         * is fixed at a value that is known to move, and distance comes from
         * repeating the hop along the polyline.
         */
        leg.Dy = _hopDy;
        _legWantYd = rem;
        Dispatch(leg, px, py, now);
    }

    private void ContinueLeg(double px, double py, double rem, int now)
    {
        int elapsed = now - _legStartMs;
        if (rem < _bestRemaining) _bestRemaining = rem;

        bool resting = (now - _lastMoveMs) >= RestGapMs;
        bool armed = elapsed >= LegArmMs;
        bool tooLong = elapsed >= MaxLegMs;

        // Passed the goal: it came close and is now getting further away. A click
        // is immediate-mode, so the client will walk the whole way past it.
        // Passing a point is only interesting if the point was not the goal: within a
        // few yards of it, walking past it IS arriving, and the old code cancelled the
        // walk and clicked again, which is how the log filled with "passed the target
        // at 2.7 yd" three times in a row.
        bool passed = _bestRemaining <= PassRadius && rem > _bestRemaining + PassGiveUp
                      && rem > _p.Tolerance * 2.0;

        if ((resting && armed) || tooLong || passed)
        {
            FinishLeg(px, py, rem, now, tooLong, passed);
            return;
        }

        double walked = ClickAimMath.Len(px - _legX, py - _legY);
        if (walked > WanderMaxYd)
        {
            FinishLeg(px, py, rem, now, true, false);
        }
    }

    private void FinishLeg(double px, double py, double rem, int now, bool timedOut, bool passed)
    {
        double moved = ClickAimMath.Len(px - _legX, py - _legY);
        double secs = Math.Max(0.05, (now - _legStartMs) / 1000.0);
        Legs++;

        // A turn leg walks along its OLD facing while the drag turns the camera,
        // so its displacement heading is not a camera heading. Only a pure click
        // leg is a clean sample of `walked = zero + degPerPx * clickPx`, and that is
        // what LearnAim fits - including the SIGN, which was wrong here and made
        // every leg walk about 180 degrees from where it was aimed.
        if (_leg.Turn == ClickAimMath.TurnKindNone && moved >= ClickAimMath.MinLearningWalk)
        {
            double deg = _p.DegPerPx;
            double zero = _p.HeadingAtZero;
            string note;
            if (ClickAimMath.LearnAim(ref deg, ref zero, _leg.ClickPx, _zeroAtClick,
                                      ClickAimMath.HeadingDeg(px - _legX, py - _legY), moved, out note))
            {
                _p.DegPerPx = deg;
                _p.HeadingAtZero = zero;
                _p.ConeHalfDeg = ClickAimMath.ConeHalfDeg(deg, _p.HalfWidth);
                bool wasProbe = _needSignProbe && _leg.ClickPx == SignProbePx;
                if (note.Length > 0)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] aim learned from {0:F1} yd on a {1} px click: {2}",
                        moved, _leg.ClickPx, note));
                }

                if (wasProbe)
                {
                    _needSignProbe = false;
                    _sessionAimKnown = true;
                    _sessionDegPerPx = _p.DegPerPx;
                    _sessionZero = _p.HeadingAtZero;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[ClickAim] aim is now degPerPx {0:F4}, zero {1:F1} deg - kept for the rest of "
                        + "the session", _p.DegPerPx, _p.HeadingAtZero));

                    // Worth keeping on disk too: the next start begins with a checked
                    // model instead of the field default from another camera.
                    if (Math.Abs(_p.DegPerPx) > 0.02 && Math.Abs(_p.DegPerPx) < 0.6)
                        ClickAimCalibration.Append(_p.DegPerPx, _p.DegPerDragPx, 0.0, _p.YdPerDyPx);
                }
            }
        }

        /* The click scale is measured later, not here.
         *
         * This is the number that made a 20 yd walk sprint 120 yd: the old model
         * assumed a -100 px click was 20-45 yd, and on this client it was more than
         * 120. But a leg can end while the character is still walking - "passed the
         * target" fires mid-run - so `moved` here is the distance walked so far, not
         * the click's reach. Measuring it now learns a scale that is too small, the
         * next click is too tall, and the walk gets worse with every leg. The
         * measurement is therefore deferred to the moment the character is really
         * standing still (Tick, _pendingDy).
         */
        if (_leg.Turn == ClickAimMath.TurnKindNone
            && Math.Abs(_leg.Dy) >= _p.MinDyPx
            && !double.IsNaN(_legWantYd))
        {
            _pendingDy = _leg.Dy;
            _pendingX = _legX;
            _pendingY = _legY;
            _pendingWantYd = _legWantYd;
            _pendingStops = 0;
            _pendingProjected = _legProjected;
        }

        /* A click that moved the character nowhere is not an obstacle - it is a click
         * the client did not take, or one that landed somewhere useless. Walking into
         * a tree still moves a little as the character slides; a dead click moves
         * zero. Repeating it is what produced six identical 0.0 yd legs in a row, so
         * the hop height changes instead, and only a streak of them counts as stuck. */
        if (_leg.Turn == ClickAimMath.TurnKindNone && moved < 1.0)
        {
            _deadClicks++;
            _deadClickStreak++;

            /* Six dead clicks in a row is the end of the walk, not another rung of the
             * ladder. On 2026-10-29 the ladder rotated for minutes while the character
             * stood still at 5.7 yd and the walk had to be killed by hand: nothing in
             * that loop was ever going to end it, because every component believed it
             * was about to make its next attempt. */
            if (_deadClicks >= 6)
            {
                Blocked = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] {0} clicks in a row moved the character nowhere (it is at "
                    + "({1:F1}, {2:F1}), {3:F1} yd from the point). Not clicking any more - the caller "
                    + "can re-plan from here.", _deadClicks, _legX, _legY, _legWantYd));
            }
            else if (_deadClickStreak >= 2)
            {
                _hopIndex = (_hopIndex + 1) % HopLadder.Length;
                _hopDy = HopLadder[_hopIndex];
                _sessionHopDy = _hopDy;

                // A movement key cancels a click-walk and clears the client's internal
                // CTM state; a click posted after it is treated as new.
                IntPtr hwnd = Handle();
                if (hwnd != IntPtr.Zero) ClickAimInput.TapKey(hwnd, StopKeyVk, 120);

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] {0} clicks in a row moved nothing ({1} in total); tapped the stop key and "
                    + "will try a {2} px hop", _deadClickStreak, _deadClicks, _hopDy));
                _deadClickStreak = 0;
                _stuck = 0;                 // not the obstacle counter's business
            }
        }
        else if (moved >= 1.0)
        {
            _deadClickStreak = 0;
            _deadClicks = 0;
            if (moved < BlockedMove) _stuck++; else _stuck = 0;
        }

        /* The runaway cap is relative to the hop, not to the goal. A click is a fixed
         * hop of 10-13 yd, so a leg that has walked much more than that did not walk
         * where it was aimed: the old cap of 3x the distance-left let a 20 yd goal
         * click run 120 yd, and this one cuts at 55. */
        double runawayYd = 3.0 * ExpectedHopYd + 10.0;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] leg ended: {0:F1} yd in {1:F1} s on a {2}, {3}",
            moved, secs, _leg.Turn == ClickAimMath.TurnKindNone ? "click" : "turn",
            WhyLegEnded(timedOut, passed, moved, runawayYd)));

        if (timedOut || moved > runawayYd)
        {
            Runaways++;
            IntPtr hwnd = Handle();
            if (hwnd != IntPtr.Zero) ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 150);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] runaway: a leg walked {0:F1} yd in {1:F1} s{2}; released the buttons",
                moved, secs, timedOut ? " and never came to rest" : ""));
        }

        if (passed)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] passed the target at {0:F1} yd; cancelling the walk", _bestRemaining));
            IntPtr hwnd = Handle();
            if (hwnd != IntPtr.Zero) ClickAimInput.TapKey(hwnd, StopKeyVk, 70);
        }

        _phase = Phase.Idle;
        _bestRemaining = double.MaxValue;
    }

    /// <summary>
    /// Settles the click that was dispatched most recently.
    ///
    /// Two jobs, both of which need the character's *final* position rather than
    /// the position when a leg happened to end:
    ///
    ///   * learn how far a click of that height actually goes (yd/px), which is the
    ///     number the whole hop policy rests on;
    ///   * stop a click that is still carrying the character past where it was meant
    ///     to go. A movement key cancels a click-walk, so a leg that exceeds three
    ///     times what was asked for is stopped instead of being watched. That is the
    ///     120 yd sprint from 2026-10-29, cut at 70 yd the first time it happens.
    /// </summary>
    private void ResolvePendingClick(double px, double py, bool moving, int now)
    {
        if (_pendingDy == 0) return;

        double travel = ClickAimMath.Len(px - _pendingX, py - _pendingY);
        double cap = 3.0 * ExpectedHopYd + 10.0;

        if (moving)
        {
            if (travel <= cap || _pendingStops >= 3) return;

            _pendingStops++;
            IntPtr hwnd = Handle();
            if (hwnd != IntPtr.Zero) ClickAimInput.TapKey(hwnd, StopKeyVk, 120);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] brake {0}/3: the click has carried the character {1:F1} yd for a hop that "
                + "wanted {2:F1} yd; tapped the stop key", _pendingStops, travel, _pendingWantYd));

            if (_pendingStops >= 3)
            {
                Runaways++;
                Logging.Write("[ClickAim] the click will not stop; giving up on this leg, "
                              + "the mover's own detours are meaningless until the character is still");
                _pendingDy = 0;
            }
            return;
        }

        /* A projected click was aimed at a world point, so it says how far that point
         * was, not how much ground a click height is worth. Feeding it to the scale
         * learner is what produced "0.49 yd/px" and "0.05 yd/px" from the same -70 px
         * in consecutive legs on 2026-10-29. */
        if (_pendingProjected)
        {
            _pendingDy = 0;
            return;
        }

        double candidate = travel / Math.Abs(_pendingDy);
        double est = _p.YdPerDyPx;
        double used;
        if (ClickAimMath.LearnScale(ref est, _p.AssumedYdPerDyPx, candidate, travel, _scaleSamples, out used))
        {
            _scaleSamples++;
            _p.YdPerDyPx = est;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] click scale: {0:F2} yd/px measured - {1:F1} yd walked on a {2} px click "
                + "(hop wanted {3:F1}); using {4:F2} after {5} measurement(s)",
                candidate, travel, _pendingDy, _pendingWantYd, used, _scaleSamples));
        }
        else if (travel >= 3.0)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] click scale: ignored {0:F2} yd/px from {1:F1} yd on a {2} px click "
                + "(too far from the {3:F2} in use)",
                candidate, travel, _pendingDy, used));
        }

        _pendingDy = 0;
    }

    private static string WhyLegEnded(bool timedOut, bool passed, double moved, double cap)
    {
        if (passed) return "it passed the target";
        if (timedOut) return "it ran out of time";
        if (moved > cap) return "it ran past the runaway limit of " + cap.ToString("F0", CultureInfo.InvariantCulture) + " yd";
        return "the character came to rest";
    }

    /// <summary>
    /// Aims by rotating the camera to face the target, then clicking the MIDDLE of
    /// the screen - the one mechanism in this client that has measured consistently.
    ///
    /// Why this replaces everything else that was tried today:
    ///
    ///   * a click to the middle of the screen walks along the camera's axis, so once
    ///     the camera faces the target the click cannot go anywhere else. There is no
    ///     pixel offset to be wrong by a few hundred pixels, no click scale and no
    ///     click sign - <c>ClickPx</c> is zero by construction.
    ///   * the drag channel is the one that measured the same twice in a row today:
    ///     -0.21 deg/px in the field, -0.21 in the 21:36 calibration, -0.21000 in the
    ///     file. Nothing else in this model has ever agreed with itself.
    ///   * it is a closed loop. The camera position is readable, so the camera's
    ///     heading is known before and after every drag; a drag that over- or
    ///     under-rotates shows up as a smaller error on the next tick and is corrected.
    ///     The gain does not have to be right, only consistent.
    ///
    /// Projection was meant to do this directly, and on 2026-10-29 it swung the click
    /// by hundreds of pixels per tick while the character stood still - the 90 degree
    /// field of view (or the camera reading itself) is not what the projection assumes.
    /// A closed loop does not need to assume.
    /// </summary>
    private bool TryAimByCamera(double tx, double ty, out ClickAimLeg leg)
    {
        leg = default(ClickAimLeg);
        leg.Turn = ClickAimMath.TurnKindNone;

        double camX, camY, camZ;
        if (!ClickAimPositions.TryReadCamera(out camX, out camY, out camZ)) return false;

        double px, py;
        if (!PlayerPosition(out px, out py)) return false;

        // The camera looks at the player; if it is sitting on top of the player its
        // heading is noise, so say nothing rather than aim by noise.
        double sep = ClickAimMath.Len(px - camX, py - camY);
        if (sep < 0.8) return false;

        double camHeading = ClickAimMath.HeadingDeg(px - camX, py - camY);
        double desired = ClickAimMath.HeadingDeg(tx - px, ty - py);
        double error = ClickAimMath.WrapDeg(desired - camHeading);

        // The loop has to notice if it is not converging. A drag whose sign is wrong
        // makes the error grow every time, and without this the character spins on the
        // spot for ever - which is one of the things this client has already done.
        if (Math.Abs(error) > AimToleranceDeg)
        {
            if (Math.Abs(error) > _aimBest + 2.0)
            {
                _aimWorse++;
                if (_aimWorse == 1)
                {
                    _aimGainSign = -_aimGainSign;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Aim] the error grew to {0:F1} deg after a drag, so the drag sign is the other "
                        + "way on this client; using {1:F2} deg/px from now on", error,
                        _p.DegPerDragPx * _aimGainSign));
                }
                else if (_aimWorse >= 3)
                {
                    Blocked = true;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Aim] the camera is not turning the way a drag predicts (three drags, error still "
                        + "{0:F1} deg). Stopping rather than spinning on the spot.", error));
                    return false;
                }
            }
            else
            {
                _aimBest = Math.Abs(error);
            }

            double gain = (_p.DegPerDragPx != 0.0 ? _p.DegPerDragPx : -0.21) * _aimGainSign;
            double dragPx = error / gain;
            if (dragPx > MaxAimDragPx) dragPx = MaxAimDragPx;
            if (dragPx < -MaxAimDragPx) dragPx = -MaxAimDragPx;

            leg.Turn = ClickAimMath.TurnKindDrag;
            leg.DragPx = (int)(dragPx >= 0 ? dragPx + 0.5 : dragPx - 0.5);
            leg.Dy = 0;

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Aim] camera {0:F1} deg, target {1:F1} deg, error {2:+0.0;-0.0} deg -> drag {3} px "
                + "(the camera turns {4:F2} deg/px)", camHeading, desired, error, leg.DragPx, _p.DegPerDragPx));
            return true;
        }

        // Facing it: a middle click walks along that axis. No offset, no scale.
        _aimBest = Math.Abs(error);
        _aimWorse = 0;
        leg.ClickPx = 0;
        leg.Dy = AimHopDy;
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Aim] camera {0:F1} deg, target {1:F1} deg, error {2:+0.0;-0.0} deg -> centre click ({3} px down)",
            camHeading, desired, error, AimHopDy));
        return true;
    }

    /// <summary>
    /// Builds a click aimed at the world position of the target, using the camera.
    ///
    /// Returns false when the camera cannot be read or the point is behind it, in
    /// which case the caller falls back to the learned pixel model - which is worth
    /// keeping, because the camera offsets are placeholders on some builds.
    /// </summary>
    private bool TryProjectedClick(double tx, double ty, out ClickAimLeg leg)
    {
        leg = default(ClickAimLeg);
        leg.Turn = ClickAimMath.TurnKindNone;

        double camX, camY, camZ;
        if (!ClickAimPositions.TryReadCamera(out camX, out camY, out camZ))
        {
            if (_projectionWorks)
            {
                _projectionWorks = false;
                Logging.Write("[ClickAim] the camera stopped being readable: falling back to the pixel model");
            }
            return false;
        }

        double px, py;
        if (!PlayerPosition(out px, out py)) return false;

        double pz = ClickAimPositions.Z();
        if (double.IsNaN(pz)) pz = camZ;                 // last resort: assume the camera height

        double tz = _targetZ;
        if (double.IsNaN(tz) || Math.Abs(tz) < 0.01) tz = pz;

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return false;

        int w, h;
        if (!ClickAimInput.ClientSize(hwnd, out w, out h)) return false;

        int dx, dy;
        if (!ClickAimMath.ProjectToScreen(camX, camY, camZ, px, py, pz, tx, ty, tz,
                                          w / 2.0, h / 2.0, _p.CameraFovDeg, out dx, out dy))
        {
            _projectionRejects++;
            if (_projectionRejects <= 3)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[ClickAim] projection rejected the point ({0:F1}, {1:F1}, {2:F1}) from camera "
                    + "({3:F1}, {4:F1}, {5:F1}) looking at the player ({6:F1}, {7:F1}, {8:F1}) - it is "
                    + "behind the camera or off the screen; using the pixel model for this leg",
                    tx, ty, tz, camX, camY, camZ, px, py, pz));
            }
            return false;
        }

        if (!_projectionWorks)
        {
            _projectionWorks = true;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] aim: projecting world points onto the screen from camera "
                + "({0:F1}, {1:F1}, {2:F1}), {3:F0} deg field of view", camX, camY, camZ, _p.CameraFovDeg));
        }

        // A click within a few pixels of the middle walks along the camera axis and
        // says nothing about where the character is, so aim at least a little off it.
        if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8) dy = dy < 0 ? -12 : 12;

        leg.ClickPx = dx;
        leg.Dy = dy;
        _projectedClicks++;
        _projectedThisDispatch = true;
        return true;
    }

    private void StartDetour(double px, double py, int now)
    {
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return;

        _detourAttempt++;
        int side = ClickAimMath.DetourSide(_detourAttempt);
        ClickAimLeg leg = default(ClickAimLeg);
        leg.Turn = ClickAimMath.TurnKindDrag;
        leg.Dy = _p.PreferredDy;
        if (Math.Abs(_p.DegPerDragPx) > 1e-6)
        {
            double px2 = (side * DetourDeg) / _p.DegPerDragPx;
            leg.DragPx = (int)(px2 >= 0 ? px2 + 0.5 : px2 - 0.5);
        }
        else
        {
            return;
        }
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[ClickAim] detour {0}/{1}: {2} {3:F0} deg, then a far click",
            _detourAttempt, DetourBudget, side > 0 ? "right" : "left", DetourDeg));
        Dispatch(leg, px, py, now);
        _stuck = 0;
    }

    private void Dispatch(ClickAimLeg leg, double px, double py, int now)
    {
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return;

        bool projected = _projectedThisDispatch;
        if (leg.Turn == ClickAimMath.TurnKindNone)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] click ({0}, {1}) - {2:F1} yd to go, {3}",
                leg.ClickPx, leg.Dy, _legWantYd > 0 ? _legWantYd : 0.0,
                projected
                    ? "aimed at the projected point"
                    : "pixel model, scale " + (_p.YdPerDyPx > 1e-6 ? _p.YdPerDyPx : _p.AssumedYdPerDyPx).ToString("F2", CultureInfo.InvariantCulture) + " yd/px"));
        }

        if (leg.Turn == ClickAimMath.TurnKindDrag)
        {
            ClickAimInput.Drag(hwnd, leg.DragPx);
            _p.HeadingAtZero = ClickAimMath.YawAfterTurn(_p, leg);
        }
        else if (leg.Turn == ClickAimMath.TurnKindKey)
        {
            ClickAimInput.TapKey(hwnd, leg.KeyMs > 0 ? 0x27 : 0x25, Math.Abs(leg.KeyMs));
            _p.HeadingAtZero = ClickAimMath.YawAfterTurn(_p, leg);
        }
        else
        {
            ClickAimInput.Click(hwnd, leg.ClickPx, leg.Dy);
        }

        _leg = leg;
        _legProjected = _projectedThisDispatch;
        _projectedThisDispatch = false;
        _legX = px;
        _legY = py;
        _zeroAtClick = _p.HeadingAtZero;
        _clickAtMs = now;
        _legStartMs = now;
        _bestRemaining = double.MaxValue;
        _phase = Phase.Walking;
    }

    private void Pump()
    {
        // Move(Forward) has no destination. A far click ahead is the closest
        // honest equivalent: it starts the character walking and the caller's
        // next MoveTowards takes over the aim.
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return;
        ClickAimInput.Click(hwnd, 0, ClickAimMath.HopDy(_p, 15.0));
    }

    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }

    private bool _warnedNoPlayer;

    private bool PlayerPosition(out double x, out double y)
    {
        // On 3.3.5a this is ObjectManager.Me.Location; on the 64-bit client Me is
        // never built, so the same call falls through to the WowB record reader.
        if (ClickAimPositions.TryRead(out x, out y)) return true;

        if (!_warnedNoPlayer)
        {
            _warnedNoPlayer = true;
            Logging.Write("[ClickAim] no player position yet - is the character in the world? "
                          + "(legacy object model and WowB record reader both returned nothing)");
        }
        return false;
    }

    /// <summary>
    /// Applies the measured model as the pathing stack's movement provider. Call
    /// once at startup on a 64-bit client; on 3.3.5a there is no reason to, the
    /// native call works there.
    /// </summary>
    public static ClickAimMover Install()
    {
        Logging.Write(ClickAimMath.SelfTest());
        var mover = new ClickAimMover();
        Navigator.PlayerMover = mover;
        Logging.Write("[ClickAim] installed as Navigator.PlayerMover "
                      + "(synthesised clicks; the native ClickToMove call is not used)");
        return mover;
    }
}

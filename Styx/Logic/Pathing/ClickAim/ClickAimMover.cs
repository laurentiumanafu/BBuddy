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
    private int _pendingDy;
    private double _pendingX, _pendingY;
    private double _pendingWantYd;
    private int _pendingStops;
    private int _stuck;
    private int _detourAttempt;
    private bool _warnedStuck;
    private bool _warnedUnsupported;

    public ClickAimMover()
    {
        _cal = ClickAimCalibration.Load();
        _p.DegPerPx = _cal.Ok ? _cal.DegPerPx : -0.115;
        _p.DegPerDragPx = _cal.DragSamples > 0 ? _cal.DegPerDragPx : -0.21;
        _p.DegPerKeyMs = _cal.KeySamples > 0 ? _cal.DegPerKeyMs : 0.0;
        _p.DegPerPxRef = _p.DegPerPx;
        _p.DegPerDragPxRef = _p.DegPerDragPx;
        _p.ConeHalfDeg = ClickAimMath.ConeHalfDeg(_p.DegPerPx, _p.HalfWidth);
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

        if (double.IsNaN(_p.HeadingAtZero))
        {
            // Nothing has measured which way "centre" points yet. One centre
            // click walks along the camera yaw, and that walk measures it.
            ClickAimLeg boot = default(ClickAimLeg);
            boot.Turn = ClickAimMath.TurnKindNone;
            boot.Dy = ClickAimMath.HopDy(_p, rem);
            _legWantYd = rem;
            Dispatch(boot, px, py, now);
            return;
        }

        ClickAimLeg leg;
        int rc = ClickAimMath.PlanLeg(_p, tx - px, ty - py, out leg);
        if (rc <= 0) return;
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
        bool passed = _bestRemaining <= PassRadius && rem > _bestRemaining + PassGiveUp;

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
        // leg can measure the yaw, and only if it walked far enough to have one.
        if (_leg.Turn == ClickAimMath.TurnKindNone)
        {
            if (moved >= MinHeadingWalk && ClickAimMath.PlausibleLeg(moved, secs))
            {
                double heading = ClickAimMath.HeadingDeg(px - _legX, py - _legY);
                double turn = ClickAimMath.WrapDeg(heading - _p.HeadingAtZero);

                /* Back the click offset out. HeadingAtZero means "the heading a click
                 * at the centre of the screen walks", and this leg's click was at
                 * ClickPx, so the heading it produced is offset by DegPerPx*ClickPx.
                 * Storing the raw heading instead put that offset into the zero on
                 * every off-centre click, and the model then aimed wide by the same
                 * amount for the rest of the walk - a bias that accumulates into
                 * "it walks off in the wrong direction" with no single bad click. */
                double zero = ClickAimMath.WrapDeg(heading - _p.DegPerPx * _leg.ClickPx);
                if (!double.IsNaN(zero)) _p.HeadingAtZero = zero;
                if (Math.Abs(_leg.ClickPx) >= 40)
                {
                    // A property cannot be passed by ref, and the learner needs a
                    // local to write into; assign back only when it accepted.
                    double slope = _p.DegPerPx;
                    if (ClickAimMath.LearnSlope(ref slope, _p.DegPerPxRef, turn, _leg.ClickPx,
                            _p.MinLearnMove, _p.MaxLearnMove, moved, 0.35))
                    {
                        _p.DegPerPx = slope;
                        _p.ConeHalfDeg = ClickAimMath.ConeHalfDeg(_p.DegPerPx, _p.HalfWidth);
                    }
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
        }

        if (moved < BlockedMove) _stuck++; else _stuck = 0;

        /* The runaway cap is now relative to what the click was asked to do. A flat
         * 120 yd let one bad click run for 14 s; 3x the wanted hop plus a margin
         * cuts it at a third of that and still leaves room for a slope that makes
         * a click land further than expected. */
        double runawayYd = 3.0 * _legWantYd + 10.0;
        if (runawayYd < 25.0) runawayYd = 25.0;
        if (runawayYd > WanderMaxYd) runawayYd = WanderMaxYd;

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
        double cap = 3.0 * _pendingWantYd + 10.0;
        if (cap < 25.0) cap = 25.0;
        if (cap > WanderMaxYd) cap = WanderMaxYd;

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

        if (leg.Turn == ClickAimMath.TurnKindNone)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[ClickAim] click ({0}, {1}) - {2:F1} yd to go, click scale {3:F2} yd/px",
                leg.ClickPx, leg.Dy, _legWantYd > 0 ? _legWantYd : 0.0,
                _p.YdPerDyPx > 1e-6 ? _p.YdPerDyPx : _p.AssumedYdPerDyPx));
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
        _legX = px;
        _legY = py;
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

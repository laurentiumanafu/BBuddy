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
    public int Runaways { get; private set; }
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

        // Between legs: nothing is dispatched until the character is standing
        // still, or the next click lands on top of a walk that is still running.
        if (moving || (now - _lastMoveMs) < RestGapMs) return;

        if (rem <= _p.Tolerance) { return; }

        if (_stuck >= 2)
        {
            if (_detourAttempt >= MaxDetours)
            {
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
            boot.Dy = _p.PreferredDy;
            Dispatch(boot, px, py, now);
            return;
        }

        ClickAimLeg leg;
        int rc = ClickAimMath.PlanLeg(_p, tx - px, ty - py, out leg);
        if (rc <= 0) return;
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
                _p.HeadingAtZero = heading;
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

        if (moved < BlockedMove) _stuck++; else _stuck = 0;

        if (timedOut || moved > WanderMaxYd)
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
            _detourAttempt, MaxDetours, side > 0 ? "right" : "left", DetourDeg));
        Dispatch(leg, px, py, now);
        _stuck = 0;
    }

    private void Dispatch(ClickAimLeg leg, double px, double py, int now)
    {
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return;

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
        ClickAimInput.Click(hwnd, 0, _p.PreferredDy);
    }

    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }

    private bool _warnedNoPlayer;

    private bool PlayerPosition(out double x, out double y)
    {
        x = 0;
        y = 0;
        try
        {
            LocalPlayer? me = StyxWoW.Me;
            if (me == null)
            {
                if (!_warnedNoPlayer)
                {
                    _warnedNoPlayer = true;
                    Logging.Write("[ClickAim] no local player object yet - is the client attached?");
                }
                return false;
            }
            WoWPoint loc = me.Location;
            if (!loc.IsValid) return false;
            x = loc.X;
            y = loc.Y;
            return true;
        }
        catch
        {
            return false;
        }
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

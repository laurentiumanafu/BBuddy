using System;
using System.Globalization;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// <c>--clickstop</c>: does a key press actually CANCEL a click-walk on this client?
///
/// This exists because of the 12:57 run, where a centre click walked 120.4 yd and
/// "tapped the stop key" three times without slowing down, leaving the character 138 yd
/// from where it started. The key was posted with <c>lParam = 0x00010001</c>: repeat 1
/// and **scan code 0**. A keystroke with no scan code is one a client may legitimately
/// ignore, and the client appears to have done exactly that. <see cref="ClickAimInput"/>
/// now sends <c>MapVirtualKey(vk, 0) &lt;&lt; 16</c> like Windows itself does, and this
/// tests the result rather than assuming it.
///
/// The test is deliberately short and self-limiting: it clicks at the centre, waits for
/// the walk to start, taps one key, and measures how far the character still goes. It
/// tries the forward key first (the usual cancel), then the two arrows, then strafe. It
/// gives up after <see cref="MaxRunawayYd"/> and says so loudly - a character that will
/// not stop is the one failure mode here that costs the user a walk back.
///
/// Why it matters for the bot: <see cref="ClickAimKeySteer.StopAndRest"/> is built on the
/// same key, so if this fails, nothing that needs the character still can be trusted, and
/// the click mover must stay disabled.
/// </summary>
internal static class ClickAimStopTest
{
    private const int StopKeyVk = 0x57;        // W
    private const int TurnLeftVk = 0x25;       // arrow left
    private const int TurnRightVk = 0x27;      // arrow right
    private const int StrafeLeftVk = 0x41;     // A

    /// <summary>A walk that started but has not moved this far yet has not started.</summary>
    private const double StartYd = 1.5;

    /// <summary>Watch this long for the walk to begin after the click.</summary>
    private const int StartTimeoutMs = 2500;

    /// <summary>Watch this long after a tap to decide whether it stopped the walk.</summary>
    private const int CheckMs = 1200;

    /// <summary>Still moving less than this after a tap counts as stopped.</summary>
    private const double StoppedYd = 0.8;

    /// <summary>Past this the experiment is over: do not let it become the 120 yd walk.</summary>
    private const double MaxRunawayYd = 25.0;

    private const int PollMs = 60;

    public static void Run()
    {
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[StopTest] no game window"); return; }

        Logging.Write("[StopTest] can a key press cancel a click-walk? click, wait for it to "
                      + "start, tap one key, measure what still happens.");

        string rest;
        if (!ClickAimKeySteer.StopAndRest(hwnd, out rest))
        {
            /* Not a reason to stop: the point of the test is that the stop key may not
             * work. But say it, because a click started while the character is moving
             * measures the old walk. */
            Logging.Write("[StopTest] the character was not standing still to begin with (" + rest
                          + ") - the numbers below are still worth reading");
        }

        double x0, y0;
        if (!ClickAimPositions.TryRead(out x0, out y0))
        {
            Logging.Write("[StopTest] cannot read the position; nothing to measure");
            return;
        }

        ClickAimInput.Click(hwnd, 0, 0);

        double sx, sy;
        if (!WaitUntilMoving(x0, y0, StartTimeoutMs, out sx, out sy))
        {
            Logging.Write("[StopTest] the centre click did not start a walk, so there is nothing "
                          + "to cancel. Run it where a click on open ground walks.");
            ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 120);
            return;
        }

        double dx, dy;
        if (!ClickAimPositions.TryRead(out dx, out dy))
        {
            Logging.Write("[StopTest] lost the position after the click started");
            ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 120);
            return;
        }
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[StopTest] the click started a walk: {0:F1} yd from where it began, {1:F1} yd to go "
            + "before the character is off the mesh probe. Tapping the keys in turn.",
            ClickAimMath.Len(dx - x0, dy - y0), MaxRunawayYd));

        int[] keys = { StopKeyVk, TurnLeftVk, TurnRightVk, StrafeLeftVk };
        string[] names = { "W (forward)", "arrow left", "arrow right", "A (strafe left)" };

        for (int i = 0; i < keys.Length; i++)
        {
            double bx, by;
            if (!ClickAimPositions.TryRead(out bx, out by)) continue;

            ClickAimInput.TapKey(hwnd, keys[i], 60);

            double moved = TravelIn(bx, by, CheckMs);
            double total = ClickAimMath.Len(bx - x0, by - y0);

            if (moved < StoppedYd)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[StopTest] {0} STOPPED it: {1:F2} yd in the {2} ms after the tap (walk was "
                    + "{3:F1} yd long when tapped). The stop key works - a click can be cancelled.",
                    names[i], moved, CheckMs, total));
                Report(true, names[i]);
                return;
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[StopTest] {0} did NOT stop it: {1:F1} yd in the {2} ms after the tap "
                + "(walk was {3:F1} yd long when tapped)",
                names[i], moved, CheckMs, total));

            if (total > MaxRunawayYd)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[StopTest] giving up: the walk is {0:F0} yd long and no key has stopped it. "
                    + "Releasing everything and stopping the test before it becomes the 120 yd walk.",
                    total));
                ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 200);
                Report(false, string.Empty);
                return;
            }
        }

        Logging.Write("[StopTest] none of the four movement keys cancelled the click. Release "
                      + "everything by hand and treat clicks as uncontrollable on this build.");
        ClickAimInput.ReleaseAll(hwnd, StopKeyVk, 200);
        Report(false, string.Empty);
    }

    /// <summary>The one line to quote when this is the difference between two designs.</summary>
    private static void Report(bool stopped, string which)
    {
        if (stopped)
            Logging.Write("[StopTest] VERDICT: clicks can be stopped with " + which
                          + ", so a click is a controllable primitive again - bounded by a stop, "
                          + "not by hoping the aim is right.");
        else
            Logging.Write("[StopTest] VERDICT: clicks cannot be stopped on this build. Keep the click "
                          + "fallback disabled and steer with the turn keys.");
    }

    /// <summary>Waits until the character is <see cref="StartYd"/> from (x0, y0).</summary>
    private static bool WaitUntilMoving(double x0, double y0, int ms, out double x, out double y)
    {
        x = x0;
        y = y0;
        int limit = Environment.TickCount + ms;
        while (unchecked(Environment.TickCount - limit) < 0)
        {
            Thread.Sleep(PollMs);
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;
            x = nx;
            y = ny;
            if (ClickAimMath.Len(nx - x0, ny - y0) >= StartYd) return true;
        }
        return false;
    }

    /// <summary>How far the character travels in the <paramref name="ms"/> after now.</summary>
    private static double TravelIn(double x0, double y0, int ms)
    {
        double far = 0;
        int limit = Environment.TickCount + ms;
        while (unchecked(Environment.TickCount - limit) < 0)
        {
            Thread.Sleep(PollMs);
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;
            double d = ClickAimMath.Len(nx - x0, ny - y0);
            if (d > far) far = d;
        }
        return far;
    }

    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }
}

using System;
using System.Threading;

namespace Styx.Logic.Pathing;

/// <summary>
/// The one flag every long loop checks, so the Stop button stops the bot instead of asking it to
/// finish what it was doing.
///
/// Why this exists. On 2026-10-31 15:51:48 the log says "Stopping the bot!" and then prints
/// twenty-eight more lines of arrow-key steering over the next nine seconds: the character kept
/// turning and walking. <c>GrindRunner</c> had its own <c>_stop</c> flag, <c>RouteRunner</c> had
/// its own <c>StopRequested</c>, and both are only read *between* waypoints - while the walk is
/// inside <see cref="ClickAimKeySteer.Steer"/>, which is a loop of its own that never asked.
///
/// A stop is a user's hand on the wheel. It is not a request to finish the leg. So the flag lives
/// here, every loop that can run for more than a second reads it, and <see cref="Wait"/> is the
/// sleep those loops use: a stop cuts a three-second wait down to nothing instead of letting it
/// run out first.
/// </summary>
internal static class ClickAimAbort
{
    private static volatile bool _requested;

    /// <summary>True once Stop was pressed, until the next run clears it.</summary>
    public static bool Requested { get { return _requested; } }

    /// <summary>Stop everything that is moving. Idempotent, and safe from any thread.</summary>
    public static void Request()
    {
        _requested = true;

        /* The forward key is held by a PostMessage, so nothing lets it up by itself. Releasing it
         * here is what turns "the loop stops in a tick" into "the character stops walking". */
        try { ClickAimKeySteer.ReleaseHeld(); } catch { }
        try { ClickAimKeySteer.ReleaseArrows(null); } catch { }
    }

    /// <summary>A new run: nothing is aborted any more.</summary>
    public static void Clear()
    {
        _requested = false;
    }

    /// <summary>
    /// Sleeps for <paramref name="ms"/>, in small slices, and returns as soon as a stop is
    /// requested. Returns true when it was cut short.
    /// </summary>
    public static bool Wait(int ms)
    {
        int waited = 0;
        while (waited < ms)
        {
            if (_requested) return true;
            int slice = Math.Min(50, ms - waited);
            Thread.Sleep(slice);
            waited += slice;
        }
        return _requested;
    }
}

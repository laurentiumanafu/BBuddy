using System;

namespace Styx.Logic.Pathing;

// Read-only wait after a single tab click. A held physical mouse button may
// settle, but no further input is sent until the full delay and safety checks
// succeed. Unknown session, combat, death, geometry and STOP still abort.
internal static class WowBMailTabWait
{
    internal const int DelayMs = 1000, HeldButtonGraceMs = 3000;
    internal const string HeldButton = "a physical mouse button is held";

    internal static bool Wait(Func<string?> check, Func<int, bool> wait, Func<long> now, out string reason)
    {
        long start = now();
        reason = "";
        while (true)
        {
            string? issue = check();
            long elapsed = now() - start;
            if (issue != null && issue != HeldButton) { reason = issue; return false; }
            if (issue == HeldButton && elapsed >= HeldButtonGraceMs)
            { reason = "physical mouse button still held after three-second grace"; return false; }
            if (issue == null && elapsed >= DelayMs) return true;
            if (wait(100)) { reason = "STOP requested during tab wait"; return false; }
        }
    }
}

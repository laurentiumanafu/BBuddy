using System;

namespace Styx.Logic.Pathing;

// Four failed hotspot walks may coincide with death, but are not evidence of it.
// Check once: a confirmed zero-health checkpoint goes to corpse recovery;
// a positively alive sample means the stuck route should be re-planned on the
// mesh instead of holding position. Anything else (STOP, session loss, or an
// unreadable sample) stops without release or travel.
internal static class WowBHotspotDeathWatch
{
    internal static bool CheckOnce(Func<(bool Ready, string Observation)> poll, Func<bool> cancelled,
        Action<string> log, out bool alive)
    {
        alive = false;
        if (cancelled()) return false; // STOP wins even before the single check
        var sample = poll();
        if (cancelled()) return false; // STOP wins even if it races a checkpoint
        if (sample.Ready) return true;
        if (sample.Observation == "alive candidate")
        {
            alive = true;
            log("[Grind] hotspot failure death check: alive candidate; reloading the mesh and re-planning the route");
            return false;
        }
        log("[Grind] hotspot failure death check: " + sample.Observation + "; stopping without release or travel");
        return false;
    }
}

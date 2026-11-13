using System;

namespace Styx.Logic.Pathing;

// A failed route is not evidence of death. The owner releases movement first;
// this watch only samples state until a checkpoint, STOP, expiry or session loss.
internal static class WowBHotspotDeathWatch
{
    internal static bool Wait(Func<(bool Ready, string Observation)> poll, Func<bool> cancelled,
        Func<int, bool> wait, Func<long> clock, Action<string> log)
    {
        string last = "";
        long nextReport = 0;
        while (!cancelled())
        {
            var sample = poll();
            if (cancelled()) return false; // STOP wins even if it races a checkpoint
            if (sample.Ready) return true;
            long now = clock();
            if (sample.Observation != last || now >= nextReport)
            {
                log("[Grind] hotspot failure death watch: " + sample.Observation +
                    "; holding position and checking again (no release or travel without a checkpoint)");
                last = sample.Observation;
                nextReport = now + 15000;
            }
            if (wait(500)) return false;
        }
        return false;
    }
}

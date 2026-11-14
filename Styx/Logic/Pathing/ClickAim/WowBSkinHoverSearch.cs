using System;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Hover-only search: never selects or clicks anything. The caller still must
// revalidate the corpse, session, geometry and exact hover before right-down.
internal static class WowBSkinHoverSearch
{
    internal static bool Find(int width, int height, int centerX, int centerY, WowBGuid corpse, int maxProbes,
        Func<int, int, bool> move, Func<(bool Readable, WowBGuid Guid)> read, Func<bool> safe, Func<int, bool> wait,
        out int x, out int y, out bool exhausted, out string reason)
    {
        x = y = 0; exhausted = false;
        reason = "invalid hover search geometry or identity";
        if (width < 3 || height < 3 || corpse.IsEmpty || maxProbes < 1 || maxProbes > 40) return false;
        reason = "could not clear or read initial hover";
        int awayX = Math.Max(1, (int)(width * 0.05)), awayY = Math.Max(1, (int)(height * 0.05));
        if (!safe() || !move(awayX, awayY) || wait(200) || !safe()) return false;
        var baseline = read();
        if (!baseline.Readable || baseline.Guid == corpse) return false;
        int probes = 0;
        int[] radii = { 0, 40, 80, 130, 190, 260 };
        foreach (int radius in radii)
        {
            int steps = radius == 0 ? 1 : 8;
            for (int step = 0; step < steps && probes < maxProbes; step++)
            {
                int px = centerX, py = centerY;
                if (radius != 0)
                {
                    double angle = Math.PI * 2.0 * step / steps;
                    px += (int)Math.Round(Math.Cos(angle) * radius);
                    py += (int)Math.Round(Math.Sin(angle) * radius);
                }
                if (px <= 0 || py <= 0 || px >= width - 1 || py >= height - 1) continue;
                reason = "hover search interrupted, geometry changed or move failed";
                if (!safe() || !move(px, py) || wait(200) || !safe()) return false;
                probes++;
                var hover = read();
                if (!hover.Readable) { reason = "candidate mouseover unreadable"; return false; }
                if (hover.Guid != corpse) continue;
                if (wait(50) || !safe()) { reason = "hover confirmation interrupted"; return false; }
                var again = read();
                if (!again.Readable || again.Guid != corpse) { reason = "candidate hover changed or became unreadable"; return false; }
                x = px; y = py;
                reason = "hover-only probe " + probes + " matched the corpse GUID twice; candidate remains unverified";
                return true;
            }
            if (probes >= maxProbes) break;
        }
        exhausted = true;
        reason = probes + " hover-only probes found no matching corpse GUID";
        return false;
    }
}

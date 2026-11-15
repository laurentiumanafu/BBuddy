using System;

namespace Styx.Logic.Pathing;

// Policy only. Clock and cast words are observational candidates, and position
// changes alone are not evidence of the intended destination.
internal sealed class WowBTownCastProgress
{
    internal enum Result { Waiting, Interrupted, ArrivedCandidate, Uncertain }
    private readonly WowBTownCastCandidate.State expected;
    private readonly uint originMap;
    private readonly double originX, originY, originZ;
    private bool seen, uncertain;
    private uint start, end, lastRemaining, firstIdleRemaining;
    private long lastActiveMs, firstIdleMs, lastElapsed = -1;
    private int idleCount, movedCount;
    private uint lastMap;
    private double lastX, lastY, lastZ;

    internal WowBTownCastProgress(WowBTownCastCandidate.State expected, uint map, double x, double y, double z)
    { this.expected = expected; originMap = map; originX = x; originY = y; originZ = z; }

    internal Result Update(WowBTownCastCandidate.State cast, uint castStart, uint castEnd, uint clock, long elapsed,
        uint map, double x, double y, double z)
    {
        if (elapsed < 0 || elapsed < lastElapsed || clock == 0 || map == uint.MaxValue || originMap == uint.MaxValue ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            !double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ) ||
            Math.Abs(x) >= 100000 || Math.Abs(y) >= 100000 || Math.Abs(z) >= 100000 ||
            Math.Abs(originX) >= 100000 || Math.Abs(originY) >= 100000 || Math.Abs(originZ) >= 100000)
        { uncertain = true; return Result.Uncertain; }
        lastElapsed = elapsed;
        if (cast == expected && (cast == WowBTownCastCandidate.State.AstralRecall || cast == WowBTownCastCandidate.State.Hearthstone))
        {
            if (castStart == 0 || castEnd == 0 || unchecked(castEnd - castStart) != 10000U)
            { uncertain = true; return Result.Uncertain; }
            if (!seen) { seen = true; start = castStart; end = castEnd; }
            else if (start != castStart || end != castEnd || idleCount != 0) uncertain = true;
            uint remain = unchecked(castEnd - clock);
            if (remain > 10000U) { uncertain = true; return Result.Uncertain; }
            lastRemaining = remain; lastActiveMs = elapsed; idleCount = 0; movedCount = 0;
            return uncertain ? Result.Uncertain : Result.Waiting;
        }
        if (cast != WowBTownCastCandidate.State.Idle)
        { uncertain = true; idleCount = movedCount = 0; return Result.Uncertain; }
        if (!seen) return uncertain ? Result.Uncertain : Result.Waiting;
        if (idleCount == 0) { firstIdleRemaining = unchecked(end - clock); firstIdleMs = elapsed; }
        idleCount++;
        double displacement = Math.Sqrt((x - originX) * (x - originX) + (y - originY) * (y - originY));
        bool moved = map != originMap || displacement >= 50 || Math.Abs(z - originZ) >= 50;
        if (moved)
        {
            if (movedCount > 0 && map == lastMap && Math.Sqrt((x-lastX)*(x-lastX)+(y-lastY)*(y-lastY)) <= 3 && Math.Abs(z-lastZ) <= 3) movedCount++;
            else movedCount = 1;
            lastMap=map; lastX=x; lastY=y; lastZ=z;
            bool clearlyEarly = firstIdleRemaining >= 200 && firstIdleRemaining <= 10000;
            if (!uncertain && !clearlyEarly && idleCount >= 2 && movedCount >= 2 && elapsed - firstIdleMs >= 100) return Result.ArrivedCandidate;
        }
        else movedCount = 0;
        // The last active sample must be close to the first idle sample and
        // have substantial time remaining; a missing/late sample cannot prove
        // interruption. Require two idle samples and an unmoved origin vicinity.
        if (!uncertain && idleCount >= 2 && elapsed - firstIdleMs >= 100 && !moved && displacement < 20 && Math.Abs(z-originZ) < 20 &&
            elapsed - lastActiveMs <= 1000 && lastRemaining >= 500 &&
            firstIdleRemaining >= 200 && firstIdleRemaining <= lastRemaining) return Result.Interrupted;
        return uncertain ? Result.Uncertain : Result.Waiting;
    }
}

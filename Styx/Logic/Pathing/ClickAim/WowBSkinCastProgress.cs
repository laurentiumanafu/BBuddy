namespace Styx.Logic.Pathing;

// Bounded, experimental interpretation of one observed 70938 skinning pattern.
// Clearing a cast-like candidate is not proof that the cast or loot succeeded.
internal sealed class WowBSkinCastProgress
{
    internal enum Result { Waiting, CandidateCleared, Fallback, ActiveTimeout }
    private const int FallbackMs = 6000, MaxHoldMs = 12000;
    private const int MinimumObservedMs = 2600, LootSettleMs = 1000;
    private bool seen, uncertain;
    private uint castStart, castEnd;
    private int firstSeen, idleCount, clearedAt;

    // Only stable idle observations with no intervening unknown/mining/cast
    // permit a second interaction. A cast candidate is never proof of loot.
    internal bool SawCast => seen;
    internal bool StableIdle => idleCount >= 2;
    internal bool ConfirmedNoCast => !seen && !uncertain && idleCount >= 2;

    // A timeout or a transiently unreadable guard must not release the caller
    // into movement. The hard limit is enforced separately by stopping the run.
    internal bool CanRelease(Result result, bool guardSafe) => guardSafe && StableIdle &&
        (result == Result.CandidateCleared || result == Result.Fallback);
    internal static bool HoldLimitReached(int elapsed) => elapsed >= MaxHoldMs;

    internal Result Update(WowBGatherCastResearch.Observation state, uint start, uint end, int elapsed)
    {
        if (state == WowBGatherCastResearch.Observation.Skinning)
        {
            if (!seen) { seen = true; castStart = start; castEnd = end; firstSeen = elapsed; }
            else if (start != castStart || end != castEnd || idleCount != 0) uncertain = true;
            idleCount = 0;
        }
        else if (state == WowBGatherCastResearch.Observation.Idle)
        {
            if (idleCount == 0) clearedAt = elapsed;
            idleCount++;
            if (seen && clearedAt - firstSeen < MinimumObservedMs) uncertain = true;
            if (seen && !uncertain && idleCount >= 2 && elapsed - clearedAt >= LootSettleMs)
                return Result.CandidateCleared;
        }
        else { uncertain = true; idleCount = 0; }

        // If a seen candidate has not cleared reliably, avoid deliberately
        // moving while it may still be active. A hard limit cannot prove idle.
        if (elapsed >= MaxHoldMs) return seen && idleCount < 2 ? Result.ActiveTimeout : Result.Fallback;
        if (elapsed >= FallbackMs && (!seen || idleCount >= 2)) return Result.Fallback;
        return Result.Waiting;
    }
}

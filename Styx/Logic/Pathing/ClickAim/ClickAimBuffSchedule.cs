using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Time since this bot session's own key attempts, not observed aura duration.
internal sealed class ClickAimBuffSchedule
{
    internal const long RefreshMs = 30L * 60 * 1000;
    internal const long RetryMs = 30_000;
    internal const int RefreshBeforeMs = 30_000;
    private readonly Dictionary<int, long> _lastAttempt = new Dictionary<int, long>();

    // Null is unknown and keeps the original 30-minute fallback; a negative
    // value means absent/expired in a guarded candidate snapshot. Fresh runs
    // and confirmed resurrections still attempt once regardless of the snapshot.
    internal bool Due(int spellId, long now, int? candidateRemainingMs = null)
    {
        if (spellId <= 0) return false;
        if (!_lastAttempt.TryGetValue(spellId, out long last) || now < last) return true;
        long elapsed = now - last;
        if (!candidateRemainingMs.HasValue) return elapsed >= RefreshMs;
        return elapsed >= RetryMs && candidateRemainingMs.Value <= RefreshBeforeMs;
    }

    internal void RecordAttempt(int spellId, long now)
    {
        if (spellId > 0) _lastAttempt[spellId] = now;
    }

    internal void Reset() => _lastAttempt.Clear();
}

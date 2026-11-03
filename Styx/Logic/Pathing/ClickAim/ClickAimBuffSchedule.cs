using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Time since this bot session's own key attempts, not observed aura duration.
internal sealed class ClickAimBuffSchedule
{
    internal const long RefreshMs = 30L * 60 * 1000;
    private readonly Dictionary<int, long> _lastAttempt = new Dictionary<int, long>();

    internal bool Due(int spellId, long now)
        => spellId > 0 && (!_lastAttempt.TryGetValue(spellId, out long last) || now < last || now - last >= RefreshMs);

    internal void RecordAttempt(int spellId, long now)
    {
        if (spellId > 0) _lastAttempt[spellId] = now;
    }

    internal void Reset() => _lastAttempt.Clear();
}

using System;

namespace Styx.Logic.Pathing;

// Research-grade recovery policy, not proof a posted click was delivered.
// The caller must repeat the complete spot, state, image, hover and focus guards
// before every retry. Any non-ghost observation permanently vetoes retries.
internal sealed class WowBResurrectionRetry
{
    internal const int DelayMs = 5000, MaxPosts = 3;
    private bool _enabled;
    private int _posts = 1, _checks, _ghostReads, _lastAttempt;

    internal WowBResurrectionRetry(bool is70938, string postReason, int now)
    {
        _enabled = is70938 && postReason == WowBReleaseTransition.CleanPost;
        _lastAttempt = now;
    }

    internal int Posts => _posts;

    internal bool Observe(bool confirmedGhost, int now)
    {
        if (!confirmedGhost) { _enabled = false; _ghostReads = 0; return false; }
        _ghostReads = Math.Min(_ghostReads + 1, 2);
        return _enabled && _checks < MaxPosts - 1 && _ghostReads >= 2 &&
            unchecked(now - _lastAttempt) >= DelayMs;
    }

    internal void Attempted(bool posted, string reason, int now)
    {
        _checks++;
        _lastAttempt = now;
        _ghostReads = 0;
        if (posted) _posts++;
        if (!posted || reason != WowBReleaseTransition.CleanPost) _enabled = false;
    }
}

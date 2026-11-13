using System;

namespace Styx.Logic.Pathing;

/// <summary>
/// Bounded observation after a posted release click. A retry callback must
/// independently revalidate the dead player and a fresh release dialog before
/// posting anything. No unknown or merely alive state authorizes a retry or route.
/// </summary>
internal static class WowBReleaseTransition
{
    internal enum State { Unknown, Alive, Dead, Ghost }
    internal const int RetryDelayMs = 5000, MaxPosts = 3, SampleMs = 500;
    internal const string CleanPost = "left-button messages posted";

    internal static bool Wait(Func<(State State, string Reason)> observe,
        Func<(bool Posted, string Reason)> retry, Func<bool> allowed, Func<int, bool> wait,
        Func<int> clock, Action<string> log, string initialPostReason, int timeoutMs, bool allowRetries)
    {
        int start = clock(), lastAttempt = start, lastLog = unchecked(start - 5000);
        int posts = 1, retryChecks = 0, deadReads = 0;
        bool clean = initialPostReason == CleanPost && allowRetries;
        State? lastState = null;
        string lastReason = "";
        if (initialPostReason != CleanPost) log("[Corpse] release button-up was not confirmed posted; no repeat click will be attempted");
        while (allowed() && unchecked(clock() - start) < timeoutMs)
        {
            var observation = observe();
            if (!allowed()) return false;
            if (observation.State == State.Ghost) return true;
            if (observation.State != State.Dead && clean)
            {
                clean = false; // changing/unreadable state permanently vetoes further clicks
                log("[Corpse] state no longer confirmed dead; further release retries disabled");
            }
            deadReads = observation.State == State.Dead ? Math.Min(deadReads + 1, 2) : 0;
            int now = clock();
            if (lastState != observation.State || lastReason != observation.Reason || unchecked(now - lastLog) >= 5000)
            {
                log("[Corpse] waiting for ghost: " + observation.State.ToString().ToLowerInvariant() +
                    " (" + observation.Reason + "); release posts=" + posts + "; no movement permitted yet");
                lastState = observation.State; lastReason = observation.Reason; lastLog = now;
            }
            if (clean && retryChecks < MaxPosts - 1 && deadReads >= 2 && unchecked(now - lastAttempt) >= RetryDelayMs && allowed())
            {
                // The callback repeats full state, fresh visual, focus, geometry,
                // hover and final button-down guards. This is not a blind retry.
                retryChecks++;
                var attempt = retry();
                lastAttempt = clock();
                deadReads = 0;
                if (attempt.Posted)
                {
                    posts++;
                    clean = attempt.Reason == CleanPost;
                    log("[Corpse] release retry " + posts + "/" + MaxPosts + " posted after repeated dead observations; transition still unconfirmed");
                    if (!clean) log("[Corpse] release button-up was not confirmed posted; no further repeat click will be attempted");
                }
                else
                {
                    clean = false; // an absent/changed dialog or failed safety check is not permission to try again
                    log("[Corpse] release retry withheld; no further retry: " + attempt.Reason);
                }
            }
            if (wait(SampleMs)) return false;
        }
        return false;
    }
}

namespace Styx.Logic.Pathing;

// Health is only a preference for voluntary pulls, never evidence of ownership,
// hostility or whether another player is attacking. Unknown/implausible maxima
// are not treated as full; all otherwise eligible targets remain fallback choices.
internal static class WowBPullPreference
{
    internal static bool Full(uint health, uint maximum)
        => maximum > 0 && health > 0 && health == maximum;

    internal static bool Better(bool full, double distance, bool bestFull, double bestDistance)
        => full != bestFull ? full : distance < bestDistance;

    internal static bool PreferAlternative(bool selectedFull, bool alternativeFull, bool sameTarget)
        => !selectedFull && alternativeFull && !sameTarget;
}

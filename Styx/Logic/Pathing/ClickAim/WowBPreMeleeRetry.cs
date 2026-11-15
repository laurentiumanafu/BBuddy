using System;

namespace Styx.Logic.Pathing;

// A single per-fight opportunity, not a new rotation or proof of line of sight.
// Once melee is reached it cannot be rearmed by the target moving away.
internal sealed class WowBPreMeleeRetry
{
    internal const int BackoffMs = 2000;
    private bool _latched, _reachedMelee, _reserved;
    private long _latchedAt;

    internal void ObserveDistance(double planarDistance, double meleeDistance)
    {
        if (double.IsFinite(planarDistance) && planarDistance >= 0 && planarDistance <= meleeDistance)
            _reachedMelee = true;
    }

    internal void Latch(long now)
    {
        if (_latched) return;
        _latched = true; _latchedAt = now;
    }

    internal bool MayTry(long now, double distance, double minDistance, double spellRange, uint mana, uint requiredMana)
        => _latched && !_reserved && !_reachedMelee && now >= _latchedAt && now - _latchedAt >= BackoffMs &&
           double.IsFinite(distance) && double.IsFinite(minDistance) && double.IsFinite(spellRange) &&
           distance > minDistance && minDistance >= 0 && spellRange > minDistance && distance <= spellRange &&
           requiredMana > 0 && mana >= requiredMana;

    // Reserve before any attempt to stop or face: even an interrupted attempt
    // cannot repeat indefinitely or send more than one key for this opportunity.
    internal void Reserve() { _reserved = true; }
}

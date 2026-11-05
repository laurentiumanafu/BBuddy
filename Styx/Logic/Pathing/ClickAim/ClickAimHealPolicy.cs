using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Hotbar and observed-power policy, not a client spell-cost or usability reader.
internal static class ClickAimHealPolicy
{
    internal static bool PowerAllowed(uint power, uint maximum, uint sessionPeak, int minimumPercent, uint observedCost)
    {
        if (power == 0) return false;
        if (observedCost > 0 && (ulong)power < (ulong)observedCost + observedCost / 4UL) return false;
        uint denominator = Math.Max(maximum, sessionPeak);
        return minimumPercent <= 0 || denominator == 0 || power * 100.0 / denominator >= minimumPercent;
    }

    internal static bool ShouldAttemptHeal(ClickAimAction? heal, bool blocked, uint health, uint maximumHealth,
                                          int healPercent, bool cooldownReady, bool powerAllowed)
        => heal != null && heal.KeyVk != 0 && heal.IsHeal && !blocked && maximumHealth > 0 && health > 0 &&
           healPercent > 0 && health * 100.0 / maximumHealth <= healPercent && cooldownReady && powerAllowed;

    internal static bool StillOnBar(ClickAimAction? heal, List<ClickAimAction> bar)
    {
        if (heal == null || heal.KeyVk == 0 || !heal.IsHeal) return false;
        foreach (ClickAimAction current in bar)
            if (current.Slot == heal.Slot && current.Id == heal.Id && current.Raw == heal.Raw &&
                current.KeyVk == heal.KeyVk && current.IsHeal) return true;
        return false;
    }
}

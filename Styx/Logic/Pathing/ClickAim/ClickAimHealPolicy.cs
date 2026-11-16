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

    // The heal is never held back by earlier outcomes. A press that shows no
    // net health gain can be a heal the attackers out-damaged, and a press the
    // client dropped during the global cooldown looks exactly the same, so a
    // counter of those presses used to switch healing off mid-fight. Only the
    // configured health line, the power gate, the retry spacing and the shared
    // cooldown decide; enough mana is the affordability rule, not a hold.
    internal static bool ShouldAttemptHeal(ClickAimAction? heal, uint health, uint maximumHealth,
                                          int healPercent, bool cooldownReady, bool powerAllowed)
        => heal != null && heal.KeyVk != 0 && heal.IsHeal && maximumHealth > 0 && health > 0 &&
           healPercent > 0 && health * 100.0 / maximumHealth <= healPercent && cooldownReady && powerAllowed;

    // A net health gain or power drop is only evidence, not proof, that the
    // attempted heal landed. The separate binding is user-configured and its
    // claimed non-toggle semantics cannot be verified by the bot.
    internal static bool QueuePostHealStartAttack(bool is70862, int binding, bool priorAttackAttempt,
        uint beforeHealth, uint afterHealth, uint beforePower, uint afterPower)
        => is70862 && binding != 0 && ClickAimSettings.ValidStartAttackVk(binding) && priorAttackAttempt &&
           (afterHealth > beforeHealth || afterPower < beforePower);

    internal static bool ReadyPostHealStartAttack(bool pending, int elapsedMs, int settleMs, double distance, double range)
        => pending && elapsedMs >= settleMs && double.IsFinite(distance) && distance >= 0 &&
           double.IsFinite(range) && range >= 0 && distance <= range;

    internal static bool StillOnBar(ClickAimAction? heal, List<ClickAimAction> bar)
    {
        if (heal == null || heal.KeyVk == 0 || !heal.IsHeal) return false;
        foreach (ClickAimAction current in bar)
            if (current.Slot == heal.Slot && current.Id == heal.Id && current.Raw == heal.Raw &&
                current.KeyVk == heal.KeyVk && current.IsHeal) return true;
        return false;
    }
}

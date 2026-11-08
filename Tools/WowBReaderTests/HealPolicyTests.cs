#if WOWB_READER_TESTS
using System;
using System.Collections.Generic;
using Styx.Logic.Pathing;

internal static class HealPolicyTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    public static void Run()
    {
        var heal = new ClickAimAction { Slot = 3, Id = 331, Raw = 331, KeyVk = ClickAimCombat.KeyForSlot(3), Kind = ClickAimActionKind.Spell };
        var damage = new ClickAimAction { Slot = 2, Id = 133, Raw = 133, KeyVk = ClickAimCombat.KeyForSlot(2), Kind = ClickAimActionKind.Spell };
        try
        {
            ClickAimCombat.ClearRoleOverrides();
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(null, false, 40, 100, 40, true, true), "no heal means no heal attempt");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(damage, false, 40, 100, 40, true, true), "damage is not a heal");
            Check(ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 40, 100, 40, true, true), "eligible heal");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 0, 100, 40, true, true), "dead cannot heal");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 41, 100, 40, true, true), "above threshold");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, true, 40, 100, 40, true, true), "blocked heal");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 40, 100, 40, false, true), "cooldown");
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 40, 100, 40, true, false), "power gate prevents heal attempt");
            Check(!ClickAimHealPolicy.PowerAllowed(0, 100, 0, 0, 0), "zero power even with percentage disabled");
            Check(!ClickAimHealPolicy.PowerAllowed(24, 100, 0, 25, 0), "below configured threshold");
            Check(!ClickAimHealPolicy.PowerAllowed(49, 100, 0, 25, 40), "below observed cost plus margin");
            Check(ClickAimHealPolicy.PowerAllowed(50, 100, 0, 25, 40), "at estimated cost plus margin");
            Check(!ClickAimHealPolicy.PowerAllowed(uint.MaxValue, uint.MaxValue, 0, 0, uint.MaxValue), "cost margin cannot overflow");
            Check(!ClickAimHealPolicy.QueuePostHealStartAttack(false, 0x75, true, 50, 70, 100, 80), "post-heal binding only on 70862");
            Check(!ClickAimHealPolicy.QueuePostHealStartAttack(true, 0, true, 50, 70, 100, 80), "binding is opt-in");
            Check(!ClickAimHealPolicy.QueuePostHealStartAttack(true, 0x31, true, 50, 70, 100, 80), "normal Attack key cannot be the binding");
            Check(!ClickAimHealPolicy.QueuePostHealStartAttack(true, 0x75, false, 50, 70, 100, 80), "no recovery before an attack attempt");
            Check(!ClickAimHealPolicy.QueuePostHealStartAttack(true, 0x75, true, 50, 45, 100, 100), "heal without observed effect does not queue");
            Check(ClickAimHealPolicy.QueuePostHealStartAttack(true, 0x75, true, 50, 70, 100, 100), "health gain queues one recovery");
            Check(ClickAimHealPolicy.QueuePostHealStartAttack(true, 0x79, true, 50, 45, 100, 80), "power drop may represent heal masked by damage");
            Check(!ClickAimHealPolicy.ReadyPostHealStartAttack(true, 3599, 3600, 3, 4), "no key while heal may still be casting");
            Check(!ClickAimHealPolicy.ReadyPostHealStartAttack(true, 3600, 3600, 4.1, 4), "no key outside melee range");
            Check(!ClickAimHealPolicy.ReadyPostHealStartAttack(true, 3600, 3600, double.NaN, 4), "unknown range withheld");
            Check(!ClickAimHealPolicy.ReadyPostHealStartAttack(false, 3600, 3600, 3, 4), "consumed request is not repeated");
            Check(ClickAimHealPolicy.ReadyPostHealStartAttack(true, 3600, 3600, 4, 4), "one binding press after settle in melee");
            Check(ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { heal }), "same heal on same slot");
            var changed = new ClickAimAction { Slot = 3, Id = 133, Raw = 133, KeyVk = heal.KeyVk, Kind = ClickAimActionKind.Spell };
            Check(!ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { changed }), "slot changed");
            ClickAimCombat.SetRoleOverride(331, ClickAimSpellRole.Ignore);
            Check(!ClickAimHealPolicy.ShouldAttemptHeal(heal, false, 40, 100, 40, true, true) &&
                  !ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { heal }), "role revoked");
        }
        finally { ClickAimCombat.ClearRoleOverrides(); }
        Console.WriteLine("Heal policy fixtures passed (synthetic only; real spell costs unknown).");
    }
}
#endif

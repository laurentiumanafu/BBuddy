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
            Check(!ClickAimHealPolicy.ShouldSelectSelf(null, false, 40, 100, 40, true, true), "no heal means no self selection");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(damage, false, 40, 100, 40, true, true), "damage is not a heal");
            Check(ClickAimHealPolicy.ShouldSelectSelf(heal, false, 40, 100, 40, true, true), "eligible heal");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, false, 0, 100, 40, true, true), "dead cannot heal");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, false, 41, 100, 40, true, true), "above threshold");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, true, 40, 100, 40, true, true), "blocked heal");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, false, 40, 100, 40, false, true), "cooldown");
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, false, 40, 100, 40, true, false), "power gate prevents self selection");
            Check(!ClickAimHealPolicy.PowerAllowed(0, 100, 0, 0, 0), "zero power even with percentage disabled");
            Check(!ClickAimHealPolicy.PowerAllowed(24, 100, 0, 25, 0), "below configured threshold");
            Check(!ClickAimHealPolicy.PowerAllowed(49, 100, 0, 25, 40), "below observed cost plus margin");
            Check(ClickAimHealPolicy.PowerAllowed(50, 100, 0, 25, 40), "at estimated cost plus margin");
            Check(!ClickAimHealPolicy.PowerAllowed(uint.MaxValue, uint.MaxValue, 0, 0, uint.MaxValue), "cost margin cannot overflow");
            Check(ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { heal }), "same heal on same slot");
            var changed = new ClickAimAction { Slot = 3, Id = 133, Raw = 133, KeyVk = heal.KeyVk, Kind = ClickAimActionKind.Spell };
            Check(!ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { changed }), "slot changed");
            ClickAimCombat.SetRoleOverride(331, ClickAimSpellRole.Ignore);
            Check(!ClickAimHealPolicy.ShouldSelectSelf(heal, false, 40, 100, 40, true, true) &&
                  !ClickAimHealPolicy.StillOnBar(heal, new List<ClickAimAction> { heal }), "role revoked");
        }
        finally { ClickAimCombat.ClearRoleOverrides(); }
        Console.WriteLine("Heal policy fixtures passed (synthetic only; real spell costs unknown).");
    }
}
#endif

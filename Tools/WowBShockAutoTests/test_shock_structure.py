"""Static wiring checks only; see RotationTests for production-policy execution."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
FIGHT = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
ROTATION = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimClassRotation.cs').read_text(encoding='utf-8-sig')


def section(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]


class ShockStructure(unittest.TestCase):
    def test_frost_shock_recognised_with_timed_shared_group(self):
        build = section(ROTATION, '    internal static ClickAimRotationPlan Build(', '    internal static bool IsDirectShock(')
        self.assertIn('Find(bar, new[] { "Frost Shock" }, ClickAimSpellRole.Damage)', build)
        self.assertIn('plan.DamageGroup = "Shocks";', build)
        self.assertIn('plan.DamageIntervalMs = 8000;', build)
        shock = section(ROTATION, '    internal static bool IsDirectShock(', '    internal static bool PreferShockMelee(')
        self.assertIn('"Frost Shock"', shock)
        self.assertIn('"Earth Shock"', shock)
        self.assertIn('plan.Class == ClickAimRotationClass.Shaman', shock)
        self.assertIn('auto.IsAutoAttack && auto.IsAttack', shock)

    def test_early_attack_is_guarded_and_precedes_spells(self):
        early = section(FIGHT, '            // For a Shock/Attack Shaman', '            /* ---- 1b. the damage-over-time spell')
        key = early.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
        for guard in ('MayPressConservativeAttack(shockAutoPlan, attackPressed, false, false, d, AttackRangeYd)',
                      'attackTarget.Guid != defenceTarget', 'attackTarget.UnitAddress != expectedPull.UnitAddress',
                      'attackTarget.RecordAddress != expectedPull.RecordAddress', 'attackTarget.HasPlayerVtable',
                      'DefenceFieldsValid(', 'attackDistance, AttackRangeYd)', 'auto == null || !auto.IsAutoAttack || !auto.IsAttack'):
            self.assertLess(early.index(guard), key)
        self.assertEqual(2, early.count('FightAttackInputAllowed('))
        self.assertGreater(early.index('attackPressed = true;'), key)
        self.assertLess(FIGHT.index('if (ClickAimHealPolicy.ShouldAttemptHeal('), FIGHT.index('            // For a Shock/Attack Shaman'))

    def test_cooldown_chases_without_latching_melee(self):
        fight = section(FIGHT, '    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('PreferShockMelee(shockAutoPlan, shockReady)', fight)
        self.assertIn('bool temporaryShockMelee = shockAutoPlan && !melee;', fight)
        self.assertIn('if (!temporaryShockMelee && !meleeOnce)', fight)
        self.assertIn('MayPressConservativeAttack(true, attackPressed, swinging, pressPending, d, AttackRangeYd)', fight)
        self.assertIn('if (attackPressed || lastMeleeClickMs != 0) preserveAttackAfterHeal = true;', fight)
        self.assertIn('damage source and swing state unverified', fight)


if __name__ == '__main__':
    unittest.main()

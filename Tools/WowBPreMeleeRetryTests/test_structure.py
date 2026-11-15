"""Source-wiring checks only; this is not a Windows or live-client test."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
g = (root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
p = (root/'Styx/Logic/Pathing/ClickAim/WowBPreMeleeRetry.cs').read_text()

def section(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class PreMeleeRetry(unittest.TestCase):
    def test_per_fight_bounded_retry_and_melee_never_unlatched(self):
        self.assertIn('internal const int BackoffMs = 2000;', p)
        for x in ('_latched && !_reserved && !_reachedMelee', 'distance > minDistance',
                  'distance <= spellRange', 'mana >= requiredMana', 'requiredMana > 0',
                  'planarDistance <= meleeDistance', '_reserved = true'):
            self.assertIn(x, p)
        fight = section(g, 'private static string Fight(', "    /// <summary>\n    /// A unit's health straight out")
        self.assertEqual(1, fight.count('var preMeleeRetry = new WowBPreMeleeRetry();'))
        self.assertIn('preMeleeRetry.ObserveDistance(d, MeleeEngageYd)', fight)
        self.assertEqual(2, fight.count('preMeleeRetry.Latch(Environment.TickCount64)'))
        self.assertIn('preMeleeRetry.MayTry(', fight)
        self.assertIn('if (retryCast) preMeleeRetry.Reserve()', fight)
        self.assertEqual(1, fight.count('bool melee = false;'))
        self.assertNotIn('melee = false;', fight[fight.index('bool melee = false;') + len('bool melee = false;'):])
        self.assertLess(fight.index('if (retryCast) preMeleeRetry.Reserve()'), fight.index('PreMeleeRetrySample(hwnd'))
    def test_retry_does_not_rewrite_shaman_or_macro(self):
        part = section(g, 'bool retryClass =', 'if (!canCast && !melee')
        for x in ('ClickAimRotationClass.Druid', 'ClickAimRotationClass.Mage',
                  'ClickAimRotationClass.Priest', 'ClickAimRotationClass.Warlock',
                  'reader.IsExperimental70938', '!keyOneMacro', '!attackPressed',
                  '!meleeSwinging', 'lastAttackPressMs == 0', 'lastMeleeClickMs == 0',
                  '!unmeasuredRage', '!maelstromCast', '!supplemental',
                  '!ClickAimClassRotation.IsAnyShock(damage)', '!ClickAimClassRotation.IsLightningBolt(damage)',
                  '_castCosts.TryGetValue', 'ClickAimClassRotation.Ready', 'damage.RangeYd'):
            self.assertIn(x, part)
        self.assertNotIn('ClickAimRotationClass.Shaman', part)
        self.assertIn('maelstromCast || (!melee && !shockMelee) || retryCast', part)
    def test_rechecks_player_target_resource_range_facing_and_slot(self):
        helper = section(g, 'private static bool PreMeleeRetrySample(', 'private static string Fight(')
        for x in ('expectedPlayer.Guid', 'player.UnitAddress != expectedPlayer.UnitAddress',
                  'player.RecordAddress != expectedPlayer.RecordAddress', 'target.Guid != expectedTarget.Guid',
                  'target.UnitAddress != expectedTarget.UnitAddress', 'target.RecordAddress != expectedTarget.RecordAddress',
                  'target.HasPlayerVtable', 'DefenceFieldsValid(', 'self.PlayerPowerTypeCandidate != 0',
                  'self.Power < requiredMana', 'map != expectedMap', 'Geometry(player, target)',
                  'Geometry(finalPlayer, finalTarget)', 'planar > minimumDistance', 'distance <= spell.RangeYd',
                  'TryReadFacing', 'current.Raw == spell.Raw', 'current.KeyVk == spell.KeyVk',
                  'FightAttackInputAllowed', 'ObjectManager.Wow != memory', 'ClickAimAbort.Requested'):
            self.assertIn(x, helper)
        cast = section(g, 'if (retryCast) preMeleeRetry.Reserve()', 'ClickAimInput.TapKey(hwnd, castDamage!.KeyVk, 40)')
        self.assertIn('PreMeleeRetrySample(hwnd', cast)
        self.assertIn('out uint retryMana', cast)
        self.assertIn('myMana = retryMana', cast)
    def test_delayed_cost_fail_closed(self):
        helper = section(g, 'private static bool PendingCostSample(', 'private static bool PreMeleeRetrySample(')
        for x in ('!reader.IsExperimental70707', '!reader.ExperimentalGrindActive', 'TraceIdentity70707',
                  'first.PlayerPowerTypeCandidate != 0', 'second.PlayerPowerTypeCandidate != 0',
                  'first.MaxPower == 0', 'first.Power != second.Power', 'first.MaxPower != second.MaxPower',
                  'beforeMap != afterMap', 'ObjectManager.Wow != memory', 'ClickAimAbort.Requested'):
            self.assertIn(x, helper)
        delayed = section(g, 'if (pendingCastId != 0)', 'double myPercent')
        for x in ('pendingCastId = 0;', 'age >= 0 && age <= 8000', 'me.Guid == pendingCastPlayer.Guid',
                  'me.UnitAddress == pendingCastPlayer.UnitAddress', 'me.RecordAddress == pendingCastPlayer.RecordAddress',
                  'PendingCostSample(', 'observedMap == pendingCastMap', 'observedMax == pendingCastMax',
                  'LearnCastCost(costId, pendingCastMana, observedPower)'):
            self.assertIn(x, delayed)
        self.assertNotIn('_pendingCastId', g)
        self.assertIn('!(keyOneMacro && ClickAimClassRotation.IsAnyShock(castDamage!))', g)
        self.assertIn('!(keyOneMacro && ClickAimClassRotation.IsAnyShock(meleeHit))', g)

if __name__ == '__main__': unittest.main()

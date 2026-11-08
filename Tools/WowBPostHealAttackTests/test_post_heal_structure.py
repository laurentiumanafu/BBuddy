"""Static wiring checks, not an in-game attack or heal test."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
G = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
P = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimHealPolicy.cs').read_text(encoding='utf-8-sig')
S = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimSettings.cs').read_text(encoding='utf-8-sig')
UI = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
UI_CODE = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')


def section(source, start, end):
    a = source.index(start)
    return source[a:source.index(end, a)]


class PostHealStartAttack(unittest.TestCase):
    def test_binding_is_explicit_restricted_and_opt_in(self):
        self.assertIn('public static int PostHealStartAttackVk;', S)
        self.assertIn('PostHealStartAttackVk = 0;', S)
        self.assertIn('vk >= 0x75 && vk <= 0x79', S)
        self.assertIn('hasNumber && ValidStartAttackVk(number) ? number : 0', S)
        self.assertIn('PostHealStartAttackVk = ', S)
        root = ET.fromstring(UI)
        ns = {'p': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation',
              'x': 'http://schemas.microsoft.com/winfx/2006/xaml'}
        checkbox = next(c for c in root.findall('.//p:CheckBox', ns)
                        if c.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name') == 'chkPostHealStartAttack')
        self.assertEqual('False', checkbox.get('IsChecked'))
        self.assertIn('ElementName=chkPostHealStartAttack', UI)
        for name in ('F6', 'F7', 'F8', 'F9', 'F10', 'not Attack Target'):
            self.assertIn(name, UI)
        self.assertIn('ClickAimUi.Running || ClickAimUi.Paused', UI_CODE)
        self.assertIn('selectedStartAttack != ClickAimSettings.PostHealStartAttackVk', UI_CODE)
        self.assertIn('chkPostHealStartAttack.IsChecked == true ? preferredStartAttack : 0', UI_CODE)
        self.assertIn('chkPostHealStartAttack.IsChecked = savedStartAttack != 0', UI_CODE)
        self.assertIn('PostHealStartAttackPreferredVk', S)
        self.assertIn('postHealPreferenceDoesNotEnable', S)
        self.assertIn('preferredStartAttack = savedStartAttack != 0', UI_CODE)

    def test_only_post_heal_with_net_effect_and_prior_attack(self):
        queue = section(P, '    internal static bool QueuePostHealStartAttack(', '    internal static bool ReadyPostHealStartAttack(')
        for guard in ('is70862', 'binding != 0', 'ValidStartAttackVk(binding)', 'priorAttackAttempt',
                      'afterHealth > beforeHealth || afterPower < beforePower'):
            self.assertIn(guard, queue)
        heal = section(G, '            if (ClickAimHealPolicy.ShouldAttemptHeal(', '            // A user-supplied non-toggle Start Attack binding')
        self.assertIn('if (attackPressed || lastMeleeClickMs != 0) preserveAttackAfterHeal = true;', heal)
        self.assertIn('QueuePostHealStartAttack(reader.IsExperimental70862', heal)
        self.assertIn('postHealPlayer = me.Guid;', heal)
        self.assertNotIn('TapKey(hwnd, auto.KeyVk', heal)
        self.assertNotIn('TapKey(hwnd, 0x70', heal)
        self.assertNotIn('TapKey(hwnd, TabVk', heal)

    def test_one_guarded_press_at_melee_after_settle(self):
        post = section(G, '            // A user-supplied non-toggle Start Attack binding', '            // Defensive Stoneclaw has priority')
        key = post.index('ClickAimInput.TapKey(hwnd, postHealStartKey, 40);')
        for guard in ('ClickAimCombat.HealVerifyMs', 'ClickAimKeySteer.ReleaseHeld()',
                      'postHealPlayer.IsEmpty', 'resumePlayer.Guid != postHealPlayer', 'resumeSelf.Health == 0',
                      'resumeTarget.Guid != defenceTarget', 'resumeTarget.UnitAddress != expectedPull.UnitAddress',
                      'resumeTarget.RecordAddress != expectedPull.RecordAddress', 'resumeTarget.HasPlayerVtable',
                      'DefenceFieldsValid(', 'resumeDistance, AttackRangeYd)', '_stop || ClickAimAbort.Requested || RouteRunner.StopRequested',
                      'ClickAimSettings.PostHealStartAttackVk != postHealStartKey'):
            self.assertLess(post.index(guard), key, guard)
        self.assertEqual(2, post.count('FightAttackInputAllowed('))
        self.assertGreater(post.index('postHealStartPending = false;', key), key)
        self.assertIn('key binding and swing state are unverified', post)
        self.assertNotIn('auto.KeyVk', post)
        self.assertNotIn('ClickAimInput.Click', post)

    def test_no_cast_or_attack_is_held_waiting_for_timer(self):
        post = section(G, '            // A user-supplied non-toggle Start Attack binding', '            // Defensive Stoneclaw has priority')
        self.assertNotIn('ClickAimAbort.Wait(ClickAimCombat.HealVerifyMs)', post)
        self.assertIn('ClickAimCombat.HealVerifyMs', post)
        ready = section(P, '    internal static bool ReadyPostHealStartAttack(', '    internal static bool StillOnBar(')
        self.assertIn('elapsedMs >= settleMs', ready)
        self.assertIn('double.IsFinite(distance)', ready)
        self.assertIn('distance <= range', ready)


if __name__ == '__main__':
    unittest.main()

"""Source wiring tests; they do not simulate the client or verify a macro."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text(encoding='utf-8-sig')
G = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
S = read('Styx/Logic/Pathing/ClickAim/ClickAimSettings.cs')
C = read('Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs')
R = read('Styx/Logic/Pathing/ClickAim/ClickAimClassRotation.cs')
U = read('UI/ClickAimCombatSettingsWindow.xaml')
UI = read('UI/ClickAimCombatSettingsWindow.xaml.cs')
def section(text, start, end):
    pos = text.index(start)
    return text[pos:text.index(end, pos)]

class MacroWiring(unittest.TestCase):
    def test_opt_in_persist_and_stopped_only(self):
        self.assertIn('UseKeyOneStartAttackMacro = false;', S)
        self.assertIn('UseKeyOneStartAttackMacro = flag;', S)
        self.assertIn('"UseKeyOneStartAttackMacro = "', S)
        self.assertIn('macroEnabled != ClickAimSettings.UseKeyOneStartAttackMacro', UI)
        self.assertIn('ClickAimUi.Running || ClickAimUi.Paused', UI)
        self.assertIn('if (!ClickAimUi.Running && !ClickAimUi.Paused)', UI)
        defaults = section(UI, 'private void btnDefaults_Click', 'private void btnSave_Click')
        self.assertIn('ClickAimUi.Running || ClickAimUi.Paused', defaults)
        ns = {'p':'http://schemas.microsoft.com/winfx/2006/xaml/presentation', 'x':'http://schemas.microsoft.com/winfx/2006/xaml'}
        cb = next(c for c in ET.fromstring(U).findall('.//p:CheckBox', ns) if c.get('{%s}Name' % ns['x']) == 'chkKeyOneAttackMacro')
        self.assertEqual(cb.get('IsChecked'), 'False')
        self.assertIn('non-toggle', U)

    def test_reserved_slot_and_competing_paths(self):
        bar = section(C, 'public static List<ClickAimAction> ReadBar(', '// UI preview')
        self.assertIn('reader.IsExperimental70862 && ClickAimSettings.UseKeyOneStartAttackMacro', bar)
        self.assertIn('decoded.RemoveAll(action => action.Slot == 1)', bar)
        self.assertIn('postHealStartKey = 0;', G)
        self.assertIn('if (!keyOneMacro && auto != null)', G)
        self.assertIn('else if (!keyOneMacro && !swinging && !pressPending', G)
        self.assertIn('if (!keyOneMacro && ClickAimClassRotation.MayPressConservativeAttack', G)

    def test_safe_guard_and_identity(self):
        helper = section(G, 'private static bool TryFightKeyOneMacro(', '    /// <summary>\n    /// One fight')
        key = helper.index('ClickAimInput.TapKey(hwnd, 0x31, 40)')
        for condition in ('reader.IsExperimental70862', 'UseKeyOneStartAttackMacro', 'CheckDeathCheckpoint(trackerOnly: true)',
                          'TryReadMapId', 'map != expectedMap', 'TryReadExperimentalActionSnapshot', 'bar[0] == 0',
                          'FightAttackInputAllowed(', 'player.Guid != expectedPlayer.Guid',
                          'player.UnitAddress != expectedPlayer.UnitAddress', 'player.RecordAddress != expectedPlayer.RecordAddress',
                          'self.Health == 0', 'target.Guid != expectedTarget.Guid', 'target.UnitAddress != expectedTarget.UnitAddress',
                          'target.RecordAddress != expectedTarget.RecordAddress', 'target.HasPlayerVtable', 'DefenceFieldsValid(',
                          'double.IsFinite(distance)', 'distance > maxDistance'):
            self.assertLess(helper.index(condition), key, condition)
        self.assertEqual(helper.count('FightAttackInputAllowed('), 2)
        guard = section(G, 'private static bool FightAttackInputAllowed(', '    // End the old rotation')
        for condition in ('_stop', 'ClickAimAbort.Requested', 'RouteRunner.StopRequested', 'Experimental70707BarSafe', 'FactionAttackInputAllowed', 'ProfileTargetAllowed'):
            self.assertIn(condition, guard)

    def test_start_then_each_heal_attempt(self):
        fight = section(G, 'private static string Fight(', "    /// <summary>\n    /// A unit's health straight out")
        start = fight.index('"at fight start"')
        self.assertLess(start, fight.index('ClickAimHealPolicy.ShouldAttemptHeal('))
        heal = section(fight, 'if (ClickAimHealPolicy.ShouldAttemptHeal(', '// A user-supplied non-toggle Start Attack binding')
        self.assertLess(heal.index('ClickAimInput.TapKey(hwnd, heal.KeyVk'), heal.index('"after a heal attempt"'))
        self.assertIn('ClickAimCombat.HealVerifyMs - unchecked(Environment.TickCount - lastHealMs)', heal)
        macro = section(heal, '                if (keyOneMacro)', '                // Healing and the actual swing')
        self.assertNotIn('afterHealth > myHealth', macro)
        self.assertNotIn('powerAfterHeal < powerBeforeHeal', macro)
        self.assertIn('ClickAimAbort.Wait(remaining)', macro)

    def test_shock_paths_and_melee_cooldown(self):
        self.assertIn('"Flame Shock"', section(R, 'internal static bool IsAnyShock(', 'internal static bool PreferShockMelee('))
        for name in ('dot', 'castDamage', 'meleeHit'):
            fragment = section(G, f'ClickAimInput.TapKey(hwnd, {name}', 'MarkFocusedAttack(target)')
            self.assertIn(f'IsAnyShock({name})', fragment)
            self.assertIn('"after a Shock attempt"', fragment)
        self.assertIn('ShockAutoPlan(rotation, auto, keyOneMacro)', G)
        self.assertIn('(keyOneMacro || (auto != null', R)
        self.assertIn('PreferShockMelee(shockAutoPlan, shockReady)', G)
        self.assertIn('bool temporaryShockMelee = shockAutoPlan && !melee', G)
        self.assertIn('reader.IsExperimental70862 && mobAfter == 0 && mobBeforeHealth > 0', G)
        self.assertIn('return "killed with " + string.Join', G)
        # A denied follow-up must not bypass the normal death/loot accounting.
        shock = section(G, 'ClickAimInput.TapKey(hwnd, castDamage!', '/* Wait for the cast')
        self.assertIn('key 1 follow-up withheld', shock)
        self.assertIn('MarkFocusedAttack(target)', shock)
        self.assertNotIn('return YieldForTargetChange', shock)

if __name__ == '__main__': unittest.main()

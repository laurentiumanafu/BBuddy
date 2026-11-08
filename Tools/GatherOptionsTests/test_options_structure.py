"""Static integration checks; the synthetic C# fixtures cover interaction policy.
These do not simulate a live client, spell cast, or navigation.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
runner = (ROOT / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text()
interaction = (ROOT / 'Styx/Logic/Pathing/ClickAim/GatherInteraction.cs').read_text()
settings = (ROOT / 'Styx/Logic/Pathing/ClickAim/GatherSettings.cs').read_text()
ui = (ROOT / 'UI/GatherSettingsWindow.cs').read_text()
policy = (ROOT / 'Styx/Logic/Pathing/ClickAim/GatherMovementSkill.cs').read_text()

class GatherOptionsTests(unittest.TestCase):
    def test_disabled_and_persisted(self):
        for name in ('IgnoreMonsters', 'UseMovementSkill'):
            self.assertIn(name + ' = false;', settings)
            self.assertIn('"' + name + '=" + ' + name, settings)
            self.assertIn('key == "' + name + '"', settings)
        self.assertIn('MovementSkillSlot = 0;', settings)
        self.assertIn('movementSlot < 0 || movementSlot > 12', ui)
        self.assertIn('_movementSkill.IsChecked == true && movementSlot == 0', ui)

    def test_combat_and_grind_guards(self):
        self.assertIn('_ignoreMonsters = GatherSettings.IgnoreMonsters;', runner)
        self.assertIn('if (fighting && !_ignoreMonsters)', runner)
        self.assertIn('if (!fighting)', runner)
        self.assertIn('!GrindRunner.TryReadGatherCombat(out fighting) || (fighting && !_ignoreMonsters)', runner)
        self.assertIn('ignoreMonsters = ignoreMonsters && extraSafety == null;', runner)
        self.assertIn('ignoreMonsters = ignoreMonsters && extraSafety == null && ignoreSafety != null;', interaction)
        self.assertIn('GatherAtNode(hwnd, node, safe)', runner)
        self.assertIn('GatherAtNode(hwnd, node, ignoreMonsters: _ignoreMonsters)', runner)
        self.assertIn('(combat && !ignoreMonsters)', interaction)
        self.assertIn('(fighting && !ignoreMonsters)', interaction)
        self.assertIn('if (ignoreMonsters && !ignoreSafety!())', interaction)
        self.assertIn('ReadHover(memory, reader, out WowBGuid final)', interaction)
        self.assertIn('GatherNodeScanner.TryCapture(5.0', interaction)
        self.assertIn('if (ignoreMonsters && !IgnoreSafety()) return;', runner)

    def test_movement_restricted_and_bounded(self):
        self.assertIn('_movementEnabled = GatherSettings.UseMovementSkill;', runner)
        self.assertIn('_movementSlot = GatherSettings.MovementSkillSlot;', runner)
        for spell in ('2645', '783', '5118'):
            self.assertIn('action.Id == ' + spell, policy)
        self.assertIn('action.Raw == (ulong)action.Id', policy)
        self.assertIn('action.KeyVk == ClickAimCombat.KeyForSlot(slot)', policy)
        self.assertIn('!reader.IsExperimental70862', runner)
        self.assertIn('if (!_movementEnabled || _movementAttempted', runner)
        self.assertIn('_movementAttempted = true; // do not blindly toggle', runner)
        self.assertIn('if (clicked && extraSafety == null && _movementEnabled) _movementAttempted = false;', runner)
        self.assertIn('TryMovementSkill(hwnd, mover, node.Distance)', runner)
        self.assertIn('TryMovementSkill(hwnd, mover, ClickAimMath.Len', runner)
        self.assertIn('MovementPlayerSafe(memory, reader, hwnd, out now, requireMana: false)', runner)
        self.assertIn('WowBGrindFactions.Experimental70707BarSafe(reader)', runner)
        self.assertIn('now.Guid != before.Guid', runner)
        self.assertIn('StopIfDeadOrGhost()', runner)
        self.assertIn('ClickAimAbort.Wait(250)', runner)

if __name__ == '__main__':
    unittest.main()

"""Source and UI checks only; does not run the Windows recorder."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBCombatResearch.cs').read_text()
UI = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text()
XAML = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml').read_text()
FIXTURE = (ROOT / 'Tools/WowBReaderTests/CombatResearchTests.cs').read_text()

class PlayerOnlyRecorder(unittest.TestCase):
    def test_opt_in_checkbox(self):
        ns = {'p': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}
        name = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
        box = next(c for c in ET.fromstring(XAML).findall('.//p:CheckBox', ns) if c.get(name) == 'chkPlayerOnlyResearch')
        self.assertEqual(box.get('IsChecked'), 'False')
        self.assertIn('bool playerOnly = false', SRC)
        for item in ('chkPlayerOnlyResearch.IsChecked == true', 'playerOnly: playerOnly',
                     'chkPlayerOnlyResearch.IsEnabled = false', 'chkPlayerOnlyResearch.IsEnabled = true'):
            self.assertIn(item, UI)

    def test_only_target_stability_changes(self):
        same = SRC[SRC.index('private static bool SameSelection'):SRC.index('private static bool SameBar')]
        self.assertIn('TryReadTraceLocalGuid(out WowBGuid local)', same)
        self.assertIn('!playerOnly && (!reader.TryReadTraceTargetGuid', same)
        stable = SRC[SRC.index('private static bool Stable('):SRC.index('// One bounded read')]
        for item in ('ObjectManager.Wow != memory', '!Stopped', 'TraceIdentity70707(memory, reader, player, true)',
                     'SameBar(reader, bar)', 'SameSelection(reader, player.Guid, selected, out reason, playerOnly)'):
            self.assertIn(item, stable)
        self.assertIn('if (!playerOnly && !snapshot.TargetGuid.IsEmpty', SRC)
        self.assertIn('Player-only mode: target selection may change; no target memory sampled.', SRC)
        self.assertIn('player-only selection change', FIXTURE)
        self.assertIn('player-only identity change', FIXTURE)
        self.assertIn('target change discards current sample', FIXTURE)

if __name__ == '__main__': unittest.main()

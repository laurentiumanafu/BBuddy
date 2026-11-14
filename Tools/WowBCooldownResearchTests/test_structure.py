"""Source checks only; these do not run C# or establish a live layout."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
R = Path(__file__).resolve().parents[2]
class Structure(unittest.TestCase):
 def test_opt_in_and_gates(self):
  xml = ET.parse(R/'UI/ClickAimCombatSettingsWindow.xaml')
  n = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
  checkbox = next(e for e in xml.iter() if e.get(n) == 'chkCooldownResearch')
  self.assertEqual(checkbox.get('IsChecked'), 'False')
  ui = (R/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text()
  self.assertIn('includeCooldownCandidate: includeCooldownCandidate', ui)
  rec = (R/'Styx/Logic/Pathing/ClickAim/WowBCombatResearch.cs').read_text()
  for s in ['bool includeCooldownCandidate = false', '!reader.IsExperimental70938', 'WowBCooldownResearch.Capture(memory, cancel)', 'StableWithBar(memory, reader, player, target']:
   self.assertIn(s, rec)
 def test_candidate_safety(self):
  text=(R/'Styx/Logic/Pathing/ClickAim/WowBCooldownResearch.cs').read_text()
  for s in ['MaxNodes = 64', 'BudgetMs = 100', 'IsCommittedReadableRange64', 'visited.Add(node)', 'cancel.IsCancellationRequested', 'first != second', 'unknown', '(node & 1) == 0']:
   self.assertIn(s,text)
  for s in ['WriteBytes', 'Lua', 'SendKey']:
   self.assertNotIn(s,text)
if __name__ == '__main__': unittest.main()

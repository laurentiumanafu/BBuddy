from pathlib import Path
import unittest
r=Path(__file__).resolve().parents[2]
x=(r/'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
c=(r/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
p=(r/'Styx/Logic/Pathing/ClickAim/WowBMailGridCalibration.cs').read_text()
g=(r/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
class GridWiring(unittest.TestCase):
 def test_research_only_calibration_ui(self):
  for s in ('btnMailGridStart_Click','btnMailGridRecord_Click','btnMailGridSave_Click','txtMailGridColumns','txtMailGridBackpack','txtMailGridPreview'): self.assertIn(s,x)
  for s in ('TryReadMailCandidateBags','MailGridObservationsMatch','ClickAimInput.GameForeground','ClickAimInput.TryCursorFraction','Task.Delay(3000)','MailGridContextCurrent','TryBuild','WowBMailGridCalibration.Save'):
   self.assertIn(s,c)
  self.assertNotIn('WowBMailGridCalibration',g)
  self.assertIn('ResearchOnly { get; set; } = true',p)
  self.assertNotIn('ClickAimInput.',p)
 def test_backpack_and_unknown_are_explicit(self):
  for s in ('backpack.first','backpack.last','last - first == count - 1','bag.Unknown','Unknown = true','!used.Add(n)','bags.Count != 5'):
   self.assertTrue(s in c or s in p,s)
if __name__=='__main__': unittest.main()

from pathlib import Path
import unittest
r=Path(__file__).resolve().parents[2]
ui=(r/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
xaml=(r/'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
lookup=(r/'Styx/Logic/Pathing/ClickAim/WowBMailGridLookup.cs').read_text()
class AttachmentTestWiring(unittest.TestCase):
 def test_manual_stopped_one_click(self):
  self.assertIn('btnMailAttachTest_Click',xaml)
  c=ui[ui.index('private static bool CaptureMailAttachment'):ui.index('private sealed class MailGridPointRow')]
  for s in ('ClickAimUi.Running','ClickAimUi.Paused','MessageBoxButton.YesNo','ClickAimAbort.Generation',
            'cancel.IsCancellationRequested','Task.Delay(3000, cancel.Token)','TryReadMailCandidateBags',
            'TryLoad','TryMatch','WowBMailGridLookup.Same','MailHoverSameSession','DiagnosticRightClick',
            'last.Item == selected.Item','attachment not verified; no mail sent'):
   self.assertIn(s,c)
  self.assertEqual(c.count('DiagnosticRightClick('),1)
  self.assertNotIn('DiagnosticLeftClick(',c)
  self.assertNotIn('DiagnosticRecipientChar(',c)
 def test_grid_and_backpack_guards(self):
  for s in ('g.Player != player','g.Width != width','g.Height != height','g.Bags.Count != 5',
            'saved.Unknown','actual.Identity.ToString()','saved.Count != actual.Slots.Length',
            'WowBMailGridCalibration.TryBuild','saved.Candidate > 3','bag.Slots[i].IsEmpty'):
   self.assertIn(s,lookup)
  for name in ('GrindRunner.cs','GatherRunner.cs'):
   c=(r/'Styx/Logic/Pathing/ClickAim'/name).read_text(encoding='utf-8-sig')
   self.assertNotIn('WowBMailGridLookup',c)
if __name__=='__main__': unittest.main()

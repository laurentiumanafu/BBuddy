from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]
p = R/'Styx/Logic/Pathing/ClickAim'
r = (p/'WowBExperimentalMail.cs').read_text()
s = (p/'ClickAimSettings.cs').read_text()
g = (p/'GrindRunner.cs').read_text()
u = (R/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text()
class Flow(unittest.TestCase):
 def test_settings_default_off(self):
  self.assertIn('ExperimentalAutoMail = false;',s)
  self.assertIn('"ExperimentalAutoMail"',s)
  self.assertIn('chkExperimentalAutoMail.IsChecked == true',u)
 def test_expected_batch_and_uncertain_send(self):
  for v in ('WowBMailGridLookup.TryPreview', 'unknownPrimary', 'expected.Count > 12',
            'WowBMailCandidateTable.Matches(raw, expected)', 'WowBMailGridLookup.Same(bags, beforeSend)',
            'Wait(1000, safe)', 'DiagnosticRecipientChar', 'no retry', 'backpack contents remain unverified'):
   self.assertIn(v,r)
  self.assertEqual(r.count('Left(hwnd, ui.SendX, ui.SendY'),1)
  self.assertIn('if (!progressed)',r)
 def test_position_and_stop(self):
  for v in ('ClickAimSettings.ExperimentalAutoMail','controls.Player == player.ToString()',
            '_vendorMailboxPending = mailbox','WowBMailSafetyHold.IsHeld()',
            'WowBExperimentalMail.Run', 'experiment interrupted or uncertain; no automatic resend'):
   self.assertIn(v,g)
  self.assertIn('ClickAimInput.GameForeground(hwnd)',r)
if __name__ == '__main__': unittest.main()

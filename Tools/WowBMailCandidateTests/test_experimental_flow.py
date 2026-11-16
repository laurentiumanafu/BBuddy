"""Static source checks; not a live game test."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]
p = R/'Styx/Logic/Pathing/ClickAim'
r = (p/'WowBExperimentalMail.cs').read_text()
s = (p/'ClickAimSettings.cs').read_text()
g = (p/'GrindRunner.cs').read_text()
u = (R/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
obs = (p/'WowBMailSendObservation.cs').read_text()
class Flow(unittest.TestCase):
 def test_settings_default_off(self):
  self.assertIn('ExperimentalAutoMail = false;',s)
  self.assertIn('"ExperimentalAutoMail"',s)
  self.assertIn('chkExperimentalAutoMail.IsChecked == true',u)
 def test_one_send_and_observation(self):
  for v in ('WowBMailGridLookup.TryPreview', 'unknownPrimary', 'expected.Count >= 12',
            'WowBMailGridLookup.Same(bags, beforeSend)', 'Wait(1000, safe)',
            'DiagnosticRecipientChar', 'No retry this trip', 'backpack contents, recipient and delivery remain unverified'):
   self.assertIn(v,r)
  self.assertEqual(r.count('Left(hwnd, ui.SendX, ui.SendY'),1)
  for v in ('expected.Count > 12', 'bag.Candidate < 4', '!seenItems.Add(item)',
            '!seenItems.Contains(item)', 'return -1'):
   self.assertIn(v,obs)
 def test_position_and_stop(self):
  for v in ('ClickAimSettings.ExperimentalAutoMail','controls.Player == player.ToString()',
            '_vendorMailboxPending = mailbox','WowBExperimentalMail.Run',
            'experiment interrupted or uncertain; no automatic resend'):
   self.assertIn(v,g)
  self.assertNotIn('WowBMailSafetyHold',g)
  self.assertIn('ClickAimInput.GameForeground(hwnd)',r)
if __name__ == '__main__': unittest.main()

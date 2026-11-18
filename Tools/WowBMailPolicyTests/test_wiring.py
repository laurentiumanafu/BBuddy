from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[2]
p=(R/'Styx/Logic/Pathing/ClickAim/WowBMailBatchPolicy.cs').read_text()
s=(R/'Styx/Logic/Pathing/ClickAim/ClickAimSettings.cs').read_text(encoding='utf-8-sig')
x=(R/'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
c=(R/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
g=(R/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
class MailPolicy(unittest.TestCase):
 def test_recipient_persistence_does_not_enable_mail(self):
  for part in ('MailRecipient = ""', 'ValidMailRecipient', '"MailRecipient"', '"MailRecipient = "'): self.assertIn(part,s)
  for part in ('txtMailRecipient', 'chkExperimentalAutoMail', 'server delivery cannot be independently verified'): self.assertIn(part,x)
  for part in ('txtMailRecipient.Text = ClickAimSettings.MailRecipient', 'ClickAimSettings.MailRecipient = mailRecipient', 'Stop the bot before changing the mail recipient'): self.assertIn(part,c)
  self.assertNotIn('WowBMailBatchPolicy',g)
 def test_skip_recipient_typing_option(self):
  for part in ('MailSkipRecipient', '"MailSkipRecipient"', '"MailSkipRecipient = "'): self.assertIn(part,s)
  self.assertIn('chkMailSkipRecipient',x)
  for part in ('chkMailSkipRecipient.IsChecked = ClickAimSettings.MailSkipRecipient',
               'ClickAimSettings.MailSkipRecipient = mailSkipRecipient',
               'Stop the bot before changing the mail recipient typing option'): self.assertIn(part,c)
  m=(R/'Styx/Logic/Pathing/ClickAim/WowBExperimentalMail.cs').read_text()
  for part in ('bool skipRecipient', 'if (!skipRecipient && !WowBMailRecipientText.ValidForTest(recipient))',
               'recipient typing skipped for batch', 'ClickAimSettings.MailSkipRecipient'): self.assertIn(part, m + g)
 def test_policy_limits_and_unknowns(self):
  for part in ('BatchLimit = 12', 'FailureLimit = 3', 'slot.Candidate >= 0 && slot.Candidate <= 4', 'slot.Candidate < 0', '!complete', '!attachmentsClear', 'SendEvidence.Accepted', 'SendEvidence.Rejected', 'Decision.Hold'):
   self.assertIn(part,p)
if __name__=='__main__': unittest.main()

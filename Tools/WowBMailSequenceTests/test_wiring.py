from pathlib import Path
import unittest
r=Path(__file__).resolve().parents[2]
p=(r/'Styx/Logic/Pathing/ClickAim/WowBMailSequence.cs').read_text()
u=(r/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
x=(r/'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
l=(r/'Styx/Logic/Pathing/ClickAim/WowBMailGridLookup.cs').read_text()
class SequenceWiring(unittest.TestCase):
 def test_policy_is_pure_and_requires_acknowledgments(self):
  for s in ('now - _tabPostedAt < 1000','WowBMailBatchPolicy.Plan(complete','Selection.Unknown',
            'attachments.Count != _attached + 1','attachments[i] != _batch[i].Item','Current = Stage.AwaitSend',
            'WowBMailBatchPolicy.AfterSend','Decision.Retry','Decision.ReturnToProfile','Current = Stage.Hold'):
   self.assertIn(s,p)
  for forbidden in ('ClickAimInput.', 'PostMessage', 'Thread.Sleep', 'Lua.'):
   self.assertNotIn(forbidden,p)
  for name in ('GrindRunner.cs','GatherRunner.cs'):
   self.assertNotIn('WowBMailSequence',(r/'Styx/Logic/Pathing/ClickAim'/name).read_text(encoding='utf-8-sig'))
 def test_preview_is_stopped_read_only_partial(self):
  self.assertIn('btnMailBatchPreview_Click',x)
  c=u[u.index('private async void btnMailBatchPreview_Click'):u.index('private sealed class MailGridPointRow')]
  for s in ('ClickAimUi.Running','ClickAimUi.Paused','TryReadMailCandidateBags','WowBMailGridLookup.Same',
            'TryLoad','TryPreview','Candidate 4 is Unknown, not absent','Backpack contents and mailbox state are not read'):
   self.assertIn(s,c)
  for s in ('DiagnosticRightClick','DiagnosticLeftClick','DiagnosticRecipientChar','ClickAimInput.Click'):
   self.assertNotIn(s,c)
  for s in ('candidate <= 3','WowBMailBatchPolicy.BatchLimit','!seen.Add(item)','unknownPrimary','candidate4Unknown'):
   self.assertIn(s,l)
if __name__=='__main__': unittest.main()

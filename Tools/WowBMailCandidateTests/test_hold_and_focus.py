"""Static checks only; C# fixture and Windows/game interaction are separate."""
from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]
p=root/'Styx/Logic/Pathing/ClickAim'
m=(p/'WowBExperimentalMail.cs').read_text()
h=(p/'WowBMailSafetyHold.cs').read_text()
i=(p/'ClickAimInput.cs').read_text()
g=(p/'GrindRunner.cs').read_text()
u=(root/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
class Wiring(unittest.TestCase):
 def test_durable_hold_before_text_and_send(self):
  self.assertIn('FileMode.CreateNew',h)
  self.assertIn('f.Flush(true)',h)
  self.assertIn('catch { return true; }',h)
  self.assertLess(m.index('WowBMailSafetyHold.TryBegin'),m.index('Left(hwnd, ui.TabX'))
  self.assertLess(m.index('WowBMailSafetyHold.TryBegin'),m.index('DiagnosticRecipientChar'))
  self.assertLess(m.index('if (!progressed)'),m.index('WowBMailSafetyHold.TryClear'))
  self.assertIn('if (!safe()) { Logging.Write("[Mail] safety changed after observed progress; inspection hold retained.',m)
  self.assertIn('recipient text may remain',m.lower())
  self.assertIn('WowBMailSafetyHold.IsHeld()',g)
  self.assertIn('ClickAimUi.Running || ClickAimUi.Paused',u[u.index('private void btnClearMailHold_Click'):u.index('private async void btnMailAttachmentResearch_Click')])
 def test_hold_clears_only_after_confirmed_return(self):
  ret=g[g.index('private static bool WalkBackToProfile('):g.index('private static bool WalkToRecordedMailbox(')]
  clear=ret.index('WowBMailSafetyHold.TryClear')
  for guard in ('if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return false;',
                'if (RouteRunner.LastRunInterrupted || !Safe())',
                'if (!arrived || !reader.TryRead70888RecoveryPlayer',
                'ClickAimMath.Len(finalPlayer.Location.X - hotspot.X',
                'Math.Abs(finalPlayer.Location.Z - hotspot.Z) > 5.0 || !Safe()',
                'ClickAimSettings.ExperimentalAutoMail',
                '!_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && WowBMailSafetyHold.IsHeld()'):
   self.assertIn(guard,ret[:clear])
  self.assertLess(clear,ret.index('_vendorReturnHotspot = null'))
  self.assertIn('prior composition or send is uncertain; skipping mailbox this trip',g)
  self.assertIn('leftover recipient or attachments remain unverified',ret)
  self.assertIn('if (!progressed)',m)
  self.assertIn('no retry on this trip',m)
 def test_focus_modes_target_game_and_stay_fixed(self):
  for v in ('ClickAimInput.GameForeground(hwnd)', 'GatherBackgroundFocus.State.OtherApplication',
            'GatherBackgroundFocus.TryClientOrigin', 'bool background = false, focusSelected = false',
            'BackgroundMailRecipientChar', 'DiagnosticRecipientChar', 'background, safe'):
   self.assertIn(v,m)
  section=i[i.index('internal static bool BackgroundMailRecipientChar'):i.index('// One dialog click only.')]
  self.assertIn('PostMessageInternal(hwnd, WM_CHAR',section)
  self.assertNotIn('SendInput',section)
  self.assertIn('confirm()',section)
 def test_recipient_reposted_for_every_new_batch(self):
  loop=m[m.index('while (batches < 64 && safe())'):]
  self.assertLess(loop.index('foreach (char c in recipient)'),loop.index('Left(hwnd, ui.SendX'))
  self.assertLess(loop.index('Left(hwnd, ui.SendX'),loop.index('batches++;'))
  self.assertEqual(loop.count('Left(hwnd, ui.SendX'),1)
  tab = loop[loop.index('if (batches == 0)'):loop.index('int generation = ClickAimAbort.Generation')]
  self.assertEqual(tab.count('Left(hwnd, ui.TabX'),1)
  self.assertIn('else',tab)
  self.assertIn('posting recipient directly for next batch without clicking the tab',tab)
  send = loop[loop.index('Left(hwnd, ui.SendX'):loop.index('batches++;')]
  self.assertLess(send.index('if (!Wait(1000, safe))'),send.index('bool progressed = false'))
  self.assertLess(send.index('if (!progressed)'),send.index('WowBMailSafetyHold.TryClear'))
  self.assertIn('next nonempty batch will post the recipient again',loop)
 def test_mail_failure_returns_without_replaying_send(self):
  route=g[g.index('private static bool WalkToRecordedMailbox('):g.index('private static bool WaitVendorSafe(')]
  self.assertIn('return ReturnWithoutMail("experiment interrupted or uncertain; no automatic resend")',route)
  self.assertIn('return WalkBackToProfile(mover)',route)
  self.assertIn('if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return false;',route)
  self.assertIn('_vendorMailboxPending = null',route)
if __name__=='__main__': unittest.main()

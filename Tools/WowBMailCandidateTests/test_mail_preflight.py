"""Static regression checks; not a Windows or live-game test."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim'
m = (R / 'WowBExperimentalMail.cs').read_text()
g = (R / 'GrindRunner.cs').read_text()

class MailPreflight(unittest.TestCase):
 def test_failures_are_logged_before_any_input(self):
  run = m[m.index('internal static bool Run('):]
  for reason in ('controls invalid or for another character', 'recipient missing or invalid',
                 'game client size unavailable', 'differs from controls', 'preflight withheld:',
                 'no mailbox, tab, recipient, attachment or send input sent'):
   self.assertIn(reason,run)
  self.assertLess(run.index('preflight withheld:'),run.index('WowBMailboxHoverSearch.Find'))
  self.assertLess(run.index('WowBMailboxHoverSearch.Find'),run.index('ConfirmedRightClick(hwnd, mailboxX'))
  self.assertLess(run.index('ConfirmedRightClick(hwnd, mailboxX'),run.index('Left(hwnd, ui.TabX'))
 def test_grace_only_for_focus_or_button_before_first_action(self):
  run=m[m.index('internal static bool Run('):]
  self.assertIn('issue == "a physical mouse button is held"',run)
  self.assertIn('GatherBackgroundFocus.State.OtherApplication',run)
  self.assertIn('ClickAimInput.GameForeground(hwnd)',run)
  self.assertIn('GatherBackgroundFocus.TryClientOrigin',run)
  self.assertIn('unchecked(Environment.TickCount - start) < 3000',run)
  self.assertLess(run.index('unchecked(Environment.TickCount - start) < 3000'),run.index('WowBMailboxHoverSearch.Find'))
  self.assertEqual(run.count('Left(hwnd, ui.SendX'),1)
  self.assertIn('send outcome uncertain; no retry',run)
 def test_route_diagnostics_preserve_guards(self):
  route=g[g.index('private static bool WalkToRecordedMailbox('):g.index('private static bool WaitVendorSafe(')]
  for v in ('AutoVendorIdentityMatches', 'ExperimentalGrindActive', 'Experimental70707BarSafe',
            'TryReadGatherCombat', 'TryRead70888RecoveryPlayer', 'TryReadGhostFlagCandidate',
            'ClickAimInput.MouseButtonsUp()', 'InputSafetyFailure',
            'experiment interrupted or uncertain; no automatic resend', 'return WalkBackToProfile(mover)'):
   self.assertIn(v,route)
 def test_post_interaction_milestones_are_explicit(self):
  for v in ('mailbox interaction posted', 'active bag observations unavailable',
            'saved bag grid missing or incompatible', 'attachment candidate table unreadable',
            'attempting recorded Send Mail tab', 'Send Mail tab click messages posted',
            'field focus are not verified'):
   self.assertIn(v,m)

if __name__ == '__main__': unittest.main()

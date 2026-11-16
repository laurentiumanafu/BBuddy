"""Static checks only; not a Windows or live-game test."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
p = root / 'Styx/Logic/Pathing/ClickAim'
m = (p / 'WowBExperimentalMail.cs').read_text()
i = (p / 'ClickAimInput.cs').read_text()
g = (p / 'GrindRunner.cs').read_text()
x = (root / 'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')

class Wiring(unittest.TestCase):
 def test_no_hold_or_hover_gate(self):
  self.assertNotIn('WowBMailSafetyHold', m + g)
  self.assertNotIn('WowBMailboxHoverSearch', m)
  self.assertNotIn('TryReadMouseoverGuidCandidate', m)
  self.assertNotIn('Clear mail safety hold', x)
  self.assertIn('no hover identity check', m)
 def test_one_send_and_requested_sequence(self):
  start = m.index('ConfirmedRightClick(hwnd, ui.MailboxX')
  tab = m.index('Left(hwnd, ui.TabX', start)
  wait = m.index('Wait(1000, safe)', tab)
  recipient = m.index('foreach (char c in recipient)', wait)
  item = m.index('ConfirmedRightClick(hwnd, x, y', recipient)
  send = m.index('Left(hwnd, ui.SendX', item)
  self.assertLess(start, tab); self.assertLess(tab, wait); self.assertLess(wait, recipient)
  self.assertLess(recipient, item); self.assertLess(item, send)
  self.assertEqual(m.count('Left(hwnd, ui.SendX'), 1)
  self.assertEqual(m.count('Left(hwnd, ui.TabX'), 1)
  self.assertIn('expected.Count >= 12', m)
  self.assertIn('Wait(1000, safe)', m[send:])
  self.assertIn('no clicked item candidates disappeared', m[send:])
  self.assertIn('outcome unknown and treated as a failed trip', m[send:])
  self.assertIn('WowBMailSendObservation.CountMissing', m[send:])
  self.assertNotIn('WowBMailCandidateReader.TryRead', m)
 def test_focus_modes_and_safety(self):
  for v in ('ClickAimInput.GameForeground(hwnd)', 'GatherBackgroundFocus.State.OtherApplication',
            'GatherBackgroundFocus.TryClientOrigin', 'BackgroundMailRecipientChar',
            'DiagnosticRecipientChar', 'background, safe'):
   self.assertIn(v, m)
  section = i[i.index('internal static bool BackgroundMailRecipientChar'):i.index('// One dialog click only.')]
  self.assertIn('PostMessageInternal(hwnd, WM_CHAR', section)
  self.assertNotIn('SendInput', section)
  route = g[g.index('private static bool WalkToRecordedMailbox('):g.index('private static bool WaitVendorSafe(')]
  for v in ('AutoVendorIdentityMatches', 'TryReadGatherCombat', 'TryReadGhostFlagCandidate',
            'ClickAimInput.MouseButtonsUp()', '_vendorMailboxPending = null', 'return WalkBackToProfile(mover)'):
   self.assertIn(v, route)

if __name__ == '__main__': unittest.main()

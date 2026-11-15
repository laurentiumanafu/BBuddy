from pathlib import Path
import unittest
s = (Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
class MailboxWiring(unittest.TestCase):
 def test_after_replay_and_no_resell(self):
  trip=s[s.index('private static bool VendorTrip('):s.index('private static bool WalkToRecordedMailbox(')]
  self.assertLess(trip.index('if (_vendorMailboxPending != null) return WalkToRecordedMailbox'), trip.index('RunSellPlan(hwnd'))
  self.assertLess(trip.index('RunSellPlan(hwnd'), trip.index('_vendorMailboxPending = point.Mailbox'))
  due=s[s.index('private static bool VendorDue('):s.index('private static bool VendorTrip(')]
  self.assertIn('if (UnderAttack()) return false;',due)
  self.assertIn('_vendorMailboxPending != null',due)
 def test_safe_mesh_and_manual_handoff(self):
  route=s[s.index('private static bool WalkToRecordedMailbox('):s.index('private static bool WaitVendorSafe(')]
  for part in ('RequireMeshDuringSession = true', 'PollInterruptDuringWalk = true', 'TryReadGatherCombat', 'TryRead70888RecoveryPlayer', 'AutoVendorIdentityMatches', 'RouteRunner.LastRunInterrupted', 'ClickAimMath.Len(finalPlayer.Location.X', 'No mail was sent.', 'Stop();'):
   self.assertIn(part,route)
  self.assertNotIn('ClickAimInput.',route)
if __name__ == '__main__': unittest.main()

from pathlib import Path
import unittest
s = (Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
class MailboxWiring(unittest.TestCase):
 def test_mailbox_handoff_is_opt_in_after_sale(self):
  trip=s[s.index('private static bool VendorTrip('):s.index('private static ClickAimAction? TownReturnAction(')]
  self.assertIn('ClickAimSettings.ExperimentalAutoMail',trip)
  self.assertLess(trip.index('RunSellPlan(hwnd'),trip.index('_vendorReturnHotspot = new RoutePoint'))
  self.assertLess(trip.index('RunSellPlan(hwnd'),trip.index('_vendorMailboxPending = mailbox'))
  self.assertLess(trip.index('if (_vendorMailboxPending != null) return WalkToRecordedMailbox'),trip.index('RunSellPlan(hwnd'))
  due=s[s.index('private static bool VendorDue('):s.index('private static bool VendorTrip(')]
  self.assertIn('if (UnderAttack()) return false;',due)
  self.assertIn('_vendorReturnHotspot != null',due)
 def test_safe_mesh_and_uncertain_input_stop(self):
  route=s[s.index('private static bool WalkToRecordedMailbox('):s.index('private static bool WaitVendorSafe(')]
  for part in ('RequireMeshDuringSession = true', 'PollInterruptDuringWalk = true', 'TryReadGatherCombat', 'TryRead70888RecoveryPlayer', 'AutoVendorIdentityMatches', 'RouteRunner.LastRunInterrupted', 'ClickAimMath.Len(finalPlayer.Location.X', 'WowBExperimentalMail.Run', 'GameForeground', 'Stop();'):
   self.assertIn(part,route)
  self.assertIn('return WalkBackToProfile(mover)',route)
  self.assertIn('no automatic', (Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/WowBExperimentalMail.cs').read_text())
if __name__ == '__main__': unittest.main()

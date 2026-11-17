"""Static regression checks for profile-free post-sell mail routing.
These are source checks, not an execution of the Windows bot or game UI.
"""
from pathlib import Path
import unittest

p = Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs'
s = p.read_text(encoding='utf-8-sig')

class ProfileFreeMail(unittest.TestCase):
 def test_roam_session_records_same_map(self):
  run = s[s.index('private static void Run(List<ProfileGrindArea>'):s.index('private static bool VendorDue(')]
  self.assertIn('_roamSession = roam != null; ClearRoamMailReturn();',run)
  self.assertIn('roamMapAgain != _profileSessionMap || !roam.MapCurrent()',run)
  self.assertIn('else if (trialReader?.Uses70938Features == true)',run)
  self.assertIn('profile-free mesh roaming',run)
 def test_return_capture_is_before_cast_and_vendor_path(self):
  capture = s[s.index('private static bool CaptureRoamMailReturn()'):s.index('private static bool VendorTrip(')]
  for guard in ('_roamMailReturn != null','AutoVendorIdentityMatches', 'map != _profileSessionMap',
                'TryReadGatherCombat', 'TryReadGhostFlagCandidate', 'second.Guid != first.Guid',
                'double.IsFinite(second.Location.Z)', 'pre-vendor roaming position'):
   self.assertIn(guard,capture)
  self.assertNotIn('ClickAimInput.',capture)
  trip = s[s.index('private static bool VendorTrip('):s.index('private static ClickAimAction? TownReturnAction(')]
  self.assertLess(trip.index('CaptureRoamMailReturn()'),trip.index('TownReturn(hwnd, points)'))
  self.assertLess(trip.index('CaptureRoamMailReturn()'),trip.index('RouteRunner.RunWithinSession'))
  self.assertLess(trip.index('RunSellPlan(hwnd'),trip.index('_vendorMailboxPending = mailbox'))
  self.assertIn('(_activeProfileArea != null || (_roamSession && _roamMailReturn != null))',trip)
  for guard in ('_roamMailReader == vendorReader','_roamMailMemory == vendorMemory',
                '_roamMailPlayer == player','returnMap != _profileSessionMap','ClickAimSettings.ExperimentalAutoMail'):
   self.assertIn(guard,trip)
 def test_latched_return_still_guarded(self):
  route = s[s.index('private static bool WalkBackToProfile('):s.index('private static bool WalkToRecordedMailbox(')]
  for guard in ('(_activeProfileArea == null && !_roamSession)', 'AutoVendorIdentityMatches',
                'map != _profileSessionMap', 'RequireMeshDuringSession = true',
                'RouteRunner.LastRunInterrupted', 'ClearRoamMailReturn();'):
   self.assertIn(guard,route)
  mail = s[s.index('private static bool WalkToRecordedMailbox('):s.index('private static bool WaitVendorSafe(')]
  self.assertIn('experiment interrupted or uncertain; no automatic resend',mail)
  self.assertIn('ReturnWithoutMail',mail)
  self.assertIn('return WalkBackToProfile(mover)',mail)
 def test_explicit_diagnostic_reports_no_recipient(self):
  self.assertIn('[Mail] experimental post-sell option ',s)
  self.assertIn('selected sell plan ',s)
  self.assertIn('not recorded for this plan',s)

if __name__ == '__main__': unittest.main()

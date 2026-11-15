from pathlib import Path
import unittest
s=(Path(__file__).resolve().parents[2]/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
class ReturnTests(unittest.TestCase):
 def test_sale_then_return_not_mail(self):
  t=s[s.index('private static bool VendorTrip('):s.index('private static ClickAimAction? TownReturnAction(')]
  self.assertLess(t.index('if (_vendorReturnHotspot != null) return WalkBackToProfile'),t.index('RunSellPlan(hwnd'))
  self.assertLess(t.index('RunSellPlan(hwnd'),t.index('_vendorReturnHotspot = new RoutePoint'))
  for part in ('_activeProfileArea.Hotspots','returnMap != _profileSessionMap','ClickAimSettings.ExperimentalAutoMail','_vendorMailboxPending = mailbox','WalkBackToProfile(mover)'): self.assertIn(part,t)
  self.assertLess(t.index('RunSellPlan(hwnd'),t.index('_vendorMailboxPending = mailbox'))
 def test_latched_guarded_mesh(self):
  f=s[s.index('private static bool WalkBackToProfile('):s.index('private static bool WalkToRecordedMailbox(')]
  for part in ('CheckDeathCheckpoint(trackerOnly: true)','AutoVendorIdentityMatches','Experimental70707BarSafe','TryReadGatherCombat','RequireMeshDuringSession = true','RouteRunner.LastRunInterrupted','_vendorReturnHotspot = null','_townTransit = false','no cross-map transport'):
   self.assertIn(part,f)
  self.assertNotIn('RunSellPlan(',f)
if __name__=='__main__': unittest.main()

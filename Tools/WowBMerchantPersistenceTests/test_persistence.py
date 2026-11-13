"""Source wiring checks for the experimental merchant respawn candidate."""
from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]
g=(root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
k=(root/'Styx/Logic/Pathing/ClickAim/WowBKnownMerchants.cs').read_text(encoding='utf-8-sig')
v=(root/'Styx/Logic/Pathing/ClickAim/VendorFile.cs').read_text(encoding='utf-8-sig')
class MerchantPersistence(unittest.TestCase):
 def test_saved_confirmations_are_loaded_and_used(self):
  self.assertIn('point.MerchantGuids.Add(guid)',v)
  self.assertIn('Save(doc, file); return true;',v[v.index('public static bool AddMerchant'):v.index('public static bool AddStep')])
  self.assertIn('points[i].MerchantGuids.Count == 0',g)
 def test_candidate_is_build_and_town_scoped_ambiguous_fail_closed(self):
  s=g[g.index('private static bool TryFindVendorNpc('):g.index('private static bool VendorCandidateStillValid(')]
  for x in ('point.TownMap != map', 'reader.IsExperimental70938', 'Same70938HighCandidate', 'fallbackCount++', 'exactObserved || fallbackCount != 1', 'townDistance > ClickAimSettings.VendorSearchYd', 'fallbackYd > ClickAimSettings.VendorSearchYd', 'Math.Abs(candidate.Location.Z - point.Z) > 5.0', 'IsRecordedLive(point, fallback, memory, reader, true)'):
   self.assertIn(x,s)
  self.assertIn('high == guid.High',k)
 def test_identity_rechecked_through_replay(self):
  s=g[g.index('private static bool VendorCandidateStillValid('):g.index('// Re-read the exact engaged body')]
  for x in ('TryReadGatherCombat', 'point.TownMap != map','npc.RecordAddress != original.RecordAddress', 'npc.UnitAddress != original.UnitAddress', 'TryFindVendorNpc(point, out WowBObservedUnit unique', 'unique.Guid != original.Guid', 'reader.ReadTargetGuid() != original.Guid'):
   self.assertIn(x,s)
  self.assertIn('VendorCandidateStillValid(point, merchant',g[g.index('private static int RunSellPlan'):g.index('private static void ReportVendorIdentityCandidates')])
  self.assertIn('ReportVendorIdentityCandidates(point)',g)
if __name__=='__main__': unittest.main()

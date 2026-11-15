from pathlib import Path
import unittest
r=Path(__file__).resolve().parents[2]
c=(r/'Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs').read_text()
i=(r/'Styx/Logic/Pathing/ClickAim/WowBInventoryProbe.cs').read_text()
g=(r/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()
class Observation(unittest.TestCase):
 def test_stopped_only_guarded_research(self):
  f=c[c.index('private static bool TryBuildMailObservation('):]
  for value in ('i = 0; i < CandidateIdentities', 'Unknown = true', 'bag.Guid != identity', '!seenItems.Add', '!Same(bag.Slots', '!Same(bag.Header', '!Same(bag.PointerBytes', '!reader.IsPoolRecordCurrent', '!Same(identities', 'SamePlayer', 'final.Guid != player.Guid'):
   self.assertIn(value,f)
  f=i[i.index('internal static bool TryReadMailCandidateBags('):i.index('internal static List<WowBContainerResearchProbe.BagChoice> ObservedBags')]
  for v in ('lock (Gate)', 'ClickAimUi.Running', 'reader?.IsExperimental70938', 'TemporaryCacheState(false)', 'ValidPlayer', 'TryCaptureMailBags'):
   self.assertIn(v,f)
  self.assertNotIn('TryReadMailCandidateBags',g)
if __name__=='__main__': unittest.main()

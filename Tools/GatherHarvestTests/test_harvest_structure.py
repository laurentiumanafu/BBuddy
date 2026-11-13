"""Source-level regressions only; these do not execute C# or verify live gathering."""
from pathlib import Path
import re, unittest
ROOT=Path(__file__).resolve().parents[2]
SCANNER=(ROOT/'Styx/Logic/Pathing/ClickAim/GatherNodeScanner.cs').read_text(encoding='utf-8-sig')
RUNNER=(ROOT/'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
def part(s,a,b): i=s.index(a); return s[i:s.index(b,i)]
class GatheringWiring(unittest.TestCase):
 def test_classic_thresholds_are_build_specific(self):
  table=part(SCANNER,'    internal static uint Classic70938MiningSkill(', '    // 70291 additions')
  found={int(k):int(v) for k,v in re.findall(r'(\d+)U\s*=>\s*(\d+)U',table)}
  self.assertEqual(found,{1731:1,1732:65,1733:75,1735:125,1734:155,2040:175,2047:230,165658:230,324:245,175404:275})
  self.assertIn('_ => fallback',table)
  self.assertIn('classic70938 && !isHerb ? Classic70938MiningSkill(kind.Entry, kind.RequiredSkill) : kind.RequiredSkill', SCANNER)
  self.assertIn('distance, reader.IsExperimental70938)', SCANNER)
  self.assertIn('node.RequiredSkill <= miningSkill', SCANNER)
  self.assertIn('node.RequiredSkill <= herbalismSkill', SCANNER)
 def test_blacklist_is_session_scoped_and_post_click(self):
  self.assertIn('HashSet<WowBGuid> FailedHarvestNodes',RUNNER)
  self.assertEqual(RUNNER.count('FailedHarvestNodes.Clear();'),2)
  ready=part(RUNNER,'    private static bool Ready(', '    private static bool FindNode(')
  self.assertIn('FailedHarvestNodes.Contains(node.Guid)',ready)
  gather=part(RUNNER,'    private static void GatherAtNode(', '    // Grind owns the thread')
  self.assertLess(gather.index('if (clicked && clickedNode.Guid != node.Guid)'),gather.index('FailedHarvestNodes.Add(node.Guid)'))
  self.assertLess(gather.index('if (!GatherNodeScanner.TryCapture(SearchYd'),gather.index('FailedHarvestNodes.Add(node.Guid)'))
  self.assertLess(gather.index('if (!observed)'),gather.index('FailedHarvestNodes.Add(node.Guid)'))
  self.assertIn('if (clicked && initialReader?.IsExperimental70938 == true)',gather)
  gate=gather[gather.index('if (clicked && initialReader?.IsExperimental70938 == true)'):gather.index('FailedHarvestNodes.Add(node.Guid)')]
  for x in ('ClickAimAbort.Requested','RouteRunner.StopRequested','ObjectManager.WowB != initialReader',
            'ObjectManager.Wow != initialMemory', '!MapStillCurrent()', '!extraSafety()',
            '!GrindRunner.TryReadGatherCombat(out fighting)', 'if (fighting)'):
   self.assertIn(x,gate)
  self.assertIn('a multi-hit vein may still contain ore',gather)
  self.assertIn('session-blacklisted',RUNNER)
if __name__=='__main__': unittest.main()

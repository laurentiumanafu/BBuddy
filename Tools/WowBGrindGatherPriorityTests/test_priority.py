"""Source wiring checks; the companion fixture executes the real C# decision code."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
g = (root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
r = (root/'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
class Priority(unittest.TestCase):
 def test_100_yard_start_and_separate_continuation(self):
  s = g[g.index('    private static bool GatherAreaSafe()'):g.index('    // Gather uses the Grind rotation')]
  self.assertIn('GatherAreaSafeCore(true)',s); self.assertIn('GatherAreaSafeCore(false)',s)
  self.assertIn('reader.IsExperimental70938 ? SearchYd : GatherPullPriorityYd',s)
  self.assertIn('if (!checkNearby) return',s)
  self.assertIn('pullRadius, reader.IsExperimental70938',s)
  self.assertIn('return blockAllLiving ||',g)
  for guard in ('CheckDeathCheckpoint(trackerOnly: true)', 'ExperimentalSessionPlayerCurrent', 'PlayerCombatFlagCandidate', 'RouteRunner.StopRequested', 'VendorDue()'):
   self.assertIn(guard,s)
 def test_grind_commitment_preempts_voluntary_actions_not_combat(self):
  start=g.index('if (gatherDuringGrind && GatherRunner.HasGrindCommitment)')
  self.assertLess(start,g.index('LootNearby(hwnd, 3, recentOnly: true)',start))
  self.assertLess(start,g.index('TryEngage(hwnd, out combatReport)',start))
  self.assertIn('if (VendorDue())',g[start:start+1100])
  s=r[r.index('    internal static bool GatherForGrind('):r.index('    private static void RunGuarded(')]
  for requirement in ('if (fighting) return false', 'if (!startSafe())', 'committed ? continueSafe : startSafe',
    'fresh.Guid == node.Guid', 'FailedHarvestNodes.Contains(node.Guid)', 'scanStats.HeaderRejected != 0 || scanStats.Changed != 0', '++_grindMissingScans >= 3',
    'RecordGrindApproachFailure', 'GatherAtNode(hwnd, mover, node, safe)', 'if (!continueSafe())'):
   self.assertIn(requirement,s)
  self.assertIn('candidate.Guid == node.Guid',r)
  self.assertIn('ClearGrindCommitment(); // a new life',g)
 def test_blacklisting_needs_observed_node_and_safe_state(self):
  s=r[r.index('    private static void RecordGrindApproachFailure'):r.index('    // Returns true while gathering')]
  for guard in ('!safe()', '!MapStillCurrent()', 'TryCapture(SearchYd', 'fresh.Guid == node.Guid && Ready(fresh)',
                '!stillObserved', 'ClickAimAbort.Requested', 'RouteRunner.StopRequested', '_grindApproachFailures >= 3'):
   self.assertIn(guard,s)
if __name__=='__main__': unittest.main()

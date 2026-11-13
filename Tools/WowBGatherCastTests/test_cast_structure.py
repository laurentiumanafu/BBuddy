"""Static candidate-wiring checks; not a C# or live casting test."""
from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]
c=(root/'Styx/Logic/Pathing/ClickAim/WowBGatherCastResearch.cs').read_text(encoding='utf-8-sig')
g=(root/'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
class CastDiagnostic(unittest.TestCase):
 def test_strict_identity_and_double_read(self):
  for x in ('reader.IsExperimental70938','reader.ExperimentalGrindActive','ObjectManager.Wow != memory',
            'ObjectManager.WowB != reader','TraceIdentity70707(memory, reader, player, true)',
            'first == 3564U','second == first','unchecked(end - start) == 3200U',
            'start1 != start2 || end1 != end2','Observation.Unknown','Observation.Idle','Observation.Mining'):
   self.assertIn(x,c)
  self.assertEqual(c.count('memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize)'),2)
  self.assertEqual(c.count('TraceIdentity70707(memory, reader, player, true)'),2)
 def test_alternate_angle_requires_safe_mesh_and_fresh_observation(self):
  helper=g[g.index('private static bool TryHoverAngle('):g.index('    // No movement, turns')]
  for text in ('RouteRunner.TryCombatMeshPath', 'RouteRunner.RequireMeshDuringSession = true',
               'RouteRunner.ShouldInterrupt = () => !Safe()', 'ObjectManager.Wow == memory',
               'ObjectManager.WowB == reader', 'WowBGatherCastResearch.Observation.Idle',
               'GrindRunner.TryReadGatherCombat(out bool fighting)',
               'triedBearings.Add(actualBearing)', 'fresh.Guid == node.Guid && Ready(fresh)',
               'ClickAimKeySteer.Face'):
   self.assertIn(text,helper)
  self.assertLess(helper.index('RouteRunner.TryCombatMeshPath'),helper.index('RouteRunner.RunWithinSession'))
  self.assertLess(helper.index('fresh.Guid == node.Guid'),helper.index('triedBearings.Add(actualBearing)'))
 def test_bounded_wait_and_failure_policy(self):
  start=g.index('private static void GatherAtNode(');end=g.index('    // Grind owns the thread',start);method=g[start:end]
  for text in ('WowBGatherCastResearch.TryBegin', 'WowBGatherCastResearch.Observe',
               'elapsed >= MiningStartWaitMs', 'MiningLootWaitMs', 'MiningCastLimitMs',
               'if (absentCasts >= 3)', 'if (angleAttempts >= 3)',
               'if (mining70938 && (!seen || !cleared))',
               'alternate approach unavailable or interrupted; deferring, not blacklisting'):
   self.assertIn(text,method)
  self.assertIn('cast completion and loot are unverified',method)
  self.assertNotIn('ClickAimInput',c)
  self.assertNotIn('WriteBytes',c)
if __name__=='__main__': unittest.main()

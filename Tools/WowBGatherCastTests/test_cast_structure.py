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
            'start1 == start2 && end1 == end2'):
   self.assertIn(x,c)
  self.assertEqual(c.count('memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize)'),2)
  self.assertEqual(c.count('TraceIdentity70707(memory, reader, player, true)'),2)
 def test_never_controls_gathering(self):
  start=g.index('private static void GatherAtNode(');end=g.index('    // Grind owns the thread',start);method=g[start:end]
  self.assertIn('WowBGatherCastResearch.TryBegin',method)
  self.assertIn('WowBGatherCastResearch.TryObserve',method)
  self.assertIn('does not prove the click caused it',method)
  self.assertIn('failure is not established',method)
  self.assertNotIn('miningCandidateSeen',method[method.index('if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> after)'):])
  self.assertNotIn('ClickAimInput',c)
  self.assertNotIn('WriteBytes',c)
if __name__=='__main__': unittest.main()

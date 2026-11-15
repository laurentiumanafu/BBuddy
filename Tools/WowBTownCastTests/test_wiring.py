from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]/'Styx/Logic/Pathing/ClickAim'
c=(root/'WowBTownCastCandidate.cs').read_text(); p=(root/'WowBTownCastProgress.cs').read_text()
class TownCast(unittest.TestCase):
 def test_candidate_checks(self):
  for s in ('first == 556', 'first == 8690', '10000U', 'first != second', 'TraceIdentity70707', 'ObjectManager.Wow != memory', 'State.Unknown'):
   self.assertIn(s,c)
 def test_progress_requires_cast_and_changed_location(self):
  for s in ('if (!seen)', 'idleCount >= 2', 'movedCount >= 2', 'firstIdleRemaining >= 200', 'lastRemaining >= 500', 'Result.Uncertain'):
   self.assertIn(s,p)
if __name__=='__main__': unittest.main()

"""Source integration tests; no C# execution, game input or live-client validation."""
from pathlib import Path
import unittest
r=Path(__file__).resolve().parents[2]
def read(p): return (r/p).read_text(encoding='utf-8-sig')
c=read('Styx/Logic/Pathing/ClickAim/WowBGhostWolfTravel.cs')
g=read('Styx/Logic/Pathing/ClickAim/GatherRunner.cs')
d=read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
b=read('Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs')
a=read('Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs')
class Integration(unittest.TestCase):
 def test_candidate_and_safety(self):
  self.assertIn('_ids.Contains(1238640)',a)
  self.assertEqual(c.count('WowBTotemAuraCandidate.Capture(memory, reader, player)'),2)
  for text in ('IsExperimental70938','ExperimentalGrindActive','TraceIdentity70707',
               'first == null || second == null','first.GhostWolf70938Active() != second.GhostWolf70938Active()',
               'TryReadGhostFlagCandidate','TryReadGatherCombat','fields.PlayerPowerTypeCandidate != 0',
               'WowBGatherCastResearch.Observation.Idle',
               'manaAfter < 100','!safe()','!WowBGrindFactions.Experimental70707BarSafe(reader)',
               'checkedPlayer.RecordAddress != player.RecordAddress','activeAfter','ClickAimAbort.Wait(250)',
               'RetryMs = 15000','HoldMs = 4000'):
   self.assertIn(text,c)
  self.assertLess(c.index('activeAfter || manaAfter < 100'),c.index('ClickAimInput.TapKey'))
  self.assertIn('now < _nextAttempt',c)
 def test_route_and_buff_ownership(self):
  self.assertIn('_ghostWolfTravel?.TryEnsure(hwnd, mover, TravelFormSafe)',g)
  self.assertIn('_ghostWolfTravel?.ShouldPauseTravel(',g)
  self.assertIn('new WowBGhostWolfTravel(GatherSettings.UseMovementSkill, GatherSettings.MovementSkillSlot)',d)
  self.assertIn('ghostWolfTravel.TryEnsure(hwnd, mover, TravelFormSafe)',d)
  self.assertIn('ghostWolfTravel.ShouldPauseTravel(hwnd, TravelFormSafe)',d)
  self.assertIn('if (formInterrupt)',d)
  self.assertIn('reader.IsExperimental70938 && (action.Id == 2645 || action.Id == 1238640)',b)
  self.assertIn('action.Id == 2645 || action.Id == 1238640',b)
if __name__=='__main__':unittest.main()

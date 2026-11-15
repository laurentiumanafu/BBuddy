from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]
g=(root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
s=(root/'Styx/Logic/Pathing/ClickAim/ClickAimSettings.cs').read_text(encoding='utf-8-sig')
x=(root/'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
c=(root/'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
class Flow(unittest.TestCase):
 def test_checkbox_is_persisted_and_gates_recall(self):
  for v in ('UseTownReturn = false', '"UseTownReturn"', '"UseTownReturn = "'): self.assertIn(v,s)
  self.assertIn('chkTownReturn',x); self.assertIn('ClickAimSettings.UseTownReturn = chkTownReturn.IsChecked == true',c)
  trip=g[g.index('private static bool VendorTrip('):g.index('private static ClickAimAction? TownReturnAction(')]
  self.assertIn('ClickAimSettings.UseTownReturn && !_townTransit',trip)
  self.assertIn('if (!TownReturn(hwnd, points)) return false',trip)
  self.assertIn('points[i].TownMap != currentMap',trip)
 def test_observation_and_town_guards(self):
  f=g[g.index('private static ClickAimAction? TownReturnAction('):g.index('private static bool WalkToRecordedMailbox(')]
  for t in ('action.Id == 556', 'action.Id == 6948', 'WowBTownCastCandidate.Observe', 'progress.Update', 'DefendNearby', 'Result.Interrupted', 'TownHasPlanNearby', 'destinationCount >= 2', 'ClickAimAbort.Wait', 'elapsed > 4000', '_townTransit = true'):
   self.assertIn(t,f)
  self.assertIn('!_townTransit && roam != null && !roam.MapCurrent()',g)
  self.assertIn('!_townTransit && roam == null && !ProfileMapCurrent()',g)
if __name__=='__main__': unittest.main()

"""Source and UI wiring checks; not a compiled or live test."""
from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[2]
G=(R/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()
H=(R/'Styx/Logic/Pathing/ClickAim/WowBSkinHoverSearch.cs').read_text()
P=(R/'Styx/Logic/Pathing/ClickAim/WowBSkinCastProgress.cs').read_text()
class Skin70938(unittest.TestCase):
 def test_search_is_selection_free_and_guarded(self):
  for s in ['baseline.Guid == corpse', 'hover.Guid != corpse', 'again.Guid != corpse', '!safe()', 'wait(200)', 'maxProbes > 40']:
   self.assertIn(s,H)
  for s in ['LeftClick', 'ConfirmedRightClick', 'TapKey', 'ReadTargetGuid']:
   self.assertNotIn(s,H)
  helper=G[G.index('private static bool FindCorpseHover'):G.index('// Selection-free nearby scan.')]
  for s in ['TryClientOrigin', 'GatherBackgroundFocus.Current', 'MouseButtonsUp', 'TryCursorScreen', 'corpseSafe()', 'WowBSkinHoverSearch.Find']:
   self.assertIn(s,helper)
 def test_two_attempts_never_repeat_unknown_or_seen_cast(self):
  block=G[G.index('private static void TrySkin70938'):G.index('private static void TrySkinLootedBeast')]
  for s in ['attempt <= 2', 'FindCorpseHover', 'SkinCorpseCurrent', 'Observation.Idle', 'hover == original.Guid', 'finalHover == original.Guid',
            'reason == "right-button messages posted"', 'progress.SawCast', '!progress.ConfirmedNoCast', '!mayRetry', 'Result.CandidateCleared']:
   self.assertIn(s,block)
  for s in ['FindUnitPixel', 'DismissRejectedSelection', 'LeftClick', 'ReadTargetGuid']:
   self.assertNotIn(s,block)
  self.assertIn('!seen && !uncertain && idleCount >= 2',P)
 def test_priority_and_avoid_totem_probes_before_skin(self):
  loop=G[G.index('// Do not wait through a long meal'):G.index('RoutePoint next;')]
  self.assertLess(loop.index('LootNearby(hwnd, 3, recentOnly: true)'),loop.index('TrySkinNearby(hwnd)'))
  self.assertLess(loop.index('TrySkinNearby(hwnd)'),loop.index('GatherRunner.GatherForGrind'))
  self.assertLess(loop.index('TrySkinNearby(hwnd)'),loop.index('RestIfNeeded(hwnd)'))
  self.assertLess(loop.index('TrySkinNearby(hwnd)'),loop.index('TryEngage(hwnd, out combatReport)'))
  engage=G[G.index('private static bool TryEngage('):G.index('// Defence always takes precedence above.')]
  self.assertLess(engage.index('if (combatNow)'),engage.index('TrySkinNearby(hwnd)'))
  click=G[G.index('private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,\n                                     bool mustSelect, out string how, out bool pixelConfirmed, Func<bool>? safeToClick,'):G.index('/// <summary>\n    /// Finds the screen pixel')]
  self.assertLess(click.index('FindCorpseHover'),click.index('FindUnitPixel'))
  self.assertIn('WowBSkinning.IsConfirmed(true, unit.Guid)',click)
if __name__=='__main__': unittest.main()

"""Source wiring checks; not a C# execution or live skinning test."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]
G = (R/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
C = (R/'Styx/Logic/Pathing/ClickAim/WowBGatherCastResearch.cs').read_text(encoding='utf-8-sig')
P = (R/'Styx/Logic/Pathing/ClickAim/WowBSkinCastProgress.cs').read_text(encoding='utf-8-sig')
class SkinCast(unittest.TestCase):
    def test_trace_candidate_is_narrow(self):
        for text in ('first == 10768U', 'second == first', 'unchecked(end - start) == 3000U',
                     'Observation.Skinning', 'reader.IsExperimental70938', 'reader.ExperimentalGrindActive',
                     'start1 != start2 || end1 != end2', 'TraceIdentity70707(memory, reader, player, true)'):
            self.assertIn(text,C)
        self.assertIn('first == 3564U',C)
    def test_wait_is_guarded_and_fallback_is_bounded(self):
        section=G[G.index('private static void TrySkinLootedBeast'):G.index('/// <summary>The click found no point')]
        for text in ('castPlayer.Guid == playerGuid', 'WowBGatherCastResearch.Observation.Idle',
                     'SkinCorpseCurrent', 'ConfirmedRightClick', 'SkinQuiet(reader, memory, map, nearby)',
                     'ClickAimAbort.Wait(200)', 'WowBSkinCastProgress.Result.ActiveTimeout',
                     'skinning success and loot remain unverified', 'waited < 6000'):
            self.assertIn(text,section)
        self.assertLess(section.index('Observation.Idle'),section.index('ConfirmedRightClick'))
        self.assertLess(section.index('ConfirmedRightClick'),section.index('progress.Update'))
        for text in ('FallbackMs = 6000', 'MaxHoldMs = 12000', 'MinimumObservedMs = 2600',
                     'LootSettleMs = 1000', 'idleCount >= 2', 'uncertain = true',
                     'start != castStart || end != castEnd', 'Result.ActiveTimeout'):
            self.assertIn(text,P)
    def test_70938_post_click_hold_and_interruption_wiring(self):
        guard = G[G.index('private static SkinPostClickGuard SkinPostClickSafety'):G.index('// 70938 only: two posted attempts')]
        for text in ('RouteRunner.StopRequested', 'ObjectManager.Wow != memory', 'memory.WindowHandle != hwnd',
                     'CheckDeathCheckpoint(trackerOnly: true, deferUnconfirmed: true)',
                     'TryRead70888RecoveryPlayer', 'TryReadGhostFlagCandidate', 'hp == 0',
                     'currentMap != map', 'combatRead && fighting', 'SkinPostClickGuard.Unknown'):
            self.assertIn(text, guard)
        for text in ('VendorDue()', '_vendorDue', 'GatherSettings.Skinning', 'GatherSettings.SkinNearby'):
            self.assertNotIn(text, guard)
        section = G[G.index('private static void TrySkin70938'):G.index('private static void TrySkinLootedBeast')]
        self.assertEqual(section.count('SkinPostClickSafety(reader, memory, hwnd, map, castPlayer,'), 2)
        for text in ('SkinPostClickGuard.Stop', 'SkinPostClickGuard.Combat', 'SkinPostClickGuard.UnsafeSession',
                     'state = WowBGatherCastResearch.Observation.Unknown', 'progress.CanRelease',
                     'WowBSkinCastProgress.HoldLimitReached(elapsed)', 'Stop(); return;',
                     '!progress.ConfirmedNoCast || !mayRetry', 'ClickAimAbort.Wait(200)'):
            self.assertIn(text, section)
        self.assertNotIn('if (!SkinQuiet(reader, memory, map, nearby)) return;', section)
        self.assertIn('if (skinRoutineCalled) break;', G)
        self.assertIn('guardSafe && StableIdle', P)
if __name__ == '__main__': unittest.main()

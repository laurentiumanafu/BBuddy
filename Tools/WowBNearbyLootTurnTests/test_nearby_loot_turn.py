"""Static integration checks; these do not test real client turning or loot."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
G = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
I = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimInput.cs').read_text(encoding='utf-8-sig')
def sec(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class NearbyLootTurn(unittest.TestCase):
    def test_nearby_3d_reach_skips_approach_calibration(self):
        a = sec(G, 'private static bool ApproachRecentCorpse(', 'private static int TryLootOne(')
        self.assertLess(a.index('RecentCorpseCurrent('), a.index('reader.IsExperimental70862 && RecentLootInReach(corpse, player)'))
        self.assertLess(a.index('reader.IsExperimental70862 && RecentLootInReach(corpse, player)'), a.index('ChaseStep('))
        self.assertIn('quickLootTurnCalibration: true', a)
        reach = sec(G, 'private static bool RecentLootInReach(', 'private static bool ApproachRecentCorpse(')
        self.assertIn('Math.Sqrt(dx * dx + dy * dy + dz * dz)', reach)
        self.assertIn('double.IsFinite(distance) && distance <= LootReachYd', reach)

    def test_quick_probe_before_turn_safe_fallback(self):
        body = sec(G, 'private static int TryLootOne(', '    // Skinning is separate')
        self.assertIn('recentOnly && lootReader?.IsExperimental70862 == true', body)
        self.assertIn('if (!fastRecentProbe)\n            ClickAimKeySteer.Face', body)
        self.assertIn('recentSafe, 9, out searchExhausted', body)
        turn = body.index('ClickAimKeySteer.Face(beforeTurnCorpse')
        for item in ('!lootClicked && fastRecentProbe && searchExhausted', 'recentSafe()', 'RecentCorpseCurrent(', 'RecentLootInReach(beforeTurnCorpse'):
            self.assertLess(body.index(item), turn)
        after = body[turn:]
        for item in ('recentSafe()', 'RecentCorpseCurrent(', 'afterTurnPlayer.Guid == beforeTurnPlayer.Guid',
                     'afterTurnPlayer.UnitAddress == beforeTurnPlayer.UnitAddress', 'afterTurnPlayer.RecordAddress == beforeTurnPlayer.RecordAddress',
                     'RecentLootInReach(afterTurnCorpse', 'TryClickUnit('):
            self.assertIn(item, after)
        click = sec(I, 'internal static bool ConfirmedRightClick(', '    // One dialog click only')
        self.assertIn('return true;', click[click.index('WM_RBUTTONDOWN'):])

    def test_probe_exhaustion_not_the_same_as_cancel(self):
        f = sec(G, 'private static bool FindUnitPixel(IntPtr hwnd, WowBObservedUnit unit, double px, double py,', '    /// <summary>\n    /// One fight\'s mesh path')
        self.assertIn('searchExhausted = false;', f)
        self.assertIn('searchExhausted = true;', f)
        self.assertLess(f.index('if (_stop || ClickAimAbort.Requested || (cancel != null && cancel()))'), f.index('searchExhausted = true;'))
        self.assertIn('if (probes >= maxProbes) break;', f)
        self.assertIn('ClickProbeMax, out _, cancel', f)

    def test_dead_target_exits_before_any_more_facing(self):
        after = sec(G, 'WowBWorldSnapshot? afterSwing;', '            if (health >= 0 && lastHealth >= 0 && health < lastHealth)')
        for item in ('target.Guid != defenceTarget', 'target.UnitAddress != expectedPull.UnitAddress', 'target.RecordAddress != expectedPull.RecordAddress',
                     'reader.IsExperimental70862 && lastHealth > 0 && health == 0', 'return "killed"'):
            self.assertIn(item, after)
        cast = sec(G, 'WowBObservedUnit t3;', '                        lastHealth = mobAfter;')
        self.assertIn('t3.UnitAddress == expectedPull.UnitAddress', cast)
        self.assertIn('t3.RecordAddress == expectedPull.RecordAddress', cast)
        self.assertIn('reader.IsExperimental70862 && mobAfter == 0 && mobBeforeHealth > 0', cast)

if __name__ == '__main__': unittest.main()

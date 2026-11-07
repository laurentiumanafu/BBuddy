"""Source integration checks; not a simulation of the WoW client or movement."""
import re
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')

def section(start, end):
    a = SOURCE.index(start)
    return SOURCE[a:SOURCE.index(end, a)]

class ApproachIntegration(unittest.TestCase):
    def test_both_filter_modes_approach_before_tab(self):
        s = section('    private static bool TryEngage(', '    /// <summary>\n    /// Whether the client')
        tab = s.index('ClickAimInput.TapKey(hwnd, TabVk, 40)')
        self.assertLess(s.index('ApproachPoolCandidate(hwnd, nearest, FactionApproachYd)'), tab)
        self.assertLess(s.index('ApproachPoolCandidate(hwnd, nearest);'), tab)
        self.assertRegex(SOURCE, r'FactionApproachYd\s*=\s*PoolApproachYd')
        self.assertRegex(SOURCE, r'PoolApproachYd\s*=\s*40\.0')
        self.assertRegex(SOURCE, r'FactionTabFallbackYd\s*=\s*ClickAimCombat\.CasterRangeYd\s*-\s*2\.0')

    def test_faction_tab_miss_closes_only_to_casting_margin(self):
        s = section('        if (GrindFactionSettings.Rules.Enabled && !have)\n        {\n            // TAB reach',
                    '        // Do not waste dozens of pixel clicks')
        self.assertIn('NearestKillable(retrySnapshot', s)
        self.assertIn('ShouldApproachPoolCandidate(false, retryCandidate, nearestYd, FactionTabFallbackYd)', s)
        self.assertIn('ApproachPoolCandidate(hwnd, nearest, FactionTabFallbackYd)', s)
        self.assertLess(s.index('ApproachPoolCandidate(hwnd, nearest, FactionTabFallbackYd)'), s.index('_tabRefusedUntil[nearest.Guid]'))
        self.assertNotIn('FindUnitPixel', s)

    def test_cast_range_releases_forward_even_when_well_inside(self):
        s = section('            if (canCast && d > selectedRange)', '            /* ---- 3. Wrath')
        self.assertIn('if (canCast)\n                ClickAimKeySteer.ReleaseHeld()', s)
        self.assertNotIn('d > selectedRange - 2.0', s)

    def test_fresh_candidate_rechecked_each_tab_attempt(self):
        s = section('        for (int i = 0; i < TabTries', '        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;')
        self.assertLess(s.index('NearestKillable(tabSnapshot'), s.index('ClickAimInput.TapKey(hwnd, TabVk, 40)'))
        self.assertIn('FactionApproachYd : PoolApproachYd', s)
        self.assertIn('out nearest, out nearestYd', s)
        self.assertIn('TryReadGatherCombat(out bool tabCombat)', s)

    def test_bar_retry_holds_movement_and_does_not_resume_without_baseline(self):
        gather = (Path(__file__).resolve().parents[2] / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
        for src in (SOURCE, gather):
            retry = src[src.index('if (trialReader != null && !WowBGrindFactions.Experimental70707BarSafe(trialReader))'):] if src is SOURCE else src[src.index('if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))'):]
            retry = retry[:retry.index('continue;') + len('continue;')]
            self.assertLess(retry.index('ClickAimKeySteer.ReleaseHeld()'), retry.index('WaitForExperimentalBar('))
            self.assertIn('ReleaseArrows(hwnd)', retry)
            self.assertIn('break;', retry)
            self.assertNotIn('TapKey', retry)
            self.assertIn('rechecking combat before continuing', retry)

    def test_roam_does_not_press_tab(self):
        s = section('    private static bool FightNeeded(', '    private static string DwellAtHotspot(')
        self.assertNotIn('TryPullOnTheWay(hwnd', s)
        self.assertNotIn('TapKey(hwnd, TabVk', s)
        self.assertIn('NearestKillable(', s)

    def test_approach_retains_faction_mesh_and_safety(self):
        s = section('    private static string ApproachPoolCandidate(', '    /// <summary>\n    /// Drops a target')
        for required in ('WowBGrindFactions.AllowsPull', 'SelectionFieldsValid', 'ChaseStep(hwnd, watch, candidate',
                         'TryReadGatherCombat', 'ObjectManager.WowB != reader', 'ClickAimKeySteer.ReleaseHeld()'):
            self.assertIn(required, s)
        self.assertGreaterEqual(int(re.search(r'FactionTabRetryMs\s*=\s*(\d+)', SOURCE).group(1)), 30000)

if __name__ == '__main__':
    unittest.main()

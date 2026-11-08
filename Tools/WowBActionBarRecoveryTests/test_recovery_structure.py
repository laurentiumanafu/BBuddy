"""Static wiring assertions only; the reader fixture exercises production C#."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
F = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs').read_text(encoding='utf-8-sig')
G = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
H = (ROOT / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
R = (ROOT / 'Styx/WoWInternals/WowBReader.cs').read_text(encoding='utf-8-sig')


def section(source, start, end):
    a = source.index(start)
    return source[a:source.index(end, a)]


class ActionBarRecovery(unittest.TestCase):
    def test_raw_baseline_unchanged_and_double_read(self):
        status = section(F, '    internal static bool ExperimentalBarStatus(', '    // Called only after releasing movement.')
        self.assertIn('if (bar[i] != expected[i])', status)
        self.assertIn('stable raw difference:', status)
        snapshot = section(R, '        internal bool TryReadExperimentalActionSnapshot(', '        /// <summary>')
        self.assertEqual(snapshot.count('_memory.ReadBytes64(address, 12 * 8)'), 2)
        self.assertIn('Convert.ToHexString(first) != Convert.ToHexString(second)', snapshot)

    def test_bounded_wait_guarded_by_stop_attachment_and_player(self):
        wait = section(F, '    internal static bool WaitForExperimentalBar(', '    private static bool CheckExperimentalBuild(')
        self.assertIn('reader.IsExperimental70862 ? 60000 : 1200', wait)
        self.assertIn('Environment.TickCount64 + timeout', wait)
        self.assertIn('ExperimentalBarStatus(reader, out reason)', wait)
        self.assertIn('first.IsEmpty', wait)
        self.assertIn('first != second', wait)
        self.assertIn('first != _boundPlayer', wait)
        self.assertIn('memory.TemporaryCacheState(false)', wait)
        self.assertGreaterEqual(wait.count('stopped() || ClickAimAbort.Requested'), 2)
        self.assertGreaterEqual(wait.count('ObjectManager.WowB != reader'), 2)
        self.assertIn('ClickAimAbort.Wait(', wait)
        self.assertIn('now >= deadline', wait)
        self.assertNotIn('_experimentalBar =', wait)
        self.assertIn('_boundPlayer = player.Guid', F)

    def test_runners_release_keys_and_sample_death_during_pause(self):
        for src, start, check in ((G, 'if (trialReader != null && !WowBGrindFactions.Experimental70707BarSafe(trialReader))', 'CheckDeathCheckpoint(trackerOnly: true)'),
                                  (H, 'if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))', 'StopIfDeadOrGhost()')):
            block = src[src.index(start):]
            block = block[:block.index('continue;')]
            self.assertLess(block.index('ClickAimKeySteer.ReleaseHeld()'), block.index('WaitForExperimentalBar('))
            self.assertLess(block.index('ClickAimKeySteer.ReleaseArrows(hwnd)'), block.index('WaitForExperimentalBar('))
            self.assertIn(check, block)
            self.assertIn('ClickAimAbort.Requested', block)
            self.assertIn('break;', block)
        self.assertIn('unchecked(Environment.TickCount - started) >= budgetMs', G)

    def test_hotspot_and_vendor_routes_interrupt_on_lost_bar(self):
        route = section(G, '                bool resourceInterrupt = false, vendorInterrupt = false;', '                if (ok)')
        self.assertIn('barInterrupt = true; return true;', route)
        self.assertIn('if (barInterrupt)', route)
        vendor = section(G, '    private static bool VendorTrip(', '    /// <summary>\n    /// Replays the recorded plan')
        self.assertIn('vendorReader.IsExperimental70862 && !WowBGrindFactions.Experimental70707BarSafe(vendorReader)', vendor)
        candidate = section(G, '    private static bool VendorCandidateStillValid(', '    private static bool TryClickRecordedMerchant(')
        self.assertIn('reader.IsExperimental70862 && !WowBGrindFactions.Experimental70707BarSafe(reader)', candidate)
        gather = section(H, '    private static bool Interrupt(bool checkNodes)', '    private static bool MapStillCurrent()')
        self.assertIn('barReader?.IsExperimental70862 == true && !WowBGrindFactions.Experimental70707BarSafe(barReader)', gather)

    def test_combat_and_postfight_loot_do_not_continue_on_changed_bar(self):
        fight = section(G, '    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        block = fight[fight.index('while (!_stop && !ClickAimAbort.Requested'):]
        self.assertIn('reader.IsExperimental70862 && !WowBGrindFactions.Experimental70707BarSafe(reader)', block)
        self.assertLess(block.index('Experimental70707BarSafe(reader)'), block.index('!Capture(out snap)'))
        engage = section(G, '    private static bool TryEngage(', '    private static bool FactionAttackInputAllowed(')
        self.assertIn('trialReader?.IsExperimental70862 == true && !WowBGrindFactions.Experimental70707BarSafe(trialReader)', engage)
        dwell_start = G.index('    private static string DwellAtHotspot(')
        dwell = G[dwell_start:G.index('    private static', dwell_start + 25)]
        self.assertIn('!WowBGrindFactions.Experimental70707BarSafe(barReader)) break;', dwell)
        loot = section(G, '    private static void PollRecentLootAfterFight(', '    private static bool RecentLootRetryPending(')
        self.assertIn('reader.IsExperimental70862 && !WowBGrindFactions.Experimental70707BarSafe(reader)', loot)
        self.assertLess(loot.index('Experimental70707BarSafe(reader)'), loot.index('LootNearby('))


if __name__ == '__main__':
    unittest.main()

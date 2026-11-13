"""Static integration checks: no live death, client, or mesh validation."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]

def read(path): return (R / path).read_text(encoding='utf-8-sig')
class HotspotDeathWatch(unittest.TestCase):
    def test_holds_and_polls_before_leaving_four_failures(self):
        s = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
        hold = s[s.index('    private static bool WatchDeathAfterHotspotFailures('):s.index('    private static bool RecoverIfPending(')]
        for text in ('mover.QuietKeyStop = true', 'mover.MoveStop()', 'ReleaseHeld()', 'ReleaseArrows(',
                     'ClickAimAbort.Requested', 'RouteRunner.StopRequested', 'ObjectManager.Wow != memory',
                     'ObjectManager.WowB != reader', 'reader.ExperimentalGrindActive', '>= budgetMs', 'ExperimentalSessionPlayerCurrent(memory, reader)', '!sessionMapCurrent()',
                     'CheckDeathCheckpoint(trackerOnly: true, deferUnconfirmed: true)',
                     '_deathCheckpoint?.LastObservation', 'WowBHotspotDeathWatch.Wait'):
            self.assertIn(text, hold)
        self.assertNotIn('TapKey(', hold)
        route = s[s.index('if (dead >= MaxDeadHotspots)'):s.index('            } while (recovered', s.index('if (dead >= MaxDeadHotspots)'))]
        self.assertIn('trialReader?.IsExperimental70938 == true', route)
        self.assertLess(route.index('WatchDeathAfterHotspotFailures'), route.index('RecoverIfPending(mover)'))
        self.assertIn('watchCancelled = !WatchDeathAfterHotspotFailures', route)
        self.assertIn('roam.MapCurrent() : ProfileMapCurrent()', route)
        self.assertIn('!watchCancelled && _recoveryReady', route)
        self.assertIn('if (recovered) dead = 0;', route)
        recovery = read('Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs')
        self.assertIn('TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true', recovery)
        self.assertIn('SuitableForClick(dialog, release', recovery)
    def test_watch_only_observes_and_stop_wins(self):
        s = read('Styx/Logic/Pathing/ClickAim/WowBHotspotDeathWatch.cs')
        self.assertLess(s.index('if (cancelled()) return false;'), s.index('if (sample.Ready) return true;'))
        self.assertIn('wait(500)', s)
        self.assertNotIn('TapKey(', s)
        self.assertNotIn('Click(', s)

if __name__ == '__main__': unittest.main()

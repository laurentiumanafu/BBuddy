"""Source-level recovery wiring checks; not an in-game test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text(encoding='utf-8-sig')
def section(text, start, end): return text[text.index(start):text.index(end, text.index(start))]

class PlayerDeathRecovery(unittest.TestCase):
    def test_recovery_observation_is_local_and_health_only(self):
        reader = read('Styx/WoWInternals/WowBReader.cs')
        method = section(reader, 'internal bool TryRead70888RecoveryPlayer(', '        /// <summary>\n        /// Captures only records')
        for requirement in ('!IsExperimental70888', '!ExperimentalGrindActive', 'TryGetPlayerObject(',
                            'IsPoolRecordCurrent(record)', 'TryGetUnitObject(', 'TryReadTraceLocalGuid(',
                            'HealthCandidateOffset, 16', 'SequenceEqual(second)', 'hp > max', 'max == 0'):
            self.assertIn(requirement, method)
        for prohibited in ('TargetGuid', 'ReadTarget', 'PowerArray', 'TryGetUnitFields('):
            self.assertNotIn(prohibited, method)
        tracker = read('Styx/Logic/Pathing/ClickAim/WowBDeathCheckpoint.cs')
        self.assertIn('reader.IsExperimental70888', tracker)
        self.assertIn('TryRead70888RecoveryPlayer', tracker)
        self.assertIn('MayRecord(_hasAlive, _saved', tracker)
        self.assertIn('TryReadGhostFlagCandidate', tracker)
        recovery = read('Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs')
        self.assertIn('WowBDeathCheckpoint.ReadRecoveryPlayer(reader', recovery)
        self.assertIn('player.Guid != checkpoint.Player', recovery)
        self.assertIn('TryReadGhostFlagCandidate', recovery)

    def test_stop_and_no_input_during_bounded_retry(self):
        grind = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
        wait = section(grind, 'private static string StopFightForSafety(', '    private static bool RecoverIfPending(')
        self.assertIn('ClickAimKeySteer.ReleaseHeld()', wait)
        self.assertIn('safetyReader?.IsExperimental70888 == true && _deathCheckpoint != null', wait)
        self.assertIn('deferUnconfirmed: extended', wait)
        self.assertIn('extended ? 200 : 20', wait)
        self.assertIn('ClickAimAbort.Requested || RouteRunner.StopRequested', wait)
        self.assertIn('ObjectManager.Wow != safetyMemory || ObjectManager.WowB != safetyReader', wait)
        self.assertNotIn('TapKey', wait)
        check = section(grind, 'private static bool CheckDeathCheckpoint(', '    // These 70707 combat exits')
        self.assertIn('!_deathCheckpoint.Recorded.HasValue', check)
        self.assertIn('_deathStopGeneration = _stopGeneration', check)
        recovery = section(grind, 'private static bool RecoverIfPending(', '    private static int _presses;')
        self.assertIn('MayBeginRecovery', recovery)
        self.assertIn('_deathStopGeneration, _stopGeneration', recovery)
        self.assertIn('WowBCorpseRecovery.TryRecover', recovery)

if __name__ == '__main__': unittest.main()

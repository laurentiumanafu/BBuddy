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
        for status in ('alive candidate', 'ghost candidate set', 'local position or health bytes unreadable',
                       'local health changed during read', 'zero-health checkpoint recorded'):
            self.assertIn(status, tracker + reader)
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
        self.assertIn('_deathCheckpoint.LastObservation', wait)
        self.assertIn('70888 safety pause observations:', wait)
        self.assertNotIn('TapKey', wait)
        check = section(grind, 'private static bool CheckDeathCheckpoint(', '    // These 70707 combat exits')
        self.assertIn('!_deathCheckpoint.Recorded.HasValue', check)
        self.assertIn('_deathStopGeneration = _stopGeneration', check)
        recovery = section(grind, 'private static bool RecoverIfPending(', '    private static int _presses;')
        self.assertIn('MayBeginRecovery', recovery)
        self.assertIn('_deathStopGeneration, _stopGeneration', recovery)
        self.assertIn('WowBCorpseRecovery.TryRecover', recovery)

    def test_heal_snapshot_target_change_yields_without_input(self):
        grind = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
        helper = section(grind, 'private static bool TryYieldHealSnapshotTargetChange(', '    private static void LogFactionFight(')
        for text in ('reader.IsExperimental70888', 'firstTarget == expectedTarget', 'firstTarget != secondTarget',
                     'CheckDeathCheckpoint(trackerOnly: true)', 'LastObservation != "alive candidate"',
                     'ReadRecoveryPlayer(reader', 'current.Guid != expectedPlayer.Guid',
                     'current.UnitAddress != expectedPlayer.UnitAddress', 'current.RecordAddress != expectedPlayer.RecordAddress',
                     'hp == 0', 'TryReadGhostFlagCandidate', 'mapAfter != map',
                     'ClickAimAbort.Requested', 'RouteRunner.StopRequested', 'YieldForTargetChange('):
            self.assertIn(text, helper)
        self.assertNotIn('TapKey(', helper)
        heal = section(grind, 'if (!Capture(out WowBWorldSnapshot? healSnapshot)', 'if (freshHeal.MaxHealth > 0')
        self.assertIn('TryYieldHealSnapshotTargetChange', heal)
        self.assertIn('heal world snapshot unreadable or changed', heal)
        self.assertIn('heal player identity unavailable or changed', heal)
        self.assertIn('heal player vitals unreadable', heal)

if __name__ == '__main__': unittest.main()

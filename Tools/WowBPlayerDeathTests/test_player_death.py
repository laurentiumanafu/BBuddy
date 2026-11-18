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
        for requirement in ('!Uses70888Features', '!ExperimentalGrindActive', 'TryGetPlayerObject(',
                            'IsPoolRecordCurrent(record)', 'TryGetUnitObject(', 'TryReadTraceLocalGuid(',
                            'HealthCandidateOffset, 16', 'SequenceEqual(second)', 'hp > max', 'max == 0'):
            self.assertIn(requirement, method)
        for prohibited in ('TargetGuid', 'ReadTarget', 'PowerArray', 'TryGetUnitFields('):
            self.assertNotIn(prohibited, method)
        tracker = read('Styx/Logic/Pathing/ClickAim/WowBDeathCheckpoint.cs')
        self.assertIn('reader.Uses70888Features', tracker)
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
        self.assertIn('safetyReader?.Uses70888Features == true && _deathCheckpoint != null', wait)
        self.assertIn('deferUnconfirmed: extended', wait)
        self.assertIn('extended ? 200 : 20', wait)
        self.assertIn('ClickAimAbort.Requested || RouteRunner.StopRequested', wait)
        self.assertIn('ObjectManager.Wow != safetyMemory || ObjectManager.WowB != safetyReader', wait)
        self.assertIn('_deathCheckpoint.LastObservation', wait)
        self.assertIn('70888/70938 safety pause observations:', wait)
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
        for text in ('reader.Uses70888Features', 'firstTarget == expectedTarget', 'firstTarget != secondTarget',
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

    def test_wider_dialog_search_keeps_the_recorded_band(self):
        """Another PC put the same dialogs outside the recorded band.

        2026-11-17_1055_16264.log ran from C:\\bbuddy on another machine and
        logged `release not clicked: background image did not meet button checks
        (release=False, ... heading=False, accept=False ...)` on every attempt, so
        no Release or Accept was ever pressed. The fallback sweep looks at the
        whole client, but only after the recorded band found nothing.
        """
        pattern = read('Styx/Logic/Pathing/ClickAim/WowBCorpseDialogPattern.cs')
        recovery = read('Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs')
        # The recorded band is unchanged and still searched first.
        band = section(pattern, 'private static void PrimaryBand(', 'private static void ExtendedBand(')
        self.assertIn('xStart = width / 5; xEnd = width * 4 / 5;', band)
        self.assertIn('yStart = height / 20; yEnd = height * 3 / 10;', band)
        wide = section(pattern, 'private static void ExtendedBand(', '// Input is top-down')
        self.assertIn('{ xStart = 0; xEnd = width; yStart = 0; yEnd = height; }', wide)
        # Recorded band first, whole client only as the fallback, and reported.
        self.assertIn('if (FindButtonIn(pixels, width, height, stride, patterns, false, out x, out y, out unique))\n        { extended = false; return true; }', pattern)
        self.assertIn('if (FindButtonIn(pixels, width, height, stride, patterns, true, out x, out y, out unique))\n        { extended = true; return true; }', pattern)
        self.assertIn('out bool extended)', pattern)
        self.assertIn('ReleaseExtended, ResurrectExtended, AcceptExtended', pattern)
        self.assertIn('internal static bool UsedExtendedSearch(Result result, bool release)', pattern)
        self.assertIn('rx2, tx2, ax2)', pattern)
        # The sweep only runs when nothing was found, so a known-good match cannot move.
        self.assertIn('if (extendedBand) ExtendedBand(width, height, out xStart, out xEnd, out yStart, out yEnd);\n        else PrimaryBand(width, height, out xStart, out xEnd, out yStart, out yEnd);', pattern)
        # A wider search relaxes nothing: every authorization check still applies.
        suitable = section(pattern, 'internal static bool SuitableForClick(', '// Background capture is accepted')
        for guard in ('!result.ReleaseUnique', '!result.ReleaseButtonRed', '!result.AcceptUnique',
                      '!result.AcceptButtonRed', 'Math.Abs(result.ResurrectX - result.AcceptX) > 15',
                      'result.AcceptY - result.ResurrectY < 15', 'result.AcceptY - result.ResurrectY > 45'):
            self.assertIn(guard, suitable)
        # And the log says where the label was, and when the wider sweep found it.
        self.assertIn('LabelWhere(dialog.ReleaseText, dialog.ReleaseX, dialog.ReleaseY)', recovery)
        self.assertIn('widerSearchOnly=" + WowBCorpseDialogPattern.UsedExtendedSearch(dialog, release)', recovery)
        self.assertIn('label matched only in the wider search area at (', recovery)

    def test_death_location_persists_across_restart(self):
        # A bot stopped and restarted while dead/ghost must return to the
        # corpse: the death location is persisted to a stable file on save,
        # adopted on the next run when the player is at zero health, and
        # cleared once the corpse is recovered.
        tracker = read('Styx/Logic/Pathing/ClickAim/WowBDeathCheckpoint.cs')
        for item in ('clickaim-death-location.txt', 'PersistedPath', 'TryLoadPersisted',
                     'TryAdoptPersisted', 'ClearPersisted', 'playerLow=', 'playerHigh=',
                     'adopted persisted death location', 'File.WriteAllLines(PersistedPath'):
            self.assertIn(item, tracker)
        grind = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
        self.assertIn('_deathCheckpoint.TryAdoptPersisted()', grind)
        self.assertIn('WowBDeathCheckpoint.ClearPersisted()', grind)

if __name__ == '__main__': unittest.main()

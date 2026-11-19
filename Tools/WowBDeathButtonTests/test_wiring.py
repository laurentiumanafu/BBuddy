from pathlib import Path
import unittest
r = Path(__file__).resolve().parents[2]
x = (r / 'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
c = (r / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
p = (r / 'Styx/Logic/Pathing/ClickAim/WowBDeathButtonCalibration.cs').read_text()
rec = (r / 'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')

class DeathButtonWiring(unittest.TestCase):
    def test_recording_ui_present(self):
        for s in ('btnRecordDeathRelease_Click', 'btnRecordDeathAccept_Click',
                  'btnSaveDeathButtons_Click', 'lblDeathButtonsStatus',
                  'Record Release Spirit (3 s)', 'Record Accept Resurrection (3 s)',
                  'Save death buttons'):
            self.assertIn(s, x, s)
        for s in ('RecordDeathButton', 'btnSaveDeathButtons_Click',
                  'WowBDeathButtonCalibration.Save', 'TryCursorFraction',
                  'Task.Delay(3000)'):
            self.assertIn(s, c, s)

    def test_calibration_class_shape(self):
        for s in ('clickaim-death-buttons.json', 'internal static bool Valid',
                  'internal static bool TryLoad', 'internal static void Save',
                  'ReleaseX', 'ReleaseY', 'AcceptX', 'AcceptY',
                  'dx * dx + dy * dy < 400'):
            self.assertIn(s, p, s)
        # Calibration is per-player and per-client-size.
        self.assertIn('value!.Player != player', p)
        self.assertIn('value.Width != width', p)

    def test_recovery_uses_calibration_with_default_fallback(self):
        # Recovery consults the calibration as a primary click source, but
        # keeps pattern matching as the default when no calibration exists.
        self.assertIn('WowBDeathButtonCalibration.TryLoad', rec)
        self.assertIn('calib.ReleaseX', rec)
        self.assertIn('calib.AcceptX', rec)
        # Calibration is loaded before pattern matching and bypasses it.
        self.assertIn('bool calibrated = false', rec)
        self.assertIn('if (!calibrated && !WowBCorpseDialogPattern.SuitableForClick(dialog, release', rec)
        # Pattern matching is only consulted when no calibration exists.
        self.assertIn('(!calibrated && (!WowBCorpseDialogPattern.SuitableForClick(away, release', rec)
        self.assertIn('!calibrated && !WowBCorpseDialogPattern.SuitableForClick(again, release', rec)
        # The calibration never bypasses the player-state verification.
        self.assertIn('if (!Safety()) return false;', rec)
        # The resurrection spot search also treats a recorded Accept button
        # as a visible prompt, so Accept works where pattern matching fails.
        self.assertIn('TryPromptAtSpot', rec)
        self.assertIn('WowBDeathButtonCalibration.TryLoad(checkpoint.Player.ToString(), width, height, out _)', rec)

if __name__ == '__main__':
    unittest.main()

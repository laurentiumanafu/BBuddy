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
        # Recovery consults the calibration but keeps pattern matching as the
        # default when no calibration exists.
        self.assertIn('WowBDeathButtonCalibration.TryLoad', rec)
        self.assertIn('calib.ReleaseX', rec)
        self.assertIn('calib.AcceptX', rec)
        # Pattern matching still runs first (SuitableForClick) and the
        # calibrated flag only skips pattern-vs-pattern position checks.
        self.assertIn('bool calibrated = false', rec)
        self.assertIn('!calibrated && (Math.Abs(awayX - x) > 2', rec)
        self.assertIn('!calibrated && (Math.Abs(x2 - x) > 2', rec)
        # The calibration never bypasses the dialog-state verification.
        self.assertIn('WowBCorpseDialogPattern.SuitableForClick(dialog, release', rec)

if __name__ == '__main__':
    unittest.main()

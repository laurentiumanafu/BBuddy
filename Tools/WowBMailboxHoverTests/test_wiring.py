from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[2]
ui = (root / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')
xaml = (root / 'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')

class MailboxHoverWiring(unittest.TestCase):
    def test_user_driven_read_only(self):
        for handler in ('btnMailHoverAway_Click', 'btnMailHoverBox_Click'):
            self.assertIn(handler, xaml)
            self.assertIn(handler, ui)
        start = ui.index('private sealed class MailHoverSample')
        end = ui.index('private async void btnMailClickTest_Click', start)
        code = ui[start:end]
        for guard in ('ClickAimUi.Running', 'ClickAimUi.Paused', 'GameForeground', 'TryCursorFraction',
                      'TryReadTraceLocalGuid', 'TryReadExperimentalMapCandidate', 'TryCaptureWorldSnapshot', 'TraceIdentity70707',
                      'TryReadMouseoverGuidCandidate', 'MailHoverSameSession', 'first.Hover != second.Hover',
                      'Task.Delay(3000)', 'Task.Delay(150)'):
            self.assertIn(guard, code)
        for forbidden in ('PostMessage', 'SendInput', 'ClickAimInput.Click', 'ClickAimInput.Move', 'Lua.', 'Send Mail'):
            self.assertNotIn(forbidden, code)
        self.assertIn('user-labelled only, no input sent', code)

    def test_supervised_click_has_guards_and_no_retry(self):
        self.assertIn('btnMailClickTest_Click', xaml)
        click = ui[ui.index('private async void btnMailClickTest_Click'):ui.index('private async void btnMailTabTest_Click')]
        for guard in ('MessageBoxButton.YesNo', 'ClickAimAbort.Generation', 'cancel.IsCancellationRequested',
                      'ClickSafetyCandidate', 's.Hover == expected.Hover', 'MailHoverSameSession',
                      'Task.Delay(3000, cancel.Token)', 'DiagnosticRightClick', '_mailHoverBox = null; _mailHoverAway = null'):
            self.assertIn(guard, click)
        self.assertEqual(click.count('DiagnosticRightClick('), 1)
        self.assertIn('_mailClickCancellation?.Cancel()', ui)
        source = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimInput.cs').read_text(encoding='utf-8-sig')
        helper = source[source.index('internal static bool DiagnosticRightClick'):source.index('internal static bool DiagnosticLeftClick')]
        for guard in ('ClickAimAbort.Generation', 'GameForeground', 'MouseButtonsUp', 'TryCursorFraction', 'confirm()', 'WM_RBUTTONDOWN', 'WM_RBUTTONUP'):
            self.assertIn(guard, helper)
        self.assertNotIn('WM_MOUSEMOVE', helper)
        self.assertNotIn('ClickAimAbort.Clear', helper)
        self.assertIn('both button-up messages failed', helper)

    def test_send_tab_is_separate_explicit_single_click(self):
        self.assertIn('btnMailTabTest_Click', xaml)
        self.assertIn('Do not hover the Send button', xaml)
        code = ui[ui.index('private async void btnMailTabTest_Click'):ui.index('private sealed class MailGridPointRow')]
        for guard in ('MessageBoxButton.YesNo', 'ClickAimUi.Running', 'ClickAimUi.Paused', 'ClickAimAbort.Generation',
                      'cancel.IsCancellationRequested', 'Task.Delay(3000, cancel.Token)', 'ClickSafetyCandidate',
                      'MailHoverSameSession', 'DiagnosticLeftClick', 'mailbox and tab state not verified'):
            self.assertIn(guard, code)
        self.assertEqual(code.count('DiagnosticLeftClick('), 1)
        self.assertNotIn('DiagnosticRightClick(', code)
        self.assertNotIn('ClickAimInput.ClickFraction', code)
        source = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimInput.cs').read_text(encoding='utf-8-sig')
        helper = source[source.index('internal static bool DiagnosticLeftClick'):source.index('// One dialog click only')]
        for guard in ('ClickAimAbort.Generation', 'GameForeground', 'MouseButtonsUp', 'TryCursorFraction', 'confirm()', 'WM_LBUTTONDOWN', 'WM_LBUTTONUP'):
            self.assertIn(guard, helper)
        self.assertNotIn('WM_MOUSEMOVE', helper)
        self.assertNotIn('ClickAimAbort.Clear', helper)

    def test_not_wired_to_gameplay(self):
        for name in ('GrindRunner.cs', 'GatherRunner.cs'):
            content = (root / 'Styx/Logic/Pathing/ClickAim' / name).read_text(encoding='utf-8-sig')
            self.assertNotIn('MailHoverSample', content)
            self.assertNotIn('RecordMailHover', content)

if __name__ == '__main__':
    unittest.main()

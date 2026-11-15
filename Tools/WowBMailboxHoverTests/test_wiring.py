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
        end = ui.index('private sealed class MailGridPointRow', start)
        code = ui[start:end]
        for guard in ('ClickAimUi.Running', 'ClickAimUi.Paused', 'GameForeground', 'TryCursorFraction',
                      'TryReadTraceLocalGuid', 'TryReadExperimentalMapCandidate', 'TryCaptureWorldSnapshot', 'TraceIdentity70707',
                      'TryReadMouseoverGuidCandidate', 'MailHoverSameSession', 'first.Hover != second.Hover',
                      'Task.Delay(3000)', 'Task.Delay(150)'):
            self.assertIn(guard, code)
        for forbidden in ('PostMessage', 'SendInput', 'ClickAimInput.Click', 'ClickAimInput.Move', 'Lua.', 'Send Mail'):
            self.assertNotIn(forbidden, code)
        self.assertIn('user-labelled only, no input sent', code)

    def test_not_wired_to_gameplay(self):
        for name in ('GrindRunner.cs', 'GatherRunner.cs'):
            content = (root / 'Styx/Logic/Pathing/ClickAim' / name).read_text(encoding='utf-8-sig')
            self.assertNotIn('MailHoverSample', content)
            self.assertNotIn('RecordMailHover', content)

if __name__ == '__main__':
    unittest.main()

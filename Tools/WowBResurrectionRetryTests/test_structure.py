"""Structure checks only; not a Windows click or in-game test."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]
class Wiring(unittest.TestCase):
    def test_retry_uses_full_guards_and_live_state(self):
        s = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs').read_text()
        retry = s[s.index('var retry = new WowBResurrectionRetry'):s.index('finally\n        {\n            ClickAimKeySteer.ReleaseHeld')]
        for guard in ('reader.IsExperimental70938', 'state == State.Ghost', 'Allowed(memory, reader, hwnd, running)',
                      'TryDialogClick(memory, reader, hwnd, checkpoint, running, false, !fallback',
                      'retry.Attempted(posted, retryReason', 'aliveReads >= 2 && WowBGrindFactions.Experimental70707BarSafe(reader)',
                      'ClickAimAbort.Wait(500)'):
            self.assertIn(guard, retry)
        dialog = s[s.index('private static bool TryDialogClick'):s.index('internal static bool TryRecover')]
        for guard in ('SpotSafe(', 'SuitableForClick(away, release', 'SuitableForClick(again, release', 'FinalCheck', 'Safety()', 'ConfirmedLeftClick'):
            self.assertIn(guard, dialog)
        self.assertIn('!release && reader.IsExperimental70938 ? 100 : 35', dialog)
    def test_policy_and_interruptible_release(self):
        p = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBResurrectionRetry.cs').read_text()
        for guard in ('MaxPosts = 3', 'DelayMs = 5000', '_ghostReads >= 2', '!confirmedGhost', 'reason != WowBReleaseTransition.CleanPost'):
            self.assertIn(guard, p)
        s = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimInput.cs').read_text()
        click = s[s.index('internal static bool ConfirmedLeftClick'):s.index('/// <summary>Right-click')]
        self.assertLess(click.index('ClickAimAbort.Wait(buttonHoldMs)'), click.index('WM_LBUTTONUP'))
        self.assertNotIn('SendInput', click)
if __name__ == '__main__': unittest.main()

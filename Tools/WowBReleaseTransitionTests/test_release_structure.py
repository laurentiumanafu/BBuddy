#!/usr/bin/env python3
"""Static wiring checks; synthetic C# fixture exercises the actual transition policy."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]

class ReleaseStructure(unittest.TestCase):
    def test_retries_are_70938_only_and_go_through_full_dialog_guard(self):
        source = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs').read_text()
        transition = source[source.index('        if (sentRelease)'):source.index('        if (ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint ghostPos)')]
        self.assertIn('TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true', transition)
        self.assertIn('TransitionWaitMs, reader.IsExperimental70938', transition)
        click = source[source.index('    private static bool TryDialogClick('):source.index('    internal static bool TryRecover(')]
        for guard in ('State.Dead', 'SuitableForClick(away, release', 'SuitableForClick(again, release', 'ConfirmedLeftClick', 'FinalCheck', 'Safety()'):
            self.assertIn(guard, click)
        self.assertLess(source.index('!RouteRunner.MeshReady()'), source.index('bool released = false'))

    def test_policy_vetoes_unknown_and_bounds_retries(self):
        s = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBReleaseTransition.cs').read_text()
        for guard in ('MaxPosts = 3', 'RetryDelayMs = 5000', 'retryChecks < MaxPosts - 1', 'deadReads >= 2', 'observation.State != State.Dead', 'initialPostReason == CleanPost && allowRetries'):
            self.assertIn(guard, s)
        self.assertIn('clean = false; // an absent/changed dialog', s)

if __name__ == '__main__':
    unittest.main()

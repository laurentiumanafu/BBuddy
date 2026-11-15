#!/usr/bin/env python3
"""Source invariants for the stopped-only mail candidate research probe."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ResearchSourceTests(unittest.TestCase):
    def test_read_only_bounded_and_guarded(self):
        source = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBMailAttachmentResearch.cs').read_text()
        for required in ('0x4E6CA90', '0x26B4F78', 'Count = 16, Stride = 40',
                         'TemporaryCacheState(false)', 'IsCommittedReadableRange64',
                         'TraceBuild70707', 'TraceIdentity70707', 'ClickAimAbort.Generation',
                         'Equal(first, second)', 'ObjectManager.Wow == memory',
                         'IsExperimental70938', '!ClickAimUi.Running', '!ClickAimUi.Paused'):
            self.assertIn(required, source)
        for forbidden in ('WriteBytes', 'PostMessage', 'ClickAimInput.', 'Lua.', 'SendObserved', 'WowBMailSequence'):
            self.assertNotIn(forbidden, source)

    def test_bag_correlations_are_bracketed_and_explicitly_inconclusive(self):
        source = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBMailAttachmentResearch.cs').read_text()
        for required in ('TryReadMailCandidateBags(out WowBGuid firstPlayer',
                         'TryReadMailCandidateBags(out WowBGuid secondPlayer',
                         'Equal(first, last)', 'SameBags(firstBags, secondBags)',
                         'if (bag.Unknown) continue', '!first.IsEmpty', '!second.IsEmpty',
                         'Backpack not observed', 'Unknown; not evidence of absence'):
            self.assertIn(required, source)
        self.assertLess(source.index('TryReadMailCandidateBags(out WowBGuid firstPlayer'), source.index('byte[]? first = memory.ReadBytes64'))
        self.assertLess(source.index('byte[]? second = memory.ReadBytes64'), source.index('TryReadMailCandidateBags(out WowBGuid secondPlayer'))

    def test_button_is_research_only(self):
        ui = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml').read_text()
        cs = (ROOT / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text()
        self.assertIn('Click="btnMailAttachmentResearch_Click"', ui)
        self.assertIn('does not verify attachments', ui)
        self.assertIn('WowBMailAttachmentResearch.Capture()', cs)

if __name__ == '__main__': unittest.main()

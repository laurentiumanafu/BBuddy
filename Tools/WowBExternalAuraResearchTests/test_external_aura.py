"""Static wiring checks; not a live client test or validation of the candidate layout."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
R = Path(__file__).resolve().parents[2]
def read(path): return (R / path).read_text(encoding='utf-8-sig')
S = read('Styx/Logic/Pathing/ClickAim/WowBCombatResearch.cs')
M = read('GreenMagic/Memory.cs')
X = read('UI/ClickAimCombatSettingsWindow.xaml')
U = read('UI/ClickAimCombatSettingsWindow.xaml.cs')
def part(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class ExternalResearch(unittest.TestCase):
    def test_explicit_opt_in_and_build(self):
        ns = {'p': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}
        name = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
        checkbox = next(c for c in ET.fromstring(X).findall('.//p:CheckBox', ns) if c.get(name) == 'chkExternalAuraResearch')
        self.assertEqual(checkbox.get('IsChecked'), 'False')
        self.assertIn('includeExternalArray = false', S)
        self.assertIn('includeExternalArray && !reader.Uses70862Features', S)
        self.assertIn('chkExternalAuraResearch.IsChecked == true', U)
        self.assertIn('includeExternalArray: includeExternalArray', U)
        self.assertIn('if (!Stopped)', S)

    def test_bounded_and_stable_read(self):
        fn = part(S, 'internal static ExternalCandidateSample? ReadExternalCandidate', '    // A new pointer/header')
        for text in ('ExternalMaxSlots = 24', 'ExternalMaxCapacity = 32', 'ExternalCandidateStride = 0x128',
                     'External70888MaxSlots = 28', 'External70888MaxCapacity = 36'):
            self.assertIn(text, S)
        for text in ('allow70888Extended ? External70888MaxSlots : ExternalMaxSlots',
                     'allow70888Extended ? External70888MaxCapacity : ExternalMaxCapacity',
                     'length > maxSlots', 'capacity > maxCapacity', 'length > capacity',
                     '(address & 7) != 0', 'address > UserLimit - (ulong)size',
                     'address < playerUnit + WindowSize', 'IsCommittedReadableRange64(address, size)',
                     'SameBytes(header1, header2)', 'SameBytes(first, second)',
                     'SameBytes(header1, header3)', 'SameBytes(header1, header4)'):
            self.assertIn(text, fn)
        self.assertEqual(fn.count('ReadBytes64(address, size)'), 2)
        self.assertEqual(fn.count('IsCommittedReadableRange64(address, size)'), 2)
        self.assertIn('out externalReason, reader.Uses70888Features', S)
        totem = read('Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs')
        self.assertIn('out string externalReason, reader.Uses70888Features', totem)
        self.assertIn('FromBytes(sample.Bytes, count, reader.Uses70888Features)', totem)
        region = part(M, 'public bool IsCommittedReadableRange64(', '        /// <summary>\n        /// True only when VirtualQueryEx')
        for text in ('VirtualQueryEx64', 'info.State != 0x1000', 'info.Protect & 0x100', 'info.RegionSize', 'info.BaseAddress'):
            self.assertIn(text, region)

    def test_identity_cancel_and_invalidation(self):
        capture = part(S, 'internal static string Capture(', '\n}')
        self.assertLess(capture.index('StableWithBar(memory, reader, player, target, snapshot.TargetGuid, bar, out why)', capture.index('for (int i')), capture.index('ReadExternalCandidate(memory, player.UnitAddress'))
        self.assertGreater(capture.index('StableWithBar(memory, reader, player, target, snapshot.TargetGuid, bar, out why)', capture.index('ReadExternalCandidate(memory, player.UnitAddress')), capture.index('ReadExternalCandidate(memory, player.UnitAddress'))
        self.assertIn('cancelled during read; last sample discarded', capture)
        self.assertIn('priorExternal = external;', capture)
        self.assertIn('previous baseline discarded', capture)
        self.assertIn('previous.Address != current.Address', S)
        self.assertIn('external baseline', S)
        self.assertIn('external changes', S)

    def test_no_game_actions(self):
        self.assertNotIn('TapKey(', S)
        self.assertNotIn('WriteBytes', S)
        self.assertNotIn('GameLua', S)
        self.assertNotIn('ReadExternalCandidate(', read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs'))

if __name__ == '__main__': unittest.main()

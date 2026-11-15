from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
p = root / 'Styx/Logic/Pathing/ClickAim'
table = (p / 'WowBMailCandidateTable.cs').read_text()
reader = (p / 'WowBMailCandidateReader.cs').read_text()
inventory = (p / 'WowBInventoryProbe.cs').read_text()
container = (p / 'WowBContainerResearchProbe.cs').read_text()

class CandidateSources(unittest.TestCase):
    def test_bounded_table_and_partial_is_unknown(self):
        for x in ('Count = 16, Stride = 40', 'expected.Count > 12', 'raw.Length != Size',
                  'item.IsEmpty || bag.IsEmpty', '!seen.Add(item)', 'seen.Count == expected.Count'):
            self.assertIn(x, table)

    def test_active_reader_guards(self):
        for x in ('ExperimentalGrindActive', '!ClickAimUi.Paused', '!ClickAimAbort.Requested',
                  '!RouteRunner.StopRequested', 'safe()', 'TraceIdentity70707', 'TemporaryCacheState(false)',
                  'Equal(first, second)', 'IsCommittedReadableRange64'):
            self.assertIn(x, reader)
        for x in ('TryReadActiveMailCandidateBags', 'ExperimentalGrindActive', 'RouteRunner.StopRequested',
                  'TryCaptureActiveMailBags'):
            self.assertIn(x, inventory)
        self.assertIn('TryCaptureActiveMailBags', container)
        self.assertIn('Unknown = true', container)

if __name__ == '__main__': unittest.main()

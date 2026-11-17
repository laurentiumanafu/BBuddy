"""Source checks only; the C# fixture is exercised separately with dotnet."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text()

class PlayerStateTests(unittest.TestCase):
    def test_offsets_and_widths(self):
        offsets = read('Styx/Offsets/Wow70938Offsets.cs')
        for line in ('PlayerGetterCandidateRva = 0x1F6EC10', 'AutoAttackCandidate = 0x2C9',
                     'CurrentTargetCandidate = 0x2C0', 'ShapeshiftFlagsCandidate = 0x2D0',
                     'ShapeshiftFlagCandidate = 0x10000000', 'AttackTargetCandidate = 0x1D40',
                     'ComboPointsCandidate = 0x1E1D', 'ShapeshiftCandidate = 0x1EEF',
                     'AttackStateFlagsCandidate = 0x250C', 'AttackEffectCandidate = 0x25C8'):
            self.assertIn(line, offsets)
        decoder = read('Styx/Logic/Pathing/ClickAim/WowBPlayerState70938.cs')
        for item in ('CurrentTargetCandidate, 8', 'AttackTargetCandidate, 8', 'AutoAttackCandidate, 1',
                     'ComboPointsCandidate, 1', 'ShapeshiftCandidate, 1', 'ShapeshiftFlagsCandidate, 4',
                     'AttackStateFlagsCandidate, 4', 'AttackEffectCandidate, 8', 'unreadable',
                     'unchecked((sbyte)combo[0])'):
            self.assertIn(item, decoder)

    def test_diagnostic_only_in_identity_checked_trace(self):
        recorder = read('Styx/Logic/Pathing/ClickAim/WowBCombatResearch.cs')
        begin = recorder.index('byte[]?[] playerBytes = ReadWindow')
        end = recorder.index('output.WriteLine(WowBPlayerState70938.Format', begin)
        self.assertIn('if (!StableWithBar', recorder[begin:end])
        self.assertIn('if (reader.Uses70938Features)', recorder[end-45:end])
        self.assertIn('not proof of getter equivalence', recorder)
        users = [p.name for p in (ROOT / 'Styx').rglob('*.cs')
                 if 'WowBPlayerState70938.' in p.read_text()]
        self.assertEqual(users, ['WowBCombatResearch.cs'])
        project = read('Tools/WowBReaderTests/WowBReaderTests.csproj')
        self.assertIn('WowBPlayerState70938.cs', project)
        self.assertIn('PlayerState70938Tests.Run()', read('Tools/WowBReaderTests/Program.cs'))

if __name__ == '__main__': unittest.main()

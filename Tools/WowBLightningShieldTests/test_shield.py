"""Static guard/wiring checks only; C# fixture and live game are separate."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]
def read(p): return (R / p).read_text(encoding='utf-8-sig')
B = read('Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs')
A = read('Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs')
G = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
F = read('Tools/WowBBuffTests/Fixture.cs.txt')

class LightningShield(unittest.TestCase):
    def test_exact_rank_build_and_unknown(self):
        self.assertIn('reader.Uses70862Features && WowBLightningShield.Rank(id) != 0', B)
        for rank in ('324 => 1', '325 => 2', '905 => 3', '945 => 4', '8134 => 5', '10431 => 6', '10432 => 7'):
            self.assertIn(rank, B)
        for cost in ('324 => 60U', '325 => 80U', '905 => 125U', '945 => 180U', '8134 => 240U', '10431 => 305U', '10432 => 370U'):
            self.assertIn(cost, B)
        self.assertIn('WowBLightningShield.Rank((int)id)', A)
        self.assertIn('external == null ? (int?)null', B)
        self.assertIn('ids.Count == count', A)
        self.assertIn('!ids.Add(id)', A)
        self.assertIn('ReadExternalCandidate(memory, player.UnitAddress', A)

    def test_combat_and_quiet_wiring(self):
        combat = B[B.index('internal bool TryApplyInCombat('):B.index('    // Returns true only if at least one buff key')]
        for guard in ('ClickAimSettings.UseBuffsInCombat', 'ShieldSupported(reader, action.Id)',
                      'shield.HasValue && shield.Value < 0', 'TotemManaEnough(reader, expectedPlayer, action)',
                      'ReadShield(memory, reader, expectedPlayer)', 'CombatPlayerSafe(',
                      'SameSlot(', 'targetSafe()', 'CombatRetryMs', '_schedule.RecordAttempt'):
            self.assertIn(guard, combat)
        self.assertNotIn('0x70', combat)
        self.assertIn('Buffs.TryApplyInCombat(', G)
        quiet = B[B.index('internal bool TryApply('):B.index('// 70707 research candidate')]
        self.assertGreaterEqual(quiet.count('ReadShield(memory, reader, player)'), 2)
        for label in ('rank 3 active is not recast', 'rank 3 below estimated cost withheld',
                      'rank 3 retry waits 10 seconds', 'rank 3 appearing before input withholds cast',
                      'rank 3 STOP during read withheld', 'rank 3 unsupported on older builds'):
            self.assertIn(label, F)

if __name__ == '__main__': unittest.main()

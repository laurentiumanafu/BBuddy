"""Source-wiring regressions; not execution of C# or a live combat test."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
s = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
def part(a, b):
    p = s.index(a)
    return s[p:s.index(b, p)]
class RemoteFallbackWiring(unittest.TestCase):
    def test_fallback_only_on_current_validated_target(self):
        select = part('    private static bool TrySelectedNearbyDefence(', '    private static bool TryTakeDefenceHandoff(')
        for guard in ('pass < 2', 'reader.ReadTargetGuid() != currentTarget.Guid',
                      'currentTarget.Guid != firstTarget.Guid', 'currentTarget.UnitAddress != firstTarget.UnitAddress',
                      'currentTarget.RecordAddress != firstTarget.RecordAddress', '!TryReadGatherCombat',
                      'Experimental70707BarSafe', 'currentTarget.IsLocalPlayer', 'currentTarget.HasPlayerVtable',
                      'Unreachable(currentTarget.Guid)', 'distance > maxDistance',
                      'selectedRemoteFallback && (!reader.IsExperimental70938 || distance <= GatherDefenceRadius)',
                      'RemoteDefenceBlocked(reader, snap, currentPlayer, currentTarget, selectedRemoteFallback)'):
            self.assertIn(guard, select)
    def test_priorities_and_bounded_chase(self):
        defend = part('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        self.assertLess(defend.index('preferred = PreferredDefenceCandidates'), defend.index('_nearDefenceExhausted && reader.IsExperimental70938'))
        self.assertIn('TAB-selected remote fallback', defend)
        self.assertIn('attacker unknown', defend)
        self.assertIn('DefendRemote(hwnd, reader, remoteSnap, remotePlayer, out engaged, true)', defend)
        remote = part('    private static string DefendRemote(', '    // Revalidate the remote fight')
        for guard in ('Math.Min(candidates.Count, 3)', '_remoteDefenceCursor % candidates.Count',
                      'RemoteDefenceRetryMs', 'selected.UnitAddress != candidate.UnitAddress',
                      'selected.RecordAddress != candidate.RecordAddress', 'WowBDefenceHandoff.MayFight(candidate.Guid, selected.Guid',
                      'RemoteDefenceBlocked(reader, after, player, selected, true)',
                      'FightDefenceWithHandoffs(hwnd, reader, true, player, selected, true, selectedRemoteFallback)'):
            self.assertIn(guard, remote)
        block = part('    private static bool RemoteDefenceBlocked(', '    // On 70862 a TAB')
        for guard in ('!reader.TryGetUnitFields', 'DefencePreferred(reader, unit)', 'if (!double.IsFinite(d)) return true'):
            self.assertIn(guard, block)
        movement = part('    private static bool RemoteDefenceMovementAllowed(', '    // A damaged voluntary')
        for guard in ('selected.Guid != expected.Guid', 'selected.UnitAddress != expected.UnitAddress',
                      'selected.RecordAddress != expected.RecordAddress', 'TryReadGatherCombat', 'RouteRunner.StopRequested',
                      'distance > RemoteDefenceRadius + 3.0', 'RemoteDefenceBlocked(reader, current, player, selected, selectedRemoteFallback)'):
            self.assertIn(guard, movement)
        fight = part('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('RemoteDefenceMovementAllowed(reader, fightMemory, expectedPull, selectedRemoteFallback)', fight)
        self.assertIn('combatMoveAllowed: defensiveMovement', fight)
        chase = part('    private static bool ChaseStep(', "    // A pool unit's position")
        self.assertIn('RouteRunner.TryCombatMeshPath', chase)
        self.assertGreaterEqual(chase.count('combatMoveAllowed != null && !combatMoveAllowed()'), 2)
        self.assertIn('no complete validated mesh path', chase)
if __name__ == '__main__': unittest.main()

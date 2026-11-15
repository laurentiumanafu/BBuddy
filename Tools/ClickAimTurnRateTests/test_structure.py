"""Source-wiring checks only; production policy fixture requires .NET."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
P = (ROOT/'Styx/Logic/Pathing/ClickAim/ClickAimTurnRateLearning.cs').read_text()
S = (ROOT/'Styx/Logic/Pathing/ClickAim/ClickAimKeySteer.cs').read_text()
M = (ROOT/'Styx/Logic/Pathing/ClickAim/ClickAimMover.cs').read_text()
G = (ROOT/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()

def section(text, first, last):
    start = text.index(first)
    return text[start:text.index(last, start)]

class TurnRateStructure(unittest.TestCase):
    def test_estimator_has_consensus_sanity_and_identity(self):
        for s in ('ReferenceEquals(_memory, memory)', 'ReferenceEquals(_reader, reader)',
                  '_window == window', '_generation == generation', '_playerRecord == playerRecord',
                  '_playerGuid == playerGuid', '_basePos == posRate',
                  'durationMs < 40', 'durationMs > 600', 'displacementYd > 0.2',
                  'Math.Abs(deltaDeg) > 90', 'measured < baseline * 0.25',
                  'measured > baseline * 4.0', 'candidate == 0',
                  'baseline * 0.5', 'baseline * 2.0', 'ForgetCandidate(positive)'):
            self.assertIn(s, P)
    def test_steer_uses_only_in_place_70938_samples(self):
        steer = section(S, 'public static bool Steer(', 'public static bool Face(')
        for s in ('sampleReader?.IsExperimental70938 == true', 'sampleMemory.WindowHandle == hwnd && !_keyDown',
                  'ClickAimAbort.Generation', 'turnLearning!.Bind(', 'turnLearning.Rate(turnUp, rate)',
                  'StableTurnSample(sampleReader!, samplePlayer', 'StableTurnSample(sampleReader, endPlayer',
                  'ObjectManager.WowB == sampleReader', 'ObjectManager.Wow == sampleMemory',
                  'TemporaryCacheState(false)', 'endPlayer.Guid == samplePlayer.Guid',
                  'finalPlayer.RecordPtr == samplePlayer.RecordPtr',
                  'ClickAimMath.WrapDeg((endFacing - sampleFacing)', 'Math.Sqrt(dx * dx + dy * dy + dz * dz)',
                  'turnLearning!.ForgetCandidate(turnUp)', 'TapMovementKey'):
            self.assertIn(s, steer)
        self.assertIn('_turnLearning))', M)
        self.assertIn('watch.TurnLearning))', G)
        self.assertNotIn('_turnPosRate =', steer)
        self.assertNotIn('_turnNegRate =', steer)
        sample = section(S, 'private static bool StableTurnSample(', 'public static bool Steer(')
        self.assertEqual(2, sample.count('reader.TryGetObjectTransform('))
        self.assertIn('float.IsFinite(z1)', sample)
        self.assertIn('float.IsFinite(f2)', sample)
        self.assertIn('Math.Sqrt(dx * dx + dy * dy + dz * dz) > 0.2', sample)
    def test_cast_power_observation_not_fabricated(self):
        block = section(G, 'uint manaAfter = 0;', '/* ---- 4. melee:')
        for s in ('bool manaObserved = false;', 'manaObserved = false;', 'me3.Guid == me.Guid',
                  'me3.UnitAddress == me.UnitAddress', 'me3.RecordAddress == me.RecordAddress',
                  'spentMana = manaObserved && manaAfter < manaBefore;',
                  'if (spentMana) LearnCastCost', 'damage source and swing state unverified; cast success unverified'):
            self.assertIn(s, block)
        self.assertEqual(block.count('LearnCastCost('), 1)
        self.assertNotIn('does damage:', block)
    def test_hot_path_waits_stop_without_further_action(self):
        self.assertNotIn('Thread.Sleep(', G)
        block = section(G, 'int settleMs = attacked ? AttackSettleMs', 'WowBWorldSnapshot? afterSwing;')
        self.assertIn('ClickAimAbort.Wait(settleMs)', block)
        self.assertIn('if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return "stopped";', block)
        verify = section(G, 'uint manaAfter = 0;', 'if (spentMana || hurtMob)\n                {')
        self.assertIn('if (ClickAimAbort.Wait(250) || _stop || RouteRunner.StopRequested) return "stopped";', verify)

if __name__ == '__main__': unittest.main()

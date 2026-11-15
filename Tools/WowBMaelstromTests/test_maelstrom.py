"""Offline synthetic and source checks; neither runs C# nor proves live behavior."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('ma', ROOT / 'Tools/analyze_maelstrom_candidate.py')
ma = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ma)
READ = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBMaelstromWeapon.cs').read_text(encoding='utf-8-sig')
ROT = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimClassRotation.cs').read_text(encoding='utf-8-sig')
FIGHT = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')

class Maelstrom(unittest.TestCase):
    def test_reader_fails_closed_and_rechecks(self):
        for text in ('CandidateId = 408505', 'StackOffset = 0x104', 'DurationOffset = 0xF4',
                     'ExpiryOffset = 0xF8', 'ClockOffset = 0x1C10', 'stack == 5',
                     'duration == 30000', 'remaining > 0 && remaining <= 30000',
                     'ids.Count == count', '!ids.Add(id)', 'External70938MaxSlots',
                     'count == BitConverter.ToUInt32(after, 0)', 'ReadExternalCandidate(memory',
                     'unchecked(lastClock - firstClock) <= 1000', '!reader.IsExperimental70938',
                     'AttemptBackoffMs = 5000'):
            self.assertIn(text, READ)
    def test_rotation_excludes_bolt_from_ordinary_paths(self):
        self.assertIn('plan.MaelstromBolt = Find(bar, new[] { "Lightning Bolt" }, ClickAimSpellRole.Damage)', ROT)
        self.assertIn('if (!IsLightningBolt(action)) withoutBolt.Add(action)', ROT)
        self.assertIn('bar = withoutBolt;', ROT)
        self.assertIn('if (experimental70938)', ROT)
        fight = FIGHT[FIGHT.index('private static string Fight('):]
        self.assertEqual(fight.count('MaelstromBoltAllowed(hwnd, reader, fightMemory'), 2)
        self.assertIn('(maelstromCast || (!melee && !shockMelee) || retryCast)', fight)
        retry = fight[fight.index('bool retryClass ='):fight.index('bool canCast =')]
        self.assertIn('!ClickAimClassRotation.IsLightningBolt(damage)', retry)
        self.assertIn('!maelstromCast', retry)
        self.assertIn('if (supplemental || maelstromCast)', fight)
        self.assertIn('if (maelstromCast) _maelstromBoltNextAttempt', fight)
        before = fight.index('if (maelstromCast && (bolt == null')
        key = fight.index('ClickAimInput.TapKey(hwnd, castDamage!.KeyVk, 40)', before)
        self.assertLess(before, key)
        self.assertIn('!maelstromCast && !supplemental', fight)
        guard = FIGHT[FIGHT.index('private static bool MaelstromBoltAllowed'):FIGHT.index('private static string Fight(')]
        for text in ('ClickAimAbort.Requested', 'RouteRunner.StopRequested', '_vendorDue',
                     'ExperimentalGrindActive', 'ObjectManager.Wow != memory',
                     'TraceIdentity70707', 'FightAttackInputAllowed', 'TryGetLocalPlayer',
                     'target.RecordAddress != expectedTarget.RecordAddress', 'self.Power < minimumPower',
                     'TryReadGhostFlagCandidate', 'DefenceFieldsValid', 'distance > bolt.RangeYd',
                     'WowBMaelstromWeapon.ReadFive', 'current.Raw == bolt.Raw', 'mapAfter == map'):
            self.assertIn(text, guard)
        self.assertGreaterEqual(guard.count('FightAttackInputAllowed'), 2)
    def test_offline_reconstruction_invalidates_on_unknown(self):
        chunk = bytearray(256); struct.pack_into('<I', chunk, 0x60, 1)
        clock = bytearray(256); struct.pack_into('<I', clock, 0x10, 1000)
        raw = bytearray(ma.STRIDE * 2)
        struct.pack_into('<I', raw, 0xe8, ma.IDENT)
        struct.pack_into('<I', raw, 0xf4, 30000)
        struct.pack_into('<I', raw, 0xf8, 30000)
        struct.pack_into('<I', raw, 0x104, 4)
        text = ('70938 COMBAT RESEARCH v1: fixture\n'
                'sample=0 startMs=0\nplayer+0x1A00 baseline ' + chunk.hex().upper() + '\n'
                'player+0x1C00 baseline ' + clock.hex().upper() + '\n'
                'external baseline pointer=0x100000 length=2 capacity=24 bytes=' + raw.hex().upper() + '\n'
                'sample=1 startMs=250\nexternal changes +0x0104:00000004>00000005\n'
                'sample=2 startMs=500\nexternal unavailable: unreadable\n'
                'sample=3 startMs=750\nexternal changes +0x0104:00000005>00000004\n')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'trace.txt'; path.write_text(text)
            events = ma.read(path)
        self.assertEqual([v[1] if v else None for _, v in events], [4, 5, None])

if __name__ == '__main__': unittest.main()

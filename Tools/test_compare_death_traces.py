#!/usr/bin/env python3
import tempfile
import unittest
from pathlib import Path
from compare_death_traces import compare, read_trace


class DeathTraceTests(unittest.TestCase):
    def write(self, folder, name, label, hp, ghost=None, identity='0x' + 'A' * 32):
        path = Path(folder) / name
        health = bytearray(256)
        health[0x28:0x30] = hp.to_bytes(8, 'little')
        health[0x30:0x38] = (202).to_bytes(8, 'little')
        lines = ['70707 COMBAT RESEARCH v1: test', 'UTC x; label=' + label,
                 'player=' + identity + ' unit=x', 'sample=0 startMs=0 endMs=0 utc=x',
                 'player+0x1E00 baseline ' + health.hex().upper()]
        if ghost is not None:
            chunk = bytearray(256)
            chunk[0xD8] = ghost
            lines.append('player+0x2600 baseline ' + chunk.hex().upper())
        lines += ['sample=1 startMs=250 endMs=251 utc=x',
                  'player+0x2600 baseline ' + (bytes([0xFF]) * 256).hex().upper(),
                  'end: completed; samples=2']
        path.write_text('\n'.join(lines), encoding='utf-8')
        return path

    def test_sample_zero_and_missing_candidates(self):
        with tempfile.TemporaryDirectory() as d:
            alive = self.write(d, 'a', 'alive', 232)
            dead = self.write(d, 'd', 'dead', 0, 0)
            ghost = self.write(d, 'g', 'ghost', 1, 0x10)
            self.assertIsNone(read_trace(alive).word(0x26D8, 1))
            text = compare([alive, dead, ghost])
            self.assertIn('0x00000000000000E8 / 0x0000000000000000 / 0x0000000000000001', text)
            self.assertIn('not recorded / 0x00 / 0x10', text)
            self.assertIn('2 sample(s), completed', text)

    def test_identity_mismatch_and_invalid_baseline(self):
        with tempfile.TemporaryDirectory() as d:
            a = self.write(d, 'a', 'alive', 10)
            b = self.write(d, 'b', 'dead', 0)
            c = self.write(d, 'c', 'ghost', 1, identity='0x' + 'B' * 32)
            with self.assertRaisesRegex(ValueError, 'GUIDs differ'):
                compare([a, b, c])
            a.write_text('70707 COMBAT RESEARCH v1: test\nUTC x; label=x\nplayer=' + '0x' + 'A' * 32 +
                         ' unit=x\nsample=0 startMs=0 endMs=0 utc=x\nplayer+0x2800 baseline ' + ('00' * 256))
            with self.assertRaisesRegex(ValueError, 'invalid'):
                read_trace(a)


if __name__ == '__main__':
    unittest.main()

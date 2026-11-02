#!/usr/bin/env python3
import tempfile
import unittest
from pathlib import Path
from analyze_combat_trace import read_trace


class TraceTests(unittest.TestCase):
    def test_samples_and_spell_id(self):
        baseline = bytearray(256)
        baseline[12:16] = (133).to_bytes(4, 'little')
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'trace.txt'
            path.write_text('70707 COMBAT RESEARCH v1: test\n'
                'sample=0 startMs=0 endMs=3 utc=2026-01-01T00:00:00Z\n'
                'player+0x0100 baseline ' + baseline.hex().upper() + '\n'
                'sample=1 startMs=5000 endMs=5001 utc=2026-01-01T00:00:05Z\n'
                'player changes +0x0110:00000000>00000085\n'
                'target+0x0200 unreadable\n'
                'end: completed; samples=2\n', encoding='utf-8')
            changes, matches, count, status = read_trace(path, 133)
            self.assertEqual(count, 2)
            self.assertEqual(status, 'completed; samples=2')
            self.assertEqual(matches, [('player', 0x10C, 0.0)])
            self.assertEqual(changes[('player', 0x110)], [(5.0, 0, 133)])

    def test_bad_header_and_out_of_bounds(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'bad.txt'
            path.write_text('other\n', encoding='utf-8')
            with self.assertRaises(ValueError): read_trace(path)
            path.write_text('70707 COMBAT RESEARCH v1: test\nsample=0 startMs=0 endMs=0 utc=x\n'
                            'player changes +0x2400:00000000>00000001\n', encoding='utf-8')
            with self.assertRaises(ValueError): read_trace(path)


if __name__ == '__main__':
    unittest.main()

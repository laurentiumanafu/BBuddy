"""Tests for offline reconstruction; synthetic data only."""
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import analyze_external_aura_trace as a

class Analyzer(unittest.TestCase):
    def make_trace(self, lines):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        path = Path(folder.name) / 'trace.txt'
        path.write_text('70862 COMBAT RESEARCH v1: raw\n' + '\n'.join(lines) + '\n', encoding='utf-8')
        return path

    def test_reconstruct_and_recover_from_unavailable(self):
        data = bytearray(a.STRIDE * 2)
        struct.pack_into('<I', data, 0xE8, 324)
        base = f'external baseline pointer=0x100000 length=2 capacity=8 bytes={data.hex().upper()}'
        path = self.make_trace(['sample=0 startMs=0', base, 'sample=1 startMs=250',
            'external changes +0x00E8:00000144>00001FE0', 'sample=2 startMs=500',
            'external unavailable: failed; previous baseline discarded', 'sample=3 startMs=750',
            'external changes +0x00E8:00001FE0>00000000', 'sample=4 startMs=1000', base,
            'end: completed; samples=5'])
        count, status, events = a.read_trace(path)
        self.assertEqual(count, 5)
        self.assertIn('completed', status)
        self.assertEqual(events[0][2][0][1], 324)
        self.assertEqual(events[1][2][0][1], 8160)
        self.assertEqual(events[2][1], 'unavailable')
        self.assertIsNone(events[3][2])
        self.assertEqual(events[4][2][0][1], 324)

    def test_70888_grown_candidate_and_bounds(self):
        data = bytearray(a.STRIDE * 28)
        struct.pack_into('<I', data, 27 * a.STRIDE + 0xE8, 10491)
        base = f'external baseline pointer=0x100000 length=28 capacity=36 bytes={data.hex().upper()}'
        path = self.make_trace(['sample=0 startMs=0', base, 'sample=1 startMs=250',
            f'external changes +0x{27 * a.STRIDE + 0xE8:04X}:000028FB>00000000'])
        original = path.read_text()
        with self.assertRaises(ValueError): a.read_trace(path)  # 70862 retains old bounds
        path.write_text(original.replace('70862 COMBAT', '70888 COMBAT'))
        events = a.read_trace(path)[2]
        self.assertEqual(events[0][2][0][1], 10491)
        self.assertEqual(events[1][2], ())
        path.write_text(original.replace('70862 COMBAT', '70888 COMBAT').replace('capacity=36', 'capacity=37'))
        with self.assertRaises(ValueError): a.read_trace(path)
        path.write_text('70888 COMBAT RESEARCH v1: raw\nsample=0 startMs=0\n' +
                        'external baseline pointer=0x100000 length=29 capacity=36 bytes=' + '00' * (29 * a.STRIDE))
        with self.assertRaises(ValueError): a.read_trace(path)

    def test_mismatch_invalidates_not_absent(self):
        data = bytearray(a.STRIDE)
        path = self.make_trace(['sample=0 startMs=0', f'external baseline pointer=0x100000 length=1 capacity=8 bytes={data.hex().upper()}',
            'sample=1 startMs=250', 'external changes +0x00E8:00000001>00000000'])
        events = a.read_trace(path)[2]
        self.assertEqual(events[-1][1], 'inconsistent change; baseline invalidated')
        self.assertIsNone(events[-1][2])

    def test_rejects_oversized_and_bad_header(self):
        path = self.make_trace(['sample=0 startMs=0', 'external baseline pointer=0x100000 length=25 capacity=32 bytes=00'])
        with self.assertRaises(ValueError): a.read_trace(path)
        path.write_text('70707 COMBAT RESEARCH v1: other build\n')
        with self.assertRaises(ValueError): a.read_trace(path)

if __name__ == '__main__': unittest.main()

import struct
import tempfile
import unittest
from pathlib import Path
from analyze_70707_aura_trace import read_trace, inspect, SLOT_ID, STRIDE, CLOCK


def chunk(base, values):
    raw = bytearray(256)
    for offset, value in values.items():
        if base <= offset < base + 256:
            struct.pack_into('<I', raw, offset - base, value)
    return f'player+0x{base:04X} baseline {raw.hex().upper()}\n'


class AuraTraceTests(unittest.TestCase):
    def test_two_slots_and_wraparound(self):
        a = SLOT_ID + 7 * STRIDE
        b = SLOT_ID + 8 * STRIDE
        words = {CLOCK: 0xFFFFFF00, a: 168, a + 12: 1800000, a + 16: 0xF0,
                 b: 1459, b + 12: 3600000, b + 16: 0x1F0}
        result = inspect(words, set())
        self.assertEqual(result[7], (168, 1800000, 0xF0, 496))
        self.assertEqual(result[8], (1459, 3600000, 0x1F0, 752))
        self.assertEqual(list(inspect(words, {168})), [7])
        del words[CLOCK]
        self.assertEqual(inspect(words, set()), {})

    def test_event_and_unreadable_not_absent(self):
        off = SLOT_ID + 7 * STRIDE
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'trace.txt'
            p.write_text('70707 COMBAT RESEARCH v1: synthetic\n'
                         'sample=0 startMs=0 endMs=0 utc=x\n'
                         + chunk(0xF00, {}) + chunk(0x1C00, {CLOCK: 100})
                         + 'sample=1 startMs=5000 endMs=5000 utc=x\n'
                         + f'player changes +0x{off:04X}:00000000>000000A8 '
                         + f'+0x{off+12:04X}:00000000>001B7740 +0x{off+16:04X}:00000000>001B77A4\n'
                         + 'sample=2 startMs=5500 endMs=5500 utc=x\n'
                         + 'player+0x0F00 unreadable\n'
                         + 'sample=3 startMs=6000 endMs=6000 utc=x\n'
                         + chunk(0xF00, {off: 168, off+12: 1800000, off+16: 1800100})
                         + 'end: synthetic\n', encoding='utf-8')
            count, status, timeline = read_trace(p, {168})
            self.assertEqual(count, 4)
            self.assertEqual(status, 'synthetic')
            self.assertEqual([at for at, _ in timeline], [0, 5000, 5500, 6000])
            self.assertEqual(timeline[1][1][7][3], 1800000)
            self.assertEqual(timeline[2][1], {})
            self.assertIn(7, timeline[3][1])

    def test_observed_mage_timing_values_as_synthetic_words(self):
        frost = SLOT_ID + 7 * STRIDE
        arcane = SLOT_ID + 8 * STRIDE
        frost_words = {CLOCK: 0x00E07032, frost: 168, frost + 12: 1800000, frost + 16: 0x00FBE772}
        self.assertEqual(inspect(frost_words, {168})[7][3], 1800000)
        both_words = {CLOCK: 0x00E219EE, frost: 168, frost + 12: 1800000, frost + 16: 0x00FC050A,
                      arcane: 1459, arcane + 12: 3600000, arcane + 16: 0x0118A0D9}
        result = inspect(both_words, {168, 1459})
        self.assertEqual(result[7][3], 1698588)
        self.assertEqual(result[8][3], 3573483)

    def test_inconsistent_old_value_invalidates_chunk(self):
        off = SLOT_ID + 7 * STRIDE
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'trace.txt'
            p.write_text('70707 COMBAT RESEARCH v1: synthetic\n'
                         'sample=0 startMs=0 endMs=0 utc=x\n'
                         + chunk(0xF00, {off: 168, off+12: 1800000, off+16: 1800100})
                         + chunk(0x1C00, {CLOCK: 100})
                         + 'sample=1 startMs=250 endMs=250 utc=x\n'
                         + f'player changes +0x{off:04X}:000000AB>00000000\n', encoding='utf-8')
            _, status, timeline = read_trace(p, set())
            self.assertIsNone(status)
            self.assertIn(7, timeline[0][1])
            self.assertEqual(timeline[1][1], {})


if __name__ == '__main__':
    unittest.main()

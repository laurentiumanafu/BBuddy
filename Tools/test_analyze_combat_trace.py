#!/usr/bin/env python3
import tempfile
import unittest
from pathlib import Path
from analyze_combat_trace import read_trace, candidate_events, trace_label, player_offsets_not_in_control


class TraceTests(unittest.TestCase):
    def test_samples_and_spell_id(self):
        baseline = bytearray(256)
        baseline[12:16] = (133).to_bytes(4, 'little')
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'trace.txt'
            path.write_text('70707 COMBAT RESEARCH v1: test\n'
                'UTC 2026-01-01T00:00:00Z; label=Example\n'
                'sample=0 startMs=0 endMs=3 utc=2026-01-01T00:00:00Z\n'
                'player+0x0100 baseline ' + baseline.hex().upper() + '\n'
                'sample=1 startMs=5000 endMs=5001 utc=2026-01-01T00:00:05Z\n'
                'player changes +0x0110:00000000>00000085\n'
                'target+0x0200 unreadable\n'
                'end: completed; samples=2\n', encoding='utf-8')
            changes, matches, count, status = read_trace(path, 133)
            self.assertEqual(trace_label(path), 'Example')
            self.assertEqual(player_offsets_not_in_control(changes, {('target', 0x110): []}), [0x110])
            self.assertEqual(player_offsets_not_in_control(changes, {('player', 0x110): []}), [])
            self.assertEqual(count, 2)
            self.assertEqual(status, 'completed; samples=2')
            self.assertEqual(matches, [('player', 0x10C, 0.0)])
            self.assertEqual(changes[('player', 0x110)], [(5.0, 0, 133)])

    def test_correlated_candidates_require_matching_fields(self):
        changes = {
            ('player', 0x3c0): [(5.75, 0, 133), (7.0, 133, 0)],
            ('player', 0x3ec): [(5.75, 0, 133), (7.0, 133, 0)],
            ('player', 0x3e8): [(5.75, 0xffffffff, 0)],
            ('player', 0x418): [(5.75, 0, 10000), (7.0, 10000, 0)],
            ('player', 0x41c): [(5.75, 0, 11200), (7.0, 11200, 0)],
            ('target', 0x6f0): [(7.75, 0, 133), (11.75, 133, 0)],
            ('target', 0x6fc): [(7.75, 0, 4000), (11.75, 4000, 0)],
            ('target', 0x700): [(7.75, 0, 16000), (11.75, 16000, 0)],
        }
        events = candidate_events(changes)
        self.assertEqual(len(events), 4)
        self.assertIn('id=133', events[0][1])
        self.assertIn('1200', events[0][1])
        self.assertIn('+0x03E8 incremented', events[0][1])
        self.assertIn('4000', events[2][1])
        del changes[('player', 0x3ec)]
        del changes[('target', 0x700)]
        self.assertEqual(candidate_events(changes), [])
        changes[('player', 0x3ec)] = [(5.75, 0, 133)]
        changes[('player', 0x41c)] = [(5.75, 0, 10000)]
        self.assertEqual(candidate_events(changes), [])  # zero-length timing

    def test_split_sample_candidate_requires_matching_bounded_fields(self):
        changes = {
            ('player', 0x3c0): [(13.52, 0, 133), (14.77, 133, 0)],
            ('player', 0x3ec): [(13.77, 0, 133), (14.77, 133, 0)],
            ('player', 0x418): [(13.77, 0, 10000), (14.77, 10000, 0)],
            ('player', 0x41c): [(13.77, 0, 11500), (14.77, 11500, 0)],
        }
        events = candidate_events(changes)
        self.assertEqual(len(events), 2)
        self.assertIn('possible split-sample', events[0][1])
        changes[('player', 0x3c0)].insert(1, (13.6, 133, 0))
        self.assertFalse(any('split-sample' in text for _, text in candidate_events(changes)))
        changes[('player', 0x3c0)].pop(1)
        changes[('player', 0x3ec)][0] = (14.03, 0, 133)
        self.assertFalse(any('split-sample' in text for _, text in candidate_events(changes)))

    def test_alternate_cluster_is_not_called_a_channel(self):
        changes = {
            ('player', 0x424): [(2.0, 0, 403)],
            ('player', 0x430): [(2.0, 0, 10000)],
            ('player', 0x434): [(2.0, 0, 11500)],
            ('player', 0x3e8): [(2.0, 5, 6)],
        }
        events = candidate_events(changes)
        self.assertEqual(len(events), 1)
        self.assertIn('meaning unknown', events[0][1])
        self.assertNotIn('channel', events[0][1])
        changes[('player', 0x3e8)] = [(2.0, 5, 7)]
        self.assertEqual(candidate_events(changes), [])
        del changes[('player', 0x3e8)]
        self.assertEqual(candidate_events(changes), [])

    def test_new_registered_build_headers(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'trace.txt'
            for build in (70707, 70755, 70790, 70862, 70888, 70938):
                path.write_text(f'{build} COMBAT RESEARCH v1: test\nsample=0 startMs=0 endMs=0 utc=x\nend: completed; samples=1\n', encoding='utf-8')
                _, _, count, status = read_trace(path)
                self.assertEqual((count, status), (1, 'completed; samples=1'))
            path.write_text('99999 COMBAT RESEARCH v1: test\n', encoding='utf-8')
            with self.assertRaises(ValueError): read_trace(path)

    def test_bad_header_and_out_of_bounds(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'bad.txt'
            path.write_text('other\n', encoding='utf-8')
            with self.assertRaises(ValueError): read_trace(path)
            path.write_text('70707 COMBAT RESEARCH v1: test\nsample=0 startMs=0 endMs=0 utc=x\n'
                            'player changes +0x26D8:00000000>00000010\n', encoding='utf-8')
            changes, _, _, _ = read_trace(path)
            self.assertIn(('player', 0x26D8), changes)
            path.write_text('70707 COMBAT RESEARCH v1: test\nsample=0 startMs=0 endMs=0 utc=x\n'
                            'player changes +0x2800:00000000>00000001\n', encoding='utf-8')
            with self.assertRaises(ValueError): read_trace(path)


if __name__ == '__main__':
    unittest.main()

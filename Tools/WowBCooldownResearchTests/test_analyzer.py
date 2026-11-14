"""Offline parser tests; synthetic traces do not validate the game layout."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('cooldown_analysis', ROOT / 'Tools/analyze_cooldown_candidate.py')
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

class AnalyzerTests(unittest.TestCase):
    def test_nodes_and_unknown(self):
        values = {0x10: 17364, 0x1C: 100, 0x20: 8000, 0x28: 100, 0x34: 100}
        node = ' '.join(f'{i:02X}={values.get(i, 0):08X}' for i in range(0x10, 0x41, 4))
        trace = ('70938 COMBAT RESEARCH v1: test\n'
                 'sample=0 startMs=0 endMs=1 utc=x\n'
                 f'cooldown candidate stable (research only; not readiness):  state0 head=0x10000 [0x10000 {node} next=0x1] end=0x1 state1 head=0x1 end=0x1\n'
                 'sample=1 startMs=250 endMs=251 utc=x\n'
                 'cooldown candidate unknown: unreadable\n')
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'trace.txt'; path.write_text(trace)
            result = mod.read_trace(path)
        self.assertEqual(result[0]['lists'][0][0][0], 17364)
        self.assertEqual(result[0]['lists'][0][0][3], 8000)
        self.assertIsNone(result[1]['lists'])
        self.assertIn('unknown', mod.format_state(result[1]['lists']))

    def test_malformed_is_unknown(self):
        self.assertIsNone(mod.parse_nodes(' state0 head=0x1 end=0x1 state1 head=0x2 [0x2 10=000043D4] end=0x1'))

    def test_early_transition_is_narrow_and_unknown_breaks_chain(self):
        # ID, arg, start, duration, category, start, duration, flag,
        # start, category, duration, field: only +0x20 changes.
        active = (17364, 0, 100, 8000, 0, 100, 0, 0, 100, 0, 0, 0)
        clear = (17364, 0, 100, 0, 0, 100, 0, 0, 100, 0, 0, 0)
        gcd = (17364, 0, 100, 0, 0, 100, 0, 0, 100, 133, 1500, 100)
        def sample(ms, nodes):
            return {'ms': ms, 'lists': None if nodes is None else (tuple(nodes), ())}
        self.assertEqual(mod.possible_duration_clears([sample(2500, [active, gcd]), sample(2750, [clear, gcd])]),
                         [(2500, 2750, 0, 100)])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, None), sample(500, [clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(1250, [clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active, active]), sample(250, [clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, [clear, clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active, clear]), sample(250, [clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, [active, clear])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, [])]), [])
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, [gcd])]), [])
        changed_start = list(clear); changed_start[2] = 101
        self.assertEqual(mod.possible_duration_clears([sample(0, [active]), sample(250, [tuple(changed_start)])]), [])

if __name__ == '__main__': unittest.main()

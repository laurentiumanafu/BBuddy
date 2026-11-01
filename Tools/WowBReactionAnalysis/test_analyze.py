import base64
import json
import tempfile
import unittest
from analyze import LABELS, CHUNK, SIGNATURE, correlations, load, template_observations


def sample(label, i, value):
    raw = value.to_bytes(4, 'little') + bytes(CHUNK - 4)
    return {'label': label, 'guid': label + str(i), 'hint': i + 1, 'chunks': {0: raw}}


class CorrelationTests(unittest.TestCase):
    def setUp(self):
        self.samples = [sample(label, i, n + 10) for n, label in enumerate(LABELS) for i in range(3)]

    def test_disjoint_candidate_and_incomplete_chunk(self):
        counts, hints, conflicts, words, bits = correlations(self.samples)
        self.assertEqual([counts[x] for x in LABELS], [3, 3, 3])
        self.assertFalse(conflicts)
        self.assertEqual(words[0][1], 0)
        self.assertTrue(any(offset == 0 for offset, bit, groups in bits))
        self.samples[-1]['chunks'] = {}
        self.assertEqual(correlations(self.samples)[3], [])

    def test_conflicting_labels_are_excluded(self):
        duplicate = dict(self.samples[0], label=LABELS[1])
        counts, hints, conflicts, words, bits = correlations(self.samples + [duplicate])
        self.assertEqual(len(conflicts), 1)
        self.assertEqual(counts[LABELS[0]], 2)
        self.assertEqual(words, [])

    def test_repeats_not_independent(self):
        counts, hints, conflicts, words, bits = correlations(self.samples[:1] * 10)
        self.assertEqual(counts[LABELS[0]], 1)
        self.assertEqual(words, [])

    def test_load_and_reject_duplicate_chunks(self):
        obj = {'schema': 1, 'diagnosticSignature': SIGNATURE, 'label': 'friendly',
               'targetGuid': '0x' + '1' * 32, 'targetEntryHint': 4,
               'targetChunks': [{'offset': 0, 'bytes': base64.b64encode(bytes(CHUNK)).decode()}]}
        with tempfile.NamedTemporaryFile(mode='w+', encoding='utf-8') as f:
            f.write(json.dumps(obj) + '\n')
            f.flush()
            self.assertEqual(len(load(f.name)), 1)
            obj['targetChunks'].append(obj['targetChunks'][0])
            f.seek(0)
            f.truncate()
            f.write(json.dumps(obj) + '\n')
            f.flush()
            with self.assertRaisesRegex(ValueError, 'repeated chunk'):
                load(f.name)

    def test_template_observations_missing_and_zero(self):
        self.samples[0]['chunks'][0x1E00] = bytes(CHUNK)
        self.samples[1]['chunks'][0x1E00] = bytes(0x5C) + (57).to_bytes(4, 'little') + bytes(CHUNK - 0x60)
        result = template_observations(self.samples)
        self.assertEqual(result['friendly'], {0, 57})
        self.assertEqual(result['neutral_attackable'], set())

    def test_hint_diversity_required(self):
        for s in self.samples:
            s['hint'] = 1
        self.assertEqual(correlations(self.samples)[3], [])


if __name__ == '__main__':
    unittest.main()

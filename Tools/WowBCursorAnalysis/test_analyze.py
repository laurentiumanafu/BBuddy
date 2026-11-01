import unittest
from analyze import analyze


def sample(label, match=True, visible=True, fingerprint='A' * 64, guid='g'):
    return {'schema': 1, 'label': label, 'matchesSelected': match, 'cursorShowing': visible,
            'targetGuid': guid, 'cursorImage': {'fingerprint': fingerprint} if fingerprint else None}


class CursorAnalysisTests(unittest.TestCase):
    def test_excludes_hidden_unmatched_and_missing(self):
        samples = [sample('friendly'), sample('friendly', match=False),
                   sample('friendly', visible=False), sample('friendly', fingerprint=None)]
        g = analyze(samples)['friendly']
        self.assertEqual((g['samples'], g['matched'], g['hidden'], g['missing']), (4, 3, 1, 1))
        self.assertEqual(g['hashes'], {'A' * 64})
        self.assertEqual(g['targets'], {'G'})

    def test_overlap_is_retained(self):
        g = analyze([sample('friendly'), sample('neutral_attackable'), sample('aggressive', fingerprint='B' * 64)])
        self.assertTrue(g['friendly']['hashes'] & g['neutral_attackable']['hashes'])
        self.assertFalse(g['friendly']['hashes'] & g['aggressive']['hashes'])

    def test_invalid_values_rejected(self):
        a = sample('friendly')
        a['cursorImage']['fingerprint'] = 'Z' * 64
        with self.assertRaisesRegex(ValueError, 'invalid cursor fingerprint'):
            analyze([a])
        a = sample('friendly')
        a['matchesSelected'] = 1
        with self.assertRaisesRegex(ValueError, 'invalid cursor sample booleans'):
            analyze([a])


if __name__ == '__main__':
    unittest.main()

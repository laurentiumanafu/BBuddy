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

    def test_off_target_controls_do_not_require_a_mouseover_match(self):
        samples = [sample('off_target_control', match=False, fingerprint='A' * 64),
                   sample('off_target_control', match=True, fingerprint='B' * 64),
                   sample('off_target_control', visible=False, fingerprint='C' * 64),
                   sample('friendly', match=False, fingerprint='D' * 64)]
        groups = analyze(samples)
        self.assertEqual(groups['off_target_control']['hashes'], {'A' * 64, 'B' * 64})
        self.assertFalse(groups['friendly']['hashes'])

    def test_identity_grouping_requires_same_player_and_target(self):
        samples = [sample('friendly', guid='target1'), sample('off_target_control', match=False, guid='target1'),
                   sample('off_target_control', match=False, guid='target2')]
        samples[0]['playerGuid'] = samples[1]['playerGuid'] = 'player1'
        samples[2]['playerGuid'] = 'player2'
        groups = analyze(samples)
        shared = groups['friendly']['by_identity'].keys() & groups['off_target_control']['by_identity'].keys()
        self.assertEqual(shared, {('PLAYER1', 'TARGET1')})
        self.assertTrue(groups['friendly']['by_identity'][('PLAYER1', 'TARGET1')] &
                        groups['off_target_control']['by_identity'][('PLAYER1', 'TARGET1')])

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

import unittest
from analyze_posted import DAGGER_IMAGES, summarize


def obs(match, fingerprint='A' * 64, visible=True):
    hover = None if match is None else ('0x' + '1' * 32 if match else '0x' + '0' * 32)
    return {'matchesTarget': match, 'hoverGuid': hover, 'cursorShowing': visible, 'cursorFingerprint': fingerprint}


def sample():
    return {'schema': 1, 'targetGuid': '0x' + '1' * 32, 'foregroundFingerprint': 'A' * 64,
            'before': obs(True), 'away': obs(False, 'B' * 64), 'returned': obs(True),
            'candidateAwayThenTarget': True}


class PostedAnalysisTests(unittest.TestCase):
    def test_reports_independent_results(self):
        result = summarize(sample())
        self.assertTrue(result['transition'])
        self.assertTrue(result['return_matches_foreground'])
        self.assertFalse(result['away_return_equal'])
        self.assertTrue(result['same_target_pattern'])

    def test_nullable_missing_and_hidden_are_not_guessed(self):
        data = sample()
        data['away'] = obs(None, None, False)
        data['candidateAwayThenTarget'] = False
        result = summarize(data)
        self.assertFalse(result['transition'])
        self.assertIsNone(result['away_return_equal'])
        self.assertIsNone(result['same_target_pattern'])
        self.assertFalse(result['visible'][1])
        data['returned']['cursorShowing'] = False
        self.assertIsNone(summarize(data)['return_matches_foreground'])

    def test_same_target_pattern_needs_both_guid_and_cursor_transition(self):
        data = sample()
        data['away']['cursorFingerprint'] = 'A' * 64
        self.assertFalse(summarize(data)['same_target_pattern'])
        data = sample()
        data['before']['matchesTarget'] = None
        data['before']['hoverGuid'] = None
        self.assertFalse(summarize(data)['same_target_pattern'])
        data = sample()
        data['returned']['cursorFingerprint'] = 'C' * 64
        self.assertFalse(summarize(data)['same_target_pattern'])
        data = sample()
        data['away']['matchesTarget'] = True
        data['away']['hoverGuid'] = data['targetGuid']
        data['candidateAwayThenTarget'] = False
        self.assertFalse(summarize(data)['same_target_pattern'])

    def test_only_two_known_images_are_positive_hints(self):
        dark = next(h for h, name in DAGGER_IMAGES.items() if name == 'dark_dagger_hint')
        bright = next(h for h, name in DAGGER_IMAGES.items() if name == 'highlighted_dagger_hint')
        data = sample()
        data['before']['cursorFingerprint'] = dark
        data['returned']['cursorFingerprint'] = bright
        self.assertEqual(summarize(data)['dagger_hints'], ['dark_dagger_hint', 'unknown', 'highlighted_dagger_hint'])
        data['returned']['cursorShowing'] = False
        self.assertEqual(summarize(data)['dagger_hints'][-1], 'unknown')

    def test_guid_verdict_is_independently_checked(self):
        data = sample()
        data['away']['hoverGuid'] = data['targetGuid']
        with self.assertRaisesRegex(ValueError, 'disagree'):
            summarize(data)
        data = sample()
        data['before']['hoverGuid'] = None
        with self.assertRaisesRegex(ValueError, 'missing hover'):
            summarize(data)
        data = sample()
        data['targetGuid'] = '0x' + '0' * 32
        with self.assertRaisesRegex(ValueError, 'selected target'):
            summarize(data)

    def test_rejects_inconsistent_and_non_bool_values(self):
        data = sample()
        data['candidateAwayThenTarget'] = False
        with self.assertRaises(ValueError):
            summarize(data)
        data = sample()
        data['away']['matchesTarget'] = 0
        with self.assertRaises(ValueError):
            summarize(data)
        data = sample()
        data['returned']['cursorFingerprint'] = 'Z' * 64
        with self.assertRaises(ValueError):
            summarize(data)


if __name__ == '__main__':
    unittest.main()

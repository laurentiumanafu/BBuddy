import base64
import unittest
from compare_state import compare


def obj():
    return {'playerGuid': 'p', 'targetGuid': 't', 'stateProbe': {
        'reputationBytes': base64.b64encode(bytes(16000)).decode(),
        'playerCandidateBytes': base64.b64encode(bytes(256)).decode()}}


class CompareStateTests(unittest.TestCase):
    def test_changes(self):
        a, b = obj(), obj()
        data = bytearray(16000)
        data[16 * 17 + 15] = 1
        b['stateProbe']['reputationBytes'] = base64.b64encode(data).decode()
        data = bytearray(256)
        data[8 * 31] = 1
        b['stateProbe']['playerCandidateBytes'] = base64.b64encode(data).decode()
        self.assertEqual(compare(a, b), ([17], [31]))

    def test_identity_and_missing(self):
        a, b = obj(), obj()
        b['targetGuid'] = 'other'
        with self.assertRaisesRegex(ValueError, 'same player and target'):
            compare(a, b)
        b = obj()
        del b['stateProbe']
        with self.assertRaisesRegex(ValueError, 'not captured'):
            compare(a, b)

    def test_short_or_invalid_bytes(self):
        a, b = obj(), obj()
        b['stateProbe']['reputationBytes'] = base64.b64encode(bytes(32)).decode()
        with self.assertRaisesRegex(ValueError, 'wrong size'):
            compare(a, b)


if __name__ == '__main__':
    unittest.main()

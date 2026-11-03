import base64
import struct
import unittest
from compare_state import compare, unit_words


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

    def test_unit_words_and_missing_chunk(self):
        a = obj()
        self.assertIsNone(unit_words(a, 'targetChunks'))
        raw = bytearray(256)
        struct.pack_into('<QQ', raw, 0x30, 120, 125)
        struct.pack_into('<I', raw, 0x60, 0x80800)
        chunk = {'offset': 0x1E00, 'bytes': base64.b64encode(raw).decode()}
        a['targetChunks'] = [chunk]
        self.assertEqual(unit_words(a, 'targetChunks'), (120, 125, 0x80800))
        a['targetChunks'].append(chunk)
        with self.assertRaisesRegex(ValueError, 'repeated unit chunk'):
            unit_words(a, 'targetChunks')

    def test_short_or_invalid_bytes(self):
        a, b = obj(), obj()
        b['stateProbe']['reputationBytes'] = base64.b64encode(bytes(32)).decode()
        with self.assertRaisesRegex(ValueError, 'wrong size'):
            compare(a, b)


if __name__ == '__main__':
    unittest.main()

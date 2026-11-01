import base64
import struct
import unittest
from inspect_table import inspect, unpack_snapshot


def encode(data):
    return base64.b64encode(data).decode()


def fixture():
    storage = bytearray(0x100)
    struct.pack_into('<I', storage, 0x38, 7)
    struct.pack_into('<II', storage, 0x40, 1, 10)
    struct.pack_into('<H', storage, 0x48, 1)
    struct.pack_into('<Q', storage, 0xB8, 1)
    fields = b''.join(struct.pack('<6I', *v) for v in [
        (0, 0, 1, 0, 12, 0), (0, 0, 5, 12, 15, 0),
        (0, 0, 1, 27, 4, 0), (0, 8, 3, 31, 4, 0),
        (0, 0, 1, 35, 4, 0), (0, 64, 4, 39, 7, 8),
        (0, 64, 4, 46, 8, 8)])
    row = (3 | (32767 << 12) | (2 << 27) | (1 << 31) |
           (4 << 35) | (1 << 39) | (1 << 46)).to_bytes(7, 'little')
    return {'storageBytes': encode(storage), 'fieldDescriptorBytes': encode(fields),
            'rowBytes': encode(row), 'indexBytes': encode(struct.pack('<HH', 2, 0)),
            'paletteBytes': [None, None, None, encode(struct.pack('<II', 2, 9))],
            'arrayPaletteBytes': [None] * 5 + [encode(struct.pack('<16I', *range(16))), None]}


class TableInspectTests(unittest.TestCase):
    def test_packed_scalar_signed_and_array(self):
        result = inspect(fixture(), 3)
        self.assertEqual(result[:5], [3, -1, 2, 9, 4])
        self.assertEqual(result[5], {'index': 1, 'values': list(range(8, 16))})
        self.assertEqual(result[6], {'index': 1, 'values': None})

    def test_missing_and_secondary_rejected(self):
        db = fixture()
        with self.assertRaisesRegex(ValueError, 'unavailable'):
            inspect(db, 4)
        db['indexBytes'] = encode(struct.pack('<HH', 2, 0xFFFE))
        with self.assertRaisesRegex(ValueError, 'special/secondary'):
            inspect(db, 3)

    def test_bad_size_duplicate_index_and_palette_bounds(self):
        db = fixture()
        db['rowBytes'] = encode(b'bad')
        with self.assertRaisesRegex(ValueError, 'dimensions'):
            unpack_snapshot(db)
        db = fixture()
        storage = bytearray(base64.b64decode(db['storageBytes']))
        struct.pack_into('<Q', storage, 0xB8, 2)
        db['storageBytes'] = encode(storage)
        db['indexBytes'] = encode(struct.pack('<HHHH', 2, 0, 2, 0))
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            unpack_snapshot(db)
        db = fixture()
        db['arrayPaletteBytes'][5] = encode(bytes(32))
        with self.assertRaisesRegex(ValueError, 'out of bounds'):
            inspect(db, 3)

    def test_unsupported_compression_and_absent_scalar(self):
        db = fixture()
        db['paletteBytes'][3] = None
        with self.assertRaisesRegex(ValueError, 'scalar palette unavailable'):
            inspect(db, 3)
        db = fixture()
        fields = bytearray(base64.b64decode(db['fieldDescriptorBytes']))
        struct.pack_into('<I', fields, 8, 2)
        db['fieldDescriptorBytes'] = encode(fields)
        with self.assertRaisesRegex(ValueError, 'unsupported compression'):
            inspect(db, 3)


if __name__ == '__main__':
    unittest.main()

#!/usr/bin/env python3
"""Inspect captured 70291 primary table rows. Diagnostic only; never classifies NPCs."""
import argparse
import base64
import json
import struct
import sys


def raw(value, maximum):
    if not isinstance(value, str) or len(value) > ((maximum + 2) // 3) * 4:
        raise ValueError("missing or oversized table bytes")
    data = base64.b64decode(value, validate=True)
    if len(data) > maximum:
        raise ValueError("oversized table bytes")
    return data


def unpack_snapshot(db):
    """Reject inconsistent/truncated data; return a lookup for primary records only."""
    if not isinstance(db, dict):
        raise ValueError("no table diagnostic")
    storage = raw(db.get('storageBytes'), 0x100)
    fields = raw(db.get('fieldDescriptorBytes'), 64 * 24)
    rows = raw(db.get('rowBytes'), 1024 * 1024)
    index = raw(db.get('indexBytes'), 16384 * 4)
    if len(storage) != 0x100 or len(fields) % 24 or len(fields) == 0:
        raise ValueError("truncated table header or descriptors")
    stride = struct.unpack_from('<I', storage, 0x38)[0]
    first, last = struct.unpack_from('<II', storage, 0x40)
    count = struct.unpack_from('<H', storage, 0x48)[0]
    index_count = struct.unpack_from('<Q', storage, 0xB8)[0]
    if not (0 < stride <= 512 and 0 < count <= 16384 and first <= last and
            len(rows) == stride * count and 0 < index_count <= 16384 and len(index) == index_count * 4):
        raise ValueError("inconsistent table dimensions")
    lookup = {}
    for key, row_index in struct.iter_unpack('<HH', index):
        if key in lookup or first + key > last:
            raise ValueError("duplicate or out-of-range table index")
        lookup[key] = row_index
    return storage, fields, rows, lookup


def inspect(db, key):
    storage, fields, rows, lookup = unpack_snapshot(db)
    first, last = struct.unpack_from('<II', storage, 0x40)
    count = struct.unpack_from('<H', storage, 0x48)[0]
    stride = struct.unpack_from('<I', storage, 0x38)[0]
    if not isinstance(key, int) or key < first or key > last or key - first not in lookup:
        raise ValueError("key unavailable in captured primary index")
    row_index = lookup[key - first]
    if row_index >= count:  # special markers, overlay or secondary row
        raise ValueError("record needs uncaptured special/secondary storage")
    row = int.from_bytes(rows[row_index * stride:(row_index + 1) * stride], 'little')
    scalar = db.get('paletteBytes') or []
    arrays = db.get('arrayPaletteBytes') or []
    result = []
    for field in range(len(fields) // 24):
        _, _, mode, offset, width, array_width = struct.unpack_from('<6I', fields, field * 24)
        if width == 0 or width > 32 or offset + width > stride * 8:
            raise ValueError("unsupported packed field bounds")
        value = (row >> offset) & ((1 << width) - 1)
        if mode == 5:
            if value & (1 << (width - 1)):
                value -= 1 << width
        elif mode == 3:
            if field >= len(scalar) or scalar[field] is None:
                raise ValueError("scalar palette unavailable")
            palette = raw(scalar[field], 16384)
            if value * 4 + 4 > len(palette):
                raise ValueError("scalar palette index out of bounds")
            value = struct.unpack_from('<I', palette, value * 4)[0]
        elif mode == 4:
            # Return the encoded index if this optional region was not captured.
            if array_width != 8:
                raise ValueError("unknown array palette width")
            if field < len(arrays) and arrays[field] is not None:
                data = raw(arrays[field], 16384)
                if len(data) % 32 or (value + 1) * 32 > len(data):
                    raise ValueError("array palette index out of bounds")
                value = {'index': value, 'values': [v & 0xFFFF for v in struct.unpack_from('<8I', data, value * 32)]}
            else:
                value = {'index': value, 'values': None}
        elif mode != 1:
            raise ValueError("unsupported compression mode")
        result.append(value)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', help='private reaction samples JSONL')
    parser.add_argument('keys', nargs='+', type=int, help='FactionTemplate keys to inspect')
    args = parser.parse_args()
    try:
        with open(args.file, encoding='utf-8-sig') as source:
            snapshots = [json.loads(line).get('dbProbe') for line in source if line.strip()]
        available = [x for x in snapshots if x and x.get('rowBytes')]
        if not available:
            raise ValueError('no captured table rows')
        db = available[-1]
        print('Last captured table snapshot; raw field numbers, not reaction or attackability:')
        for key in args.keys:
            try:
                print(f'  {key}: {inspect(db, key)}')
            except ValueError as exc:
                print(f'  {key}: unavailable ({exc})')
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        print(f'Cannot inspect: {exc}', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

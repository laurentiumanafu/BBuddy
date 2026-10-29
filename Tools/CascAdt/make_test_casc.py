#!/usr/bin/env python3
"""Builds a fake but structurally correct CASC store so cascdump can be tested
without any Blizzard data.

    python3 make_test_casc.py /tmp/casctest

It writes

    /tmp/casctest/data/0000000001.idx     index with one guarded block of entries
    /tmp/casctest/data/0000000002.idx     index in the 4 KB-page layout
    /tmp/casctest/data/data.000           the BLTE payloads

and inside it: two "continents" that both contain tile (32,32) but at different
heights, a second tile for the first continent so the seam can be checked, a WDT
lookalike and a plain text file that must be ignored, one ADT split over two zlib
frames, and one zero-length entry that must be skipped.

The ADTs carry real MCNK chunk headers with world positions, which is how the
extractor solves the tile index, so a successful run proves the offset math in
cascdump.c against known coordinates and not just against itself.
"""

import struct
import sys
from pathlib import Path

ADT_YD = 533.3333
CHUNK_YD = ADT_YD / 16.0
FLOATS = 145
SEGMENT_BITS = 30


# --------------------------------------------------------------------------
# lookup3 hashlittle, matching the one in cascdump.c
# --------------------------------------------------------------------------
M = 0xFFFFFFFF


def rot(x, k):
    return ((x << k) | (x >> (32 - k))) & M


def hashlittle(data: bytes, initval: int = 0) -> int:
    length = len(data)
    a = b = c = (0xDEADBEEF + length + initval) & M
    i = 0
    remaining = length

    while remaining > 12:
        a = (a + struct.unpack_from("<I", data, i)[0]) & M
        b = (b + struct.unpack_from("<I", data, i + 4)[0]) & M
        c = (c + struct.unpack_from("<I", data, i + 8)[0]) & M

        a = (a - c) & M; a ^= rot(c, 4); c = (c + b) & M
        b = (b - a) & M; b ^= rot(a, 6); a = (a + c) & M
        c = (c - b) & M; c ^= rot(b, 8); b = (b + a) & M
        a = (a - c) & M; a ^= rot(c, 16); c = (c + b) & M
        b = (b - a) & M; b ^= rot(a, 19); a = (a + c) & M
        c = (c - b) & M; c ^= rot(b, 4); b = (b + a) & M

        i += 12
        remaining -= 12

    tail = data[i:]
    # The tail is loaded as up to three little-endian words; a padded copy is
    # simpler than three switch tables and gives the same result.
    if remaining:
        padded = tail + b"\x00" * (12 - remaining)
        a = (a + struct.unpack_from("<I", padded, 0)[0]) & M
        if remaining > 4:
            b = (b + struct.unpack_from("<I", padded, 4)[0]) & M
        if remaining > 8:
            c = (c + struct.unpack_from("<I", padded, 8)[0]) & M

        c ^= b; c = (c - rot(b, 14)) & M
        a ^= c; a = (a - rot(c, 11)) & M
        b ^= a; b = (b - rot(a, 25)) & M
        c ^= b; c = (c - rot(b, 16)) & M
        a ^= c; a = (a - rot(c, 4)) & M
        b ^= a; b = (b - rot(a, 14)) & M
        c ^= b; c = (c - rot(b, 24)) & M

    return c


# --------------------------------------------------------------------------
# ADT pieces
# --------------------------------------------------------------------------
def chunk(fourcc: bytes, body: bytes) -> bytes:
    return fourcc + struct.pack("<I", len(body)) + body


def chunk_world(tile_x: int, tile_y: int, index_x: int, index_y: int):
    """Centre of one 33.33 yd chunk in world coordinates."""
    world_x = (32 - tile_y) * ADT_YD - index_y * CHUNK_YD - CHUNK_YD * 0.5
    world_y = (32 - tile_x) * ADT_YD - index_x * CHUNK_YD - CHUNK_YD * 0.5
    return world_x, world_y


def mcnk(tile_x: int, tile_y: int, index_x: int, index_y: int, height: float,
         with_position: bool = False) -> bytes:
    # Offsets here are relative to the MCNK body, which starts after the fourcc
    # and the chunk size, so they are 8 bytes below the published file offsets.
    header = bytearray(128)
    struct.pack_into("<i", header, 0x04, index_x)
    struct.pack_into("<i", header, 0x08, index_y)

    if with_position:
        # The modern build puts the chunk position here; the extractor only trusts
        # it when every chunk agrees, so this doubles as a check of that guard.
        world_x, world_y = chunk_world(tile_x, tile_y, index_x, index_y)
        struct.pack_into("<f", header, 0x68, world_x)
        struct.pack_into("<f", header, 0x6C, world_y)
        struct.pack_into("<f", header, 0x70, 0.0)

    mcvt = struct.pack(f"<{FLOATS}f", *([height] * FLOATS))
    header_size = len(header)
    struct.pack_into("<I", header, 0x14, header_size)      # ofsHeight -> MCVT
    return bytes(header) + chunk(b"MCVT", mcvt)


def build_adt(tile_x: int, tile_y: int, height, placements: bool = True,
              chunk_positions: bool = False) -> bytes:
    out = bytearray()
    out += chunk(b"MVER", struct.pack("<I", 18))
    out += chunk(b"MHDR", bytes(24))
    for row in range(16):
        for col in range(16):
            out += chunk(b"MCNK", mcnk(tile_x, tile_y, row, col, height(row, col),
                                      with_position=chunk_positions))

    if placements:
        # One MDDF record: nameId, uniqueId, then the world position. Land tiles
        # always have doodads, and this is what places the tile when the chunk
        # position field is missing or moved.
        world_x, world_y = chunk_world(tile_x, tile_y, 8, 8)
        record = struct.pack("<II", 1, 1) + struct.pack("<fff", world_x, world_y, 50.0)
        record += struct.pack("<fff", 0.0, 0.0, 0.0) + struct.pack("<Hh", 1024, 0)
        out += chunk(b"MDDF", record)

    return bytes(out)


def build_wdt() -> bytes:
    out = bytearray()
    out += chunk(b"MVER", struct.pack("<I", 18))
    out += chunk(b"MPHD", bytes(32))
    out += chunk(b"MAIN", bytes(4096 * 8))
    return bytes(out)


# --------------------------------------------------------------------------
# BLTE
# --------------------------------------------------------------------------
def blte_stored(payload: bytes) -> bytes:
    return b"BLTE" + struct.pack(">I", 0) + b"N" + payload


def blte_zlib(payload: bytes, frames: int = 1) -> bytes:
    import zlib

    step = (len(payload) + frames - 1) // frames
    pieces = [payload[i:i + step] for i in range(0, len(payload), step)]
    table = bytearray()
    body = bytearray()
    for piece in pieces:
        packed = zlib.compress(piece, 9)
        table += struct.pack(">I", len(packed) + 1) + struct.pack(">I", len(piece)) + bytes(16)
        body += b"Z" + packed
    header_size = 12 + len(table)
    header = b"BLTE" + struct.pack(">I", header_size) + bytes([0x0F]) + len(pieces).to_bytes(3, "big")
    return header + bytes(table) + bytes(body)


# --------------------------------------------------------------------------
# index files
# --------------------------------------------------------------------------
def entry(ekey: bytes, span: int, size: int) -> bytes:
    return ekey.ljust(9, b"\x00")[:9] + span.to_bytes(5, "big") + struct.pack(">I", size)


def index_header() -> bytes:
    header = bytearray(16)
    struct.pack_into("<H", header, 0, 7)          # revision
    header[2] = 0                                  # bucket
    header[3] = 0                                  # flags
    header[4] = 4                                  # span size bytes
    header[5] = 5                                  # span offset bytes
    header[6] = 9                                  # key bytes
    header[7] = SEGMENT_BITS
    struct.pack_into("<Q", header, 8, 0x40000000)  # max file offset
    return bytes(header)


def block_hash(entries: list) -> int:
    """The blocked-index hash Blizzard writes: each 18-byte record is hashed with
    the previous result as the seed, so the block is a chain, not one hash."""
    h = 0
    for e in entries:
        h = hashlittle(e, h)
    return h


def build_index_block(entries: list, gap: int = 0) -> bytes:
    """Single guarded block of entries. `gap` inserts the 8 bytes of padding that
    CascLib skips after the header and that some writers omit, so both offsets get
    exercised."""
    header = index_header()
    payload = b"".join(entries)
    out = bytearray()
    out += struct.pack("<I", len(header)) + struct.pack("<I", hashlittle(header, 0)) + header
    out += bytes(gap)
    out += struct.pack("<I", len(payload)) + struct.pack("<I", block_hash(entries))
    out += payload
    return bytes(out)


def build_index_pages(entries: list, guarded: bool = True) -> bytes:
    """Pages start on a 4 KB boundary. Guarded pages put a 32-bit hash in front of
    every entry with 2 bytes of padding; unguarded pages pack the 18-byte entries."""
    header = index_header()
    out = bytearray()
    out += struct.pack("<I", len(header)) + struct.pack("<I", hashlittle(header, 0)) + header
    out += bytes(0x1000 - 24)                       # pages start on a 4 KB boundary

    record_size = 24 if guarded else 18
    for e in entries:
        record = (e + b"\x00" * 18)[:18]
        if guarded:
            guard = hashlittle(record + b"\x00", 0) | 0x80000000
            out += struct.pack("<I", guard) + record + bytes(2)
        else:
            out += record
    out += bytes((0x200 - (len(entries) * record_size) % 0x200) % 0x200)
    return bytes(out)


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/casctest")
    data_dir = root / "data"
    data_dir.mkdir(parents=True, exist_ok=True)

    # Continent A: tile (32,32) with a 20 yd wall in chunk column 8, rows 6-7
    # missing so a path has to detour; tile (32,33) flat at 50.
    def heights_a(row, col):
        return 72.0 if (col == 8 and not (6 <= row <= 7)) else 50.0

    payloads = []           # (ekey, bytes)
    def add(name, payload):
        ekey = bytes.fromhex(name.ljust(18, "0"))
        payloads.append((ekey, payload))
        return ekey

    a1 = add("a1a1a1a1a1a1a1a1a1", blte_zlib(build_adt(32, 32, heights_a), frames=2))
    # Tile (32,33) has no placement records and no chunk positions at all, so it
    # can only be kept as unknown: that is the case the tool has to report, not
    # silently drop.
    a2 = add("a2a2a2a2a2a2a2a2a2", blte_zlib(build_adt(32, 33, lambda r, c: 50.0, placements=False)))
    # Continent B: same tile index, terrain 100 yd higher, so a height check at a
    # reference point must be able to tell the two apart.
    b1 = add("b1b1b1b1b1b1b1b1b1", blte_zlib(build_adt(32, 32, lambda r, c: 150.0)))
    # Tile (32,34) has no doodads but does carry chunk positions.
    a3 = add("f1f1f1f1f1f1f1f1f1", blte_zlib(build_adt(32, 34, lambda r, c: 60.0,
                                                      placements=False, chunk_positions=True)))
    wdt = add("c1c1c1c1c1c1c1c1c1", blte_zlib(build_wdt()))
    txt = add("d1d1d1d1d1d1d1d1d1", blte_stored(b"this is not terrain, just a stored blob"))
    # A second stored blob so the unguarded page layout has two records to judge by.
    x1 = add("e2e2e2e2e2e2e2e2e2", blte_stored(b"also not terrain"))
    skip = add("e1e1e1e1e1e1e1e1e1", b"")             # zero length: span head, skipped

    # Lay the payloads out in one archive.
    archive = bytearray()
    spans = {}
    for ekey, payload in payloads:
        if not payload:
            spans[ekey] = None
            continue
        spans[ekey] = (len(archive), len(payload))
        archive += payload

    def make_entry(name):
        span = spans[name]
        if span is None:
            return entry(name, 0, 0)
        offset, size = span
        return entry(name, offset, size)

    (data_dir / "data.000").write_bytes(bytes(archive))
    # Four index files, one per layout the tool has to recognise on its own.
    (data_dir / "0000000001.idx").write_bytes(
        build_index_block([make_entry(p[0]) for p in payloads[0:2]]))
    (data_dir / "0000000002.idx").write_bytes(
        build_index_block([make_entry(p[0]) for p in payloads[2:4]], gap=8))
    (data_dir / "0000000003.idx").write_bytes(
        build_index_pages([make_entry(p[0]) for p in payloads[4:6]], guarded=True))
    (data_dir / "0000000004.idx").write_bytes(
        build_index_pages([make_entry(p[0]) for p in payloads[6:]], guarded=False))

    print(f"wrote {data_dir}/data.000 ({len(archive)} bytes) and four index files")
    print(f"  continent A: tiles (32,32) height 50/72, (32,34) height 60 by chunk position")
    print(f"  tile (32,33) has no placement data, so it must come out as unknown")
    print(f"  continent B: tile (32,32), height 150")
    print("run: cascdump dump --data %s --out <dir> --ref -100,-100,50" % data_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

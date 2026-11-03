#!/usr/bin/env python3
"""Writes a two-tile synthetic ADT map so the baker and router can be exercised
without any WoW data.

    python3 make_test_map.py /tmp/navtest
    Tools/NavGrid/navbake bake --adt /tmp/navtest/1_32_32.adt --map 1 --tx 32 --ty 32 --out /tmp/navtest
    Tools/NavGrid/navbake path --tiles /tmp/navtest --map 1 --from -100,-100 --to -430,-100

Tile (32,32) contains a north-south wall with a 33 yd gap, so a path across it has
to detour; tile (32,33) is flat and only exists to prove the seam works.
"""

import struct
import sys
from pathlib import Path

CHUNK_YD = 533.3333 / 16.0
FLOATS = 145


def chunk(fourcc: bytes, body: bytes) -> bytes:
    return fourcc + struct.pack("<I", len(body)) + body


def mcnk(index_x: int, index_y: int, height: float, holes: int = 0) -> bytes:
    header = bytearray(128)
    struct.pack_into("<i", header, 4, index_x)      # IndexX = row
    struct.pack_into("<i", header, 8, index_y)      # IndexY = column
    struct.pack_into("<H", header, 0x3C, holes)     # hole mask
    mcvt = struct.pack(f"<{FLOATS}f", *([height] * FLOATS))
    return bytes(header) + chunk(b"MCVT", mcvt)


def build_adt(path: Path, height_at) -> None:
    out = bytearray()
    out += chunk(b"MVER", struct.pack("<I", 18))

    mhdr = bytearray(24)
    struct.pack_into("<I", mhdr, 0, 0)              # flags
    out += chunk(b"MHDR", bytes(mhdr))

    for row in range(16):
        for col in range(16):
            out += chunk(b"MCNK", mcnk(row, col, height_at(row, col)))

    path.write_bytes(out)


def main() -> int:
    out_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/navtest")
    out_dir.mkdir(parents=True, exist_ok=True)

    # Tile (32,32): flat ground at 50, with a wall (a 20 yd step) at chunk column 8
    # except rows 6-7, which is the gap.
    def height_32_32(row: int, col: int) -> float:
        if col == 8 and not (6 <= row <= 7):
            return 72.0
        return 50.0

    build_adt(out_dir / "1_32_32.adt", height_32_32)
    build_adt(out_dir / "1_32_33.adt", lambda row, col: 50.0)
    print(f"wrote {out_dir}/1_32_32.adt and {out_dir}/1_32_33.adt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Check recorded 70790 PE anchors against the supplied decoded image, without running it.

Usage: python Tools/check_wow70790_image.py path/to/wow-decoded.exe
A passing result checks a known file and selected static bytes, not live behavior.
"""
import hashlib
from pathlib import Path
import struct
import sys

EXPECTED_SHA256 = "a4ac58d2998386fe93ce77dda0d2df6d775231d078c849b017876e11879db620"
MARKERS = {
    0x211826C: "488D0DCD3FA104E878303AFE8B93541E0000",
    0x2142757: "F2480F2A83281E0000",
    0x2931DA5: "448B8C86C01B0000",
    0xA6B06D: "488D0D4CB83706488D34C1488B04C1",
    0x21FA4C0: "48895C2408574883EC2080792000",
    0x27696E0: "48895C24084889742418574881EC2004",
    0xD8E69C: "E8AF6C8701", 0xD8E6C0: "8B80C00A0000",
    0x2606E66: "486390C00A0000", 0x2606E74: "4C8D8090040000", 0x2606E80: "498B00",
    0x2606EB6: "4038A880940000", 0x2606EC9: "0FB6B8047D0000", 0x2606ED4: "83C723",
}


def check(path):
    data = Path(path).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != EXPECTED_SHA256:
        raise ValueError(f"Unexpected executable SHA-256: {digest}")
    if data[:2] != b"MZ":
        raise ValueError("Missing DOS header")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Missing PE signature")
    machine, section_count = struct.unpack_from("<HH", data, pe + 4)
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    if machine != 0x8664 or struct.unpack_from("<H", data, opt)[0] != 0x20B:
        raise ValueError("Expected an x64 PE32+ image")
    base = struct.unpack_from("<Q", data, opt + 24)[0]
    table = opt + optional_size
    sections = []
    for i in range(section_count):
        start = table + i * 40
        vsize, rva, raw_size, raw = struct.unpack_from("<IIII", data, start + 8)
        sections.append((rva, raw_size, raw))

    def read(rva, size):
        for start, raw_size, raw in sections:
            if start <= rva and rva + size <= start + raw_size:
                offset = raw + rva - start
                if offset + size <= len(data):
                    return data[offset:offset + size]
        raise ValueError(f"RVA 0x{rva:X} is not backed by file bytes")

    for rva, expected in MARKERS.items():
        want = bytes.fromhex(expected)
        if read(rva, len(want)) != want:
            raise ValueError(f"Marker mismatch at 0x{rva:X}")

    def relative(instruction_rva, displacement_offset, size):
        return instruction_rva + size + struct.unpack_from("<i", read(instruction_rva, size), displacement_offset)[0]

    for rva, displacement, length, target in [
        (0xA6B06D, 3, 7, 0x6DE68C0),  # action array
        (0x2605373, 3, 7, 0x6DE9900),  # candidate bag identity table
        (0x27697D0, 1, 5, 0x21FA4C0),  # inventory lookup call
        (0xD8E69C, 1, 5, 0x2605350),  # identity helper call
    ]:
        if relative(rva, displacement, length) != target:
            raise ValueError(f"Relative target mismatch at 0x{rva:X}")
    for table_rva, slot, function in [(0x427CAF8, 3, 0x1FDF480), (0x4290AD8, 0, 0x20F8A40), (0x4284398, 7, 0x2054440)]:
        if struct.unpack("<Q", read(table_rva + slot * 8, 8))[0] != base + function:
            raise ValueError(f"Candidate vtable mismatch at 0x{table_rva:X}")
    print(f"70790 x64 image SHA-256 and {len(MARKERS)} selected markers, relative references and candidate vtable slots match.")
    print("Static evidence only: this does not validate runtime layouts, gameplay, inventory completeness or selling.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    try:
        check(sys.argv[1])
    except (OSError, ValueError, struct.error) as exc:
        raise SystemExit(str(exc)) from exc

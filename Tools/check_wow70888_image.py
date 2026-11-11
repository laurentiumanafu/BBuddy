#!/usr/bin/env python3
"""Check recorded 70888 PE anchors against the supplied decoded image, without running it.

Usage: python Tools/check_wow70888_image.py path/to/wow-decoded.exe
A passing result checks a known file and selected static bytes, not live behavior.
"""
import hashlib
from pathlib import Path
import struct
import sys

EXPECTED_SHA256 = "c75ee14d98f7deff9f5067cd58ac48870a70727943f36df6c5a1b3db2f1d7e39"
MARKERS = {
    0x1F9299C: "488D0D1D29A004E8188552FE8B93541E0000",
    0x1FBCA57: "F2480F2A83281E0000",
    0x2799845: "448B8C86C01B0000",
    0xA2BA0D: "488D0D2C772206488D34C1488B04C1",
    0x2074710: "48895C2408574883EC2080792000",
    0x25D4E70: "48895C24084889742418574881EC2004",
    0xD0FD6C: "E84F407601", 0xD0FD90: "8B80C00A0000",
    0x24758D6: "486390C00A0000", 0x24758E4: "4C8D8090040000", 0x24758F0: "498B00",
    0x2475926: "4038A880940000", 0x2475939: "0FB6B8047D0000", 0x2475944: "83C723",
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
        (0x1C712E0, 3, 7, 0x4C1A770),  # player GUID
        (0x1C712EB, 2, 6, 0x4C1A780),  # player name
        (0x23C76D, 3, 7, 0x6A32478),   # independent target reference
        (0x21C901A, 3, 7, 0x4CD5F98),  # mouseover GUID
        (0x190D4D5, 3, 7, 0x6C349F8),  # object manager
        (0x27D38B, 3, 7, 0x4728BE0),   # pools
        (0x1F1FCA, 3, 7, 0x6C34978),   # map root
        (0x5ACFDD, 3, 7, 0x4A51EB0),   # camera frame
        (0xA2BA0D, 3, 7, 0x6C53140),  # action array
        (0x2473DE3, 3, 7, 0x6C56180),  # bag identity table
        (0x25D4F60, 1, 5, 0x2074710),  # inventory lookup call
        (0xD0FD6C, 1, 5, 0x2473DC0),  # identity helper call
    ]:
        if relative(rva, displacement, length) != target:
            raise ValueError(f"Relative target mismatch at 0x{rva:X}")
    for table_rva, slot, function in [(0x40E64F8, 4, 0x1E61240), (0x40FA570, 0, 0x1F73160), (0x40EED10, 7, 0x1ECE9E0)]:
        if struct.unpack("<Q", read(table_rva + slot * 8, 8))[0] != base + function:
            raise ValueError(f"Candidate vtable mismatch at 0x{table_rva:X}")
    print(f"70888 x64 image SHA-256 and {len(MARKERS)} selected markers, relative references and candidate vtable slots match.")
    print("Static evidence only: this does not validate runtime layouts, gameplay, inventory completeness or selling.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    try:
        check(sys.argv[1])
    except (OSError, ValueError, struct.error) as exc:
        raise SystemExit(str(exc)) from exc

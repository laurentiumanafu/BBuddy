#!/usr/bin/env python3
"""Check recorded 70862 PE anchors against the supplied decoded image, without running it.

Usage: python Tools/check_wow70862_image.py path/to/wow-decoded.exe
A passing result checks a known file and selected static bytes, not live behavior.
"""
import hashlib
from pathlib import Path
import struct
import sys

EXPECTED_SHA256 = "b332526a904931e81dcbcf1f95a42d64e842177b37ab5af52ab1f27b875fd0af"
MARKERS = {
    0x1F9298C: "488D0D2D29A004E8288552FE8B93541E0000",
    0x1FBCA47: "F2480F2A83281E0000",
    0x27997E5: "448B8C86C01B0000",
    0xA2B9FD: "488D0D3C772206488D34C1488B04C1",
    0x2074700: "48895C2408574883EC2080792000",
    0x25D4E10: "48895C24084889742418574881EC2004",
    0xD0FD5C: "E8FF3F7601", 0xD0FD80: "8B80C00A0000",
    0x2475876: "486390C00A0000", 0x2475884: "4C8D8090040000", 0x2475890: "498B00",
    0x24758C6: "4038A880940000", 0x24758D9: "0FB6B8047D0000", 0x24758E4: "83C723",
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
        (0x1C712D0, 3, 7, 0x4C1A770),  # player GUID
        (0x1C712DB, 2, 6, 0x4C1A780),  # player name
        (0x5ABB58, 3, 7, 0x6A32478),   # selected target GUID
        (0x23C76D, 3, 7, 0x6A32478),   # independent target reference
        (0x21C900A, 3, 7, 0x4CD5F98),  # mouseover GUID
        (0x190D4C5, 3, 7, 0x6C349F8),  # object manager
        (0x27D38B, 3, 7, 0x4728BE0),   # pools
        (0x1F1FCA, 3, 7, 0x6C34978),   # map root
        (0x5ACFDD, 3, 7, 0x4A51EB0),   # camera frame
        (0xA2B9FD, 3, 7, 0x6C53140),  # action array
        (0x2473D83, 3, 7, 0x6C56180),  # bag identity table
        (0x25D4F00, 1, 5, 0x2074700),  # inventory lookup call
        (0xD0FD5C, 1, 5, 0x2473D60),  # identity helper call
    ]:
        if relative(rva, displacement, length) != target:
            raise ValueError(f"Relative target mismatch at 0x{rva:X}")
    for table_rva, slot, function in [(0x40E6498, 4, 0x1E61230), (0x40FA510, 0, 0x1F73150), (0x40EECB0, 7, 0x1ECE9D0)]:
        if struct.unpack("<Q", read(table_rva + slot * 8, 8))[0] != base + function:
            raise ValueError(f"Candidate vtable mismatch at 0x{table_rva:X}")
    print(f"70862 x64 image SHA-256 and {len(MARKERS)} selected markers, relative references and candidate vtable slots match.")
    print("Static evidence only: this does not validate runtime layouts, gameplay, inventory completeness or selling.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    try:
        check(sys.argv[1])
    except (OSError, ValueError, struct.error) as exc:
        raise SystemExit(str(exc)) from exc

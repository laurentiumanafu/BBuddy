#!/usr/bin/env python3
"""Check recorded 70938 PE anchors against the supplied decoded image, without running it.

Usage: python Tools/check_wow70938_image.py path/to/wow-decoded.exe
A passing result checks a known file and selected static bytes, not live behavior.
"""
import hashlib
from pathlib import Path
import struct
import sys

EXPECTED_SHA256 = "436a1b0dc17006895d46b1619dff45c84b683d37e2b24a458cdec333f2accb3a"
MARKERS = {
    0x209774C: "488D0DED1EA104E8F8E942FE8B93541E0000",
    0x20C1C37: "F2480F2A83281E0000",
    0x28A8105: "448B8C86C01B0000",
    0xA6017D: "488D0D3C733006488D34C1488B04C1",
    0x21796E0: "48895C2408574883EC2080792000",
    0x26E1230: "48895C24084889742418574881EC2004",
    0xD6C5BC: "E8BF1A8101", 0xD6C5E0: "8B80C00A0000",
    0x257FB96: "486390C00A0000", 0x257FBA4: "4C8D8090040000", 0x257FBB0: "498B00",
    0x257FBE6: "4038A880940000", 0x257FBF9: "0FB6B8047D0000", 0x257FC04: "83C723",
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
        (0x1D75F20, 3, 7, 0x4D2EAE0),  # player GUID candidate
        (0x1D76B51, 3, 7, 0x4D2EAF0),  # player name candidate
        (0x5B871D, 3, 7, 0x6B467F8),   # target
        (0x21FB216, 3, 7, 0x4DEA308),  # mouseover candidate
        (0x2036CD, 3, 7, 0x6D48D78),   # manager
        (0x269152, 3, 7, 0x483CBE0),   # pools
        (0x203520, 3, 7, 0x6D48CF8),   # map
        (0x2C8D23, 3, 7, 0x4B661F0),   # camera
        (0xA6017D, 3, 7, 0x6D674C0),  # action array
        (0x257E0A3, 3, 7, 0x6D6A500), # bag identity table
        (0x26E1320, 1, 5, 0x21796E0), # inventory lookup call
        (0xD6C5BC, 1, 5, 0x257E080),  # identity helper call
    ]:
        if relative(rva, displacement, length) != target:
            raise ValueError(f"Relative target mismatch at 0x{rva:X}")
    for table_rva, slot, function in [(0x41F8070, 4, 0x1F66860), (0x420C0D8, 0, 0x2077F10), (0x41FFF48, 0, 0x1FCFA70)]:
        if struct.unpack("<Q", read(table_rva + slot * 8, 8))[0] != base + function:
            raise ValueError(f"Candidate vtable mismatch at 0x{table_rva:X}")
    print(f"70938 x64 image SHA-256 and {len(MARKERS)} selected markers, relative references and candidate vtable slots match.")
    print("Static evidence only: this does not validate runtime layouts, gameplay, inventory completeness or selling.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    try:
        check(sys.argv[1])
    except (OSError, ValueError, struct.error) as exc:
        raise SystemExit(str(exc)) from exc

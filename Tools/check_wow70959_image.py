#!/usr/bin/env python3
"""Check recorded 70959 PE anchors against the supplied decoded image, without running it.

Companion to Tools/check_wow70938_image.py and Tools/WowBBuildPort/derive_build_anchors.py.
Every anchor below was carried across from 1.60.1.70938 by matching the referencing
code in both images, not by applying a delta; the derivation, its per-candidate
confidence and its limits are recorded in hunt/WOW_70959_PORT.md.

Usage: python Tools/check_wow70959_image.py path/to/wow-decoded.exe
A passing result checks a known file and selected static bytes, not live behavior.
"""
import hashlib
from pathlib import Path
import struct
import sys

EXPECTED_SHA256 = "dd2f820f68a825cd1b3ab62c2efe7e1993bbe974be6537f3ad2312de9d3012df"

# (rva, expected bytes). A trailing number marks the position of a 4-byte
# relative displacement inside the pattern, which is resolved separately below.
MARKERS = {
    0x1F9D48C: "488D0D6D61A004E8F88852FE8B93541E0000",                  # faction
    0x1FC7547: "F2480F2A83281E0000",                                    # health
    0x27A2B65: "448B8C86C01B0000",                                      # power
    0xA3695D: ("488D0D1CAB2206488D34C1488B04C1", 3),                    # action array
    0x207EF20: "48895C2408574883EC2080792000",                          # inventory lookup
    0x25DDD60: "48895C24084889742418574881EC2004",                      # inventory handler
    0xD1ACBC: ("E8AF207601", 1),                                        # container identity helper call
    0xD1ACE0: "8B80C00A0000",
    0x247E886: "486390C00A0000",
    0x247E894: "4C8D8090040000",
    0x247E8A0: "498B00",
    0x247E8D6: "4038A880940000",
    0x247E8E9: "0FB6B8047D0000",
    0x247E8F4: "83C723",
    0x1E56C94: "448BF8488B791040F6C70175054885FF7503",                  # cooldown research only
    0x1E56CC3: "3977100F858C0000008B45603947140F8580000000",
    0x1E56CF6: "8B471C448D0401",
    0x1E56EF4: "488B7F08",
    0x279CE7E: ("4C8D4C24604869C8B8000000488D05FF9804024533C04803C8", 15),
    0x25B1AA8: ("FFC883F8107338488D0C80488D05A64F7B02488D04C84885C0", 14),  # mail table candidate
    0x25B1B4A: ("FFC883F8100F83E2010000488D0C80488D05004F7B02488D0CC8", 18),
}

# (site rva, displacement offset, instruction length, expected target)
RELATIVE = [
    (0xA3695D, 3, 7, 0x6C61480),     # action array
    (0xD1ACBC, 1, 5, 0x247CD70),     # container identity helper
    (0x25B1AB3, 3, 7, 0x4D66A60),    # mail attachment table (0x25B1AA8 + 11)
    (0x25B1B59, 3, 7, 0x4D66A60),    # mail attachment table, second site (0x25B1B4A + 15)
    (0x279CE8A, 3, 7, 0x47E6790),    # indexed candidate state array (0x279CE7E + 12)
]


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
        disp = None
        if isinstance(expected, tuple):
            expected, disp = expected
        want = bytes.fromhex(expected)
        got = read(rva, len(want))
        if disp is not None:
            # Relocation-dependent bytes differ by design; compare around them.
            got = got[:disp] + want[disp:disp + 4] + got[disp + 4:]
        if got != want:
            raise ValueError(f"Marker mismatch at 0x{rva:X}: {got.hex().upper()}")

    for rva, displacement, length, target in RELATIVE:
        resolved = rva + length + struct.unpack_from("<i", read(rva, length), displacement)[0]
        if resolved != target:
            raise ValueError(f"Relative target mismatch at 0x{rva:X}: 0x{resolved:X} != 0x{target:X}")

    print(f"70959 x64 image SHA-256, {len(MARKERS)} selected markers and {len(RELATIVE)} "
          f"relative references match (image base 0x{base:X}).")
    print("Static evidence only: this does not validate runtime layouts, gameplay, "
          "inventory completeness or selling.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    try:
        check(sys.argv[1])
    except (OSError, ValueError, struct.error) as exc:
        raise SystemExit(str(exc)) from exc

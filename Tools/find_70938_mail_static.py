#!/usr/bin/env python3
"""Locate static 70938 mail API name and RIP-relative instruction leads, without running the client.

Usage: python Tools/find_70938_mail_static.py /path/to/wow-decoded.exe
Requires the exact decoded image recorded in check_wow70938_image.py. Results are
unverified byte-pattern leads, not proven instructions, offsets, or callable APIs.
"""
import argparse
import hashlib
import re
import struct
from pathlib import Path

from check_wow70938_image import EXPECTED_SHA256

NAMES = ("GetSendMailItem", "HasSendMailItem", "GetSendMailItemLink", "GetSendMailMoney",
         "GetSendMailCOD", "GetSendMailPrice", "SetSendMailShowing", "SendMail", "ClearSendMail")
# REX.W LEA reg,[RIP+disp32], including high registers. May match inside data
# or another instruction; even in executable sections these are only leads.
LEA = re.compile(rb'[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]....', re.DOTALL)


def sections(data):
    if data[:2] != b'MZ' or len(data) < 64:
        raise ValueError('missing DOS header')
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe+4] != b'PE\0\0': raise ValueError('missing PE signature')
    machine, count = struct.unpack_from('<HH', data, pe+4)
    opt_size = struct.unpack_from('<H', data, pe+20)[0]
    opt = pe + 24
    if machine != 0x8664 or struct.unpack_from('<H', data, opt)[0] != 0x20b or count > 96:
        raise ValueError('expected bounded x64 PE32+ image')
    base = struct.unpack_from('<Q', data, opt+24)[0]
    found = []
    for i in range(count):
        pos = opt + opt_size + 40*i
        name = data[pos:pos+8].split(b'\0', 1)[0].decode('ascii', 'replace')
        _, rva, size, raw = struct.unpack_from('<IIII', data, pos+8)
        flags = struct.unpack_from('<I', data, pos+36)[0]
        if raw + size > len(data): raise ValueError('section exceeds file')
        found.append((name, rva, raw, size, flags))
    return base, found


def find_leads(data, image_sections, names=NAMES):
    locations = {}
    for name in names:
        needle = name.encode('ascii') + b'\0'
        hits = []
        for _, rva, raw, size, _ in image_sections:
            start, end = raw, raw + size
            while True:
                offset = data.find(needle, start, end)
                if offset < 0: break
                # Avoid accepting a suffix of a longer identifier.
                if offset == raw or not (chr(data[offset-1]).isalnum() or data[offset-1] == 95):
                    hits.append(rva + offset - raw)
                start = offset + 1
        locations[name] = hits
    refs = {name: [] for name in names}
    targets = {}
    for name, hits in locations.items():
        for hit in hits: targets.setdefault(hit, []).append(name)
    for section, rva, raw, size, flags in image_sections:
        if not flags & 0x20000000: continue
        for match in LEA.finditer(data, raw, raw+size):
            offset = match.start()
            insn = rva + offset - raw
            dest = insn + 7 + struct.unpack_from('<i', data, offset+3)[0]
            for name in targets.get(dest, ()):
                # Neighboring RIP LEAs may contain a handler candidate. Their
                # provenance is deliberately not inferred from proximity.
                begin = max(raw, offset - 32); end = min(raw + size, offset + 39)
                near = []
                for other in LEA.finditer(data, begin, end):
                    p = other.start(); address = rva + p - raw
                    near.append((address, address + 7 + struct.unpack_from('<i', data, p+3)[0]))
                refs[name].append((section, insn, dest, near))
    return locations, refs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path)
    args = parser.parse_args()
    data = args.image.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != EXPECTED_SHA256: raise SystemExit('Unexpected image SHA-256: ' + digest)
    base, image_sections = sections(data)
    locations, refs = find_leads(data, image_sections)
    print('Exact recorded SHA-256:', digest, 'preferred image base:', hex(base))
    print('Static byte-pattern leads only; no instruction boundaries or API handlers proven.')
    for name in NAMES:
        print('\n' + name + ': strings=' + ', '.join(hex(x) for x in locations[name]))
        if not refs[name]: print('  No matching RIP-relative LEA found; this does not prove absence.')
        for section, insn, dest, nearby in refs[name]:
            print(f'  {section} RVA {insn:#x} -> name RVA {dest:#x}')
            for address, target in nearby:
                print(f'    nearby LEA-like RVA {address:#x} -> {target:#x}')
    print('\nValidate instruction boundaries and dataflow independently. No runtime behavior or safety is established.')


if __name__ == '__main__': main()

#!/usr/bin/env python3
"""Port recorded WowB anchors from one decoded client image onto another.

Companion to Tools/check_wow70938_image.py, which only *confirms* recorded
anchors in one known file. This tool is for the day a new decoded build
arrives: for every recorded global or function RVA it finds the code sites
that reference it in the old image, carries each site across to the new image
as a masked byte signature, and resolves that site's displacement again in the
new image. Several independent sites that agree are the evidence. One site that
matches uniquely is a lead. No match, or sites that disagree, is reported as
unknown rather than guessed.

Nothing here runs the client, reads a live process, or proves semantics: a
resolved address is a code cross-reference, not a validated field.

Usage:
  python3 Tools/WowBBuildPort/derive_build_anchors.py --old OLD.exe --new NEW.exe
  python3 Tools/WowBBuildPort/derive_build_anchors.py --old OLD.exe --new NEW.exe --json out.json
  python3 Tools/WowBBuildPort/derive_build_anchors.py --selftest

Stdlib only; works on Windows and Linux.
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

# ---------------------------------------------------------------- known anchors

# 1.60.1.70938 candidates as shipped in Styx/Offsets/Wow70938Offsets.cs.
GLOBALS_70938 = {
    "PlayerGuid": 0x4D2EAE0,
    "PlayerNamePrimary": 0x4D2EAF0,
    "PlayerTargetGuid": 0x6B467F8,
    "MouseoverGuidCandidate": 0x4DEA308,
    "ObjectManager": 0x6D48D78,
    "HandlePools": 0x483CBE0,
    "MapRoot": 0x6D48CF8,
    "CameraWorldFrameGlobal": 0x4B661F0,
    "PlayerVtable": 0x41F8070,
    "UnitVtable": 0x420C0D8,
    "GameObjectVtableCandidate": 0x41FFF48,
    "StoredActionArrayCandidate": 0x6D674C0,
    "ContainerIdentityTableCandidate": 0x6D6A500,
}

# Code addresses (functions), ported through their call sites and prologues.
FUNCTIONS_70938 = {
    "InventoryLookup": 0x21796E0,
    "InventoryHandler": 0x26E1230,
    "PlayerGetterCandidate": 0x1F6EC10,
    # Container identity helper named in hunt/WOW_70938_PORT.md (called at 0xD6C5BC).
    "ContainerIdentityHelper": 0x257E080,
    # Evidence addresses behind the supplied player-state member offsets
    # (uploaded newoffsets.txt); porting them lets the member offsets be re-read
    # in the new image instead of assumed.
    "AttackBeginHelper": 0x1FAFFA0,
    "StartAttackGuard": 0x712110,
    "AttackTargetReader": 0x20BA540,
    "StateFlagsWriter": 0x1FB4880,
    "ShapeshiftGetter": 0x241DCC0,
    "ComboPointsReader": 0x17E4AE0,
    "ComboPointsReaderAlt": 0x1470770,
}

# Recorded byte markers from Tools/check_wow70938_image.py, plus the position of
# a 4-byte RIP/relative displacement inside each pattern when there is one, so
# the same pattern can be resolved in another image instead of only matched.
# (rva, hex pattern, displacement offset within the pattern or None)
MARKERS_70938 = [
    (0x209774C, "488D0DED1EA104E8F8E942FE8B93541E0000", None),          # faction
    (0x20C1C37, "F2480F2A83281E0000", None),                            # health
    (0x28A8105, "448B8C86C01B0000", None),                              # power
    (0xA6017D, "488D0D3C733006488D34C1488B04C1", 3),                    # action array
    (0x21796E0, "48895C2408574883EC2080792000", None),                  # inventory lookup
    (0x26E1230, "48895C24084889742418574881EC2004", None),              # inventory handler
    (0xD6C5BC, "E8BF1A8101", 1),                                        # container helper call
    (0xD6C5E0, "8B80C00A0000", None),
    (0x257FB96, "486390C00A0000", None),
    (0x257FBA4, "4C8D8090040000", None),
    (0x257FBB0, "498B00", None),
    (0x257FBE6, "4038A880940000", None),
    (0x257FBF9, "0FB6B8047D0000", None),
    (0x257FC04, "83C723", None),
    (0x15DF02A, "8B164C8D7E084D8BC7E858332C01", 10),                    # cooldown research
    (0x28A241E, "4C8D4C24604869C8B8000000488D05AFA304024533C04803C8", 15),
    (0x1F51104, "448BF8488B791040F6C70175054885FF7503", None),
    (0x1F51133, "3977100F858C0000008B45603947140F8580000000", None),
    (0x1F51166, "8B471C448D0401", None),
    (0x1F51364, "488B7F08", None),
    (0x26B4F78, "FFC883F8107338488D0C80488D05067B7B02488D04C84885C0", 14),
    (0x26B501A, "FFC883F8100F83E2010000488D0C80488D05607A7B02488D0CC848", 18),
]

# Sites recorded in Tools/check_wow70938_image.py: (rva, disp offset, length, target).
# These are a second, independent way to reach the same globals; the tool checks
# the old image still resolves them before it trusts the new one.
REFERENCE_SITES_70938 = [
    (0x1D75F20, 3, 7, 0x4D2EAE0),
    (0x1D76B51, 3, 7, 0x4D2EAF0),
    (0x5B871D, 3, 7, 0x6B467F8),
    (0x21FB216, 3, 7, 0x4DEA308),
    (0x2036CD, 3, 7, 0x6D48D78),
]

WINDOW = 24          # bytes of context kept either side of a displacement
MAX_SITE_MATCHES = 12
MAX_PATTERN_MATCHES = 8

# ------------------------------------------------------------------- PE reading


class Image:
    """Just enough PE32+ to map RVAs to file bytes and read the file version."""

    def __init__(self, path):
        self.path = Path(path)
        self.data = self.path.read_bytes()
        self.sha256 = hashlib.sha256(self.data).hexdigest()
        self.size = len(self.data)
        if self.data[:2] != b"MZ":
            raise ValueError("not a DOS/PE image: %s" % path)
        pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("missing PE signature")
        self.machine, self.section_count = struct.unpack_from("<HH", self.data, pe + 4)
        optional_size = struct.unpack_from("<H", self.data, pe + 20)[0]
        opt = pe + 24
        self.magic = struct.unpack_from("<H", self.data, opt)[0]
        if self.machine != 0x8664 or self.magic != 0x20B:
            raise ValueError("expected an x64 PE32+ image, got machine=0x%X magic=0x%X" % (self.machine, self.magic))
        self.image_base = struct.unpack_from("<Q", self.data, opt + 24)[0]
        self.size_of_image = struct.unpack_from("<I", self.data, opt + 56)[0]
        table = opt + optional_size
        self.sections = []
        for i in range(self.section_count):
            start = table + i * 40
            name = self.data[start:start + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, rva, raw_size, raw = struct.unpack_from("<IIII", self.data, start + 8)
            self.sections.append({"name": name, "rva": rva, "vsize": vsize, "raw_size": raw_size, "raw": raw})
        self.executable_sections = [s for s in self.sections if s["name"] in (".text",)]
        self.file_version = self._read_file_version(opt)
        self.version_strings = self._scan_version_strings()

    def section_for(self, rva):
        for s in self.sections:
            span = max(s["vsize"], s["raw_size"])
            if s["rva"] <= rva < s["rva"] + span:
                return s
        return None

    def read(self, rva, size):
        """File bytes for an RVA, or None when the RVA is not backed by the file."""
        s = self.section_for(rva)
        if s is None:
            return None
        offset = s["raw"] + rva - s["rva"]
        if offset < 0 or offset + size > len(self.data) or rva - s["rva"] >= s["raw_size"]:
            return None
        return self.data[offset:offset + size]

    def _scan_version_strings(self):
        """UTF-16 build strings such as '1.60.1.70959' anywhere in the file.

        Independent of the resource walk: a decoded image does not have to keep
        a parseable VS_VERSIONINFO for the build number to be readable.
        """
        found = []
        for prefix in (b"1\x00.\x006\x000\x00.\x001\x00.\x00", b"1.60.1."):
            start = 0
            while len(found) < 8:
                at = self.data.find(prefix, start)
                if at < 0:
                    break
                digits = ""
                if prefix.endswith(b"\x00"):        # UTF-16: read wide digits
                    cursor = at + len(prefix)
                    while cursor + 1 < len(self.data):
                        char, high = self.data[cursor], self.data[cursor + 1]
                        if high != 0 or not chr(char).isdigit():
                            break
                        digits += chr(char)
                        cursor += 2
                else:                                # ASCII
                    cursor = at + len(prefix)
                    while cursor < len(self.data) and chr(self.data[cursor]).isdigit():
                        digits += chr(self.data[cursor])
                        cursor += 1
                if len(digits) >= 4:
                    found.append("1.60.1." + digits)
                start = at + 1
        return sorted(set(found), key=lambda s: int(s.rsplit(".", 1)[1]))

    def _read_file_version(self, opt):
        """Minimal VS_VERSIONINFO walk: returns 'a.b.c.d' or None."""
        dirs = struct.unpack_from("<I", self.data, opt + 116)[0]
        if dirs < 1:
            return None
        rsrc_rva, rsrc_size = struct.unpack_from("<II", self.data, opt + 128 + 2 * 8)
        base = self.section_for(rsrc_rva)
        if base is None:
            return None
        def at(rva, size):
            s = self.section_for(rva)
            if s is None:
                return None
            off = s["raw"] + rva - s["rva"]
            return self.data[off:off + size]
        try:
            def walk(rva, level, want_type=None):
                blob = at(rva, 16)
                if blob is None:
                    return []
                named, ids = struct.unpack_from("<HH", blob, 12)
                out = []
                for i in range(named + ids):
                    entry = blob[16 + i * 8:24 + i * 8]
                    name, offset = struct.unpack_from("<II", entry)
                    if len(entry) < 8:
                        continue
                    if level == 0 and name != 16:      # RT_VERSION
                        continue
                    sub = rva + (offset & 0x7FFFFFFF)
                    if offset & 0x80000000:
                        if level == 2:
                            data = at(sub, 16)
                            if data is None:
                                continue
                            frva, fsize = struct.unpack_from("<II", data)
                            out.append((frva, fsize))
                    else:
                        out.extend(walk(sub, level + 1))
                return out
            entries = walk(rsrc_rva, 0)
        except (struct.error, IndexError):
            return None
        for frva, fsize in entries:
            blob = at(frva, min(fsize, 4096))
            if not blob or len(blob) < 92:
                continue
            try:
                length, value_len, vtype = struct.unpack_from("<HHH", blob, 0)
                key = blob[6:6 + 30]
                if b"V" not in key:
                    continue
                fixed = 6 + len(key.split(b"\0\0")[0]) + 2
                fixed += (4 - fixed % 4) % 4
                sig = struct.unpack_from("<I", blob, fixed)[0]
                if sig != 0xFEEF04BD:
                    continue
                ms, ls = struct.unpack_from("<II", blob, fixed + 8)
                return "%d.%d.%d.%d" % (ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF)
            except (struct.error, IndexError):
                continue
        return None


# ------------------------------------------------------- x64 reference scanning

RIP_MODRMS = {0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D}
# opcodes that take a ModRM and are common around global reads:
#  8B mov r,rm | 89 mov rm,r | 8D lea | 8A/88 byte forms | 0F B6/BF movzx/movsx
#  0F 10/11 movups | 0F 28/29 movaps | 0F 6E/7E movd/movq | 0F 2A/5A cvt
TWO_BYTE = {0xB6, 0xB7, 0xBF, 0xBE, 0x10, 0x11, 0x28, 0x29, 0x6E, 0x7E, 0x2A, 0x5A, 0x58, 0x59}


SIMPLE_OPCODES = (0x8B, 0x89, 0x8D, 0x8A, 0x88, 0x8F, 0xC6, 0xC7)


def _rip_at(blob, i):
    """Decode a candidate RIP-relative instruction starting at blob[i].

    Returns (disp_offset, instruction_length) or None. Handles an optional REX
    prefix, the two-byte opcodes in TWO_BYTE, and the plain ModRM opcodes.
    """
    n = len(blob)
    rex = 1 if 0x40 <= blob[i] <= 0x4F and i + 1 < n else 0
    op = blob[i + rex] if i + rex < n else 0
    if op == 0x0F:
        if i + rex + 2 >= n or blob[i + rex + 1] not in TWO_BYTE:
            return None
        modrm_at = i + rex + 2
    elif op in SIMPLE_OPCODES:
        modrm_at = i + rex + 1
    else:
        return None
    if modrm_at >= n or blob[modrm_at] not in RIP_MODRMS or modrm_at + 4 >= n:
        return None
    return modrm_at + 1 - i, modrm_at + 5 - i


def scan_rip_references(image, section_names=(".text",)):
    """Every RIP-relative data reference in the given sections.

    Returns (site_rva, disp_offset_within_instruction, instruction_length, target_rva).
    Alignment is unknown, so this is a superset: a byte pair can decode inside an
    immediate. Every hit is re-verified by the caller against a known target.
    """
    hits = []
    for section in image.sections:
        if section["name"] not in section_names:
            continue
        blob = image.read(section["rva"], section["raw_size"])
        if blob is None:
            continue
        for i in range(len(blob) - 6):
            shape = _rip_at(blob, i)
            if shape is None:
                continue
            disp_off, length = shape
            disp = struct.unpack_from("<i", blob, i + disp_off)[0]
            target = section["rva"] + i + length + disp
            hits.append((section["rva"] + i, disp_off, length, target))
    return hits


def scan_relative_calls(image, section_names=(".text",)):
    """Every E8 call / E9 jmp with its target (used to port code addresses)."""
    hits = []
    for section in image.sections:
        if section["name"] not in section_names:
            continue
        blob = image.read(section["rva"], section["raw_size"])
        if blob is None:
            continue
        for i in range(len(blob) - 5):
            if blob[i] in (0xE8, 0xE9):
                disp = struct.unpack_from("<i", blob, i + 1)[0]
                hits.append((section["rva"] + i, 1, 5, section["rva"] + i + 5 + disp))
    return hits


# ------------------------------------------------------------ signature carrying


def mask_other_displacements(image, window_rva, window, primary_offset, length):
    """Blank every displacement in the window except the one being ported.

    A neighbouring RIP-relative or call displacement usually moves with the
    image too; leaving it literal would stop the signature from matching.
    """
    mask = bytearray([1] * len(window))
    primary = window_rva + primary_offset
    for site, disp_off, length_, _ in scan_rip_references_window(image, window_rva, len(window)):
        if site <= primary < site + length_:
            continue
        for k in range(4):
            pos = site - window_rva + disp_off + k
            if 0 <= pos < len(window):
                mask[pos] = 0
    for j in range(len(window) - 5):
        if window[j] in (0xE8, 0xE9) and window_rva + j != primary:
            for k in range(1, 5):
                mask[j + k] = 0
    return mask


def scan_rip_references_window(image, window_rva, size):
    blob = image.read(window_rva, size)
    if blob is None:
        return []
    out = []
    for i in range(len(blob) - 6):
        shape = _rip_at(blob, i)
        if shape is None:
            continue
        disp_off, length = shape
        disp = struct.unpack_from("<i", blob, i + disp_off)[0]
        out.append((window_rva + i, disp_off, length, window_rva + i + length + disp))
    return out


def find_masked(haystack, pattern, mask):
    """All offsets where pattern matches, ignoring bytes whose mask is 0."""
    runs, current = [], []
    for i, flag in enumerate(mask):
        if flag:
            current.append(i)
        elif current:
            runs.append(current)
            current = []
    if current:
        runs.append(current)
    if not runs:
        return []
    anchor = max(runs, key=len)
    needle = bytes(pattern[i] for i in anchor)
    results, start = [], 0
    while True:
        found = haystack.find(needle, start)
        if found < 0:
            break
        begin = found - anchor[0]
        if begin >= 0 and begin + len(pattern) <= len(haystack):
            # mask 1 = literal byte that must match, mask 0 = wildcard displacement.
            if all(mask[k] == 0 or haystack[begin + k] == pattern[k] for k in range(len(pattern))):
                results.append(begin)
        start = found + 1
        if len(results) > 64:
            break
    return results


def carry_sites(old, new, target_rva, references):
    """Port every site in the old image that references target_rva into the new one."""
    text = None
    for section in new.sections:
        if section["name"] == ".text":
            blob = new.read(section["rva"], section["raw_size"])
            text = (section["rva"], blob)
    if text is None:
        return [], "new image has no readable .text"
    new_text_rva, new_blob = text
    carried = []
    kept = []
    for site, disp_off, length, ref_target in references:
        if ref_target != target_rva:
            continue
        # The same instruction decodes twice, one byte apart, when the REX
        # prefix is skipped. Count it once.
        if any(abs(site - previous) <= 8 for previous in kept):
            continue
        kept.append(site)
        window_rva = site - WINDOW
        window = old.read(window_rva, WINDOW + length + WINDOW)
        if window is None:
            continue
        pattern = bytearray(window)
        mask = mask_other_displacements(old, window_rva, window, WINDOW + disp_off, length)
        for k in range(4):
            mask[WINDOW + disp_off + k] = 0
        matches = find_masked(new_blob, bytes(pattern), mask)
        resolved = []
        for offset in matches[:MAX_SITE_MATCHES]:
            match_rva = new_text_rva + offset
            site_rva = match_rva + WINDOW
            disp_bytes = new_blob[offset + WINDOW + disp_off: offset + WINDOW + disp_off + 4]
            if len(disp_bytes) != 4:
                continue
            resolved.append(site_rva + length + struct.unpack("<i", disp_bytes)[0])
        carried.append({
            "old_site": site,
            "window": bytes(pattern).hex().upper(),
            "masked_window": bytes(pattern[k] if mask[k] else 0x2A for k in range(len(pattern))).hex().upper()
            if False else "".join(format(pattern[k], "02X") if mask[k] else ".." for k in range(len(pattern))),
            "matches": len(matches),
            "resolved": resolved,
        })
    return carried, None


def summarize(carried):
    """Collapse the carried sites into a verdict for one anchor."""
    if not carried:
        return {"status": "no reference site found in the old image", "candidates": [], "sites": 0, "agreeing": 0}
    votes = {}
    for item in carried:
        if item["matches"] == 1 and item["resolved"]:
            votes[item["resolved"][0]] = votes.get(item["resolved"][0], 0) + 1
    unique_sites = sum(1 for c in carried if c["matches"] == 1)
    if not votes:
        return {"status": "no site carried over uniquely", "candidates": [], "sites": len(carried), "agreeing": 0}
    best, count = max(votes.items(), key=lambda kv: kv[1])
    if len(votes) == 1 and count >= 2:
        status = "resolved: %d independent sites agree" % count
    elif len(votes) == 1:
        status = "resolved: one site matched uniquely"
    else:
        status = "AMBIGUOUS: %d sites disagree (%d unique, %d of %d carried)" % (
            len(votes), unique_sites, count, len(carried))
    return {"status": status, "candidates": sorted(votes, key=lambda v: -votes[v]), "sites": len(carried), "agreeing": count}


def instruction_start(pattern, disp_off):
    """Where the instruction carrying a 4-byte displacement begins.

    E8/E9 carry the displacement right after the opcode; ModRM forms carry it
    after the ModRM byte, preceded by an optional REX and an optional 0F opcode.
    """
    if disp_off >= 1 and pattern[disp_off - 1] in (0xE8, 0xE9):
        return disp_off - 1
    if disp_off >= 4 and pattern[disp_off - 3] == 0x0F:
        return disp_off - 4
    return disp_off - 3


def locate_markers(old, new):
    """Carry the recorded byte markers across; resolve displacements when known."""
    text = None
    for section in new.sections:
        if section["name"] == ".text":
            text = (section["rva"], new.read(section["rva"], section["raw_size"]))
    results = []
    for rva, hex_pattern, disp_off in MARKERS_70938:
        pattern = bytes.fromhex(hex_pattern)
        recorded = old.read(rva, len(pattern))
        row = {"old_rva": rva, "pattern": hex_pattern.upper(), "in_old": recorded == pattern if recorded else False}
        if text is not None:
            new_text_rva, blob = text
            mask = [1] * len(pattern)
            if disp_off is not None:
                if disp_off + 4 > len(pattern):
                    row["error"] = "recorded displacement offset %d is outside the %d-byte pattern" % (
                        disp_off, len(pattern))
                    results.append(row)
                    continue
                for k in range(4):
                    mask[disp_off + k] = 0
            matches = find_masked(blob, pattern, mask)
            if not matches and disp_off is None:
                # The window moved as a whole: fall back to the longest halves of
                # the recorded pattern, which still pin down where it went.
                for cut in (len(pattern) // 2, len(pattern) - 6):
                    if cut < 6:
                        continue
                    for piece, base in ((pattern[:cut], 0), (pattern[-cut:], len(pattern) - cut)):
                        hits = find_masked(blob, piece, [1] * len(piece))
                        if hits:
                            row.setdefault("fallback", []).append({
                                "offset": base, "length": len(piece), "hex": piece.hex().upper(),
                                "matches": len(hits),
                                "new_rvas": [new_text_rva + h - base for h in hits[:MAX_PATTERN_MATCHES]]})
                    if "fallback" in row:
                        break
            row["matches"] = len(matches)
            row["new_rvas"] = [new_text_rva + m for m in matches[:MAX_PATTERN_MATCHES]]
            if disp_off is not None and len(matches) >= 1:
                start = instruction_start(pattern, disp_off)
                length = disp_off + 4 - start
                targets = []
                for m in matches[:MAX_PATTERN_MATCHES]:
                    d = struct.unpack_from("<i", blob, m + disp_off)[0]
                    targets.append(new_text_rva + m + start + length + d)
                row["resolved"] = targets
                row["instruction_length"] = length
                old_disp = struct.unpack_from("<i", pattern, disp_off)[0]
                row["old_resolved"] = rva + start + length + old_disp
        results.append(row)
    return results


def check_reference_sites(old):
    """The recorded (instruction, target) pairs from the 70938 checker."""
    rows = []
    for rva, disp_off, length, target in REFERENCE_SITES_70938:
        blob = old.read(rva, length)
        if blob is None or len(blob) != length:
            rows.append({"site": rva, "ok": False, "resolved": None, "expected": target})
            continue
        resolved = rva + length + struct.unpack_from("<i", blob, disp_off)[0]
        rows.append({"site": rva, "ok": resolved == target, "resolved": resolved, "expected": target,
                     "bytes": blob.hex().upper()})
    return rows


# ------------------------------------------------------------------------ report


def run(old_path, new_path, json_path=None):
    old, new = Image(old_path), Image(new_path)
    report = {"old": {"path": str(old_path), "sha256": old.sha256, "size": old.size,
                      "image_base": hex(old.image_base), "file_version": old.file_version,
                      "sections": [(s["name"], hex(s["rva"]), s["raw_size"]) for s in old.sections]},
              "new": {"path": str(new_path), "sha256": new.sha256, "size": new.size,
                      "image_base": hex(new.image_base), "file_version": new.file_version,
                      "sections": [(s["name"], hex(s["rva"]), s["raw_size"]) for s in new.sections]}}
    print("old  %s  %s  %d bytes  base %s  version %s  strings %s" % (
        old.path.name, old.sha256, old.size, hex(old.image_base), old.file_version,
        ", ".join(old.version_strings) or "none"))
    print("new  %s  %s  %d bytes  base %s  version %s  strings %s" % (
        new.path.name, new.sha256, new.size, hex(new.image_base), new.file_version,
        ", ".join(new.version_strings) or "none"))
    print()

    sites = check_reference_sites(old)
    report["recorded_reference_sites"] = sites
    bad = [s for s in sites if not s["ok"]]
    print("recorded 70938 reference sites: %d checked, %d mismatched" % (len(sites), len(bad)))
    for s in bad:
        print("   MISMATCH at 0x%X: resolves 0x%X, recorded 0x%X" % (s["site"], s["resolved"], s["expected"]))
    if bad:
        print("   -> the old image is not the recorded 70938 build; results below are not trustworthy")
    print()

    print("scanning old .text for RIP-relative references ...", flush=True)
    refs = scan_rip_references(old)
    calls = scan_relative_calls(old)
    print("   %d RIP-relative data references, %d relative calls in the old image" % (len(refs), len(calls)))

    print("carrying globals into the new image ...", flush=True)
    globals_out = {}
    for name, rva in sorted(GLOBALS_70938.items(), key=lambda kv: kv[1]):
        carried, error = carry_sites(old, new, rva, refs)
        verdict = summarize(carried)
        verdict["old_rva"] = rva
        verdict["sites_detail"] = carried
        globals_out[name] = verdict
        cand = verdict["candidates"][:3]
        print("   %-32s 0x%08X -> %s" % (name, rva,
              ", ".join("0x%X" % c for c in cand) if cand else verdict["status"]))
        if cand:
            print("        %s" % verdict["status"])
    report["globals"] = globals_out

    print("carrying function addresses into the new image ...", flush=True)
    functions_out = {}
    for name, rva in sorted(FUNCTIONS_70938.items(), key=lambda kv: kv[1]):
        carried, error = carry_sites(old, new, rva, calls)
        verdict = summarize(carried)
        verdict["old_rva"] = rva
        verdict["sites_detail"] = carried
        functions_out[name] = verdict
        cand = verdict["candidates"][:3]
        print("   %-32s 0x%08X -> %s" % (name, rva,
              ", ".join("0x%X" % c for c in cand) if cand else verdict["status"]))
        if cand:
            print("        %s" % verdict["status"])
    report["functions"] = functions_out

    print("carrying recorded markers into the new image ...", flush=True)
    markers = locate_markers(old, new)
    report["markers"] = markers
    for row in markers:
        if not row["in_old"]:
            print("   marker 0x%X absent from the old image (skipped)" % row["old_rva"])
            continue
        if row.get("matches", 0) == 0:
            print("   marker 0x%X -> NOT FOUND in the new image" % row["old_rva"])
        elif "resolved" in row:
            print("   marker 0x%X -> %d match(es), target(s) %s (was 0x%X)" % (
                row["old_rva"], row["matches"],
                ", ".join("0x%X" % t for t in row["resolved"][:3]), row["old_resolved"]))
        else:
            print("   marker 0x%X -> %d match(es) at %s" % (
                row["old_rva"], row["matches"], ", ".join("0x%X" % r for r in row["new_rvas"][:3])))

    print()
    print("A resolved address is a code cross-reference carried across two files.")
    print("It is not a validated field, not a live read, and not proof of semantics.")

    if json_path:
        Path(json_path).write_text(json.dumps(report, indent=1), encoding="utf-8")
        print("wrote %s" % json_path)
    return report


# --------------------------------------------------------------------- self test


def selftest():
    """Build a tiny synthetic PE pair and check the mechanics end to end."""
    import tempfile

    def build(path, base, target, call_target):
        dos = bytearray(64)
        dos[0:2] = b"MZ"
        struct.pack_into("<I", dos, 0x3C, 0x40)
        pe = bytearray(24)          # PE signature (4) + COFF header (20)
        pe[0:4] = b"PE\0\0"
        struct.pack_into("<HH", pe, 4, 0x8664, 2)      # machine, sections
        struct.pack_into("<H", pe, 20, 240)            # optional header size
        opt = bytearray(240)
        struct.pack_into("<H", opt, 0, 0x20B)
        struct.pack_into("<Q", opt, 24, base)
        struct.pack_into("<I", opt, 56, 0x8000)
        struct.pack_into("<I", opt, 116, 16)
        sections = bytearray(80)
        # .text at RVA 0x1000, raw 0x200; .data at RVA 0x2000, raw 0x400
        struct.pack_into("<8s", sections, 0, b".text\0\0\0")
        struct.pack_into("<IIII", sections, 8, 0x200, 0x1000, 0x200, 0x200)
        struct.pack_into("<8s", sections, 40, b".data\0\0\0")
        struct.pack_into("<IIII", sections, 48, 0x200, 0x2000, 0x200, 0x400)
        header = bytes(dos) + bytes(pe) + bytes(opt) + bytes(sections)
        header += b"\0" * (0x200 - len(header))
        text = bytearray(b"\x90" * 0x200)
        # mov rcx,[rip+d] -> target, at .text+0x40 ; lea rax,[rip+d] -> target+8
        site = 0x40
        disp = target - (0x1000 + site + 7)
        text[site:site + 7] = b"\x48\x8B\x0D" + struct.pack("<i", disp)
        site2 = 0x60
        disp2 = (target + 8) - (0x1000 + site2 + 7)
        text[site2:site2 + 7] = b"\x48\x8D\x05" + struct.pack("<i", disp2)
        site3 = 0x80
        text[site3:site3 + 5] = b"\xE8" + struct.pack("<i", call_target - (0x1000 + site3 + 5))
        data = bytearray(b"\x11" * 0x200)
        Path(path).write_bytes(header + bytes(text) + bytes(data))

    with tempfile.TemporaryDirectory() as tmp:
        a, b = Path(tmp) / "a.exe", Path(tmp) / "b.exe"
        build(a, 0x140000000, 0x2000 + 0x40, 0x1000 + 0x100)
        build(b, 0x140000000, 0x2000 + 0x80, 0x1000 + 0x140)   # data and code moved
        ia, ib = Image(a), Image(b)
        assert ia.image_base == ib.image_base == 0x140000000
        refs = scan_rip_references(ia)
        carried, _ = carry_sites(ia, ib, 0x2040, refs)
        verdict = summarize(carried)
        assert verdict["candidates"] == [0x2080], verdict
        calls = scan_relative_calls(ia)
        carried_calls, _ = carry_sites(ia, ib, 0x1100, calls)
        verdict_calls = summarize(carried_calls)
        assert verdict_calls["candidates"] == [0x1140], verdict_calls
        print("selftest OK: a global at 0x2040 carried to 0x2080, a call target 0x1100 to 0x1140")
        print("(%d old references, %d old calls)" % (len(refs), len(calls)))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--old", help="recorded decoded image (e.g. 70938)")
    parser.add_argument("--new", help="new decoded image to derive candidates for")
    parser.add_argument("--json", help="write the full report as JSON")
    parser.add_argument("--selftest", action="store_true", help="exercise the mechanics on a synthetic PE pair")
    args = parser.parse_args()
    if args.selftest:
        selftest()
        return
    if not args.old or not args.new:
        parser.error("--old and --new are required (or use --selftest)")
    run(args.old, args.new, args.json)


if __name__ == "__main__":
    sys.exit(main())

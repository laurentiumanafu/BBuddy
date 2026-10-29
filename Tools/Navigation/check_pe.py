#!/usr/bin/env python3
"""Verifies a built Navigation.dll: architecture, exports against the C# P/Invoke
list, and imports.

The bot crashes at the first marshal if an entry point is missing or the DLL is the
wrong word size, and both failures look identical from the UI ("tripper init
failed"). This check catches them before the DLL is shipped.

    python3 Tools/Navigation/check_pe.py Lib/x64/Navigation.dll [--expect x64|x86]
"""

import re
import struct
import sys
from pathlib import Path

CS_FILE = Path(__file__).resolve().parents[2] / "Tripper/Navigation/NativeMethods.cs"
MACHINES = {0x8664: "x64", 0x014C: "x86"}


def read_pe(path: Path):
    data = path.read_bytes()
    if data[:2] != b"MZ":
        raise SystemExit(f"{path}: not a PE file (no MZ header)")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise SystemExit(f"{path}: no PE signature")
    machine = struct.unpack_from("<H", data, pe + 4)[0]
    sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    data_dirs = opt + (112 if magic == 0x20B else 96)

    table = []
    for i in range(sections):
        off = opt + opt_size + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        table.append((name, va, vsize, raw, rsize))

    def rva_to_off(rva):
        for _name, va, vsize, raw, rsize in table:
            if va <= rva < va + max(vsize, rsize):
                return raw + (rva - va)
        return None

    def cstring(rva):
        off = rva_to_off(rva)
        if off is None:
            return ""
        end = data.index(b"\0", off)
        return data[off:end].decode("ascii", "replace")

    exp_rva = struct.unpack_from("<I", data, data_dirs)[0]
    exports = []
    if exp_rva:
        ed = rva_to_off(exp_rva)
        count = struct.unpack_from("<I", data, ed + 24)[0]
        names_rva = struct.unpack_from("<I", data, ed + 32)[0]
        for i in range(count):
            exports.append(cstring(struct.unpack_from("<I", data, rva_to_off(names_rva) + i * 4)[0]))

    imp_rva = struct.unpack_from("<I", data, data_dirs + 8)[0]
    imports = []
    pos = rva_to_off(imp_rva) if imp_rva else None
    while pos is not None:
        ilt, ts, fc, name_rva, iat = struct.unpack_from("<IIIII", data, pos)
        if not any((ilt, ts, fc, name_rva, iat)):
            break
        imports.append(cstring(name_rva))
        pos += 20

    return machine, exports, imports


def expected_entry_points() -> set:
    """Every name the DLL must export: explicit EntryPoint values, plus the method
    name for any DllImport that does not set one."""
    text = CS_FILE.read_text(encoding="utf-8", errors="replace")
    named = set(re.findall(r'EntryPoint\s*=\s*"([A-Za-z_0-9]+)"', text))

    flat = " ".join(text.split())
    wanted = set(named)
    for attribute, method in re.findall(
            r'(\[DllImport\(DllName[^\]]*?\)\])\s*internal static extern [\w<>\[\]\.\*]+ (\w+)\(', flat):
        if "EntryPoint" not in attribute:
            wanted.add(method)
    return wanted


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = Path(sys.argv[1])
    expect = None
    if "--expect" in sys.argv:
        expect = sys.argv[sys.argv.index("--expect") + 1]

    machine, exports, imports = read_pe(path)
    arch = MACHINES.get(machine, f"0x{machine:04x}")
    print(f"{path.name}: {arch}, {len(exports)} exports, {len(imports)} imported DLLs")
    print("  imports: " + ", ".join(imports))

    ok = True
    if expect and arch != expect:
        print(f"  FAIL architecture is {arch}, expected {expect}")
        ok = False

    wanted = expected_entry_points()
    missing = sorted(wanted - set(exports))
    if missing:
        print(f"  FAIL {len(missing)} entry points missing from the DLL:")
        for name in missing:
            print(f"        {name}")
        ok = False
    else:
        print(f"  OK all {len(wanted)} P/Invoke entry points exported")

    # A CRT import means the DLL needs a redistributable next to the bot.
    heavy = [d for d in imports if d.lower().startswith(("libstdc++", "libgcc", "libwinpthread"))]
    if heavy:
        print(f"  FAIL non-static runtime dependencies: {heavy}")
        ok = False

    print("  result:", "ready to ship" if ok else "NOT ready")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

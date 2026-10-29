#!/usr/bin/env python3
"""Acceptance test for the legacy (MaNGOS / TrinityCore / AzerothCore) tile path.

The 3.3.5a mmaps a server ships are one Detour tile per file, 533.33 yd, header
either 16 bytes or 20 (with `usesLiquids`), and their version field is whatever
that server's extractor wrote - 4 through 8 all exist. Our loader used to accept
only its own multi-tile container, so those files were rejected outright.

`legacyfixture` writes a real mesh in that container: a flat 533 yd square with a
wall across the middle and one doorway. Passing this test means the loader takes
stock files, and takes a tile it cannot use without half-loading it.

    python3 test_legacy_mmaps.py [/tmp/legacy-mmap-test]
"""
import os
import re
import shutil
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "legacyfixture")
CHECK = os.path.join(HERE, "mmapcheck")

# WoW (x, y, z) -> Detour (x, z, y) inside mmapcheck, so travelling along world x
# is travelling along Detour z: it crosses the wall, which runs along Detour x.
START = "100,100,0"
GOAL = "100,500,0"
STRAIGHT = 400.0

failures = []
total = [0]


def check(name, ok, detail=""):
    total[0] += 1
    print(("  PASS " if ok else "  FAIL ") + name + ("" if ok else "\n" + detail.rstrip()))
    if not ok:
        failures.append(name)


def tile_file(store):
    names = [n for n in os.listdir(store) if n.endswith(".mmtile")]
    assert len(names) == 1, names
    return os.path.join(store, names[0])


def rename_to_trinity(store):
    """001.mmap / 0013131.mmtile -> 0001.mmap / 0001_31_31.mmtile."""
    tile = tile_file(store)
    os.rename(os.path.join(store, "001.mmap"), os.path.join(store, "0001.mmap"))
    base = os.path.basename(tile)[3:7]                 # "3131"
    os.rename(tile, os.path.join(store, "0001_%s_%s.mmtile" % (base[:2], base[2:])))


def make_store(work, name, header=16, closed=False):
    path = os.path.join(work, name)
    if os.path.isdir(path):
        shutil.rmtree(path)
    os.makedirs(path)
    args = [FIXTURE, "--out", path, "--header", str(header)]
    if closed:
        args.append("--closed")
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit("legacyfixture failed: " + result.stdout + result.stderr)
    return path


def wrap_as_multi_tile(store, version):
    """Rewrite the fixture's single tile as a one-blob multi-tile container.

    Version 5 is what the 4x4 builds that came before this one wrote, and the
    loader must not care: the container is recognised by its shape, not by a
    number that has already meant three different things.
    """
    path = tile_file(store)
    with open(path, "rb") as f:
        data = f.read()
    magic, dt_version, _version, size = struct.unpack_from("<4I", data, 0)
    blob = data[16:16 + size]
    with open(path, "wb") as f:
        f.write(struct.pack("<5I", magic, dt_version, version, 1, 1))
        f.write(struct.pack("<I", len(blob)))
        f.write(blob)


def run(path, query=True):
    args = [CHECK, "--mmaps", path, "--map", "1"]
    if query:
        args += ["--from", START, "--to", GOAL]
    return subprocess.run(args, capture_output=True, text=True)


def path_length(stdout):
    for line in stdout.splitlines():
        if " yd" in line and "path " in line:
            return float(line.split("path ")[1].split(" yd")[0])
    return None


def main():
    work = sys.argv[1] if len(sys.argv) > 1 else "/tmp/legacy-mmap-test"
    os.makedirs(work, exist_ok=True)

    print("stock single-tile mmaps")
    for header in (16, 20):
        store = make_store(work, "header%d" % header, header=header)
        result = run(store)
        out = result.stdout
        check("%d-byte header: one file, accepted by dtNavMesh" % header,
              result.returncode == 0 and "(1 single-tile), 1 navmesh tiles added, 0 rejected" in out,
              out + result.stderr)
        # The name the loader derives from the start position has to be a file that
        # exists: that is the whole 32 - worldAxis / 533.33 vs 32 + worldAxis / 533.33
        # business, and an off-by-one here makes a stock set silently unusable.
        hint = re.search(r"tile file (\d{7})\.mmtile", out)
        check("%d-byte header: the file it looks for is the file it wrote" % header,
              hint is not None and os.path.exists(os.path.join(store, hint.group(1) + ".mmtile")),
              out)
        length = path_length(out)
        check("%d-byte header: routes around the wall, not through it" % header,
              length is not None and length > STRAIGHT + 50.0,
              out)
        if length is not None:
            print("    path %.1f yd, straight line %.1f yd" % (length, STRAIGHT))

    # TrinityCore and newer name the same files 0001.mmap / 0001_32_32.mmtile.
    store = make_store(work, "trinity-names")
    rename_to_trinity(store)
    result = run(store)
    check("TrinityCore-style names are found without renaming anything",
          result.returncode == 0 and "(1 single-tile), 1 navmesh tiles added, 0 rejected"
          in result.stdout,
          result.stdout + result.stderr)
    length = path_length(result.stdout)
    check("TrinityCore-style names route as well",
          length is not None and length > STRAIGHT + 50.0, result.stdout)

    # The 4x4 container this project wrote before, spelled version 5.
    store = make_store(work, "multi5")
    wrap_as_multi_tile(store, 5)
    result = run(store)
    check("a version-5 multi-tile container loads by shape, not by its version",
          result.returncode == 0
          and "(0 single-tile), 1 navmesh tiles added, 0 rejected" in result.stdout,
          result.stdout + result.stderr)
    length = path_length(result.stdout)
    check("a version-5 multi-tile container routes",
          length is not None and length > STRAIGHT + 50.0, result.stdout)

    # A wall with no doorway: the tile must still load, and the query must fail
    # instead of inventing a straight line.
    closed = make_store(work, "closed", closed=True)
    result = run(closed)
    # No doorway: the tile still loads, and the answer is Detour's partial result -
    # a path that stops against the wall instead of one that walks through it.
    length = path_length(result.stdout)
    check("a walled-off tile loads and the path stops at the wall",
          "1 navmesh tiles added" in result.stdout
          and "0x40000040" in result.stdout
          and length is not None and length < STRAIGHT,
          result.stdout + result.stderr)

    # Detour refuses a blob built for another version, and the rejection has to be
    # counted rather than silently leaving half a tile behind.
    store = make_store(work, "wrongversion")
    victim = tile_file(store)
    with open(victim, "r+b") as f:
        f.seek(16 + 4)                       # blob version, past the 16-byte header
        f.write(struct.pack("<i", 5))
    result = run(store, query=False)
    check("a tile built for another Detour version is rejected",
          "1 tile files (1 single-tile), 0 navmesh tiles added, 1 rejected" in result.stdout,
          result.stdout + result.stderr)

    # A size field that overruns the file must not be trusted: no blind read of
    # whatever is past the end.
    store = make_store(work, "oversize")
    victim = tile_file(store)
    real = os.path.getsize(victim)
    with open(victim, "r+b") as f:
        f.seek(12)
        f.write(struct.pack("<I", real + 4096))
    result = run(store, query=False)
    check("an overrunning size field is refused cleanly",
          "0 navmesh tiles added, 0 rejected" in result.stdout and result.returncode == 1,
          result.stdout + result.stderr)

    # Three tiles side by side, each with its doorway in a different column: the only
    # way across is to pass one doorway, cross the seam, and line up with the next.
    # Detour asks the loader for those neighbours during the search, which is exactly
    # what Navigation::LoadTileForDetour does in the game.
    row = os.path.join(work, "row")
    if os.path.isdir(row):
        shutil.rmtree(row)
    os.makedirs(row)
    for tile, door in ((30, 2), (31, 5), (32, 6)):
        subprocess.run([FIXTURE, "--out", row, "--tile", str(tile), "30", "--door", str(door)],
                       check=True, capture_output=True)
    result = subprocess.run([CHECK, "--mmaps", row, "--map", "1", "--ondemand",
                             "--from", "-600,-600,0", "--to", "-600,400,0"],
                            capture_output=True, text=True)
    out = result.stdout
    check("a route across three tiles is found with on-demand loading",
          result.returncode == 0 and "0 rejected" in out, out + result.stderr)
    found = re.search(r"on-demand: (\d+) tile file", out)
    loaded = [int(found.group(1))] if found else []
    check("the search pulled the neighbouring tiles itself",
          bool(loaded) and loaded[0] >= 3, out)
    length = path_length(out)
    check("the route bends through three separate doorways",
          length is not None and length > 400.0 + 50.0, out)
    if length is not None:
        print("    path %.1f yd, straight line 400.0 yd, %d tile files loaded"
              % (length, loaded[0] if loaded else -1))

    # Continent-scale shape: 7x7 stock-format tiles (0.5 km each, 3.7 km across),
    # a wall in every one of them with its doorway in a different column, and only
    # the two end tiles loaded up front.
    grid = os.path.join(work, "grid7")
    if os.path.isdir(grid):
        shutil.rmtree(grid)
    os.makedirs(grid)
    for tx in range(28, 35):
        for ty in range(28, 35):
            subprocess.run([FIXTURE, "--out", grid, "--tile", str(tx), str(ty),
                            "--door", str((tx * 7 + ty * 3) % 8)],
                           check=True, capture_output=True)
    result = subprocess.run([CHECK, "--mmaps", grid, "--map", "1", "--ondemand",
                             "--from", "-1800,-1800,0", "--to", "1400,1400,0"],
                            capture_output=True, text=True)
    out = result.stdout
    length = path_length(out)
    found = re.search(r"on-demand: (\d+) tile file", out)
    loaded = int(found.group(1)) if found else -1
    check("a 3.7 km route across 49 tiles is found",
          result.returncode == 0 and length is not None and length < 4525.5 * 1.15,
          out + result.stderr)
    check("most of the grid was pulled in by the search, not preloaded",
          loaded >= 40, out)
    if length is not None:
        print("    path %.1f yd, straight line 4525.5 yd, %d tile files loaded" % (length, loaded))

    print("\n%d checks, %d failed" % (total[0], len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

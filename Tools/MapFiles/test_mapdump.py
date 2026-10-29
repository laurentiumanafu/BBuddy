#!/usr/bin/env python3
"""Builds synthetic server-side .map files and checks that mapdump converts them.

The four tiles cover the shapes that actually differ between repacks:

  0013232.map   float heights, a gentle slope in X and a 40 yd cliff halfway
  0013332.map   the same terrain encoded as int16 with a base/range, which is
                what 3.3.5a-era tools emit
  0013432.map   MAP_HEIGHT_NO_HEIGHT: constant height, no grid at all
  0013532.map   a hole mask block in the middle

Heights are checked against the closed form the fixture wrote, not against a
golden file, so a change in axis convention or in the vertical order of the grid
fails here instead of quietly producing walkable cliffs.
"""
import os
import struct
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
NAVGRID = os.path.join(HERE, "..", "NavGrid")

MAP_SIZE_OF_GRIDS = 533.33333
RES = 128
V9 = (RES + 1) * (RES + 1)
V8 = RES * RES

NG_CELLS = 268
NG_CELL_YD = MAP_SIZE_OF_GRIDS / NG_CELLS
NG_WALK, NG_HOLE, NG_STEEP = 0x01, 0x02, 0x08

MAP_AREA_NO_AREA = 0x0001
MAP_HEIGHT_NO_HEIGHT = 0x0001
MAP_HEIGHT_AS_INT16 = 0x0002

failures = []
total = [0]


def check(name, ok, detail=""):
    total[0] += 1
    print(("  PASS " if ok else "  FAIL ") + name + ("" if ok else "  " + detail))
    if not ok:
        failures.append(name)


def clamp(v, lo, hi):
    return lo if v < lo else (hi if v > hi else v)


# --- the terrain, in the file's own index space -----------------------------

def height_at(i, j, t):
    """`i` runs along world X, `j` along world Y, both 0..128 over the tile."""
    h = 100.0 + 0.5 * i
    if i >= 64:                      # a cliff: 40 yd step over 4.17 yd
        h += 40.0
    if t == 2:                       # a mound in the second tile
        d = ((i - 90) ** 2 + (j - 40) ** 2) ** 0.5
        if d < 20:
            h += 30.0 * (1.0 - d / 20.0)
    return h


def v9_float(t):
    return [height_at(i, j, t) for i in range(RES + 1) for j in range(RES + 1)]


def v8_float(v9):
    out = []
    for i in range(RES):
        for j in range(RES):
            a = v9[i * (RES + 1) + j]
            b = v9[(i + 1) * (RES + 1) + j]
            c = v9[i * (RES + 1) + j + 1]
            d = v9[(i + 1) * (RES + 1) + j + 1]
            out.append((a + b + c + d) / 4.0)
    return out


def block(fourcc, flags, grid_height, grid_max, v9=None, v8=None, enc="float"):
    body = struct.pack("<IIff", fourcc, flags, grid_height, grid_max)
    if v9 is not None:
        if enc == "float":
            body += struct.pack("<%df" % len(v9), *v9)
            body += struct.pack("<%df" % len(v8), *v8)
        elif enc == "int16":
            span = grid_max - grid_height
            conv = lambda h: int(clamp(round((h - grid_height) / span * 65535.0), 0, 65535))
            body += struct.pack("<%dH" % len(v9), *[conv(h) for h in v9])
            body += struct.pack("<%dH" % len(v8), *[conv(h) for h in v8])
    return body


def write_map(path, gx, gy, kind):
    """kind: 'float', 'int16', 'flat', 'holes'"""
    area = struct.pack("<256H", *([0] * 256))
    holes = struct.pack("<256H", *([0] * 256))

    if kind == "float":
        v9 = v9_float(1)
        height = block(int.from_bytes(b"MHGT", "little"), 0, 0.0, 0.0, v9, v8_float(v9), "float")
    elif kind == "int16":
        v9 = v9_float(2)
        height = block(int.from_bytes(b"MHGT", "little"), MAP_HEIGHT_AS_INT16,
                       -100.0, 1000.0, v9, v8_float(v9), "int16")
    elif kind == "flat":
        height = block(int.from_bytes(b"MHGT", "little"), MAP_HEIGHT_NO_HEIGHT, 250.0, 250.0)
    elif kind == "holes":
        v9 = [120.0] * V9
        height = block(int.from_bytes(b"MHGT", "little"), 0, 0.0, 0.0, v9, [120.0] * V8, "float")
        words = [0] * 256
        for cell_row in range(4, 8):         # i in [32,64): a 166 yd band
            for cell_col in range(4, 6):     # j in [32,48)
                words[cell_row * 16 + cell_col] = 0xFFFF
        holes = struct.pack("<256H", *words)
    else:
        raise ValueError(kind)

    liquid = block(int.from_bytes(b"MLIQ", "little"), 0x03, 0.0, 0.0) + struct.pack(
        "<BBBBHf", 0, 0, 0, 0, 0, 0.0)

    area_off = 40
    height_off = area_off + len(area)
    liquid_off = height_off + len(height)
    holes_off = liquid_off + len(liquid)

    header = struct.pack("<4sI" + "II" * 4, b"MAPS", int.from_bytes(b"z1.4", "little"),
                         area_off, len(area), height_off, len(height),
                         liquid_off, len(liquid), holes_off, len(holes))
    assert len(header) == 40

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + area + height + liquid + holes)


def write_map_terrain(path, gx, gy, func):
    """A tile whose heights come from a function of world position, so neighbouring
    tiles are continuous across the shared edge."""
    v9 = []
    for i in range(RES + 1):
        for j in range(RES + 1):
            x = MAP_SIZE_OF_GRIDS * ((32 - gx) - i / float(RES))
            y = MAP_SIZE_OF_GRIDS * ((32 - gy) - j / float(RES))
            v9.append(func(x, y))
    height = block(int.from_bytes(b"MHGT", "little"), 0, 0.0, 0.0, v9, v8_float(v9), "float")
    area = struct.pack("<256H", *([0] * 256))
    holes = struct.pack("<256H", *([0] * 256))
    liquid = block(int.from_bytes(b"MLIQ", "little"), 0x03, 0.0, 0.0) + struct.pack(
        "<BBBBHf", 0, 0, 0, 0, 0, 0.0)
    area_off = 40
    height_off = area_off + len(area)
    liquid_off = height_off + len(height)
    holes_off = liquid_off + len(liquid)
    header = struct.pack("<4sI" + "II" * 4, b"MAPS", int.from_bytes(b"z1.4", "little"),
                         area_off, len(area), height_off, len(height),
                         liquid_off, len(liquid), holes_off, len(holes))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + area + height + liquid + holes)


def rewrite_swapped(src_path, dst_path):
    """Copies a .navtile with tile_x and tile_y exchanged, CRC repaired - the shape a
    transposed axis convention would produce."""
    with open(src_path, "rb") as f:
        data = bytearray(f.read())
    magic, version, header_bytes, map_id, tx, ty = struct.unpack_from("<4sHHiii", data, 0)
    struct.pack_into("<i", data, 12, ty)      # tile_x
    struct.pack_into("<i", data, 16, tx)      # tile_y

    height_bytes = (NG_CELLS * NG_CELLS) * 2
    heights = bytes(data[header_bytes:header_bytes + height_bytes])
    flags = bytes(data[header_bytes + height_bytes:header_bytes + height_bytes + NG_CELLS * NG_CELLS])

    struct.pack_into("<i", data, header_bytes - 4, 0)          # crc32 field
    crc = zlib.crc32(bytes(data[:header_bytes]))
    crc = zlib.crc32(heights) ^ crc
    crc = zlib.crc32(flags) ^ crc
    struct.pack_into("<i", data, header_bytes - 4, crc & 0xFFFFFFFF if crc < 0x80000000 else crc - 0x100000000)
    with open(dst_path, "wb") as f:
        f.write(bytes(data))


# --- the .navtile written back ----------------------------------------------

def read_navtile(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, version, header_bytes, map_id, tx, ty, cells, hq, ox, oy, hb, fb, crc = \
        struct.unpack_from("<4sHHiiiHHffiii", data, 0)
    assert magic == b"NAVG", magic
    heights = struct.unpack_from("<%dh" % (cells * cells), data, header_bytes)
    flags = data[header_bytes + len(heights) * 2: header_bytes + len(heights) * 2 + cells * cells]
    return dict(map_id=map_id, tile_x=tx, tile_y=ty, cells=cells, height_q=hq,
                origin_x=ox, origin_y=oy, heights=heights, flags=flags)


def cell_height(tile, col, row):
    return tile["heights"][row * tile["cells"] + col] / float(tile["height_q"])


def main():
    work = sys.argv[1] if len(sys.argv) > 1 else "/tmp/mapdump-test"
    maps_dir = os.path.join(work, "maps")
    out_dir = os.path.join(work, "navtiles")
    os.makedirs(maps_dir, exist_ok=True)
    os.makedirs(out_dir, exist_ok=True)

    print("map feature test")
    print("build")
    write_map(os.path.join(maps_dir, "0013232.map"), 32, 32, "float")
    write_map(os.path.join(maps_dir, "0013332.map"), 32, 33, "int16")
    write_map(os.path.join(maps_dir, "0013432.map"), 32, 34, "flat")
    write_map(os.path.join(maps_dir, "0013532.map"), 32, 35, "holes")
    write_map(os.path.join(maps_dir, "0023232.map"), 32, 32, "float")   # another map, must be filtered out
    check("five .map files written", len(os.listdir(maps_dir)) == 5)

    binary = os.path.join(HERE, "mapdump")
    info = subprocess.run([binary, "info", "--maps", maps_dir], capture_output=True, text=True)
    check("info reads the section table", info.returncode == 0 and '"MAPS"' in info.stdout,
          info.stdout + info.stderr)
    check("info names the height encoding", "float" in info.stdout and "int16" in info.stdout)

    converted = subprocess.run([binary, "convert", "--maps", maps_dir, "--out", out_dir,
                                "--map", "1"], capture_output=True, text=True)
    print(converted.stdout.strip())
    check("convert exits clean", converted.returncode == 0, converted.stderr)
    names = sorted(os.listdir(out_dir))
    check("four tiles converted, other map ignored", names == ["1_32_32.navtile", "1_33_32.navtile",
                                                               "1_34_32.navtile", "1_35_32.navtile"],
          str(names))

    if len(names) != 4:
        print("\n%d checks, %d failed" % (total[0], len(failures)))
        return 1

    print("float heights")
    t1 = read_navtile(os.path.join(out_dir, "1_32_32.navtile"))
    check("tile keeps the map id", t1["map_id"] == 1)
    check("tile_x is the Y grid index", t1["tile_x"] == 32)
    check("tile_y is the X grid index", t1["tile_y"] == 32)

    # height grows along world -X, and col walks toward -X
    west = cell_height(t1, 0, 10)
    east = cell_height(t1, t1["cells"] - 1, 10)
    check("height rises along -X as the file says", east > west + 60.0, "%.1f -> %.1f" % (west, east))

    # cell (0,0) is the north-west centre of the tile
    expect = height_at(0, 0, 1)
    check("north-west corner height matches the fixture",
          abs(cell_height(t1, 0, 0) - expect) < 1.5,
          "%.1f vs %.1f" % (cell_height(t1, 0, 0), expect))

    # the cliff at i=64 sits halfway across the tile
    mid = t1["cells"] // 2
    step = cell_height(t1, mid + 2, 100) - cell_height(t1, mid - 2, 100)
    # the step is one 40 yd jump between two 4.17 yd grid samples, so measured over
    # four cells it is most of that, not all of it
    check("the cliff is at the middle of the tile", step > 25.0, "%.1f" % step)

    steep = sum(1 for f in t1["flags"] if f & NG_STEEP)
    check("the cliff is marked steep", steep > 0, "%d cells" % steep)
    check("steep cells are not walkable",
          all(not (f & NG_WALK) for f in t1["flags"] if f & NG_STEEP))
    walkable = sum(1 for f in t1["flags"] if f & NG_WALK)
    check("most of the tile is still walkable", walkable > 0.8 * len(t1["flags"]),
          "%d of %d" % (walkable, len(t1["flags"])))

    print("int16 heights")
    t2 = read_navtile(os.path.join(out_dir, "1_33_32.navtile"))
    worst = 0.0
    for row in (0, 60, 150, 260):
        for col in (0, 60, 150, 260):
            x = (32 - t2["tile_y"]) * MAP_SIZE_OF_GRIDS - col * NG_CELL_YD
            y = (32 - t2["tile_x"]) * MAP_SIZE_OF_GRIDS - row * NG_CELL_YD
            expected = height_at(128.0 * ((32 - t2["tile_y"]) - x / MAP_SIZE_OF_GRIDS),
                                 128.0 * ((32 - t2["tile_x"]) - y / MAP_SIZE_OF_GRIDS), 2)
            worst = max(worst, abs(cell_height(t2, col, row) - expected))
    check("int16 heights decode within a yard", worst < 1.0, "worst %.2f yd" % worst)
    check("the int16 mound is higher than its surroundings",
          cell_height(t2, 0, 0) < cell_height(t2, t2["cells"] // 2, t2["cells"] // 2))

    print("flat terrain")
    t3 = read_navtile(os.path.join(out_dir, "1_34_32.navtile"))
    vals = set(t3["heights"])
    check("no height field gives one constant height", len(vals) == 1, str(sorted(vals)[:4]))
    check("flat terrain is fully walkable", all(f & NG_WALK for f in t3["flags"]))
    check("flat height is the header value", abs(cell_height(t3, 5, 5) - 250.0) < 0.3)

    print("holes")
    t4 = read_navtile(os.path.join(out_dir, "1_35_32.navtile"))
    holes = sum(1 for f in t4["flags"] if f & NG_HOLE)
    check("the mask produces holes", holes > 0, "%d cells" % holes)
    check("hole cells are not walkable",
          all(not (f & NG_WALK) for f in t4["flags"] if f & NG_HOLE))
    # the mask covers i in [32,64) for the first 8x8 cell rows.
    # the mask covers i in [32,64) and j in [32,48): i grows with col, j with row
    def w(col, row):
        return bool(t4["flags"][row * t4["cells"] + col] & NG_WALK)
    check("holes sit where the mask says",
          w(0, 0) and not w(100, 84) and w(100, 170),
          "corner=%d band=%d south of band=%d" % (w(0, 0), w(100, 84), w(100, 170)))
    band = sum(1 for f in t4["flags"] if not (f & NG_WALK))
    check("the holed band is a minority of the tile", 0 < band < 0.6 * len(t4["flags"]),
          "%d of %d" % (band, len(t4["flags"])))

    print("seam check")
    seam_maps = os.path.join(work, "seam-maps")
    seam_tiles = os.path.join(work, "seam-tiles")
    for d in (seam_tiles,):
        if os.path.isdir(d):
            for name in os.listdir(d):
                os.remove(os.path.join(d, name))
        os.makedirs(d, exist_ok=True)

    def terrain(x, y):
        return 300.0 + 0.02 * x + 0.03 * y + 18.0 * ((x / 400.0) % 1.0)

    for gx, gy in ((32, 32), (33, 32), (32, 33), (33, 33)):
        write_map_terrain(os.path.join(seam_maps, "001%02d%02d.map" % (gy, gx)), gx, gy, terrain)

    subprocess.run([binary, "convert", "--maps", seam_maps, "--out", seam_tiles, "--map", "1"],
                   capture_output=True, text=True)
    seams = subprocess.run([binary, "check", "--tiles", seam_tiles, "--map", "1"],
                           capture_output=True, text=True)
    check("adjacent tiles line up", seams.returncode == 0, seams.stdout + seams.stderr)
    check("seam report names the pairs", "median" in seams.stdout and "west edge" in seams.stdout)

    swapped = os.path.join(work, "swapped-tiles")
    os.makedirs(swapped, exist_ok=True)
    for name in os.listdir(swapped):
        os.remove(os.path.join(swapped, name))
    for name in os.listdir(seam_tiles):
        rewrite_swapped(os.path.join(seam_tiles, name), os.path.join(swapped, name))

    bad = subprocess.run([binary, "check", "--tiles", swapped, "--map", "1"],
                         capture_output=True, text=True)
    check("a transposed grid is caught", bad.returncode != 0 and "transposed" in bad.stdout,
          bad.stdout + bad.stderr)

    print("renderer input")
    builder = os.path.join(HERE, "..", "NavMeshBuilder")
    suffix = ".exe" if os.name == "nt" else ""
    mmapbuild = os.path.join(builder, "mmapbuild" + suffix)
    mmapcheck = os.path.join(builder, "mmapcheck" + suffix)
    if os.path.exists(mmapbuild) and os.path.exists(mmapcheck):
        mmaps = os.path.join(work, "mmaps")
        os.makedirs(mmaps, exist_ok=True)
        built = subprocess.run([mmapbuild, "--tiles", out_dir, "--map", "1", "--out", mmaps],
                               capture_output=True, text=True)
        check("mmapbuild accepts the tiles", built.returncode == 0 and "ADT files written" in built.stdout,
              built.stdout + built.stderr)
        # both points are on the high side of the fixture's cliff, so the walk is
        # possible; tile (32,32) covers world x,y in (-533.33, 0]
        checked = subprocess.run([mmapcheck, "--mmaps", mmaps, "--map", "1",
                                  "--from", "-500,-500,200", "--to", "-300,-300,176"],
                                 capture_output=True, text=True)
        check("Detour finds a path across the converted tile",
              checked.returncode == 0 and "path" in checked.stdout.lower(),
              checked.stdout + checked.stderr)
        print("\n".join("  " + line for line in checked.stdout.strip().splitlines()[-6:]))
    else:
        print("  SKIP mmap pipeline (mmapbuild/mmapcheck not built)")

    print("\n%d checks, %d failed" % (total[0], len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

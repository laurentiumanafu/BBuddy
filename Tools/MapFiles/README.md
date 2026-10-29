# MapFiles — server `.map` files to real mmaps

Turns the terrain files that ship with a MaNGOS-family repack into the same
`.navtile` files `navbake` produces from ADTs, so `mmapbuild` can turn a whole
continent into Detour mesh without a CASC extractor.

```
<repack>\data\maps\0013232.map    server terrain, 129x129 heights + hole mask
        |  mapdump convert          1-2 s per tile
        v
<TEMP>\bbuddy-maptiles-1\1_32_32.navtile
        |  mmapbuild                ../NavMeshBuilder
        v
<game dir>\mmaps\0013232.mmtile + 001.mmap
```

One command on Windows:

```bat
maps-to-mmaps.cmd "B:\World of Warcraft\ForeverRepack\data" 1 C:\BBuddy
```

## Why this route

The ADT route needs the CASC index of build 70291 to read, and the public ADT
extractors are MPQ-only (`Likon69/extractor-csharp`) or need a CDN that no longer
serves this build. The repack that ships next to the client already has the same
terrain in its own format, and that format is fully documented in the server
source (`vmangos/src/game/Maps/GridMap.cpp`): a 129x129 height grid, a 128x128
half grid, a 16x16 hole mask, per tile.

## What it gives, and what it does not

| | from `.map` | needs `vmaps` |
| --- | --- | --- |
| hills, slopes, cliffs | yes | |
| holes, cave mouths, terrain cut-outs | yes | |
| shorelines and water depth | yes | |
| buildings, bridges, walls, ships | | yes |
| dungeon entrances, ramps in WMOs | | yes |

So the mesh is honest about terrain and blind to architecture. A route can walk
through a house. Everything the client's own collision stops you on is still
stopped by the client; the bot's job is to not plan through it, and on terrain it
now does not.

Liquid is deliberately ignored: deep water is swimmable, so marking it unwalkable
would cut rivers that the character can cross. The height field under the water
is what gets baked.

## Reading it

`mapdump` reads the file through its own section table (`area`, `height`,
`liquid`, `holes` offsets and sizes in the header), not a fixed layout, so the
1.12, 2.4.3 and 3.3.5a revisions all work. Heights are decoded as float, int16 or
int8 according to the height header's flags, with the base and range applied.

Two index conventions have to line up, and both are taken from the server rather
than guessed:

* the tile named `0013232.map` is `map`, `gy`, `gx` - world Y first;
* inside the file, index `i` runs along **world X** and `j` along **world Y**,
  both `0..128` over the tile, growing as the world coordinate **decreases**.

`navgrid` stores `tile_x` from world Y and `tile_y` from world X, so the converter
writes `1_<gy>_<gx>.navtile`. The self-test checks this by writing terrain with a
known closed form and reading the heights back out of the tile, which is how an
axis flip would be caught.

## Commands

```bat
mapdump info    --maps <dir> [--map 1]                  what the files contain
mapdump convert --maps <dir> --out <dir> [--map 1] [--slope 45] [--verbose]
```

`info` prints the magic (`MAPS`), the format version (`z1.4`), the section table
and the height encoding for the first files, plus how many distinct map ids are
present. If `convert` ever finds nothing, that is the output to keep.

## Self-test

```sh
make test        # 25 checks: names, float/int16/flat/hole tiles, then mmapbuild
                 # and a real Detour findPath over the result
```

The fixture writes five `.map` files: float terrain with a 40 yd cliff, the same
terrain as int16, a flat tile with no height field, a tile with a hole band, and a
file for a different map id that must be filtered out. Every height assertion is
against the closed form the fixture wrote, not a golden file.

Requires `../NavMeshBuilder/mmapbuild` and `mmapcheck` for the last two checks;
they are skipped when those are not built.

## Limits

* Terrain only, as described above.
* One 533.33 yd tile at a time; a continent is 256 files per map, which is about
  a minute of conversion and half a minute of mesh building.
* The hole mask is used as-is. A repack assembled from a different client build
  can have a mask that disagrees with the client you are playing, which shows up
  as a route that plans over a cave mouth.

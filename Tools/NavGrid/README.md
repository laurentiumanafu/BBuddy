# navgrid — offline routing geometry, no native library

The bot's normal navigation stack is Tripper/Detour behind `Lib\Navigation.dll`.
That library is **32-bit** (`PE machine 0x014C`) and its tiles are Warcraft 3.3.5a
`.mmtile` data, so on the x64 client (`wowb.exe` build 70291) it cannot load and
would not match the terrain anyway.

This folder replaces it with something that needs no native code and no C++ on
your machine: a **walkability grid per ADT tile**, baked offline, plus A* and
line-of-sight smoothing. The C here is the reference implementation with unit
tests; `Styx/Logic/Pathing/NavGrid/NavGrid.cs` is the C# port the bot uses at
runtime, and it carries the same assertions as a self-test.

## Build and test

```
cd Tools/NavGrid
make            # builds navbake and test_navgrid with gcc
make test       # 44 checks, all synthetic - no WoW data needed
```

On Windows, `gcc` is not required: `navbake` is a developer tool, and the same
bake can be run on the machine that has the ADT files (see below).

## Tools

```
# bake one ADT tile
navbake bake --adt /tmp/1_32_32.adt --map 1 --tx 32 --ty 32 --slope 45 --out <tiles-dir>

# route between two world positions using whatever tiles are present
navbake path --tiles <tiles-dir> --map 1 --from -100,-100,50 --to -430,-100,50

# sanity-check the geometry at a position you are standing on in game
navbake probe --tiles <tiles-dir> --map 1 -283,-216,50
```

`probe` is the command that decides whether the bake is trustworthy: it prints the
tile's height and walkability at those coordinates, plus the nearest walkable cell.
Compare its height with the Z the bot logs for your character. A delta of tens of
yards means the ADT axis convention in `navgrid.c` (`ng_tile_init`) is flipped for
this client — one function to fix, and `probe` proves it in one command.

## Format

`<map>_<tx>_<ty>.navtile`, 214 KB each:

| field | meaning |
|---|---|
| 44-byte header | magic `NAVG`, version, map, tile indices, origin, sizes, CRC32 |
| `int16[71289]` | heights in quarter yards |
| `uint8[71289]` | per-cell flags: `WALK`, `HOLE`, `LIQUID`, `STEEP` |

267 cells per ADT tile at 1.9975 yd each, so tiles are exactly contiguous:
`267 * 1.9975 = 533.3333 yd`. That constant is deliberate — a rounded 2.0 yd cell
makes neighbouring tiles drift apart by 0.67 yd each.

## What the tests prove

44 checks over synthetic tiles: a smoothed straight path on open ground; a detour
around a wall that must use its gap; refusal to path into a sealed box; refusal to
cut a diagonal between two blocked cells (the classic grid pathfinder bug); slope
flagging at 0.4 yd (walkable) versus 6 yd (not); file round-trip plus CRC rejection
of a corrupted tile; routing across a tile seam; and baking a synthetic ADT with
four `MCNK`/`MCVT` chunks.

## Limits, stated plainly

* **Doodads are not baked.** Walls made of terrain (cliffs, hills) are in the grid;
  walls made of buildings, fences or props are not. A city interior looks walkable
  to this baker. `_obj0.adt` holds those placements and is not read yet.
* **Liquids are ignored.** The `LIQUID` flag exists but the baker does not set it
  from `MCLQ`/`MH2O`, so water is treated as ground.
* **Z is coarse.** Heights come from the chunk height field, good to about a yard;
  it is there to reject cliffs and to log, not to place the character.
* **The 70291 ADTs have not been read yet.** The parser is validated against
  generated files in the documented layout, not against Blizzard data. The root
  ADT of a modern client still carries `MCNK`/`MCVT`, which is what this reads.

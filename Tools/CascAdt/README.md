# CascAdt — get the terrain ADTs out of the game's own data folder

The navmesh pipeline needs root ADTs. The extractor the upstream C++ repo points at
(`extractor-csharp` / `Extractor_projects`) is private, and every CASC tool that reads
files *by name* needs two manifests Blizzard does not ship inside the game folder:
the root file (name → CKey) and the encoding file (CKey → EKey). Both normally come
from the CDN, and the CDN for a closed-beta build is long gone.

Neither manifest is needed to build a navmesh. `cascdump` reads the store from the
bottom up instead:

```
Data\data\*.idx     every EKey in the install, with archive and offset
Data\data\data.0xx  the BLTE-wrapped payloads
```

It walks every index entry, decodes the payload, and keeps the ones that are terrain.
A root ADT starts with `MVER` and contains `MCNK`, which no other file type in the
store does. The tile index is not in a name, so it is solved from the data:

* **primary:** the first `MDDF`/`MODF` record, which carries absolute world
  coordinates (doodad and world-model placements). Land tiles always have them.
* **secondary:** the `MCNK` chunk position field, accepted only when every chunk in
  the file agrees on the same tile — a wrong field offset scatters over the grid,
  so the agreement check rejects it instead of writing garbage.
* anything with no position data at all is kept as `unknown_<ekey>.adt` and listed in
  the CSV, never silently dropped.

Output uses the naming `navbake` already accepts, `<map>_<x>_<y>.adt`.

## Use it

```bat
:: 1. what is in there
cascdump.exe list --data "C:\Games\WoW\Data\data"

:: 2. extract, with a coordinate you know from the game
cascdump.exe dump --data "C:\Games\WoW\Data\data" --out C:\export\adt --map 1 ^
                  --ref -614.8,-4254.2,39
```

A full install has hundreds of thousands of index entries, so before decoding
anything the tool reads a few KB per entry and inflates only the first BLTE frame: a
data file that does not start with `MVER` cannot be terrain, and it is dropped without
a full decode. `--no-prefilter` turns that off if a tile ever goes missing.

`--ref` prints the terrain height of every candidate tile that covers that point. The
continent you are standing on is the one whose height matches the Z the bot logs, so
two candidates that both claim tile (32,32) become distinguishable: Kalimdor's terrain
at a given spot and Eastern Kingdoms' terrain at the same coordinates are different.

Or run the whole chain in one go:

```bat
Tools\build-navmesh.cmd "C:\Games\WoW" 1 -614.8,-4254.2,39 "C:\BBuddy"
```

The pieces it calls:

```bat
Tools\NavGrid\bin-x64\navbake.exe bakeall --adts C:\export\adt --map 1 --out C:\tiles
Tools\NavMeshBuilder\bin-x64\mmapbuild.exe --tiles C:\tiles --map 1 --out <game dir>\mmaps
```

## When the index does not parse

Stores differ in how the entries are laid out, and the tool does not assume: it
scores every layout it knows against the store's own limits (archive number below the
archive count, offset below the segment size, size below 64 MB) and also tries
neighbouring widths for the span split. `--verbose` says which combination won:

```
0000000001.idx: layout block+guard, first record at 32, stride 18, span split at 30 bits, score 1.75
0000000004.idx: layout paged, first record at 4096, stride 18, span split at 30 bits, score 1.05
```

Known layouts:

| name | shape |
| --- | --- |
| `block+guard` | u32 block length + u32 hash, then 18-byte entries back to back (hash verified, worth +1.5) |
| | the hash is the one Blizzard writes - **chained**, each 18-byte record hashed with the previous result as the seed - but a single hash over the whole block is accepted too |
| `block` | the same entries with no block guard |
| `paged+guard` | 0x200-byte pages, 4-byte hash in front of each entry, 2 bytes of padding |
| `paged` | 0x200-byte pages with the entries packed and no per-entry hash |

If nothing matches, there are two ways forward. The first is a report:

```bat
cascdump.exe inspect --data "B:\World of Warcraft\Data\data" --verbose
```

and the second is to ignore the index completely:

```bat
cascdump.exe carve --data "B:\World of Warcraft\Data\data" --out <dir> --map 1
```

`carve` reads the `data.###` archives themselves and looks for BLTE streams, whose
frame tables carry their own lengths. Each candidate stream is then decoded and
checked for terrain. No index, no root manifest, no listfile: if the terrain is in
the store, this finds it. It is slower - every archive is read - and it cannot
recover payloads stored as a single frame, whose length only the index knows.
`build-navmesh.cmd` runs it automatically when `list` comes back empty.

Then:

```bat
cascdump.exe inspect --data "B:\World of Warcraft\Data\data"
```

It prints, for the first three index files, the header fields, the first 48 bytes, the
first 48 bytes at offset 0x1000, the detected layout, and the first three entries it
can read. That output identifies the format - it is the report to keep if the
extractor still finds nothing.

## Files

| file | role |
| --- | --- |
| `cascdump.c` | the extractor: index parse, BLTE decode, ADT sniffing, tile solving |
| `make_test_casc.py` | builds a synthetic CASC store with known contents |
| `zlib/` | zlib 1.2.11 sources (Zlib licence), vendored because nothing else is available to link on both targets |
| `bin-x64/cascdump.exe` | prebuilt x64 tool |
| `Makefile` | `make`, `make test`, `make win` |

## The formats, and where each assumption comes from

Checked against CascLib (`ladislav-zezula/CascLib`), which is the reference reader.

**Index (.idx, version 7)**

```
[0]  u32 header block size (16)   u32 hash of the 16-byte header
[8]  u16 revision 7, u8 bucket, u8 flags, u8 spanSize=4, u8 spanOffset=5,
     u8 keyBytes=9, u8 segmentBits, u64 maxFileOffset
     -> either one guarded entry block, or 0x1000-aligned pages of guarded entries
```

CascLib skips 8 bytes of padding after the header before the entry block, but not every
writer emits them, so both offsets are tried and the block hash decides. Entries are
18 bytes: 9-byte EKey, 5-byte big-endian (archive index in the high bits, offset
below), 4-byte big-endian encoded size. Page layout guards each entry with a 32-bit
hash of the entry plus one byte, with the top bit forced on.

**Payload (BLTE)**

`"BLTE"`, u32 header size (big endian). Zero means one frame and the body starts at 8.
Otherwise the header size already counts the 12-byte prefix plus 24 bytes per frame,
so the frame data starts exactly at `headerSize`. Each frame is u32 encoded size,
u32 content size, 16-byte hash, then a mode byte: `N` stored, `Z` zlib. `E` (encrypted)
and `F` (recursive) are reported and skipped, not guessed at.

**Tile from a world position**

```
tile_x = 32 - (int)(y / 533.3333)
tile_y = 32 - (int)(x / 533.3333)
```

Truncation, not floor: with `floor` every western tile shifts by one. This is the same
convention as `ng_adt_tile_of` in `Tools/NavGrid`.

## Tests that are actually run

`make test` builds a store with two "continents" that both own tile (32,32) at heights
50 and 150, a tile with chunk positions only, a tile with no position data, a WDT
lookalike, a stored (uncompressed) blob, an ADT split over two zlib frames, both index
layouts, and a zero-length span entry. It then asserts that:

* both index layouts parse (5 entries in a block, 2 in pages),
* the zlib-framed ADT decodes and its height at `-100,-100` is exactly 50.00,
* the competing tile is kept as `alternates/1_32_32__b1b1b1.adt` with height 150.00,
* the chunk-position tile lands on (32,34),
* the tile with no position data is reported as unknown rather than dropped.

The extracted ADTs are then fed through `navbake` and `mmapbuild`, and the resulting
mmaps answer a real Detour query — the seam test in `Tools/NavMeshBuilder/README.md`.

## Known limits, stated up front

* **Continent identity is not in the data.** Tile (32,32) exists on both Kalimdor and
  Eastern Kingdoms. The first candidate found keeps the plain file name; the other is
  written under `alternates/` with its EKey, and both are listed with the height at
  `--ref` so the right one can be picked. For a whole continent this needs a growth
  pass that clusters tiles by shared-edge heights — not written yet.
* Encrypted (`E`) entries and multi-archive spans (encoded size 0) are skipped.
* An ADT with no doodads *and* no chunk positions is written as `unknown_*`; it cannot
  be placed on the world grid.
* Stores whose index files use a layout beyond the four below will report no entries;
  `inspect` exists for that case.
* The tool has been tested against generated stores, not against a real 70291 install.
  The first run on real data is the actual test: `list` should report hundreds of
  thousands of entries, and `dump` should report thousands of ADTs with very few
  `unknown_*` files. If `dump` finds nothing, the store is encrypted or `--data` points
  at the wrong folder.

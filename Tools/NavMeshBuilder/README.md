# NavMeshBuilder — ADTs to Detour mmaps

Builds the `.mmtile` files that **`Lib\x64\Navigation.dll`** loads at runtime, in the
exact multi-tile format its `MoveMap.cpp` reads. This is the tool that turns an ADT
export into real mesh pathing; without it the DLL loads, answers "no mmaps", and
`Navigator.FindPath` returns zero points.

## The pipeline

```
world\maps\<map>\*.adt          <- CASC export (CASCExplorer / wow.tools.local)
        |  navbake bakeall            Tools/NavGrid    -> *.navtile
        v
   <tmp>\<map>_<x>_<y>.navtile
        |  mmapbuild                  this folder      -> *.mmtile + <map>.mmap
        v
<game dir>\mmaps\0013232.mmtile        <- what the DLL reads, next to Navigation.dll
<game dir>\mmaps\001.mmap
```

On Windows, one command does all of it (no compiler needed, the `.exe`s are checked in):

```bat
Tools\NavMeshBuilder\make-mmaps.cmd C:\export\kalimdor 1 C:\CopilotBuddy
```

## What each file is

| file | role |
| --- | --- |
| `mmapbuild.cpp` | `.navtile` → `dtCreateNavMeshData` → **MMAP v6 multi-tile** files. Splits every ADT tile into a 4×4 grid of Detour tiles (67×67 cells each) because a `dtPoly` index is 16-bit and one full ADT has ~72k cells. Emits portal links across both seams so a route can leave the tile it started in. |
| `mmapcheck.cpp` | Loads the written files with the *same* Detour sources the DLL was compiled from, then runs `findNearestPoly` → `findPath` → `findStraightPath`. With no `--from/--to` it just proves every blob is accepted; that is the check the bake script runs. Accepts both our 4×4 container and stock single-tile files. |
| `legacyfixture.cpp` | Writes one 533 yd Detour tile in the **stock** (MaNGOS/TC/AC) single-tile container: a flat square with a wall and one doorway, so the legacy loader path can be tested against a real mesh. `--header 16\|20` picks the header size, `--closed` removes the doorway. |
| `test_legacy_mmaps.py` | Acceptance test for the legacy path: both header sizes route *around* the wall (not through it), a walled-off tile returns Detour's partial path instead of a straight line, a blob built for another Detour version is rejected, and an overrunning size field is refused without a blind read. |
| `make-mmaps.cmd` | Windows end-to-end driver with error checks at each step. |
| `../SelfCheck.cmd` | One pass over what can be checked without the game: DLL hash and size, which naming scheme the mmaps folder holds, `mmapcheck` over every tile of map 1, and the navmesh lines from the newest log. Writes `Tools\selfcheck.txt`. |
| `../UseExistingMmaps.cmd` | Points the DLL at an mmaps folder that already exists (junction beside every `Navigation.dll`), reports the naming scheme it finds, then load-checks every tile of map 1. |
| `Makefile` | Linux/macOS build of both tools against a `Navigation-C-` checkout. |
| `bin-x64/*.exe` | Prebuilt x64 tools: `mmapbuild.exe`, `mmapcheck.exe`. |

`navbake.exe` (with `bakeall`) and `test_navgrid.exe` live in `Tools\NavGrid\bin-x64\`.

## Format written (what the loader demands)

`<map>.mmap` is a bare `dtNavMeshParams` dump (28 bytes with `DT_POLYREF64` off,
40 with it — this build uses the 64-bit polyref layout):

```
orig  (-17066.7, 0, -17066.7)
tile  133.3333 x 133.3333 yd
maxTiles 8192   maxPolys 8192
```

Each `<mmm><xx><yy>.mmtile` (7-digit name, from the loader's own `WorldToTile`) is:

```
u32 "PAMM" (0x4d4d4150)   u32 DT_NAVMESH_VERSION
u32 6 (MMAP_MULTI_TILE_VERSION)   u32 16 (sub-tile count)
u32 1 (flags)
16 × (u32 blobSize, blob)     0 size = empty slot
```

The header in the upstream `Navigation.h` still says version 5; the loader's own
constant for this container is **6**. Do not "fix" this back to 5.

## Stock 3.3.5a mmaps load too

The files a MaNGOS / TrinityCore / AzerothCore install already has are a *different*
container: one Detour tile per file, 533.33 yd tiles, and a header that may be 16 or
20 bytes:

```
u32 "PAMM"   u32 dtVersion   u32 mmapVersion   u32 blobSize   [u32 usesLiquids]
byte blob[blobSize]          <- starts with DT_NAVMESH_MAGIC, DT_NAVMESH_VERSION 7
```

The version field is *not* a gate: 4 through 8 all exist in the wild, and Detour does
not care how a tile file is arranged - only that the tile was built against the
`<map>.mmap` params beside it. So `MoveMap.cpp` probes for `DT_NAVMESH_MAGIC` at
offset 16 and then 20, reads exactly `blobSize` bytes, and lets `dtNavMesh::addTile`
decide. `blobSize == 0` or `> 64 MB` is refused before any allocation.

Practical consequence: the 3.3.5a mmap set you already have for Kalimdor and Eastern
Kingdoms can be fed to the x64 DLL as-is. Our own 4x4 output and stock files must not
be **mixed inside one map**: a `dtNavMesh` is initialised from one params file, and the
tile size in it (133.33 vs 533.33 yd) has to match every tile added. Pick one per map.

Both file-naming schemes are read, because they do not overlap:

| extractor | params | tile |
| --- | --- | --- |
| MaNGOS / cmangos / AzerothCore | `mmaps\\001.mmap` | `mmaps\\0013232.mmtile` |
| TrinityCore (and newer) | `mmaps\\0001.mmap` | `mmaps\\0001_32_32.mmtile` |

The tile manifest is built by parsing every `*.mmtile` beside the DLL, so a directory
holding either scheme - or both, for different maps - is usable without renaming
anything.

`Navigation.dll` resolves `mmaps\` next to itself, so the directory the loader reads is
the one beside the DLL - `Lib\x64\mmaps\` in this repo. Point it at an existing set
instead of copying 2 GB:

```bat
mklink /J "C:\BBuddy\Lib\x64\mmaps" "B:\World of Warcraft\ForeverRepack\data\mmaps"
```

## Tile numbering — the second trap

Three different numbers describe "which tile":

| number | formula | used by |
| --- | --- | --- |
| blob tile index (`dtMeshHeader::x/y`) | `floor((meshAxis - orig) / tileSize)`, i.e. `32 + worldAxis/533.33` | Detour: `calcTileLoc`, every query, the on-demand loader |
| grid / file number | `32 - worldAxis/533.33` | the `.mmtile` file name, `.map` file names, `WorldToTile` |
| this build's 4x4 sub-tile | 16 blobs per file, index `127..130` for ADT 32 | our own `mmapbuild` output |

They are **mirrors of each other**: for a 533 yd tile, `fileNumber = 64 - blobIndex`.
vmangos has both halves in the source (`TileWorker.cpp` derives `params.tileX` from
`(bmin+bmax)/2 - orig`, while `MapBuilder.cpp` writes the file as
`%03u%02u%02u` = map, tileY, tileX with `bmax[0] = (32 - tileX)*GRID`). Tile 32 is the
one place where all of them agree, which is why a bug here hides until the second
tile. `legacyfixture` mirrors the name on purpose (`<map><64-y><64-x>.mmtile`) so the
test would catch a regression, and `Navigation::LoadTileForDetour` loads the 3x3
neighbourhood so a single off-by-one cannot stop a route dead at a seam.

## Axis convention — the trap that costs a day

The DLL queries in **Detour `(x, y, z) = WoW `(y, z, x)`**. A mesh built in WoW order
loads with no error, reports tiles added, and then never finds a polygon near the player.
Both tools swizzle, so a mismatch here shows up as "no polygon near the start" rather
than silently bad paths.

## Building

Linux/macOS:

```bash
git clone --depth 1 https://github.com/Likon69/Navigation-C- /tmp/nav
make NAV_SRC=/tmp/nav/Navigation        # needs DT_POLYREF64, see Makefile
```

x64 Windows `.exe`s from Linux (what produced `bin-x64/`):

```bash
zig c++ -target x86_64-windows-gnu -O2 -w -DDT_POLYREF64 \
    -I $NAV/Detour/Include -I Tools/NavGrid \
    -o Tools/NavMeshBuilder/bin-x64/mmapbuild.exe \
    Tools/NavMeshBuilder/mmapbuild.cpp Tools/NavGrid/navgrid.c $DETOUR_SOURCES

zig cc -target x86_64-windows-gnu -O2 \
    -o Tools/NavGrid/bin-x64/navbake.exe \
    Tools/NavGrid/navbake.c Tools/NavGrid/navgrid.c Tools/NavGrid/adt.c
```

`DETOUR_SOURCES` = the 7 `Detour/Source/Detour*.cpp` files (`Alloc`, `Assert`,
`Common`, `NavMesh`, `NavMeshBuilder`, `NavMeshQuery`, `Node`).
`-DDT_POLYREF64` is mandatory: without it `dtNavMesh::init` returns
`DT_FAILURE|DT_INVALID_PARAM` because the salt bits no longer fit `maxTiles 8192`.

## Verification that has been run

Synthetic two-ADT map with a wall between the points (`Tools\NavGrid\make_test_map.py`):

```
navbake bakeall   -> 2 ADTs baked, 0.14M walkable cells
mmapbuild         -> 142,648 polys, 147,207 verts, 16/16 sub-tiles per ADT
mmapcheck         -> 2 tile files, 32 tiles added, 0 rejected
   -100,-100 -> -120,-120   28.3 yd path / 28.3 yd straight
   -100,-100 -> -200,-100  100.0 yd path / 100.0 yd straight
   -100,-100 -> -430,-100  451.5 yd path / 330.0 yd straight
      route: (-100,-100) (-264.7,-199) (-266.7,-201) (-300.5,-201)
             (-386,-117.4) (-430,-100)
   -100,-100 -> -700,-100  709.8 yd path / 600.0 yd straight
```

The detour through the gap at x≈−267 and back north is the proof that the seams link:
a straight-line answer here would mean the tiles were emitted but never joined.

## Verification that has been run, legacy container

`python3 test_legacy_mmaps.py /tmp/legacy-mmap-test` - 18 checks, 0 failed:

```
16-byte header: one file, accepted by dtNavMesh
16-byte header: routes around the wall, not through it
                path 460.7 yd, straight line 400.0 yd
20-byte header: one file, accepted by dtNavMesh
20-byte header: routes around the wall, not through it
                path 460.7 yd, straight line 400.0 yd
TrinityCore-style names are found without renaming anything
a version-5 multi-tile container loads by shape, not by its version
a version-5 multi-tile container routes
a walled-off tile loads and the path stops at the wall
a tile built for another Detour version is rejected
an overrunning size field is refused cleanly
a route across three tiles is found with on-demand loading
the search pulled the neighbouring tiles itself
the route bends through three separate doorways
                path 1024.0 yd, straight line 400.0 yd, 3 tile files loaded
```

The `--ondemand` checks load only the two end tiles and then answer Detour's tile
requests the way `Navigation::LoadTileForDetour` does. Three tiles, three doorways in
different columns: 1024 yd of path against a 400 yd straight line. At grid scale -
7x7 stock tiles, a wall and a differently placed doorway in every one, 3.7 km
across - the route comes out at 4715 yd against 4525 yd straight, and the search
pulled 45 of the 49 files in itself.

The 60.7 yd of extra path is the doorway detour: with a straight-line answer the wall
would not have been in the mesh at all.

## What the bot logs about the mesh

On attach, `ClickAimDiagnostics` prints three things and then, if the library loaded,
runs a probe:

```
[NavMesh] 5948 tile files in the folder (312 params files), 4096 for map 1; params for this map: 001.mmap
[MeshProbe] player -616.4,-4257.9,39.0 map 1 -> grid tile (39,33), file 0013933.mmtile
[MeshProbe] tile (39,33) exists for map(s): 0, 1
[MeshProbe] tiles loaded for map 1: 25; tile (39,33) loaded: True
[MeshProbe] 40 yd: mesh path OK - 4 points, 41.2 yd, 0.8 ms, ends at (-601.1,-4221.0,39.4)
[MeshProbe] 120 yd: mesh path OK - 9 points, 123.7 yd, 1.4 ms, ends at (-566.5,-4136.0,38.8)
```

Read it in that order: is the map right, is the tile there, did it load, did Detour
answer. A `NO PATH` line names the tile file it expected. `exists for map(s)` is the
cheap check that catches the wrong continent before anything else does, and if the
bot's map is missing from that list the probe switches to one that is.

`Tools\SelfCheck.cmd` reports the same facts from outside the game.

## Still needed before this is real pathing in 70291

Nothing here, if the 3.3.5a mmap set is used: that is a full continent of real terrain
plus WMO/doodad collision, built by an extractor that read a real client. Generating
our own set from 70291 (ADT -> `.navtile` -> mmaps) is **postponed** - it needs the
CASC extractor, and it would still lack WMO collision because `cascdump`/`mapdump`
only read terrain.

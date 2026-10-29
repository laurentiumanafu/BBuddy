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
| `mmapcheck.cpp` | Loads the written files with the *same* Detour sources the DLL was compiled from, then runs `findNearestPoly` → `findPath` → `findStraightPath`. With no `--from/--to` it just proves every blob is accepted; that is the check the bake script runs. |
| `make-mmaps.cmd` | Windows end-to-end driver with error checks at each step. |
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
constant is **6**, and 5 is rejected. Do not "fix" this back to 5.

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

## Still needed before this is real pathing in 70291

Real ADTs. The extactor that pulls `world\maps\*` out of the 70291 CASC store is not
in this repo (upstream points at the private `extractor-csharp` / `Extractor_projects`).
Export them with CASCExplorer or `wow.tools.local` as loose `.adt` files, keep the
`Kalimdor_32_32.adt` naming, then run `make-mmaps.cmd`. Only the root ADTs are needed —
`_tex0`, `_obj0` and `_lod` siblings are detected and skipped automatically.

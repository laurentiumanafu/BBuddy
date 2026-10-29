# Real routing on build 70291 — what was built, and the one stage left

## The plan that was asked for, and what it collides with

The request was: *rebuild `Navigation.dll` for x64, extract the 70291 ADTs from
CASC, bake with Recast.*

Three facts decide how much of that can happen here:

1. `Lib\Navigation.dll` — and the `Navigation 1x1.dll` beside it — are **PE machine
   0x014C, i386**. The bot is x64. The DLL cannot even be probed from it, so
   `Navigator.IsNavigatorLoaded` is false on this client, always.
2. Their tiles are **V5 `.mmtile` baked from 3.3.5a ADT data**. No tile set exists
   for 70291, and the 3.3.5a tiles describe different terrain.
3. This workspace has **no Windows toolchain** — no MSVC, no mingw, no cmake. Any
   x64 DLL written here could be typed but never compiled or run, and a
   C++ rewrite of Detour that has never been executed is not a deliverable, it is a
   liability.

So the native-DLL half of the plan is not something this environment can produce.
What it *can* produce — and what has been produced — is everything either side of
it: the geometry bake, the router, and the wiring into the mover. That removes the
native library from the problem entirely rather than rebuilding it.

## What exists now

```
Tools/NavGrid/            reference implementation, C99, no dependencies
  navgrid.h/.c            tile container, A*, line-of-sight, smoothing  (44 checks)
  adt.h/.c                root-ADT reader: MCNK + MCVT + holes
  navbake.c               bake / path / probe CLI
  test_navgrid.c          unit tests on synthetic geometry
  make_test_map.py        writes a two-tile synthetic map with a wall and a gap
Styx/Logic/Pathing/NavGrid/NavGrid.cs
                          tile reader, A*, smooth, tile store, self-test
Styx/Logic/Pathing/ClickAim/RouteRunner.cs
                          uses the grid when no mesh exists
```

Verified here, not asserted:

* `make test` → **44 checks, 0 failed**, including the diagonal-corner-cut case
  (which the first version of the line-of-sight code got wrong: Bresenham alone
  steps diagonally without testing the two orthogonal cells).
* End-to-end CLI: synthetic ADT with a 22 yd wall and a 33 yd gap → bake → path
  detours through the gap, 395 yd against a 330 yd straight line, every segment
  re-checked walkable.
* Two real bugs the tests caught and the fix list kept: negative world coordinates
  mapped to the wrong tile (`floorf` where the client truncates toward zero), and
  ADT tiles drifting apart by 0.67 yd apiece because 267 cells × 2.0 yd ≠ 533.33.

## The bake geometry

One `.navtile` per ADT tile, 267×267 cells at 1.9975 yd, 214 KB:

* heights in quarter yards, so a cell can say "this is a cliff" and the router can
  refuse a 1.5 yd step;
* a per-cell flag byte: `WALK`, `HOLE` (from the MCNK hole mask), `STEEP` (slope
  above the limit, cleared from `WALK`), `LIQUID` (reserved);
* CRC32 over header+payload, because a half-written tile must be rejected loudly
  rather than routed on.

Resolution is coarser than a Detour polygon mesh and that is on purpose: the mover
is a click-walker, so what it needs from geometry is "where can I not go" at
20-40 yd granularity, not precise polygon boundaries.

## The stage that needs your machine: getting the ADTs

The baker reads root ADTs. Producing them from a CASC-era client is the part this
environment cannot do — it has no WoW install to test a CASC reader against, and a
reader written blind against a format with encrypted indices and BLTE compression
is exactly the kind of code that would be reported as "done" and then fail.

Use an existing extractor instead (this is the "external tooling" of the original
plan, and it is the reliable half of it):

1. `CASCExplorer` (or `wow.tools.local`) pointed at
   `...\World of Warcraft\_classic_\` — the 70291 install.
2. Export the terrain ADTs for the map you care about: `world\maps\kalimdor\*.adt`
   (map id 1) or `world\maps\azeroth\*.adt` (map id 0). **Root** ADTs only; the
   `_tex0`/`_obj0`/`_occ1` siblings are not needed.
3. Bake each one:

```
navbake bake --adt kalimdor_32_32.adt --map 1 --tx 32 --ty 32 --out <app>\NavTiles
```

`tx`/`ty` come from the file name. Batch it with a loop; the baker is one tile per
invocation and takes well under a second.

4. Verify before trusting it:

```
navbake probe --tiles <app>\NavTiles --map 1 <your x>,<your y>,<your z>
```

The delta it prints is the whole acceptance test for the bake. If the height is
right and the walkability matches what you see, the convention holds. If it is off
by tens of yards, the axis mapping in `ng_tile_init` is wrong for this client — and
because that mapping lives in one function, the fix is one function.

## What the bot does with it

`RouteRunner`, when no mesh is loaded:

1. asks `NavGridStore.FindPath` for the whole leg (it loads only the tiles spanned
   by start and goal, and refuses spans over 6×6 tiles);
2. if a path comes back, breaks it into ≤ 40 yd click hops and walks them;
3. if not, walks the profile waypoint directly, refusing legs over 100 yd.

The grid self-test runs at attach, next to the movement self-test, so a broken port
shows up in the log before anything walks.

Tiles live in `<application path>\NavTiles` by default; `--navtiles <dir>` and
`--navmap <id>` override that at startup.

## Honest limits

* Doodads are not baked: terrain walls are in the grid, buildings and fences are
  not. `_obj0.adt` placements are the next input to add.
* Liquids are not baked; water is treated as ground.
* Heights are chunk-field interpolation, accurate to about a yard.
* Nothing here has been compiled or run on a Windows machine yet — the C has, the
  C# has not. Its own self-test is the first thing to read in the log.

## Next, in order

1. Bake one real 70291 tile and run `probe` against your logged position. That
   single number decides whether the geometry pipeline is correct.
2. If it is: bake the region you play in (a 9×9 tile block is ~17 MB and covers
   four ADTs' worth of walking in each direction).
3. Add `_obj0.adt` doodad footprints to the baker — that is what makes towns and
   cave mouths correct.
4. Only then is a mesh worth revisiting, and by then the grid has already been
   walking the character for a while.

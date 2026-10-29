# Navmesh / world meshes — would they make the profiles walkable?

Short answer: **meshes fix routing, and routing is exactly what is missing.** They do
not fix combat, transports, or the per-click reach. This note records what a mesh
buys, the three ways to get one for build 70291, and what has to exist in the bot
before any of them are useful.

## What a mesh buys, in this bot's own numbers

The walker today aims straight at the goal and detours blindly. Measured limits:

| quantity | value | where it comes from |
|---|---|---|
| one click leg | 5-45 yd | field logs, `dy=-140` legs of 17.92-45.75 yd |
| run budget | 24 legs | `MAX_LEGS` |
| best approach | 78.8 yd in 5 legs | 15:57 run, unobstructed |
| typical profile hop (same cluster) | 27-60 yd | parsed from `Ashenvale (22-24).xml` |
| quest-to-quest hop | 276-1485 yd | same file |

A path of waypoints spaced 20-30 yd turns a 1485 yd hop into ~50-70 clicks, which is
12-20 minutes at the observed 3-6 s per leg including the rest wait. That is slow but
it is *bounded and reliable*, which is the difference between "walkable" and "not".

## Three ways to get geometry for THIS build

### A. Runtime bake by raycasting the client's own collision (Winkr `LiveMesh`)

Winkr bakes quarter-ADT tiles (133.33 yd) at runtime by raycasting the client's
collision, and cannot mix them with full-ADT tiles (533.33 yd) in one `dtNavMesh`.

* **Good:** sees exactly what the client collides with — including buildings, bridges
  and cave interiors, which terrain-only data does not.
* **Good:** no dependence on the data-file format.
* **Blocker:** it needs a raycast entry point. In Winkr that is the family around
  `WORLD_INTERSECT`. On 70291 our signature scan reports `WORLD_INTERSECT` UNSEEN —
  but at only 19.8 % of executable bytes readable, which is *not* evidence of
  absence (SigScan v7 says so itself). Re-scanning early in a session, at higher
  coverage, is the cheap next test.
* **Blocker:** calling it means injection. Authorised, but it is the same class of
  work as the movement-function attempts that were abandoned for being locked.

### B. Offline extraction from the client's data files, fed to Recast/Detour

This is what the 26,071 baked `.dnm` tiles in Winkr are, and it is build-independent
*data*, not build-specific offsets.

* **Good:** no injection, no runtime offsets, reproducible, cacheable, and it can be
  done once and shipped as 2.7 GB of tiles.
* **Good:** Recast + Detour are mature; path queries are cheap.
* **Blocker:** needs the beta client's data format. Classic Beta 70291 is CASC-era,
  not 3.3.5a MPQ, so the existing 3.3.5a extractors do not apply unchanged.
* **Blocker:** Winkr's own mmaps are a WotLK extract — Classic-only geometry is
  missing, which is exactly why they added `LiveMesh`. Using them as-is would route
  through walls that exist on this client.

### C. Learned occupancy grid from the walks themselves (works today)

No data files, no offsets, no injection: every leg already reports where it went and
whether it moved. Accumulate a 10 yd grid of "moved through here" and "clicked here
and did not move", and route on that.

* **Good:** buildable from the existing tool in a couple of iterations.
* **Good:** it is the only option with zero unknown prerequisites, and it improves
  every run instead of requiring a decision first.
* **Bad:** slow to learn, blind where it has never been, and it cannot see a wall
  until it has walked into it.

## What meshes do not fix

1. **Mobs.** The tool has no combat. A grinding hotspot is a mob camp; a mesh gives a
   beautiful path to the middle of it and the character dies there.
2. **Transports and portals.** `<RunTo>` → `UseTransport` → `GetOff` are moving
   objects and scripted teleports. They are not on the terrain mesh at all, and the
   profile's coordinates for them are on other continents.
3. **Reach per leg.** A mesh produces waypoints, not speed; each waypoint still costs
   one click and one rest.
4. **Z-dependent points.** A hotspot on a tower floor needs the interior geometry on
   the mesh (option A has it, terrain-only extraction does not).

## Prerequisite in the bot, independent of the mesh

A **waypoint chain walker**: `--route "x,y;x,y;..."`, or `--profile file.xml --quest
1010` walking the `<Hotspot>` list in order, with

* a per-waypoint leg budget (12) so one bad point cannot eat the run,
* arrival tolerance inherited from the walker (2 yd) and a "gave up on this one"
  report rather than a silent skip,
* a Z sanity check against the hotspot's Z (skip if more than ~8 yd away),
* continue-on-failure across the list, and a final "reached 4 of 5" summary.

That is small, testable offline, and it is the consumer every mesh option needs. It
also makes option C immediately useful, because a learned grid is only worth having
if the bot can walk a route rather than a single goal.

## Order I would do it in

1. Waypoint chain walker (no new dependencies; makes profile hotspots usable now,
   one at a time, inside a cluster).
2. Learned occupancy grid on top of it (needs only the walker).
3. Re-scan for `WORLD_INTERSECT` early in a session at higher coverage; if it
   resolves, prototype the runtime raycast bake on one 133 yd tile and compare the
   tile against the walk results at the same spot.
4. Only if (3) fails: offline CASC extraction + Recast, which is the largest single
   piece of work in this file and the one most likely to be out of date the moment
   the client build changes.

# ClickAim movement inside CopilotBuddy

The field tool in `Tools/WowBClickAim` is the laboratory: it established, on the
64-bit Classic Beta client (build 70291), that a synthesised right-click moves the
character, and it measured the camera response that makes the click aimable. This
document describes the port of that movement into the bot itself.

## What is in the repo now

| file | what it is |
|---|---|
| `Styx/Logic/Pathing/ClickAim/ClickAimMath.cs` | the measured camera model: cone, leg planner, robust slope learning, pass-by and plausibility rules, plus `SelfTest()` |
| `Styx/Logic/Pathing/ClickAim/ClickAimInput.cs` | synthesised mouse/key input (PostMessage), including the double button-up and the panic release |
| `Styx/Logic/Pathing/ClickAim/ClickAimCalibration.cs` | reads and pools `clickaim-cal.txt`, the same file the field tool writes |
| `Styx/Logic/Pathing/ClickAim/ClickAimMover.cs` | `IPlayerMover` implementation: tick-driven, never blocks more than one click |
| `Styx/Logic/Pathing/ClickAim/ClickAimPilot.cs` | blocking `WalkTo(x, y)` for driving the mover without the behaviour tree |

`ClickAimMover` implements the existing `IPlayerMover` interface, so it drops in
where the tree already moves the character:

```csharp
ClickAimMover.Install();            // Navigator.PlayerMover = new ClickAimMover();
```

Nothing else in the pathing stack needs to change: `MeshNavigator` calls
`MoveTowards(WoWPoint)` and `MoveStop()`, which is exactly what the mover offers.

## Calibration is shared, not repeated

The bot does **not** run the calibration probes. Those are eight walks of up to
45 yd, and doing them from inside the bot would be a second, worse copy of a
procedure that already exists. Run the field tool once on the client:

```
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --load --walk 40 270
```

It writes `C:\ghidra-scripts\clickaim-cal.txt`; the bot reads the same file from
`<application path>\Settings\clickaim-cal.txt`. Copy it over, or point the tool at
the same place. If the file is absent the mover starts from field defaults
(click -0.115 deg/px, drag -0.21 deg/px) and says so in the log.

The file is a population: every converged run appends a line and the reader takes a
robust median, because two verified runs on the same client measured -0.10709 and
-0.12674 deg/px - 18 % apart.

## What is different from the field tool, and why

1. **Tick-driven instead of blocking.** `MoveTowards` returns immediately; the
   walk is measured on the next calls. The tree keeps its own timing.
2. **Shorter hops near a waypoint.** The tool clicks `dy=-140` for everything,
   which is fine for one goal but overshoots a path waypoint by 20-30 yd and cuts
   the corner. The port uses `-60` under 15 yd, `-100` under 40 yd, `-140`
   beyond. This is a heuristic, not a measurement - the tool's own log shows dy
   does not control distance, only bias it.
3. **Turn legs advance the yaw by the model, not by the walk.** A right-drag holds
   the button down, so the character runs along its OLD facing while the camera
   turns; the displacement heading is therefore not a camera heading. Feeding it
   back made the field tool ask for drag after drag (24 legs, no arrival, 172 deg
   of yaw drift). A pure click leg still measures the yaw directly.
4. **Give-up semantics.** Six failed detours or a runaway stop the mover and log
   once, instead of walking in circles with the bot's clock running.

## Building it

Build the **project**, not the solution:

```
dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64
dotnet build CopilotBuddy.csproj -c Release -p:Platform=x86
```

Building `CopilotBuddy.slnx` used to fail before compiling a line of code:

```
CopilotBuddy.slnx.metaproj : error MSB4126: The specified solution
configuration "Release|x64" is invalid.
```

The solution file declared its platforms but no configurations, so it only knew
`Debug`; `-c Release` had nowhere to go. `CopilotBuddy.slnx` now declares
`<BuildType Name="Debug" />` and `<BuildType Name="Release" />` alongside the
platforms, so the solution form works too. The project-level command above stays
the recommended one - it is what `Publish-Release.ps1` uses, and it cannot be
broken by solution configuration mapping.

## How to exercise it today

The 64-bit bot loop still refuses to start (`TreeRoot.Start()` returns with
"WowB x64 is read-only"), because the object model is incomplete. Movement does not
need the object model - it needs the player's X and Y, which the x64 reader already
provides - so the pilot can be driven from the running application in diagnostic
mode. After attach the log shows the self-test and then:

```
[ClickAim] pilot armed. Write "x,y" into ...\Settings\clickaim-walk.txt to walk there,
           or start CopilotBuddy with --clickaim-walk x,y
[ClickAim] position source: wowb at (1977.4, 232.0)
```

Two triggers, no rebuild between attempts:

```
CopilotBuddy.exe --clickaim-walk 1977.5,232.0        :: world position at startup
CopilotBuddy.exe --clickaim-walk 1977.5,232.0,2.0    :: x,y,tolerance in yards
CopilotBuddy.exe --clickaim-walk 40                  :: 40 yd straight ahead
```

or, with the bot already running, write the same text into
`<application path>\Settings\clickaim-walk.txt`. The file is consumed and deleted,
so the same request can be repeated by writing it again. A request is refused while
a walk is already running and waits up to 30 s for a readable player position.

A single number means "that many yards straight ahead", computed from the
character's own facing (`sin`/`cos` of the orientation the reader reports) - no map
coordinates needed. On the x64 client a **ClickAim 40 yd** button next to *World
snapshot* does the same thing, so the usual loop is: attach, click the button, watch
the character walk.

The pilot reports progress every 15 s, and stops on arrival, timeout, repeated
blocking or a runaway leg. Everything it uses is the same `ClickAimMover` the tree
will call.

## Loading a profile (waypoint chain)

A profile is a *waypoint source*; the mover is the driver.

```
--clickaim-profile "C:\Profiles\Ashenvale (22-24).xml"
```

or write the `.xml` path (or `profile:<path>`) into `Settings\clickaim-walk.txt`.
`ProfileRoute` reads `Hotspot` and `RunTo` elements in document order;
`--profile-quest <id>` narrows to one quest's cluster, `--profile-nearest` starts at
the closest waypoint, `--profile-legs <n>` caps the number of legs, `--profile-all`
walks the file as one chain.

The default is *the cluster you are standing in*. Measured on
`Ashenvale (22-24).xml`: 50 waypoints in **20 separate clusters** (5, 8, 1, 1, 2, 1,
2, 2, 1, 3, 8, 8, and eight singletons), 86,343 yd of naive chain, because the seven
`RunTo` elements hop across continents - the longest single leg in the file is
17,705 yd. Walking the file as one chain is therefore meaningless even with a mesh;
that is the transport problem, not a pathfinding problem.

`RouteRunner` walks a leg with `Navigator.GeneratePath` **when a mesh is loaded**,
and refuses legs beyond `ProfileRoute.MaxClickWalkYd` (100 yd) when one is not,
instead of aiming a click at something 600 yd away.

### The mesh is live now

This section used to say there was no mesh on 70291. Both blockers are gone:

* `Lib/x64/Navigation.dll` is a real x64 build (PE32+ `0x8664`, 116 exports, 74/74
  P/Invoke entry points). `Tools/Navigation/build-navigation.sh` reproduces it from
  the upstream source plus one patch.
* The tiles do not have to be baked from 70291 at all: `MoveMap.cpp` now reads the
  single-tile `.mmtile` files the 3.3.5a extractors write, in either naming scheme,
  with a 16- or a 20-byte header, gated on the blob rather than on a version field.
  A stock 3.3.5a set for Kalimdor and Eastern Kingdoms loads as-is.

Confirmed on the real client (2026-10-29, Ashenvale, `(-616.4, -4257.9, 39.0)`):
`Navigation.dll loaded: yes`, 5948 tile files, 25 files / 400 sub-tiles pulled in
around the player on demand, and Detour returned a 42.8 yd path (4 points) and a
139.6 yd path (7 points).

`RouteRunner` and a plain `x,y` walk both route on the mesh when it is queryable;
the straight click is the fallback, and the log says which one was used.

### Following the polyline, and what happens when something is in the way

Routing a leg and *walking* it are two problems, and the first version only solved
the first one: a 40 yd leg came back as a 3-point polyline and then the mover aimed
one click at the far end, so the character walked the straight line and the 55°
"detour" ladder fired when it hit something. Measured on the client, 2026-10-29:
11 yd of 40 in 90 s, two detours, `gave up on mesh point 1/2`. The mesh was right;
the movement ignored it.

What the walk does now:

* **The polyline is resampled** so no click is longer than 15 yd
  (`RouteRunner.Resample`). Detour's corners can be 40 yd apart on open ground and
  every yard of a click is aim error. Aiming 15 yd ahead instead of 40 makes the
  same calibration three times less wrong, and it is what "follows the path" means
  in practice.
* **A stopped character re-plans instead of detouring.** `ClickAimMover.DetourBudget`
  is 6 when there is no mesh (the blind ladder is the only escape), and 1 on a
  routed leg - on a routed leg the answer is a new path, not another heading.
  `ClickAimPilot.WalkTo(..., stopWhenBlocked: true)` returns the moment the budget is
  spent, so the mover's own 20 s legs are not burned on a place it cannot pass.
* **The place that stopped it is marked bad.** `MeshBlackspots.Mark` queries the
  polygons within 8 yd and re-types them as `NavTerrain::NAV_BLACKSPOT` (area 17,
  cost 60 in the DLL's own filter), so the re-plan is genuinely a different route
  and not the same click harder. Marks live in the loaded `dtNavMesh`: they vanish
  with the tile after the one-minute idle unload, so a mob in a doorway does not
  poison the map for the session.
* **Re-planning is bounded.** Three attempts, and it gives up when a re-plan gains
  less than 1.5 yd - a closed route is reported as closed instead of being retried
  for another two minutes.
* **No route is not always failure.** If Detour has no route from here and the goal
  is inside one click (40 yd), the straight click is still taken. That is the old
  fallback and it exists for data gaps, not for walls.

The log tells the four outcomes apart:

```
[Route] mesh path: 4 points, 41.2 yd for a 40.0 yd leg (nearly straight)
[Route] stopped at mesh point 3/7, attempt 1 - (-583.5, -4433.2) was 12.1 yd away
[Blackspot] marked 9 polygons within 8 yd of (-586.2, -4437.0, 44.8) - the next path will go around it
[Route] re-plan 1/3 from (-586.2, -4437.0), 34.7 yd still to go
```

or, when there is nothing to save:

```
[Route] re-plan 2 made no progress (0.4 yd): the way out is closed in every direction Detour can see from (...)
```

### Checking the mesh without moving the character

`--clickaim-route x,y[,z]`, or `route:x,y` written into `Settings\clickaim-walk.txt`,
plans the route and prints every waypoint, and touches neither the mouse nor the
keyboard:

```
[Route] plan only, no walking: (-825.5, -5054.0, 3.5) -> (1977.5, 232.0, height unknown) on map 1
[MeshQuery] the goal had no usable height (given 0.0); searched 400 yd and found the mesh at 84.6 ...
[Route] 14 points, 6180.2 yd for a 6173.2 yd straight line (3.1 ms)
[Route]    1: ( -825.5,   -5054.0,     3.5)
[Route]    2: ( -814.2,   -5048.7,     4.9)
...
```

That is the road test: pick the far end of a road, run it, and compare the printed
waypoints with the map. Done on the client on 2026-10-29 from `(-636.0, -4246.2)`:

```
[Route] 6 points, 141.3 yd for a 120.0 yd straight line (0.8 ms)
[Route]    1: (   -636.0,   -4246.2,    38.4)
[Route]    2: (   -633.3,   -4258.1,    38.7)
[Route]    3: (   -608.5,   -4283.4,    38.0)
[Route]    4: (   -608.5,   -4316.8,    38.2)
[Route]    5: (   -627.9,   -4341.8,    41.8)
[Route]    6: (   -643.0,   -4366.0,    46.3)
```

141 yd for 120 yd straight, terrain climbing 38 -> 46 yd over the last two points,
and `polygons within 25 yd: 160 (area 4 x55, ground x105)` - area 4 is `Road`. That
is Detour following the ground it was given. **The mesh side is done**; what is left
is the mover. A path that follows the road comes back with
`(points, length)` where the length is a few percent over the straight line and the
waypoints run down the middle of it. A path that ignores the mountain and goes
straight up it means the tiles for that area are missing or the height data is flat,
which the polygon report in `[MeshProbe]` explains.

The walking test is separate and has to pass first: see the click-scale section
below, because on 2026-10-29 the mesh was already correct while the walk ran 120 yd
past a 20 yd goal.

### Calibrate first: four walks that measure the model

```
CopilotBuddy.exe --clickaim-calibrate        (or write  calibrate  into Settings\clickaim-walk.txt)
```

`Settings\clickaim-cal.txt` was empty on this install - `calibration click 0.00000
(0 samples)` in every log - so every walk ran on constants from a different camera.
`ClickAimSelfCalibration` measures the model by walking five probe legs and reading
where the character actually went:

```
[Calibrate] centre click: 28.4 yd at heading -101.4 deg
[Calibrate] click +200 px: 27.9 yd at heading -113.9 deg
[Calibrate] +200 px moved the walk -12.5 deg from the centre heading -> -0.06250 deg/px
[Calibrate] click -200 px: 26.1 yd at heading -89.0 deg
[Calibrate] -200 px moved the walk -12.4 deg from the centre heading -> -0.06200 deg/px
[Calibrate] drag +200 px: 27.2 yd at heading -108.7 deg
[Calibrate] drag +200 px turned the walk -7.3 deg -> -0.03650 deg/px
[Calibrate] the -200 px drag brought the camera -7.1 deg back -> -0.03550 deg/px
[Calibrate] a -140 px click is worth 0.2036 yd/px over 5 probe(s)
[Calibrate] wrote degPerPx=-0.062250 degPerDragPx=-0.036000 ydPerDyPx=0.2036 to ...\clickaim-cal.txt
```

Three things make the numbers trustworthy, and the first version of this had none of
them (it produced a 240 px drag "turning" a 6.3 yd walk by 146 deg):

* **Long probes.** Every probe clicks -140 px, which walks 20-40 yd here. A 10 yd
  walk is a direction with tens of degrees of error in it.
* **A measured centre.** The zero comes from a plain centre click, and each channel
  is measured against it, so the camera's own heading cancels.
* **Drags are measured twice.** A drag rotates the camera and the rotation stays, so
  the second drag is measured against where the first left the camera. Two estimates
  of one number: if they disagree by more than half, the channel is discarded rather
  than guessed.

Plausibility bands (|click| 0.03-0.60 deg/px, |drag| 0.03-1.00) come from the field
runs, and a channel outside them is not written. A wrong number is worse than no
number, because no number is logged - so a refused write always says why.

The file also carries the **click scale** (`ydPerDyPx`), which no field calibration
ever recorded. Measured here: -60 px walked 10.1, 11.3 and 13.2 yd, i.e. 0.17-0.22
yd/px, against the 1.1 yd/px the old model assumed. `ClickAimParams.AssumedYdPerDyPx`
is now 0.28 - deliberately high, because over-estimating makes the first click
*shorter* than wanted, and one wasted click is cheaper than a 60 yd sprint.

### Two more numbers the field log got wrong

* **A leg measured while the character is still walking is not a measurement.**
  `[ClickAim] click scale: 0.42 yd/px from 12.6 yd walked on a -30 px click` appeared
  2 s after a click whose hop was still running; the next leg then walked 40 yd, so
  0.42 was roughly half the truth. The scale is now settled at the moment the
  character really is standing still (`ClickAimMover.ResolvePendingClick`), and a
  click that is still carrying it past `3 x the wanted hop + 10 yd` is braked with
  the stop key instead of being watched.
* **`HeadingAtZero` was being overwritten with the wrong number.** It means "the
  heading a click at the centre of the screen walks", but every leg stored the
  heading it actually walked - including legs whose click was 48 px off centre. The
  offset (`DegPerPx * ClickPx`) now gets backed out, which removes a few degrees of
  aim bias that accumulated over a walk and looked like "it circles the target".

### The extents were transposed in the snap, so a route to a road waypoint was refused

```
[MeshQuery] no polygon within 5 yd of the goal at (-495.3, -4221.1, 0.0), even after searching 400 yd up and down
```

The goal was fine - the probe reported a path ending at exactly that point, 50.4 yd
up. The snap searched a box that was 400 yd *wide* and 5 yd *tall*: `_C` entry
points swizzle the XYZ they receive into `{Y, Z, X}` before handing it to Detour,
whose extents are `(horizontal, vertical, horizontal)`, so the vertical extent has to
travel in the **Z** slot. `Navigator.FindNearestPolyRef` now takes two scalars
(`horizontal`, `vertical`) for the new overload, and `MeshQuery`, `MeshBlackspots`
and the polygon report all pass `(H, H, V)`. `NavigatorTerrainHeightProvider` had
this right all along and is the reference for the convention.

### The click scale was the real reason a 20 yd walk ran 120 yd

Field log, 2026-10-29 20:33, standing in `(-825.5, -5054.0)`:

```
[Route] mesh path: 2 points, 20.0 yd for a 20.0 yd leg (nearly straight)
[ClickAim] passed the target at 3.1 yd; cancelling the walk
[ClickAim] runaway: a leg walked 120.5 yd in 14.0 s and never came to rest
[Route] stopped at mesh point 2/2, attempt 1 - (-809.3, -5042.3) was 241.8 yd away
```

The mesh was right - two points, 20.0 yd. The mover clicked once, the character
walked through the goal and kept going for 120 yd. The click height table said
`-100 px` was a 20-45 yd hop; on this client it was more than 120 yd. That table
came from field runs with the camera pitched down, and the click distance is set by
where the ray under the cursor meets the terrain, so it changes with the camera. A
constant was never going to hold.

What replaced it:

* **`ClickAimMath.HopDy`** computes the click height from the distance left and a
  *measured* scale, clamped to 30-260 px. The near clamp keeps the cursor off the
  character and the sky; the far clamp bounds the damage of a wrong scale.
* **The scale is measured every click leg** (`ClickAimMath.LearnScale`, called from
  `ClickAimMover.FinishLeg`): `moved / |dy|`. The first accepted measurement replaces
  the guess outright, later ones are averaged, and a candidate more than 4x away
  from the current value is thrown out - a leg cut short by a wall is not evidence
  about the scale. Log line: `[ClickAim] click scale: 1.35 yd/px from 40.5 yd walked
  on a 30 px click; using 1.35 (hop wanted 20.0 yd)`.
* **The runaway cap is relative.** It used to be a flat 120 yd, which is why one bad
  click ran for 14 s; it is now `3 x the hop that was asked for + 10 yd` (bounded
  25-120), so a click meant to be 20 yd is abandoned at 70.

The first click of a session is deliberately short: with no measurement yet the
assumed scale is 1.1 yd/px, so a 20 yd goal asks for 30 px and walks 30-40 yd. One
leg of overshoot in the worst case, then the loop knows the scale.

### A goal written as `x,y` has no Z, and that used to fail the walk outright

```
[MeshQuery] no polygon within 5 yd of the goal at (1977.5, 232.0, 0.0) - the mesh has nothing there
[Route] no route on the mesh ... after 0 re-plan(s)
```

`1977.5,232` is a fine goal; `Z = 0` is a placeholder, and the ground there is not at
zero. Detour's nearest-polygon search looks 20 yd up and down, so the request was
refused for a reason that has nothing to do with the mesh. `MeshQuery.Snap` now
retries with a 400 yd vertical search when the tight box finds nothing, and says so:

```
[MeshQuery] the goal had no usable height (given 0.0); searched 400 yd and found the mesh at 84.6 - using (1977.5, 232.0, 84.6)
```

Entering a goal with its Z (`1977.5,232,84`) skips that path and is still the better
habit.

## Where the position comes from

`ClickAimPositions.TryRead` prefers the 3.3.5a object model (`ObjectManager.Me`),
and falls back to the 64-bit reader: `WowBReader.TryGetPlayerObject` plus
`TryGetObjectTransform`, cached for 40 ms. This matters - `ObjectManager.Update()`
on a 64-bit client publishes a `WowBWorldSnapshot` and returns, so `Me` is never
built and the first version of the mover would have reported "no local player"
forever on the exact client it was ported for. The position source is logged at
attach and per walk (`[wowb]` or `[legacy]`).

## What this does NOT do

* **Combat.** A hotspot in a mob camp is unwalkable for this bot: nothing fights,
  so the character is attacked, dazed and killed, and no navigation change fixes
  that.
* **Turning a quest into waypoints.** The navmesh pathing is live and the walk
  follows it (see above), but the waypoints still come from the caller
  (`MeshNavigator`, a profile, or the pilot's single goal). There is no quest
  objective that produces them by itself.
* **Obstacles the mesh does not contain.** Trees, carts, fences and player
  characters are not in the mmaps. They are handled as *reactions* - stop, mark,
  re-plan - which costs a few seconds per obstacle. A real avoidance provider
  (mob positions fed into the filter) is the dedicated fix and is not written.
* **Z.** Only X and Y are used. A hotspot on a tower floor needs interior geometry.
* **Transports, portals, taxis.** Profile `<RunTo>` chains that cross a continent
  are not movement problems and are not handled.
* **Backwards/strafe in `Move(...)`.** Only forward is synthesised; the others stop
  the character and warn once.

## Test checklist on the user's machine

Do these in order. Each one answers a different question, and the log line to look
for is named, so a failure points at one thing instead of "movement is broken".

0. `Tools\SelfCheck.cmd` - no game needed. DLL hash, tile counts per naming scheme,
   `mmapcheck` over one map, and the navmesh lines from the newest log.
1. `dotnet build -c Release -p:Platform=x64 .` - the first real compiler pass over
   the ClickAim files. Then, before any walking test, run `CopilotBuddy.exe
   --clickaim-calibrate` once (or `Tools\Walk.cmd calibrate`): it writes real
   `degPerPx`/`degPerDragPx` numbers into `Settings\clickaim-cal.txt`, and every
   walk after that plans with measured values instead of field defaults from
   another camera. Then start the bot and check the attach lines:
   `[ClickAim] self-test 26/26 passed`, `NavGrid self-test` all passed,
   `[NavMesh] ... N tile files`, then the `[MeshProbe]` block. Two `mesh path OK`
   lines mean the mesh is real and queryable from inside the process.
2. **Does the mover still work?** `Tools\Walk.cmd 20` - it writes the request into
   `Settings\clickaim-walk.txt` (a bare number means "that many yards ahead of my
   facing") and tails the newest log, printing only the lines that matter. Without
   the helper, write `20` into the file by hand. Expect `[ClickAim] routing 20.0 yd
   on the mesh`, `[Route] mesh path: ...`, `[ClickAim] arrived: ... yd remaining`.
   A short walk is nearly a straight line, so this isolates the mover from routing.
3. **Does the mesh change anything?** Put `100` in the same file, facing something
   you have to walk around. The tell is `[Route] mesh path: N points, X yd for a
   100.0 yd leg (detours around terrain)` - X larger than the leg means it went
   around. A straight click would have been exactly 100.
4. **A real goal.** `Tools\Walk.cmd 1977.5,232 -Seconds 180`. Expect
   `[ClickAim] routing N yd on the mesh`, a polyline, then `[ClickAim] click (px, dy)
   - N yd to go, click scale K yd/px` per hop and a `[ClickAim] click scale:` line
   whenever a leg measured the scale. Negative coordinates are fine:
   `Tools\Walk.cmd -1977.5,-232`. When something the
   mesh does not know about is in the way, the log shows `[Route] stopped at mesh
   point k/n`, `[Blackspot] marked ... polygons`, `[Route] re-plan 1/3` - and then
   either arrival or `no progress`. Both are answers; the failure that is no longer
   possible is standing still for 90 s.
5. **Obstacle proof.** Walk into a tree or a fence on purpose with a 40 yd goal, and
   confirm the character ends up beside it rather than against it. That is the
   blackspot loop doing its job. Three lines tell the story, in this order:
   `[Route] stopped at mesh point k/n`, `[Blackspot] marked N polygons ...`,
   `[Route] re-plan k/3 from (...)`.
6. Only when that works: `ClickAimMover.Install()` in a session where the tree runs.

If step 2 fails but step 1's probe passed, the problem is the mover, not the mesh.
If the probe fails, nothing downstream can work - send that log.

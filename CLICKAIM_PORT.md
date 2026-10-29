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
* **Routing.** Waypoints come from the caller (`MeshNavigator`, or the pilot's
  single goal). The navmesh is live (see above); what is missing is a caller that
  turns a quest into waypoints.
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
   the ClickAim files. Then start the bot and check the attach lines:
   `[ClickAim] self-test 26/26 passed`, `NavGrid self-test` all passed,
   `[NavMesh] ... N tile files`, then the `[MeshProbe]` block. Two `mesh path OK`
   lines mean the mesh is real and queryable from inside the process.
2. **Does the mover still work?** Put `20` in `Settings\clickaim-walk.txt`
   (a number = "that many yards ahead of my facing", no coordinates needed).
   Expect `[ClickAim] routing 20.0 yd on the mesh`, then `[Route] mesh path`, then
   `[ClickAim] arrived: ... yd remaining, N legs`. A short walk is nearly a straight
   line, so this isolates the mover from the routing.
3. **Does the mesh change anything?** Put `100` in the same file, facing something
   you have to walk around. The tell is `[Route] mesh path: N points, X yd for a
   100.0 yd leg (detours around terrain)` - X larger than the leg means it went
   around. A straight click would have been exactly 100.
4. **A real goal.** Put `x,y` in the file. Expect `[ClickAim] routing N yd on the
   mesh`, a polyline, and the character walking it. Watch for `[ClickAim] BLOCKED`
   (the mover's own detours exhausted) or `gave up on mesh point` (the walk stopped
   physically, e.g. a fence the mmap does not know about).
5. Only when that works: `ClickAimMover.Install()` in a session where the tree runs.

If step 2 fails but step 1's probe passed, the problem is the mover, not the mesh.
If the probe fails, nothing downstream can work - send that log.

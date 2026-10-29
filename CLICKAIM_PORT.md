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
CopilotBuddy.exe --clickaim-walk 1977.5,232.0        :: one walk at startup
CopilotBuddy.exe --clickaim-walk 1977.5,232.0,2.0    :: x,y,tolerance in yards
```

or, with the bot already running, write `1977.5,232.0` into
`<application path>\Settings\clickaim-walk.txt`. The file is consumed and deleted,
so the same coordinates can be asked for again by writing them again. A request is
refused while a walk is already running and waits up to 30 s for a readable player
position.

The pilot reports progress every 15 s, and stops on arrival, timeout, repeated
blocking or a runaway leg. Everything it uses is the same `ClickAimMover` the tree
will call.

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
  single goal). There is no navmesh yet; see `hunt/NAVMESH_PLAN.md`.
* **Z.** Only X and Y are used. A hotspot on a tower floor needs interior geometry.
* **Transports, portals, taxis.** Profile `<RunTo>` chains that cross a continent
  are not movement problems and are not handled.
* **Backwards/strafe in `Move(...)`.** Only forward is synthesised; the others stop
  the character and warn once.

## Test checklist on the user's machine

1. `python3 Tools/lint_cs_types.py Styx/Logic/Pathing/ClickAim/*.cs` (repo root).
2. `dotnet build -c Release -p:Platform=x64 .` - this is the first real compiler
   pass over the five new files; they have only been linted, not compiled.
3. At attach the log should show `[ClickAim] self-test 26/26 passed`, the
   `pilot armed` line, a readable position source and the calibration line. If the
   self-test fails, nothing else is worth trying.
4. `--clickaim-walk <x>,<y>` to a point you can see, 30-60 yd of open ground.
   Expect `[ClickAim] from ... to ... - N yd`, then `arrived: ... yd remaining,
   N legs`.
5. Only after that, `ClickAimMover.Install()` in a game session where the tree runs
   (i.e. once the x64 object model is complete).

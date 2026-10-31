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

### Building a hotspot list in the field

The profile above is a file somebody else wrote. `Settings\clickaim-hotspots.xml` is the
same thing written from the game: an ordinary profile with one `<Hotspot>` per point, so
it can be walked, replaced with a real HonorBuddy profile, or hand-edited.

In x64 diagnostic mode, next to "ClickAim 40 yd", four buttons appear after attaching:

| Button | What it does |
|---|---|
| **Add hotspot** | reads the player's X/Y/Z and appends a `<Hotspot>` element. Stand where you want a point and press it. |
| **Walk hotspots** | walks the list in the order it was captured — whole file, not just the cluster nearest you, because every point in it was added on purpose. |
| **Plan hotspots** | plans a mesh path to every point from where you stand and prints the length and detour per point, **without moving**. This is the "test more points against the mesh" button. |
| **Clear hotspots** | empties the list; the file stays. |

The same list from a shell:

```
Tools\Walk.cmd hotspots           walk the captured list
Tools\Walk.cmd profile:C:\p\a.xml   walk any profile's hotspots
```

`Add hotspot` reports as `[Route] hotspot 7 = (-623.6, -4286.8, 44.5) - 7 hotspots in
clickaim-hotspots.xml: (-682, -4351), ...`, and `Plan hotspots` prints one
`[Route] plan only, no walking` block per point, so a point that is off the mesh or
unreachable says so before anything walks.

Two things that are deliberate:

* **a capture is a position read, not a click.** Nothing is dispatched, so pressing Add
  hotspot in the middle of a walk does not disturb it.
* **the file is written temp-then-move.** A half-written profile is a hotspot list that
  silently stops loading, and that failure is indistinguishable from "the bot ignored
  the profile".

### Starting from the window

The window is the interface: **Load Profile** → pick **Grind** in the bot list → **Start**.
On a 3.3.5a client that starts the behaviour tree; on `wowb.exe` the tree refuses to start
(`TreeRoot.Start` still needs the 32-bit object model), so the same three clicks now run
`GrindRunner` on the profile the window loaded (`Styx/Logic/Pathing/ClickAim/ClickAimUi.cs`).

* **Load Profile** on `wowb.exe` does not build the legacy `Profile` object - it remembers the
  path and reports what is in the file: `loaded Horde12-60_cloth.xml: 19 grind area(s), 214
  hotspot(s), first "Durotar 1-6". Press Start to grind.` A file the old parser would have
  rejected still loads, because the machinery that rejected it is not in the path any more.
* **Start** decides what to run from the file, not from a setting: a profile with
  `<GrindArea>` blocks is ground (circle them, fight, pick the area by level band); a profile
  with plain `Hotspot`/`RunTo` waypoints is walked once. Picking any bot other than Grind
  prints which bot does work on this client instead of silently doing nothing.
* **Stop** ends the loop after the step it is on (a mid-click stop on this client is an
  unstoppable walk), and **Pause** in the arrow menu stops it and remembers the profile so
  **Resume** starts the same one again.
* The window keeps itself honest: when the loop ends on its own - an unreachable ring, an
  empty area, a stop request - the buttons go back to Start instead of leaving a dead
  "running" state.

The .cmd tools still work and still write the same request file, but nothing needs them:
they are for scripting and for when the window is not up.

### Grinding: circling hotspots and fighting what you meet

```
Tools\Grind.cmd "F:\Profiles\Horde12-60_cloth.xml"      grind until stopped
Tools\Grind.cmd "F:\..." -Seconds 300                    for five minutes
Tools\Walk.cmd stop                                       stop the loop
```

`ProfileRoute.LoadGrindAreas` reads `<GrindArea>` blocks - the hotspots to circle plus the
`TargetMinLevel` / `TargetMaxLevel` / `Factions` the profile authored - and
`GrindRunner` uses the area nearest the player. Walking a grind area with `profile:`
instead would visit its hotspots once and stop, which looks like a working bot until it
quietly does nothing.

Each tick the loop does one of two things:

1. **Fight** if something is near. Targeting is the client's own TAB selection, checked
   against `WowBWorldSnapshot.TargetGuid` and refusing players by vtable. That matters:
   a grind profile's `<Factions>16 109 189</Factions>` cannot be evaluated here, because
   no faction id is readable on this build - but the client already knows who is hostile,
   and TAB only selects enemies.
2. Otherwise **walk to the next hotspot** in the ring, one leg at a time, through the
   same mesh route the walking tests use.

Attacking has two modes, and the bot picks whichever it can reach.

**Lua first.** `GameLua` attaches the resident x64 bridge
(`GreenMagic/LuaBridge64.cs`) to `wowb.exe` when the grind starts, and asks the client
itself what the character can do:

```
[Grind] Lua: pid 4321, payload resident (641 bytes), execution ServerThread
[Grind]   UnitName('player') -> "Grunt"
[Grind]   UnitLevel('player') -> 14
[Grind]   HasAction(1) -> true
[Grind] action bar (3 slot(s)):
[Grind]   slot 1 [Spell_Shadow_ShadowBolt] usable=yes range=yes cd=0.0s
```

That bar is the first honest answer this bot has ever had to "which skills does this
character have". A slot is fired only when the client's own `IsUsableAction`,
`IsActionInRange` and `GetActionCooldown` all say yes - mana, reagents, range and
cooldown are the client's judgement, not ours - through `UseAction(slot)`. Which of them
actually hurt something is still decided by watching health, so a slot is only remembered
once it has taken health off a target. `--lua-probe` does all of this once and stops,
without walking, if the user wants the surface of the build without a fight:

```
CopilotBuddy.exe --lua-probe     at startup: probe, print, exit the probe
Tools\Walk.cmd lua               on the running bot, through the request file
```

What the bridge cannot do, and therefore what is still not solved: **two-argument calls
and expressions**. The payload runs exactly `pcall(globals[name], "one string")`, so
`UnitReaction("target","player")`, `CastSpell(slot, book)` and every `C_` namespace are
out of reach; hostility stays with the client's own TAB selection. Numbers get in only
because Lua coerces `"3"` for a C function asking for a number.

**Keys as the fallback.** If the bridge cannot attach (four gates: process, Lua canary,
trampoline shape, Lua thread) the loop presses `1`-`5`, `T`, `F` and believes a key only
when the selected target's health actually drops. The loop learns which keys work and
logs it (`key '2' does damage: health 100 -> 87`); if none of them removes health it says
so once and stops pressing, rather than attacking the void for ten minutes:

> no key on the action bar removed health (health read 100). The keyboard can walk on
> this client but it cannot attack - combat needs the routine, or a working Lua call

The routines are **Singular (WotLK)**, read out of the user's Routines.zip. Their shape is a
`PrioritySelector` of `Spell.Cast(name, on => target, ret => condition)`: never act while a
cast is running (`Spell.WaitForCast(true)`), put self-buffs up before pulling
(`PreCombatBuffs`), pull from the spell's own range and let
`Movement.CreateMoveToTargetBehavior(true, 20f)` decide when to step in, and heal off
`Me.HealthPercent`. Four of those port to an action bar whose abilities have no names:

| Singular | here |
| --- | --- |
| `Spell.WaitForCast(true)` | `UnitCastingInfo`/`UnitChannelInfo` before every cast - a second `UseAction` mid-cast interrupts the first |
| `Movement.CreateMoveToTargetBehavior(true, 20f)` | close only when `IsActionInRange` is false for every usable slot, so a ranged character fights at 30 yd instead of walking into melee's 4 |
| `PreCombatBuffs` | one slot the client calls usable but NOT range-limited (a buff or a stance) before the first cast |
| priority order | back to the top of the bar after every hit that lands, instead of round-robin |
| `Spell.Heal(..., Me.HealthPercent < 60)` | below 60% health, fire one ability the client calls usable and does **not** range-check - that is a self-cast, and a self-cast below 60% is Singular's heal slot in everything but name |

Not ported, because they need a spellbook and this build does not expose one: interrupts,
and anything that has to know a spell's name, school or cooldown rather than a slot's.
`GameLua.CastSpell(name)` is there for the day that changes - the routines also give the
seam: a priority list of `(spell name, condition)` per class is all Singular's
`ClassSpecific/*/Lowbie.cs` files really are.

**Which area, though.** A levelling profile is a ladder - the 19-SubProfile
`Horde12-60_cloth` file is one - so the area is not simply the nearest one. The loop asks
the client for the player's level (`UnitLevel("player")`, only ever through Lua: the
`LevelOffset` in `WowBOffsets` is marked V-ish and a wrong level would pick the wrong rung)
and chooses the nearest area whose `TargetMinLevel`/`TargetMaxLevel` contains it. Every two
minutes it asks again, and moves if the answer now belongs to a different band:

```
[Grind] using "Stonetalon 18-22" - 6 hotspot(s), 412 yd away, target level 18-22,
        factions '16 109 189'. Chosen because level 19 is inside its band (3 of 19 areas match).
[Grind] level 22 -> 23. using "Ashenvale 23-27" - 5 hotspot(s), 1180 yd away, ...
```

With no Lua there is no level, and the loop says so rather than guessing: it grinds the
nearest area and prints `The player level is unknown (no Lua), so this is the NEAREST area,
not necessarily the right one.`

Bands are read from all four shapes a profile writes them in - elements inside `<GrindArea>`,
attributes on it, elements on the enclosing `<SubProfile>`, or `<MinLevel>`/`<MaxLevel>` -
because a band that silently reads as 0 switches level selection off without a word. The
`Grind self-test N/M passed` line at attach covers exactly that, with a fixture per shape,
plus the area choice itself; it runs with no game attached.

Known gaps, stated rather than hidden: **combat is only checked between hotspots**, not
mid-leg (`WalkTo` has no interrupt hook yet), so an add that aggros during a long leg is
fought at the next point; the profile's `<Blackspots>` are not applied to the mesh; and a
mob's own level is not checked against the band - the band selects the area, and the
client's TAB selection still decides which individual mob is an enemy.

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

### What 110 yd of walking for a 20 yd goal actually was

Field log, 2026-10-29 21:16, Ashenvale, goal 20 yd ahead. Every leg of that walk is in
the table below, and between them they explain the whole failure:

| click | walked | what it means |
| --- | --- | --- |
| `(0, -43)` | 10.3 yd | moves, and ends up **further** from the goal |
| `(88, -105)` | 10.6 yd | moves, still further away |
| `(31, -183)` | 28.6 yd | moves a lot, in the wrong direction |
| `(27, -171)` | 4.5 yd | shorter and shorter |
| `(10, -171)` | 0.5 yd | |
| `(0, -171)`, `(7,-171)` | **0.0 yd** | the click does nothing at all |

110 yd of walking, ending 90 yd from a 20 yd goal. Two independent faults:

1. **The click channel's sign was wrong.** `degPerPx` was a negative constant from a
   field run, and on this client a positive pixel offset turns the walk the other way.
   So every aim was mirrored: the character walked away from what it was aiming at,
   the zero was then re-learned from that walk, and the error compounded.
2. **The click height saturates and then dies.** Past about 170 px below the centre,
   clicks stop moving the character (`0.0 yd`, five times). Since the old planner
   computed the height from the *distance left*, a goal 40 yd away asked for -253 px -
   i.e. the further away the goal, the more certain the click was to do nothing.

What changed, and none of it needs a separate calibration run:

* **`ClickAimMath.LearnAim`** fits `walked = zero + degPerPx * clickPx` from each
  click leg and, crucially, corrects the **sign** when the leg walked more than 100
  degrees away from what the click asked for - because 180 degrees is not noise. It
  also blends the magnitude and re-learns the zero, in that order, since each depends
  on the previous one.
* **Two clicks buy the model.** The first click of a session is a centre click (it
  measures the heading a centre click walks); the second is a `+170 px` click, which
  measures which way an offset turns the walk. That is 20 yd of walking once, in the
  middle of the route, and then the aim is right for the rest of the session - the
  values are cached statically and written to `clickaim-cal.txt`.
* **The hop height is fixed at -70 px** with a ladder of alternates; distance comes
  from repeating the hop along the polyline, never from the distance left. Every
  height on the ladder was observed to move the character; the ones at -180 and
  beyond were observed not to, and are not on it.
* **A dead click is not an obstacle.** A leg that moved under 1 yd twice in a row
  rotates the ladder instead of counting towards "stuck", which is what produced six
  identical no-op clicks and then a spurious `BLOCKED`.

### Aiming by projection, because the pixel model was not measurable

The 21:27 walk settled it. Two facts from that log:

* **Every click was `(0, dy)`.** The aim probe never ran: the boot click exists to
  *set* the zero, `LearnAim` refused any leg whose reference zero was NaN, so
  `HeadingAtZero` stayed NaN for 190 s and every tick posted another boot click. The
  character walked wherever the camera happened to face.
* **`-70 px` walked 0.6 yd and 34.1 yd in consecutive legs.** A click is aimed at
  whatever the ray under the cursor hits, so its distance is a property of the terrain
  and camera pitch, not of the pixel offset. The "-70 px = 10 yd" idea was never true.

So the click is no longer aimed by a learned pixel model. `ClickAimMath.ProjectToScreen`
projects a **world point** onto the client area using the camera position and the
point it looks at (the player) - and the camera is readable, it has been printed in
every attach log:

```
[ClickAim] camera readable at (-834.1, -5065.9, 8.9): clicks are aimed at world points
           by projecting them onto the screen (90 deg field of view), not by a learned pixel model
```

The mover clicks the projected position of the point it wants to stand on. That point
comes from the mesh polyline and it has a Z, so:

* the walk goes where the path goes, not where a ray happened to land;
* the distance is the distance to that point - there is no hop to tune;
* the click height is whatever the geometry says, so downhill and uphill both work.

Field of view is the one unknown (a client setting, `ClickAimParams.CameraFovDeg`,
default 90). An error there moves the click up or down the same ray, which changes how
far along the path the character walks, not which way - and the next leg re-aims.

The learned model is kept as a fallback for a build whose camera offsets are still
placeholders, and its boot click now works: with a camera the zero comes from the
player's facing; without one, `LearnAim` adopts the heading the boot walk measured
instead of refusing and repeating forever.

### The camera heading drifts, so it cannot be a feedback signal (22:09)

The first run of drag-to-face spent four minutes and dispatched **zero clicks**: the
error never fell inside the 6 degree tolerance, so nothing was ever clicked, and the
route then marked 52 polygons as obstacles around ground the character could walk to.

The log has the reason. While the character stood at 0.0 yd for nine consecutive ticks,
the camera heading went:

```
109.1  113.8  118.4  123.1  127.7  132.4  137.0     (+4.6 deg per tick, nothing moved)
```

and every drag, left or right, "measured" that same -4.6 deg. Reading the gain off
those two facts gives, in one run:

```
drag -150 px turned the camera -10.4 deg: measuring  0.070 deg/px
drag  150 px turned the camera  -4.6 deg: measuring -0.031 deg/px
drag  150 px turned the camera  -4.6 deg: measuring  0.042 deg/px
drag -150 px turned the camera  -6.3 deg: measuring  0.042 deg/px
```

A measured gain that changes sign while the commanded drag alternates produces a
drag in a direction nothing predicted, every tick. That is an oscillation by
construction, not a tuning problem.

Three rules follow, and they are in the code now:

1. **The aim never gates the click.** At most 3 drags, then a centre click happens
   regardless, with a line in the log saying so. A drag on its own is not progress; a
   centre click moves the character 5.8-7.2 yd (measured at 21:50).
2. **Do not measure the drag gain from the camera.** Use the calibrated value, or the
   -0.21 field default. The camera position is trustworthy; the camera *orientation*
   implied by it is not.
3. **Nothing is blacklisted by a walk that never clicked.** `mover.Clicks == 0` means
   the terrain was never tested, so the route stops and says that instead.

### 22:09 and 22:21: the aim cannot be made to converge, and why

Two more runs, both with the aim counting drags and then clicking regardless:

| run | started | walked | ended |
|---|---|---|---|
| 22:09 | 32.7 yd from the goal | 90 yd | **83.7 yd from it** |
| 22:21 | 64.5 yd from the goal | 269 yd | **95.4 yd from it** |

The numbers that explain it:

* **the drag gain is ~0.031 deg/px, not 0.21.** Every `drag -150 px` in the 22:09 run
  turned the camera by 4.6 deg. 0.21 would have needed 21 px. So the field calibration
  in `clickaim-cal.txt` is off by a factor of seven, and 118 deg of error therefore
  needs ~25 drags, which is why the error never reached the tolerance.
* **the camera heading drifts with nothing moving.** Nine consecutive ticks with the
  character at 0.0 yd: the heading climbed 109.1, 113.8, 118.4, 123.1, 127.7, 132.4,
  137.0 - exactly +4.6 deg per tick. A quantity that drifts on its own cannot be the
  feedback of a control loop, and measuring the gain from it produced sign flips
  between consecutive ticks (0.070, -0.031, 0.042, -0.031).
* **the same click walks different distances.** `(0, -140)` walked 5.8, 6.5, 7.2, 23.8,
  12.9, 14.0, 23.8, 7.8 and 63.1 yd in one run. A pixel offset cannot encode a distance:
  the click's ground point is where the camera ray meets the ground, and that depends
  on the camera PITCH, which nothing in this project can read.
* **dragging while a click is in flight walks the character in an arc.** The 22:09 run
  has "turn" legs of 27.3, 27.1, 19.5 and 10.0 yd - a drag with a click still running
  does not just turn the camera. A drag now taps the stop key first.

So aiming needs the camera's orientation, and the camera position this project reads is
not it. Two things follow, and they are in the tree:

1. **`--clickdest`** (`ClickAimDestHunt.cs`) stops trying to aim and looks for what the
   click actually WRITES. Click once, snapshot the player record while it walks, and
   look for a float triple that equals where it stopped but not where it was - the
   destination field. If that field exists and can be written, the mover needs no
   camera, no pitch and no pixels: put the mesh waypoint in it and the client walks.
   Three trials, offsets that appear in more than one are printed with the record offset.
2. The aim gate can no longer hold the click hostage (it did at 22:09: four minutes,
   zero clicks dispatched); after three drags it clicks anyway and says so.

### The aim, rebuilt around the one channel that measured consistently

Everything above was an attempt to make a *pixel offset* mean something: a click at
`px` right of centre turns the walk by `degPerPx * px`. That model needed five
separate numbers (sign, magnitude, zero, click scale, hop height), none of which ever
agreed with itself on this client - and the 21:41 log is what it looks like when they
disagree:

```
click (291,-169) - 22.8 yd to go, aimed at the projected point   -> walked 4.4 yd
click (193,-178) - 25.3 yd to go                                 -> walked 0.0 yd
click (103,-182) - 25.3 yd to go                                 -> walked 0.0 yd
click (-2,-183)  - 25.3 yd to go                                 -> walked 2.7 yd
click (401,-222) - 25.2 yd to go                                 -> walked 0.8 yd
```

The click position swings by 300 px while the character stands still, and the target
never gets closer. Every click turns the character instead of walking it.

The rebuilt aim uses two facts that held all day:

* **a click on the middle of the screen walks along the camera's axis.** No offset
  means no `degPerPx`, no scale, no sign: `ClickPx` is zero by construction.
* **the camera position is readable**, so the camera's heading is known:
  `HeadingDeg(player - camera)`. The heading it must have is
  `HeadingDeg(target - player)`. The difference is the error, and a drag of
  `error / degPerDragPx` pixels turns it - and the drag channel is the only number
  that measured the same twice (`-0.21` in the field, `-0.21000` in the file,
  `-0.21` in the 21:36 calibration).

So the mover now: **drag to face the target, then click the middle.** Both steps are
logged, and the loop is closed on the camera - the next tick re-reads the camera
heading and corrects whatever the drag got wrong:

```
[Aim] camera -75.4 deg, target -111.2 deg, error -35.9 deg -> drag 171 px (the camera turns -0.21 deg/px)
[Aim] camera -111.3 deg, target -111.2 deg, error +0.1 deg -> centre click (-140 px down)
```

Offline check of that loop: with the gain exactly right, one drag lands (error 0.1
deg); with the gain 40 % wrong, the error goes 15.4 -> 6.8 -> 2.8 deg, i.e. it
converges in three drags because each one re-measures; with the sign wrong it
oscillates - which the mover detects, flips the sign once, and says so in the log.

Projection stays in the tree behind `ClickAimParams.UseProjection` (off by default)
because the 90 degree field of view it assumes is demonstrably not this client's.

### Three reasons projection did not run on its first outing (21:36)

The camera was read on the first try - `camera readable at (-602.8, -4179.8, 50.0)` -
and the walk still failed. All three causes are worth writing down, because each was
invisible in the log:

* **The aim probe still ran.** It was gated on `_projectionWorks`, a flag that is only
  set *after* a projection has been attempted - which happens further down the same
  method. So the probe went first and spent four clicks proving nothing. It is now
  gated on the camera being readable: camera, project; no camera, probe.
* **The clicks aimed 5.7 yd out.** A point that close projects onto the character's own
  feet, and a click at your own feet does nothing - twelve of them moved 0.0 yd.
  `RouteRunner` now skips ahead to the first polyline point at least 12 yd away
  (`LookAheadYd`) unless it is the last one, so a click always has an angle to express.
* **Nothing ended it.** The hop ladder rotated for minutes at 5.7 yd while the
  character stood still. Six dead clicks in a row now set `Blocked` and say so, letting
  the route re-plan or give up; and each ladder rotation taps the stop key first,
  because a movement key clears the client's click-to-move state so the next click
  counts as new.

The click line now names the aim, so none of this needs a code change to diagnose:

```
[ClickAim] aim: projecting world points onto the screen from camera (-602.8, -4179.8, 50.0), 90 deg field of view
[ClickAim] click (312, -88) - 14.2 yd to go, aimed at the projected point
[ClickAim] click (0, -70) - 9.8 yd to go, pixel model, scale 0.14 yd/px
```

### It stops itself now

Two walks on 2026-10-29 had to be killed by hand: one cycled through six click heights
at 16.8 yd without moving for minutes. `ClickAimPilot` now watches the best distance
reached, and if it has not improved by 2 yd in 25 s it stops and says so:

```
[ClickAim] no progress for 25 s: stuck at 16.8 yd from the point (best was 16.7). Stopped instead of clicking at it forever.
```

That is information, not noise: it means the last re-plan and the mover disagree about
where the character can go, and it is the caller's job to re-plan or give up.

### Entering the point

The point is the interface: write `x,y` and the rest is the bot's problem.

```
Tools\Walk.cmd -623.6,-4286.8
```

which is the same as writing `-623.6,-4286.8` into `Settings\clickaim-walk.txt`. A
third number is a tolerance, not a height (`-623.6,-4286.8,4`). The height is read
from the mesh, so it never has to be typed:

```
[ClickAim] file request: point (-623.6, -4286.8), tolerance 2.0 yd
[MeshQuery] the target had no usable height (given 0.0); searched 400 yd and found the mesh at 38.7 - using (-623.6, -4286.8, 38.7)
[Point] target (-623.6, -4286.8) -> mesh point (-623.6, -4286.8, 38.7) on map 1; 20.0 yd from the player, 0.0 yd higher
[Route] mesh path: 2 points, 20.0 yd for a 20.0 yd leg (nearly straight)
```

`--clickaim-route x,y` prints the whole polyline without walking, which is the mesh
test; `x,y` walks it. The "N yards ahead" form still works but is no longer the shape
of the tool, and the log now says so.

### Optional: calibrating on purpose (the walk now does this itself)

The aim learning above runs inside the walk and needs nothing from you. This command
is for measuring the two channels deliberately, with five long probes, when something
looks wrong:

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

## 2026-10-30 — the grind reached the first hotspot and the client died

The window worked: Load Profile → Grind → Start, one waypoint walked 789.7 yd in
2 min 1 s, `[Route] waypoint 1/1 reached`. 230 ms later the client terminated
with `ASSERT(FrameScript_IsLuaThread())` (`FrameScriptEvents.cpp:208`).

The trigger was `TargetNearestEnemy()` through the Lua bridge. Every call before
it had been a question that answers without raising an event; that one selected a
mob, the client tried to dispatch `PLAYER_TARGET_CHANGED` on the payload's thread
and killed itself. Details and the fix: **`LUA_BRIDGE_64.md`, section
"2026-10-30 — the first *action* killed the client"**.

Three things changed in the loop because of it:

* `GameLua` now runs commands on the client's own Lua thread (`LuaThreadHijack`)
  and refuses every event-raising global when it cannot — the crash is not
  reachable any more, whatever else goes wrong.
* Targeting has an order that works without Lua: `TargetNearestEnemy()` (only
  when events are allowed) → TAB → right-click the nearest unit. The right-click
  is the client's own attack: it selects the target and starts auto-attack on the
  client's thread.
* A fight that gives up immediately is not retried every 0.29 s. The 2026-10-30
  log counted 22 fights in 6 seconds, all of them the same 17-yd-away mob; that
  target is now left alone for 15 s and the loop walks to the next hotspot
  instead, which is what "no wandering on non-viable mobs" means in practice.

## 2026-10-30, second run — the client died during attach, and what replaced Lua

`Start` at 15:34:02, crash reports at 15:34:04.790 and 15:34:05: `Lua stack is
unbalanced` (`Ui/GameUI.cpp:8250`) and then `0xC0000028` in ntdll. The bot kept going
on a dead client, which is why the same log then says "neither arrow key turned the
character" — **movement was not broken**; it was steering a frozen process.

The Lua bridge is now retired (`GameLua.TryOpen` refuses), so pressing Start cannot
touch Lua at all. What the loop does instead:

| step | mechanism | where the answer comes from |
|---|---|---|
| who to fight | TAB; then a right-click on the nearest unit the snapshot lists | the client's own hostility filter for TAB, a corpse filter for the click |
| how far / how hurt | `WowBReader.TryGetUnitFields(unit, …)` → `hp/max/lvl/uflags/pow` | the unit's data block (`+0x1E30`, `+0x1E40`, `+0x1E60`, `+0x1BC8`) |
| the attack | a right-click on the mob: select + the client's auto-attack | the client's own input handling |
| when to stop | the mob's health stops dropping, or it dies, or 20 yd | one 32-bit read per round |

Two numbers changed because the last log earned it: `ChaseYd` is 20 yd (it was 12,
and TAB selects the *nearest* enemy rather than a close one, so every engage began
out of range and gave up before the first swing — 22 fights in six seconds), and a
target a fight gives up on is left alone for 15 s.

### The Offsets button

`Offsets` (right-hand column, wowb only) runs `ClickAimDiagnostics.Offsets()` and
logs, in one block:

* the player's `hp/max lvl uflags pow` and a 32-word dump at `data+0x1DF0`;
* the stored action array at `0x7DFF4E0` — every non-zero slot with **both** plausible
  packings (`low24 id` + `type8`, and `low32 id` + `high32`), so which one is right is
  decided from data rather than by another build cycle;
* the nearest 8 other units with distance, GUID, address and their fields;
* the nearest non-player unit's raw words at `data+0x1DF0` and `data+0x1B80`.

Nothing is written and no Lua is used. Paste the block and the missing offsets
(level/reaction on a mob, the action-slot packing, spell ids) can be pinned down
without guessing.

## 2026-10-30, the offsets arrived — combat is now the real spells

The 15:57:29 Offsets block settled the three questions the second run could not:

| word | value on a level-2 Tauren druid | verdict |
|---|---|---|
| `data+0x1E30` | 75 (mob: 64, 71) | current health |
| `data+0x1E34` | 0 | padding — this is why every line read `/0` |
| `data+0x1E38` | 95 | **max health** (the old read took `+0x1E34` as the max) |
| `data+0x1E40` | 2 (mobs: 3, 4) | level, and it matches `UnitLevel("player")` |
| `data+0x1BC8` / `+0x1BD0` | 92 / 100 | power / max power |
| `data+0x1E60` | `0x80808` player, `0x80800` the mob being fought, `0x800` the idle ones, `0x0` the ones TAB refused | unit flags |

`0x7DFF4E0`, 8 of 51 slots: `6603` Attack, `5176` Wrath, `5185` Healing Touch,
`20549` War Stomp, `20552` Tauren racial, and `0x800000000000009F` = item 159
(Refreshing Spring Water). So the packing is `id = raw & 0xFFFFFFFF`,
`type = raw >> 32` (`0x80000000` = item). Those are the **real spell ids**, which is
what makes a rotation possible without Lua.

### The rotation is Singular's, the keys are the bar's

`Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs` decodes the bar and holds the spell
table; `GrindRunner.Fight` runs it. The order is copied from
`Singular wotlk/ClassSpecific/Druid/Lowbie.cs`, minus the two spells this character
does not have (Cat Form, Moonfire — they are not on the bar):

1. **heal self** at `HealthPercent <= 60` — Singular's
   `Spell.BuffSelf("Rejuvenation", …)`; F1 (target self), then the heal's slot key;
2. **auto-attack** — a right-click on the mob, which is `CreateAutoAttack(true)`
   plus the selection it assumes;
3. **Wrath** from up to 28 yd on a 1.8 s global-cooldown timer;
4. **melee** — close to 4 yd, right-click, swing — and the fallback whenever two
   casts in a row neither spend mana nor take health off the mob.

Nothing is assumed to have worked. A heal has to raise my health reading, a cast has
to spend mana or remove mob health, and both are printed with the numbers (`mana
92 -> 78`, `mob health 64 -> 52`). The slot→key map (1…9, 0, `-`, `=`) is checked by
`ClickAimCombat.SelfTest()` at attach, because a wrong key map looks exactly like
"the keyboard does not work".

### Choosing what to fight

`TryClickAttack` now reads the same fields before clicking: a corpse is skipped
(`health == 0`), a unit outside the level band `player-4 … player+3` is skipped
(`data+0x1E40`), and `UnitFlags & 0x800` makes a unit *preferred* over a nearer
unflagged one rather than filtering it out — a wrong bit must not empty the world.
TAB's own selection is level-checked the same way before a fight starts. A mob that
dies takes the target with it, so that case is reported as "killed or fled; what
landed: …" and counts as a kill, which the last run's zero did not.

The Offsets button now dumps the **unit object** at `+0x1E00`/`+0x1B80` (the two
empty windows in the 15:57 block were the wrong base — `DumpWords` also no longer
requires 8-byte alignment, which monster unit objects do not have) and prints the
decoded bar as the rotation reads it.

## 2026-10-30 16:06 — the first run that cast a spell, and the three things it exposed

Wrath landed on the first try: `mob health 102 -> 86 (mana 115 -> 90)`. So the key map,
the slot decoding and the attack-key path are all correct — the client's own action bar
responds to `2`. Everything that failed after that was about *when* to fight and *how the
result is measured*, not about the offsets:

| symptom in the log | cause | fix |
|---|---|---|
| `3 unit(s) within 40 yd ... walking on` while mobs were hitting the character | the loop only looked for a fight **between** hotspot legs, and a leg is 25-40 s | `RouteRunner.ShouldInterrupt` — asked every follow point (5-20 yd); true releases forward and abandons the leg |
| fights starting only after 30+ s of walking, then the next hotspot immediately | no dwell at the hotspot | `DwellAtHotspot` — stand up to 9 s, fighting anything that turns up |
| `the nearest mob is behind the camera or off the screen` on **every** engage | TAB only looks in front of the character and a right-click only selects what is on screen | `ClickAimKeySteer.Face()` — turn (calibrated arrow keys) toward the nearest viable unit before TAB, and toward the chosen mob before projecting the click |
| `couldn't attack the current target, not facing it` mid-fight | nothing re-faced after the mob moved | `Face()` at the top of every fight round, before heal/cast/click |
| `Wrath changed nothing: mana 40 -> 40` on casts that had just worked | one read 1.8 s after the keypress, while a 1.5 s cast + 1.5 s GCD was still running | poll mana and mob health for up to `CastVerifyMs` (3.2 s), and stop as soon as either moves |
| two of those switched the fight to melee for good | `castFailures >= 2` regardless of whether casting had ever worked | only fall back when the key has **never** cast this fight, and after three failures |
| `Healing Touch did not lift it (still 12/113)` four times in one fight | same single-read bug, plus F1 retarget latency | poll health for up to `HealVerifyMs` (3.6 s); then cycle TAB until the original guid is selected again |
| `health 0% and Healing Touch did not lift it` | the character was already dead | `PlayerDead()` stops the grind; `Fight` returns "the character died" |
| `slot 11 '½'`, `slot 12 '»'` | `(char)0xBD` / `0xBB` | `ClickAimCombat.KeyLabel` prints `-` and `=` |

### UnitFlags is not a reaction field after all

The 15:57 dump suggested `0x800` was hostility (hostile mob `0x80800`, idle mobs `0x800`).
The 16:06 run printed the **player** as `uflags=0x808` — carrying the same bit — so it is a
transient combat/PvP bit, not reaction, and it is no longer used to rank or filter targets.
Hostility stays with the client's own filter (TAB) and with "something live and
level-appropriate is this close". Target choice is now: corpse filter, level band
`player-4 … player+3`, nearest first.

## 2026-10-30 16:42 — casting works, but the character turned its back on the mob

The run opened with a landed Wrath (`71 -> 56`), then nine `changed nothing in 3200 ms`
lines for the same mob and the character ended up facing away from it. Then, after a
restart, a mob died properly (`auto-attack 56 -> 42 -> 27`, `Wrath 27 -> 14`) — but the
user's own description of the second fight pins down the rest: *it walked into melee,
auto-attacked, cast again.* Three separate faults, none of them offsets:

| what the log shows | why | fix |
|---|---|---|
| `[Keys] facing 175, want -81: +104 -> right arrow for 21 ms`, then `facing 152`, then `94` — the facing drifts 20-60 deg between taps | the calibrated rate (0.18 deg/ms) is a *walking* rate; inside a fight a 21 ms tap moved 23 deg, six times as much. And a turn key that is logically still down (its keyup lost among the other presses) keeps spinning the character while the 3.2 s cast is waited out | `Face()` now measures its **own** last tap (deg moved / ms held) and re-sizes every tick; it releases **both** arrow keys before and after every tap (`ReleaseArrows`); and if a tap moves the facing the wrong way it swaps the key pair once and says so |
| every cast after the first `changed nothing in 3200 ms`, mana unchanged, three times, at a target 17.8 yd away | the cast was pressed without checking that the mob was in front — the client refuses a cast at your back, silently, and each refusal cost 3.2 s | the cast key is only pressed after `Face(...)` returns true (30 deg, 8 ticks); when it cannot face, it logs `cannot turn onto the target … Held the cast N time(s)` and turns again instead of casting into a refusal |
| `engaged at 30.3 yd … target is 30 yd away and moving; giving up on it`, eighteen times in 20 s, `0 hotspot(s)` | `ChaseYd` (20) was used as the give-up distance | `GiveUpYd = 45` (the search radius plus margin) is the give-up; `ChaseYd` is now only how far a *melee* fallback will walk. Casting range (28 yd) is reached by walking, then the walk stops |
| `auto-attacking while casting wrath`, character running into melee | the right-click that starts auto-attack also walks the character to the mob, and the melee branch was reachable at any time | the rotation is cast-first: heal, then approach only while outside 28 yd, then `Wrath`; the melee block (right-click + auto-attack) runs only when casting is off the table — no damage spell, under 10 mana, or three casts in a row that landed nothing |

Two follow-ups the same log makes obvious, both done: the `<Grind> … is 17.8 yd away -
stopping the walk` interrupt fired correctly on the second run (the first run's
`0 hotspot(s)` was the stand-and-give-up loop), and the action-bar keys print as `-`
and `=` now, so slot 11/12 no longer read as `½`/`»`.

## CS0103 'step' — and the lint that now catches its whole class

A one-line build break: `ClickAimKeySteer.cs(648,91): error CS0103: The name 'step' does
not exist`. It was a rename (`step` -> `delta`) where one occurrence sat inside a wrapped
`string.Format(...)` argument list and the text replacement missed it. The line reads:

```csharp
+ "{1:+0;-0} deg while {2:+0;-0} was wanted) - swapped them", ms, delta, error));
```

Nothing in `LINT.cmd` could see it: a parse check finds a valid argument list, and
`lint_cs_undeclared.py` only looks at *calls* (`Foo(...)`), not at names passed as *values*.
The two earlier CS0103s from the same machine (`RestMs`, `AimGaveUp`) were also names used
as values, so this is the third of one shape.

`Tools/lint_cs_bare_names.py` closes it. It reports every `identifier` used as a value (an
argument, an operand, a return, an initializer) that the file declares **nowhere** - the
rule is deliberately blunt, because a name declared nowhere is always a bug while a name
declared somewhere is the compiler's problem. Declarations it knows about include
`foreach` variables, lambda parameters, `is`/`out var` designations, `catch` declarations,
enum members and XAML-free fields; it exempts `partial` types whose other half is in the
repository, types with a base class this repository does not declare, and `*.xaml.cs`.

Verified the only way that counts: `step` was reintroduced and the check reported
`ClickAimKeySteer.cs:649:87: 'step' is used as a value and declared nowhere in this file`,
then the fix was restored and it reported zero. Scoped to the tree under work like the
undeclared-call check - over the whole repository it reports 508 legacy findings
(`TreeSharp`, `Tripper`, discards and generated fields), none of them ours.

## 2026-10-30 17:22 — it attacked the town

From the log: `using "Grind" … 684 yd away`, then 0.3 s later
`[Keys] the turn keys were the wrong way round (a 220 ms tap moved the facing +42 deg …)`,
then `right-clicked the mob 3.6 yd away - the client selected it and starts the attack
itself`, then a fight that landed nothing. The "mob" 3.6 yd away in the middle of town was
a **friendly NPC**, and the character never walked the 684 yd because every friendly unit
in the square kept re-triggering the walk interrupt.

Two wrong assumptions, both now gone:

1. **A right-click can choose a target.** It cannot tell a mob from a guard - the snapshot
   has positions, levels and flags, and no reaction word - so it clicked a townsman. The
   untargeted click fallback (`TryClickAttack`, 5.5 kB of it) is **deleted**. TAB is now the
   only way a target is chosen, because the client's own filter is the only thing here that
   knows who is an enemy. When TAB selects nothing the loop logs
   `N unit(s) within 40 yd, none of them selected by TAB … nothing here is attackable` and
   keeps walking - a correct answer in a town.
2. **"A level-appropriate unit is nearby" means "there is a fight here".** It does not: 27
   friendly units within 40 yd in the square. The interrupt now needs either the player's own
   `UnitFlags & 0x800` (in combat) or a unit within 18 yd that carries it.

`0x800` finally has a settled meaning, and it is not reaction - it is **in combat**:

| dump | player | mobs |
|---|---|---|
| 15:57 | `0x80808` fighting | `0x80800` the mob being fought, `0x800`/`0x0` idle ones |
| 16:42 | `0x808` fighting | `0x80800` |
| 17:22 | `0x8` standing in town, fighting nothing | - |

A friendly NPC standing in a square never sets it, so it is the one hostility-adjacent
signal this build actually offers. Also new: a target that a whole fight could not touch
(`nothing landed …`) is remembered for the session as not viable, instead of being selected
and fought again every three seconds.

The turn-key swap firing on the first tap of the run is the intended behaviour of the
measure-don't-assume `Face()`: the walk calibration had not run yet, so the first guess (220
ms for 57 deg) was 40% short, and the code swapped the pair once and went on.

## 2026-10-30 17:26 — the run that killed a mob and then froze

Good news first: the whole loop worked for two minutes. It walked 700 yd of mesh, was
interrupted by a mob at 17:27:56, cast `Wrath 71 -> 56`, auto-attacked `56 -> 41 -> 26 ->
11`, and finished with `Wrath 11 -> 0`. First confirmed kill by a rotation.

Then it stopped moving and the log filled with the same three lines, 40 times a second:

```
[Grind] the only target in reach is the one given up on 1 s ago - walking on to the next hotspot
[Route] navmesh available: every leg is routed on the mmaps ...
[Grind] the character is in combat - stopping the walk
[Route] the walk was cut short on purpose: something worth fighting came within 18 yd
```

Two bugs, both about the same edge:

1. **`cannot cast at it and it is 22 yd away` with 139 mana.** The cast branch was gated on
   the global cooldown (`lastCastMs + CastSettleMs`), and when the cooldown had not elapsed
   the loop fell straight through into the melee block - which then decided the mob was too
   far to chase and gave up. So the very tick after a landed Wrath, the fight ended. Now a
   viable cast either approaches, casts, or waits (`Thread.Sleep(120)`) - it can never reach
   the melee branch, and melee announces itself once per fight with the reason
   (`casting is not available (damage spell Wrath, mana 4, 2 failed cast(s))`).
2. **The mob was still hitting the character while being "given up on".** The give-up exists
   so a hopeless target is not re-selected every 0.3 s, but it was also refusing the thing
   that was eating us: interrupt → refresh target → refused → walk → interrupt, forever, with
   the character rooted. Now a refused target is fought anyway when the player is in combat
   and that unit carries the in-combat bit - "gave up" means do not chase, not stand still
   and die - and an interrupt that produces no fight silences the next one for 4 s
   (`InterruptRetryMs`), so a target that can be neither engaged nor ignored costs 4 s
   instead of pinning the character.

Also: out of mana with a mob on the character, the melee chase limit is `GiveUpYd` rather
than `ChaseYd`, so it walks to the thing hitting it instead of refusing at 21 yd.

## 2026-10-30 17:36 — it fights corpses

The run beat the game (kill at 17:36:55 with two heals and four Wraths), and then fought the
**body** three times: `engaged at 2.8 yd (mob health 0)`, three 3.2 s Wrath attempts, 25 s of
turning in circles, `gave up on the target after 15 press(es)`, then again at 1.1 yd, then
again at 0.7 yd.

Nothing filtered a corpse after the kill. `TryEngage` accepted whatever the client had
selected, and a kill leaves the body selected - TAB moves to the next live enemy, and when
there is none the body stays. Three fixes:

* **TAB now cycles past a corpse.** Each press's result is checked; `health == 0` means press
  again rather than accept. If nothing live comes up, ESC clears the body, the guid goes into
  `_notViable`, and the loop walks on with `the only thing the client offered was a corpse`.
* **`Fight` refuses to start on one**: `lastHealth == 0` at entry returns
  `the target is already dead (health 0)`, because the death check inside the loop needs a
  health reading that was once above zero - which a corpse can never give.
* **No bearing-steering within 2 yd.** The spin is visible in the raw log:
  `want 62 / want 176 / want -1 / want 157`, a 400 ms arrow tap each time. At 0.7-1.1 yd the
  bearing to the target flips 180 deg for a hand's width of movement, so `Steer` was chasing
  its own tail. Inside 2 yd it now just faces the point with a 60 deg tolerance and swings.

Also: `right-click auto-attack landed` is credited only when the mob's health actually
moved - the click alone used to count as a hit, which is why 15 presses at a corpse still
reported success, and why the "nothing landed" path (which bans a target for the session)
never triggered.

## 17:45 run — looting, and the chat message "cannot do that while moving"

The 17:45 log (`2026-10-30_1745_9688.log`) shows the corpse fix holding: one kill, then
`the only thing the client offered was a corpse (health 0) - cleared it and walking on`, and
the walk continues. Two things from the user's message: the chat kept saying **"cannot do
that while moving"**, and **the mobs are not being looted** (auto-loot is already on in the
client, so a right-click on the body is all that is needed).

### 1. Loot

`LootNearby` + `TryLootOne` in `GrindRunner.cs`. A corpse is any unit that is not the player,
not a player character, and reads **health 0**, inside `LootYd` (18 yd). The bot walks to it
with the normal key steering, stops, faces it, right-clicks it once and waits up to 2.4 s for
the body to leave the unit pool.

- **Auto-loot needs no help.** Nothing reads a loot window, nothing presses a key to take an
  item, no memory is written. The client opens, empties and closes the window on its own.
- **Bones linger**, so "still in the pool" is not proof of failure. The log says what was
  done, not what it cannot know: *"right-clicked the body … it is still in the pool — normal
  for bones after a loot"*. Three attempts with an ESC between them (ESC closes a window that
  stayed open, and clears the target anyway), then the body gets one slot in `_looted` and is
  never visited again this session. If auto-loot were off, every corpse would report that,
  which is the symptom to look for in the next log.
- **A fight outranks looting.** `UnderAttack()` is the in-combat flag *plus* a live unit
  fighting within 25 yd — the flag alone lags for seconds after a kill and would have blocked
  every loot trip.
- **When it runs**: after a confirmed kill (`killed` / `killed or fled`), when the client
  offers nothing but a corpse, and once more at the end of a hotspot dwell. Up to 3 bodies per
  call. The run summary gained a `corpse(s) looted` count.

### 2. "cannot do that while moving"

| where | before | now |
| --- | --- | --- |
| first press of a fight | `Fight` started casting with the forward key possibly still held from the interrupted walk | `StopAndSettle(hwnd)` at the top of `Fight` — release, then read the position until two reads agree within 0.15 yd |
| melee swing | right-click issued while `Steer` had the forward key down | `ClickAimKeySteer.ReleaseHeld()` immediately before the click |
| spell cast | `StopAndSettle(hwnd)` before every cast key | same |

Turning with the arrow keys is not movement and does not block an attack; only the walk does.

### 3. Two smaller things the same log showed

- **In combat with nothing to fight.** `[Grind] the character is in combat - stopping the
  walk` fired every second for minutes while TAB saw three units, all corpses
  (`0x808` on the player, nothing live nearby). The in-combat flag now only stops the walk
  when `NearestFighting` actually returns a live unit in range; otherwise the bot notes it at
  most every 15 s and **walks on**. That was the cause of `Detour returned 2 points … Refusing
  to walk it` on a 137 yd leg — the leg never got far enough to have a path worth following.
- **TAB holding a stale target.** When TAB keeps a selection more than `SearchYd` away the bot
  now presses ESC to clear it and throttles the log line to once every 5 s (it printed six
  times in eight seconds).
- The kill counter now accepts `killed or fled` as a kill, in the main loop and in the hatch
  dwell (17:45 finished real kills and still reported 0).

## 18:08 run — the game menu after every kill: ESC

The user's report: **"opens game menu after monster dies"**. The 18:08 log
(`2026-10-30_1808_9616.log`) walked, fought and killed properly — 4 fights, 4 kills, `Wrath does
damage` four times per mob, a hotspot reached, 225 s — and the loot lines appear exactly where
the kill was:

```
[18:11:11.898] [Grind] right-clicked the body of 0x…1A41 (2.8 yd away); it is still in the pool
[18:11:11.898] [Grind] looting: 1 body(s) right-clicked (0 of them disappeared)
[18:11:11.898] [Grind] killed or fled after 11 s; what landed: Wrath (slot 2)
```

**The cause was ESC, and it was mine.** In the client, ESC closes the topmost window when one is
open and **opens the Game Menu when none is** — and with auto-loot on, no loot window is ever
open. The loot retry pressed ESC between attempts, so every corpse ended with the main menu over
the game, which then swallowed TAB and every rotation key. The same mistake was in three more
places: clearing a corpse off the target, dropping a player target, and dropping a far TAB
target.

**ESC is gone from the action path entirely** (`grep 0x1B` in `Styx/` now finds only offset
tables). What each of those places does instead:

| was | now |
| --- | --- |
| corpse selected → ESC | nothing. `Fight` refuses health 0 at entry and the TAB loop skips it; a corpse left selected blocks nothing |
| player selected → ESC | nothing, plus the guid goes into `_notViable` so TAB results are not re-examined every 300 ms |
| TAB target out of `SearchYd` → ESC | nothing; the walk continues under the selection and the log line stays throttled to 5 s |
| loot retry → ESC | `StopAndSettle` only, then a second right-click |

Loot retries are down to **2 attempts with a 1.5 s wait** (auto-loot finishes in well under a
second), so a corpse costs about 3 s instead of 7.

### Proving the loot instead of assuming it

A body that has been looted stays in the unit pool as bones, so `still in the pool` proves
nothing either way. On the **first corpse of a session** the bot now reads two 64-word windows of
the corpse object (`+0x1B00` and `+0x1E00`), right-clicks, reads them again and prints only the
words that moved:

```
[Loot] word +0x1E64: 0x00000001 -> 0x00000000
[Loot] 1 word(s) changed on the body of 0x… across the right-click
```

A word going `1 -> 0` across the click is the client's lootable flag — `UnitDynamicFlags`
`LOOTABLE` is bit 0 — and once the log names it, "did that right-click actually take the body"
stops being a guess. If nothing moved the log says so, which is equally useful.

## 18:35 run — the loot right-click never landed on the body

User: **"still won't work, you can right click on the monster after it is dead (once) to loot it"**.
The log shows the click being sent and nothing happening:

```
[18:35:13.263] [Loot] 0 word(s) changed on the body of 0x…12E6 across the right-click
[18:35:20.288] [Grind] right-clicked the body of 0x…12E6 (2.8 yd away); it is still in the pool
```

The ESC/game-menu bug is fixed (no menu in this run, the corpse line no longer presses a key),
so this is a **new, older bug that the loot code merely exposed**: the projected screen point is
not where the body is.

**Why.** `TryProject` puts the unit exactly where the maths says, and for a unit 2–4 yd in
front of the character that vector is almost parallel to the camera axis — the camera sits
behind the character and looks *through* it at the character. So the projected point lands on
the character's own back, and a right-click there clicks the character, not the mob. This also
explains the older logs: **no right-click auto-attack has ever done damage**, `what landed:` is
always `Wrath (slot 2)`, and the one `right-click auto-attack landed` in the 17:36 log was a
false credit at a corpse.

### The fix: a click the client confirms

`TryClickUnit(hwnd, unit, px, py, mustSelect, out how)` now clicks, then **asks the client**:
is the target guid now this unit? A right-click on a body both selects and loots it, so a target
guid that becomes the corpse *is* the proof that the click landed.

1. Click the projected point (plus any offset already learned).
2. If the target did not change, click a small ladder of points around it — `0, ±40, ±80, ±120`
   px across and `0, +30, +60, -25, +90, -50, +120` px down, 18 candidates at most. A body 3 yd
   ahead sits low and partly behind the character's own model, which is why the ladder is
   wider than it is tall and why the horizontal steps come first.
3. Every miss that hit the ground starts a click-walk: release, tap `W` to cancel it, turn back
   onto the body, then try the next candidate.
4. The offset that worked is kept for the session in `_clickBiasX/_clickBiasY`, so every later
   unit click is a single click again. If the stored offset ever stops being confirmed it is
   dropped and relearned.
5. Loot only counts as loot when step 2 confirmed it. `TryLootOne` returns
   `LootNoClick` when no point reached the body and the log says so in full:
   `[Loot] could not reach the body of 0x… with the mouse: none of 18 click points around the projected one selected it`.

Melee gets the same treatment from the other side: the `Attack` hotkey (slot 1) is now pressed
on **every** swing, not only when the projection fails, because the key cannot miss and the mob
is already selected by TAB. The right-click is the human-looking path; the key is the one that
works.

### The loot probe is now trustworthy

The five-window word diff (`+0x1900 … +0x2100`, 320 words) used to run across a click that had
done nothing, so "0 words changed" meant nothing. It now runs **only across a click the client
confirmed selected the corpse**, which is the first time the diff can actually see a loot flag go
`1 -> 0`. It stays once per session and prints only the words that moved.

## 18:41 run — one corpse looted, and the two things that still break it

User: **"managed to loot one monster (after clicking a lot near it) then tried to do the same for
the other monster and failed (at least it knows when the loot happened)"**. The confirmation
machinery works — that is the important part of this run:

```
[18:43:52.560] [Loot] the right-click on 0x…1E47 landed (the projected point missed the body; 40,60 px
                       from it selected it, and that offset is now used for every unit click)
[18:43:54.759] [Loot] the right-click on 0x…1E47 landed (the click landed on the learned offset 40,60 px)
[18:43:54.759] [Grind] looted the body of 0x…1E47 (2.8 yd away); the bones are still in the pool
```

The learned offset was reused and re-confirmed one second later, and the 0-word diff across a
**confirmed** click settles a question the earlier runs could not: this build does not keep a
readable loot flag in the windows `+0x1900 … +0x2100`, so a body leaving the pool is the only
external signal there is, and the confirmation that the client selected the body is the real proof.

### Why the second corpse failed

```
[18:43:56.157] [Grind] engaged at 18.3 yd (mob health 86)     <- killed at range with Wrath
[18:44:24.940] [Loot] could not reach the body of 0x…1F32 with the mouse:
                      none of 18 click points around the projected one selected it
```

**Nothing was wrong with the clicks.** A right-click on a body more than ~5 yd away is not a loot
at all — the client reads it as "walk there". The walk budget (12 s, started 17 s earlier) ran out
with the body still 18 yd away, and the sweep then swept a corpse that could not be clicked from
where it stood. The sweep was measuring the wrong thing and said so honestly.

Fixed:

- **`LootReachYd = 4`** — the walk loop runs until the character is inside 4 yd, with a 20 s
  budget, and if it is not there afterwards **no click is sent at all**. The log now says
  `could not get to the body of 0x… - still 12.4 yd away and the reach is 4 yd, so no click was
  sent (a right-click from here is a walk order)`.
- **The sweep turns the character.** A unit 2–3 yd in front sits almost on the camera's own axis,
  so its projected point can be squarely on the character's own model, and no pixel offset of a
  hundred gets off that model. Candidates are now `(yaw, dx, dy)` pairs: the plain point, the
  offsets that have worked (`0,30` `0,60` `±40,60` `±40,30`), and then **±35° and ±55°** turns
  with the projection recomputed from the moved camera — the body is then well to the side of the
  character's silhouette and the click lands. Turning is only used when the cheap offsets fail, and
  the log names which one worked.

## 18:50 run — stop guessing pixels: find the body with left-clicks

User: *"still failed for the other monster"*. The log is unambiguous:

```
[18:52:46.410] [Loot] could not reach the body of 0x…21A7 with the mouse:
                      none of 11 click points around the projected one selected it
```

Eleven right-clicks at a body four yards away, every one a guess. The run before had looted a
corpse at 2.8 yd with a `40,60 px` offset; this one at ~4 yd found nothing in a ladder, in 35° and
55° turns, or anywhere else. **The projection is simply not accurate enough to aim a click with,
and a right-click is the wrong probe to discover that with** — a missed right-click walks the
character or attacks something.

### The probe is now a left-click

A left-click on a unit only **selects** it. A left-click on the ground only clears the selection.
Neither moves the character, starts a fight, or presses a key. So it is safe to click somewhere,
read the client's target guid, and learn whether the body was under that pixel.

`FindUnitPixel`:

1. Start at the projected point, corrected by the offset learned earlier this session.
2. Probe outward in rings — radii `0, 40, 80, 130, 190, 260` px, eight directions each, at most 40
   probes. The rings are in *hundreds* of pixels on purpose: a body a few yards ahead projects
   onto the character's own model, so the error is not a nudge, it is most of the character.
3. Each probe: left-click, wait 70 ms, read `targetGuid`. Equal to the body's guid means that
   pixel has the body under it.
4. Remember `found - projected` as the session offset, so the *first* probe of the next search is
   already right.
5. Right-click **that** pixel — a click that cannot miss the body because the client just said
   the body is there.

The loot line now reads what actually happened:

```
[Loot] left-click probe 1 found it 40,60 px from the projected point; that offset is now used to
       start every search
[Loot] left-click probe 7 found it -80,130 px from the projected point; ...
[Loot] 40 left-click probe(s) found no pixel with that unit under it (search centred 12,44 px from
       the screen centre)
```

The last form is the useful failure: it says the body was not under any of 40 pixels near where
the projection expected it, and where the search was centred.

One case a left-click cannot settle: the unit is **already the target**, so the guid cannot
change and every probe looks like a miss. There the right-click goes to the projected point and
the log says so (`it was already the target, so this right-click went to the projected point
unverified`).

### Also in this log, not fixed here

`[ClickAim] no progress for 25 s: stuck at 25.5 yd from the point … [Grind] could not walk to
hotspot` — the click-to-move fallback used when Detour refuses a leg stalled 25 yd out and the
bot stood there until it gave up. That is the mover, not the loot; it needs its own run.

## 18:57 run — looting works; one click per body, then look away

User: **"success, loots the monster after it is dead - successfully, then it selects the dead
monster again (after it was already looted earlier)"**. Four corpses, four loots:

```
[18:59:50.565] [Loot] the right-click on 0x…2469 landed (left-click probe 20 found it 0,130 px from
                      the projected point; that offset is now used to start every search)
[18:59:52.740] [Loot] the right-click on 0x…2469 landed (left-click probe 1 found it 0,130 px …)
[18:59:52.740] [Grind] looted the body of 0x…2469 (2.8 yd away); the bones are still in the pool
```

Two things in that pair of lines:

1. **The probe works.** The first search cost 20 probes and learned `0,130 px`; every later corpse
   was found by probe 1. That is the projection being wrong by most of the character's height and
   the search correcting it, exactly as intended.
2. **The second pair of lines is the bug the user saw.** Every body was clicked *twice*, 2.2 s
   apart — the retry from `LootTries`, which could not tell "these are bones, the loot happened"
   from "the click missed". A right-click on a body also SELECTS it, so the second click picked up
   the corpse that had just been looted.

Fixed:

- **`LootTries` is gone.** A left-click probe has already proved the click lands on the body, so
  there is nothing a retry could establish. One right-click per corpse, full stop.
- **The target is cleared afterwards.** The loot click leaves the body selected, so once the body
  is settled the bot presses a left-click on a pixel the search already proved empty (a probe that
  came back with *nothing* selected). A left-click there deselects and does nothing else — no walk,
  no attack, no window. That is the only way to drop a target in this client: ESC opens the Game
  Menu. The corpse then stays untouched: `_looted` keeps it out of the loot list for the session,
  and the TAB loop refuses it by health 0.

One line is left that a reader might wonder about — `the only thing the client offered was a
corpse (health 0) - walking on without touching it`. That is TAB, the client's own targeting,
finding nothing but the body it just stood over. The bot does not press a key on it and does not
stop for it; it walks on.
## 19:07 run — settings, Moonfire, and a window to change both

The user's words for this run: *"ran pretty well. Next I want settings - eat/dring at certain
Hp/MP procent, and using up new skills (I learn moonfire but the bot isn't using it or if I want
to use a new class With different skills)"*. Looting is settled; this run is about the numbers the
rotation works to and about spells it did not know.

**Moonfire was on the bar all evening and was never cast.** Slot 5 held 8921 from the first log to
the last. The old rotation looked for a *direct-damage* spell and for a heal, and 8921 was in
neither list, so a DoT the character had learned was invisible to the bot. Two things follow from
that:

- **Roles are derived from the spell NAME, not from an id list.** `KnownSpells` still maps id to
  name (ids are the only thing on the bar), but what a spell is *for* comes from `RolesByName`.
  One new id, one new line, and the role follows. The table now covers the 3.3.5a families for
  every class, because the next character may not be a druid.
- **A DoT is applied once per target, not spammed.** Casting it every rotation would be a global
  cooldown spent for nothing. It is put on a target guid once and re-applied only after
  `DotRefreshSeconds` (default 12 s, which covers Moonfire/Corruption/Pain/Immolate at low ranks).
  The client's debuff list is not readable on this build, so the timer is the bot's own cast time
  — the honest version of "is it still ticking".

**Eating and drinking.** Between fights, out of combat, with nothing on us: release the movement
keys, stop, press the food or drink key, and wait. The thresholds are the user's, in percent, and
`0` turns the rule off. Food and drink are *items on the action bar* (slot 11 was 159, Refreshing
Spring Water; slot 12 was 4536, Shiny Red Apple), so this is a key press like everything else —
no Lua, no item API.

**The mana percentage needed a different denominator.** Every log line on this build says
`pow=139/100`: the max-power word is a candidate and lies here. A percentage taken from it would
read 139% and the drink rule would never fire. The fix is the session peak — `max(recorded max,
highest power seen)` — and it is self-correcting because power only climbs while resting or on a
level-up, both of which the peak follows within one read.

A meal is not one bite: the loop rests until health and mana are back to 90% (or `RestMs`, 20 s,
runs out, or something starts hitting). Stopping the moment the threshold clears was the first
version's mistake — a character at 34% mana drank for six seconds, reached 39%, walked off, and
drank again at the next gap.

**The settings are a file and a window.** `Settings\clickaim-grind.txt` holds the numbers
(`EatBelowPercent`, `DrinkBelowPercent`, `HealBelowPercent`, `UseDamageOverTime`, `UseBuffs`,
`FoodSlot`, `DrinkSlot`, `DotRefreshSeconds`, `RestMs`), `Settings\clickaim-skills.txt` holds the
user's own spell roles (`<spell id> = Attack|Damage|Dot|Heal|Buff|Utility|Ignore`). Both are read
by `ClickAimSettings.Load()` at the start of every run, and both are written by the **Combat
settings** button in the CopilotBuddy window — which is visible before the client is attached, so
the numbers can be set up front. The window is a view: the files stay authoritative, a hand edit is
picked up the same way, and there is no second source of truth to disagree with.

The Spells tab exists for exactly the case the user described — a spell the bot does not recognise,
or a class whose bar it has never seen. It lists every filled slot with the key it is bound to and
the role the rotation will use, and a role set there wins over the name table, including `Ignore`
for a spell that must never be pressed. If the bar cannot be read (no client attached) the tab
falls back to the roles already set by hand, so a bad override can be undone without the game
running.

Both self-tests grew: `Grind settings self-test` covers the parser, the clamping and the round
trip; `Combat self-test` now checks that Moonfire is a DoT, that a hand-set role beats the table in
both directions, and that food/drink are told apart by item id.

## The 19:07 leg that ended at 25.5 yd — sidestep before giving up

    [ClickAim] no progress for 25 s: stuck at 25.5 yd from the point (best was 26.3)
    [Route] stopping at waypoint 1/1: the mover did not arrive

A 37 yd leg, 11 yd of it walked. What happened is the shape of a click-walk: the client
walks in a straight line from where the character stands and **stops at the first thing it
meets**. The mover noticed (`Blocked`, after two clicks that moved nothing and its one
detour spent), but `WalkSteps` asks `WalkTo` with `stopWhenBlocked: false` — so nobody looked
at `Blocked`, the mover refused to dispatch another click, and the pilot kept calling
`MoveTowards` into a mover that had already given up. Nothing in that loop was ever going to
move the character; the only exit was the 25 s stall timer, and the grind then abandoned the
leg.

Fixed the way a player handles it:

- **Sidestep first.** Blocked, or eight seconds without two yards of progress → aim seven yards
  off the straight line (right, then left, then right), jump once, walk that, then ask for the
  goal again from the new position. `ClickAimMover.ClearBlock()` clears `Blocked`, `_stuck` and
  the detour count, because "cannot get there" was a statement about where the character was
  standing, not about the goal.
- **Three sidesteps per walk**, then the old 25 s give-up still fires (and now says how many
  sidesteps were spent). The mesh path is untouched: it re-plans on its own and passes
  `stopWhenBlocked: true`, which keeps its blackspot behaviour.
- **A jump is part of the sidestep.** Space is jump, it is free, and a kerb, a stump or the lip
  of a hill is what stops a click-walk most often in Elwynn.
## Two things the user found in play: a heal it cannot pay for, and melee skills treated as ranged

**1. "below 60% and no mana → it tries to heal until it dies."** Exactly what the code did: the
heal gate was `health <= HealBelowPercent && cooldown` and nothing else, so a character with 8
power pressed Healing Touch every 4 s. Each press failed for want of mana, each press ate the
global cooldown, and the mob finished it off. Three changes:

- **`HealMinManaPercent` (default 25, in the settings file and the window)** — the heal is only
  considered while power is at least that share of the highest maximum seen this session. Below
  it, the heal is skipped **and the loop carries on** to damage and auto-attack, which is the
  behaviour asked for: fight with what you can still afford.
- **The cost is learned, not assumed.** `LearnHealCost` records the biggest power drop a
  successful heal has produced this session (`_healCostGuess`); after that, `HealPowerOk` also
  requires the current power to cover that cost with 25% spare. No spell-cost API exists on this
  build, so this is the honest version of "can I afford this".
- **Two dud presses end it for the fight.** If a heal neither lifts health nor spends power, that
  is counted; at two, the heal is off for the rest of the fight and the log says so. Previously
  the only exits were the mob dying or the character dying.

The failure line now distinguishes the two cases — `power 20 -> 20, the press did nothing at all`
versus `power 139 -> 99, so it cast and the mob out-damaged it` — because they have different
fixes (more mana / smaller heal) and used to read the same.

**2. "the bot thinks all skills are ranged."** True: every spell was driven at `CasterRangeYd`
(28 yd). Claw, Heroic Strike, Sinister Strike, Rend and the rest of the melee kit are 5 yd
abilities, so a cat stood 25 yd off the mob pressing Claw — the client's "out of range", nothing
else.

- **`RangesByName` + `RangeOf(id)` / `IsMeleeSpell(id)`** in `ClickAimCombat`. The default stays
  28 yd (that is what most spells are); the table lists the exceptions by name, so one entry
  covers a whole rank family like every other table here. `MeleeRangeYd = 5`.
- **A melee ability is not a cast.** `canCast` now requires a *ranged* damage spell; a melee one
  goes to the melee branch, which closes to `AttackRangeYd`, right-clicks, presses Attack and
  presses the ability. Waiting 3.2 s for a "cast" of Claw would be an expensive way to miss.
- **The DoT has its own range** (`dotRange`), so melee DoTs (Rend, Rake, Rip, Rupture) are
  applied at arm's length instead of never.
- **Missing ids added**: the whole feral druid kit (Claw, Maul, Shred, Swipe, Rake, Rip, Ferocious
  Bite, Mangle, Pounce, Bash, Demoralizing Roar, Cat/Bear Form) plus the warrior, rogue, paladin,
  shaman and hunter melee abilities the name table never had — `spell <id>` in the log is now a
  name.
- **The Spells tab shows the reach** of every slot (`melee 5 yd` / `28 yd`), so "is this skill
  ranged?" is answered by the window rather than by watching the character stand still.

Self-tests grew accordingly: `wrathIsRanged`, `clawIsMelee`, `rendIsMeleeDot`,
`judgementIsShortRanged`, `unknownSpellHasNoRange`, `prefersRanged`/`findsMelee`, plus
`healPowerEnough`/`healPowerTooLow`/`healPowerLearnedCost` and `meleeReachNests`.

## The 22:47 run — healing with no mana, and never swinging

    [Grind] rotation: heal at 40 and only above 25% power% health (slot 3 '3' Healing Wave) ...
    [Grind] health 38% and Healing Wave did not lift it (still 51/135, power 30 -> 15 ...)
    [Grind] health 34% and Healing Wave did not lift it (still 37/135, power 15 -> 19, the press did nothing at all)
    [Grind] the local player reads 0 health - the character is dead

Two separate bugs, both in the gate that was supposed to prevent exactly this.

**The power gate had an escape hatch.** `HealPowerOk` started with `if (denom == 0) return true;` -
"no maximum readable, so no opinion". On this session the max-power word read **0** (it has read
100 for 139 power before; it is a candidate word, not a field), so the denominator was 0, the gate
said yes, and the price rule that had already learned Healing Wave costs 25 power was never
reached. A shaman with 15 power pressed it twice and died.

Fixed:
- **The price rule comes first and does not need a maximum.** Once a heal has been seen to cost
  something, "can I afford it" is answered outright: power must cover the cost plus 25%.
- **The price is learned from every press that spent power**, including one the mob out-damaged -
  that is still proof of what the spell costs, and learning it early is what protects the next
  press.
- **An unknown maximum no longer means yes.** With no denominator and no price, the only honest
  rule is that a heal needs power at all (`power > 0`).
- **Every read feeds the session peak** (`_manaPeak`), not just the resting code, so after the
  first fight a percentage has a real denominator even when the word lies.

**Casting was gated on 10 power, which is not a price.** Lightning Bolt costs 15, so at 13 power
the loop kept casting: the press was refused for want of mana, `canCast` stayed true, and the
melee branch - the only thing that would have swung - was never reached. That is the user's "did
not auto attack when out of mana".

- **`_castCosts` + `PowerForCast`**: the price of each damage spell is measured and casting needs
  cost + 20%. Below it, `canCast` is false, the loop logs
  `13 of power left and Lightning Bolt needs about 18 - not casting again until the bar comes
  back; auto-attacking instead`, and control falls to the melee branch, which walks in, right-
  clicks, presses Attack and any melee ability.
- **The measurement happens one read later.** On this client the cost leaves the bar at the end of
  the cast, and the poll that catches the mob's health move can land first - every bolt in the log
  says "mana 150 -> 150, 135 -> 135". So the press is remembered (`_pendingCastId`) and priced on
  the next look at the bar.
- **The banner has brackets now**: `heal at 40% health and only above 25% power (slot 3 '3'
  Healing Wave)` - the old string glued `% health` onto the power phrase.

Also in this run: `[MeshQuery] the player had no usable height (given 386.2); searched 400 yd and
found the mesh at 3.5`. A polygon 383 yd below the character is a different layer of the world, and
Detour was being asked to route between two points that are not connected - which is where the
recurring `Detour returned 2 points` comes from. `MeshQuery.Snap` now refuses an answer more than
`MaxWidenedGap` (100 yd) away in height and says so; the caller falls back to aim-and-click, which
is what actually walked those legs.

And the dwarf racials are named: 20594 `Stoneform`, 2481 `Find Treasure` (both `Utility`, never
pressed - the log just stopped printing "spell 20594 (not in the table yet)").
## The 23:00 run — out of mana it circled the mob instead of hitting it

    [Grind] 9 of power left and Shadow Bolt needs about 30 - not casting again ... auto-attacking instead
    [Grind] auto-attack does damage: mob health 42 -> 37
    [Keys] facing 271 deg, want 88 deg: +177 deg -> left arrow for 400 ms
    [Keys] facing 317 deg, want 149 deg: -168 deg -> right arrow for 400 ms
    [Keys] facing 245 deg, want 149 deg: -96 deg  -> right arrow for 400 ms
    [Grind] cannot turn onto the mob at arm's length (3.1 yd) - the arrow keys are not moving the facing field

The mana gate did its job — it stopped casting at 9 power and started swinging (the mob's health
was dropping). What went wrong is everything the melee branch did around that swing:

- **It right-clicked the mob on every pass.** At arm's length the mob's projected point is its own
  feet or the ground beside it, and a right-click on the ground is a **walk order**. The character
  ran forward, the distance changed, the loop steered again — that is the "runs back and forward",
  and because each walk order turned it, the same loop produced the circus: `facing 271 -> 317 ->
  245 -> 333 -> 175`, ninety degrees at a time, orbiting the mob while auto-attack did the damage.
- **It re-aimed and re-faced on every pass** (`Steer` turn-only, then `Face` with a 20 deg
  tolerance) even while swings were landing.
- **The walk stopped exactly at `AttackRangeYd`**, so the character was still moving when the key
  came up, coasted past the mob, found itself out of range on the far side and walked back.

Fixed:

- **Inside `MeleeEngageYd` (5.2 yd) nothing moves the character.** The only things sent are the
  Attack key, a melee ability key, and — if the bar has no Attack at all — one click every 2.5 s.
- **The Attack key is preferred to the click** for exactly this reason: a key cannot be mistaken
  for a walk order.
- **Facing only when nothing has landed for 2.5 s** (`MeleeRefaceAfterMs`), with a 45 deg
  tolerance; a character that is hurting the mob is already facing it.
- **The swing is tracked** (`meleeSwinging`, set when the mob's health actually drops) and the
  loop says `the swing is running ... holding position and letting the client swing`.
- **The walk stops 1.2 yd short** (`MeleeEngageYd`) so the coast lands inside the range; and if
  nothing lands for 2.5 s while the mob is 2.5+ yd away, one short steered step forward closes it
  — a step, not a click.
- **`quietRounds` resets whenever damage lands**, so a long melee no longer abandons the mob after
  twelve passes.

The rotation banner also reads properly now: only the first log line in that run still carried the
old broken string, from the build before `9ce9165`.
## The 23:00→ next run: auto-attack flickering, and mobs walked past

**"It activates and deactivates auto-attack."** Because that is exactly what the code was doing:
the melee branch re-pressed the Attack key every `MeleePressMs` (1.2 s) on a timer, and the Attack
action **toggles** auto-attack on this client — press it while the swing is running and the swing
stops. So the character alternated start/stop/start while the user watched.

Fixed: the swing is only (re)started when it is demonstrably NOT running — nothing has landed on
the mob for `MeleeSwingQuietMs` (4 s) — and then exactly once, with the log line
`pressing Attack to start the swing (it stays on until something stops it; pressing it again would
turn it off)`. Anything that cancels a swing (a heal, a damage cast) sets `meleeSwinging = false`,
so the key is pressed again afterwards, and only then. A melee ability key (Claw/Heroic Strike) does
not disturb the swing and is still pressed on its own timer.

**"When moving between hotspots it walks past attackable monsters."** `FightNeeded` — the route
interrupt — only ever returned true for a unit that was *already in combat* (`UnitFlags & 0x800`),
i.e. a mob that had attacked the character. A wolf standing ten yards off the path has that flag
clear, so the walk ignored it; the only fights on a leg were ambushes, and everything else had to
wait until a hotspot's dwell loop pressed TAB.

Fixed with a *pull probe*:

- between polyline points, at most once every `PullProbeMs` (1.5 s), the walk asks the client
  whether there is anything attackable nearby: first the current selection (costs no key), then one
  TAB — which is the client's own hostility filter, the only thing here that can tell a wolf from a
  guard;
- a candidate is taken when it is alive, not a player, not already given up on, inside the level
  band and within `PullYd` (30 yd — the old interrupt radius was 18 and required the mob to be
  hitting us);
- `TryEngage` now accepts the selection the walk stopped for (`going with the target already
  selected`) instead of pressing TAB again and cycling past it;
- a target the walk would not take is explained at most every 15 s, so a field full of grey mobs is
  one line, not one per probe.

Self-tests: `pullInsideSearch` (18 yd interrupt < 30 yd pull ≤ 40 yd search, ≥ 28 yd cast range),
`pullProbeIsThrottled`, `swingQuietWindow`.

## The 13:23 run: a 100 yd circle, chasing, and the pool instead of TAB

User, after `6518c03` (auto-attack confirmed fixed): *"It's not attacking monsters between hotspots
unless the monsters are in front of the character, also the bot is not chasing monsters (if a monster
at the hotspot is 40 yards away it skips it - it should move to the monster and attack it then try to
find more monsters around the character (max 100 yards, if no monsters are around THEN move to next
hotspot). Do we have a memory offset so that the bot sees the monsters around it? so that we don't have
to rely on TAB?"*

The 13:23 log (`2026-10-31_1323_6888.log`) has every symptom in it: `TAB has a target 48 yd away
(40 yd is the search radius) - not chasing it, walking on`, and `the only thing the client offered was
a corpse (health 0) - walking on without touching it` eight times in eight seconds.

**Radii are the user's numbers now, all measured from the CHARACTER.**

| constant | was | now | why |
| --- | --- | --- | --- |
| `SearchYd` | 40 | **100** | the circle the bot fights in |
| `PullYd` (walk-off path) | 30 | **100** | a leg runs through the same circle |
| `ChaseYd` (melee walk-in) | 20 | **`SearchYd`** | out of mana is the normal state at level 2, and 20 yd refused the mob the search had just accepted |
| `GiveUpYd` | 45 | **`SearchYd + 15`** | the fight only gives up past the circle |
| `PoolSelectYd` | — | **50** | how far an *unverified* pool pick may be walked to |
| `HotspotDwellMs` | 9 000 | **30 000** | a ceiling, not a timer: two clear scans end the hotspot early |

**The hotspot ends when the area is clear, not when the clock says so.** `DwellAtHotspot` counts
consecutive empty scans (`AreaRescanMs` 1.2 s apart) and leaves after two, so a hotspot that has been
swept is left immediately and one where a mob walked in is not cut off mid-fight.

**The unit pool is now a first-class source of targets** (`NearestKillable`), not just a way to count
what is around. Per unit it has the position (`Location`), the distance, `hp/max` (`+0x1E30`/`+0x1E38`),
`level` (`+0x1E40`) and `UnitFlags` (`+0x1E60`). It filters on: alive, not a player, not refused, inside
the level band, and not carrying `NonAttackableFlag` (`0x2` — bit 1 of `+0x1E60`, which is the first
thing the client's own attack predicate tests; see `hunt/WOWB_70291_TRACE_FINDINGS.md`, 16:27). What it
does **not** have is a reaction (friendly/hostile) word, so the order of asking is:

1. the current selection (free);
2. **TAB** — the client's enemy filter, still the only authority on "is it an enemy";
3. ~~the **pool**, selected with a left-click~~ — **removed in the 13:46 run**, see the section below.
   It found the lazy peon. The pool now only decides *whether* the area is clear, *which way* to face
   before asking TAB, and *when* to stop the walk.

`FightNeeded` (the route interrupt) also asks the pool now, so a mob beside or behind the character
stops the walk even when TAB — which only looks in front — offers nothing. The selecting is left to the
main loop: a 40-probe left-click search does not belong on the route thread.

**Diagnostic for the reaction hunt:** `ReportMemory` now prints up to ten units inside the circle with
`distance, level, hp/max, flags`. Standing next to a wolf and a guard in one spot puts the two lists in
one log; the field that differs between them is the faction/reaction candidate.

Self-tests added: `engagementRadius` (SearchYd == 100), `chaseReachesTheSearch`, `pullUsesTheSameRadius`,
`giveUpBeyondSearch`, `poolSelectInsideSearch`; `rangesNest` re-expressed without `ChaseYd`.

## The 13:46 run: a friendly peon, and casting that kills the swing

User after the 100 yd build: *"target lookup was much better BUT it tried to attack a friendly NPC lazy
peon. So I stopped the bot ... Also if the bot is auto-attacking but enough mana is regained it
disables auto attack and tries to use a spell (but if the character is hit by the monster it looses
the gained mana so auto-attack is canceled - DPS is lost)."* Log `2026-10-31_1346_1528.log`.

### The peon

```
13:49:28.053 TAB offered nothing live, so the pool was used:
             0x20011800200A4F00000000000000001F is alive at 34.4 yd (level 4) and the client
             selected it on a left-click at -219,-254 px
13:49:33.116 Shadow Bolt changed nothing in 3200 ms: mana 97 -> 132, mob health 86   (x3)
```

Every real mob in that log was selected by TAB or was the current selection; the one thing the pool
fallback added was a friendly peon, and it cost twenty seconds of casting at something that cannot be
damaged. The pool has no reaction word, so it cannot tell a peon from a wolf — and TAB, which can, had
already said no.

**First answer: the fallback was removed** (commit `64983cc`). **Second answer, on the user's
instruction: it is back, with a verdict attached** — see "Move to it, then let TAB decide" at the end
of this file.

What replaces it is bookkeeping on TAB's own answer: a unit the pool can see **inside `TabReachYd`
(45 yd — TAB has been seen to reach 48)** that TAB refuses twice, at least `TabMissSpacingMs` (5 s)
apart, is left out of the *scan* for `TabRefusePauseMs` (60 s), so a hotspot holding nothing but
friends finishes instead of standing there. It is a pause, not a blacklist: a mob at the edge of
TAB's reach is refused for being far, not for being friendly, and it is asked about again a minute
later — or the moment the walk brings the character closer. Beyond TAB's reach a refusal proves
nothing, so nothing is recorded there at all.

`FightNeeded` now stops the walk for a pool unit inside `TabReachYd` (not 50 yd): the walk is what
closes the rest of the distance, and the main loop faces the unit and asks TAB, which only looks in
front.

### The swing

The log is four copies of the same four seconds:

```
13:47:37.639 Shadow Bolt does damage: mob health 37 -> 34 (mana 31 -> 37)   <- mana came back
13:47:37.925 pressing Attack to start the swing ...                        <- 0.3 s later
13:47:40.758 pressing Attack to start the swing ...                        <- 2.8 s later
13:47:41.460 the swing is running (mob health 34 -> 33)
13:47:44.085 Shadow Bolt does damage: mob health 30 -> 27 (mana 37 -> 23)  <- and again
```

Three separate faults, all in the melee half of the rotation:

1. **Melee was not latched.** `canCast` was re-evaluated every pass, so as soon as mana regenerated
   past `PowerForCast` the fight went back to casting — and a cast breaks a running swing, which is
   the DPS the user lost. Now the first time a fight reaches arm's length it sets `melee = true` for
   **the rest of that fight** (`melee from here FOR THE REST OF THIS FIGHT`). A player does not stop
   swinging to start a 2.5 s cast in melee at level 3.
2. **Attack was pressed twice.** The key is a toggle; the second press at 40.758 turned off the swing
   the first one started. A press is now given `MeleePressGraceMs` (6 s — longer than any swing timer
   seen) to prove itself, and a second press needs `2 × MeleeSwingQuietMs` (8 s) of real silence.
   Auto-attack survives a heal or a spell on this client, so the default is not to press again at all.
3. **The fight gave up mid-swing.** `quietRounds >= 12` is only ~4 s, and a level-3 swing lands every
   3.5 s, so at 13:48:28 it gave up on a mob at 25 health, re-engaged it 0.3 s later and killed it.
   The break now also requires `2 × MeleeSwingQuietMs` of no damage.

Self-tests: `meleePressGraceExceedsSwing`, `tabReachInsideSearch` (replaces `poolSelectInsideSearch`).

## Move to it, then let TAB decide

User, on `64983cc`: *"OK I don't like this change. Make it so THAT it moves to the alive NPC AND if the
HP stays full for 10 seconds and IT IS IN THE RANGE of TAB try to use TAB, if nothing is offered then
it means the target is friendly or BLOCKED so ignore it and try a new target or go to the next hotspot
if no target is found. OR we try to find the Factions OFFSET (if this doesn't work)."*

So the pool selects again — it is the only way to reach a mob that is not in front of the character or
further out than TAB — but a pool pick is now **on trial**:

1. `TryEngage` step 3 left-clicks it (`FindUnitPixel`; success means the client's own target guid
   answered with that unit), logs `TAB offered nothing live, so the pool was used: … it has 10 s
   inside TAB's reach to prove itself`, and the fight starts — the fight walks to it, so this is the
   "move to the monster" the user asked for.
2. In `Fight`, for a target that did not come from TAB: the clock starts when the target is first
   inside `TabReachYd` (45 yd) and only runs while **its health has not moved**. After `PoolVerifyMs`
   (10 s, the user's number) the client is asked directly — one TAB.
   - **nothing live offered** → the client does not treat it as an enemy (friendly NPC, or something
     behind a wall). It goes into `_notViable` for the session (`it is ignored from now on and the
     next target is taken`), so the scan goes clear and the hotspot finishes if there is nothing else.
   - **the same unit** → it *is* an enemy: `TAB confirmed the pool's target is an enemy - the fight
     carries on`.
   - **a different live unit** → the client moved the selection; the fight stops and the loop
     re-engages what TAB picked (`TAB skipped … and selected … instead`). Nothing is blacklisted on a
     guess.
3. A TAB-selected target is never doubted (no verdict), and one ask per fight is the limit.

Together with the pause rule this means a peon costs at most: the walk to it, ten seconds in reach,
one TAB, and then it is invisible for the rest of the run — and a wolf that TAB cannot see from the
front is fought instead of walked past.

**The faction offset hunt, in the same run.** Every engage now prints a second line:

```
[Grind] target words: +0x90: 00000000 … | +0x1AB0: … | +0x1E50: …
```

24 raw dwords covering the words the client's own attack predicate reads (+0x94, +0x1ABE bit 7,
+0x1E60 the flags, +0x1E64 bit 28, +0x1E68 bit 8 — `hunt/WOWB_70291_TRACE_FINDINGS.md`, 2026-10-13 and
16:27). A wolf and a peon in one log, same shape: the word that differs is the reaction/faction
answer, and once it is known the pool can pre-filter and the whole trial disappears.

Self-tests: `poolVerifyWindow` (5 s ≤ PoolVerifyMs ≤ 15 s, and inside EngageTimeoutMs),
`tabRefusePauseFitsVerify` (the pause is longer than the verdict window, the double-refusal spacing is
shorter).

## The 14:23 run: the peon on trial, and the offset that gave it away

User: *"It tried to engage the Peon BUT didn't try another target after 10 seconds."* — plus two word
dumps, one for the peon and one for a scorpion it killed cleanly.

### The offset

Same window, two units, base +0x1E50:

```
peon      (level 4, hp 86/86)  +0x1E50: 00000004 00000000 CECECE00 0000001D 00000200 00000800 00000010 00000000
scorpion  (level 3, hp 71/71)  +0x1E50: 00000003 00000000 CECECE00 00000007 00000000 00000800 00000000 00000000
```

| word | peon | scorpion | read as |
| --- | --- | --- | --- |
| +0x1E50 | 4 | 3 | level (0x1E40 does not lie either) |
| +0x1E5C | 0x1D | 0x07 | unknown, next candidate |
| **+0x1E60** | **0x200** | **0x000** | **UnitFlags** — the answer |
| +0x1E64 | 0x800 | 0x800 | identical, so not it |
| +0x1E68 | 0x10 | 0x00 | candidate, needs a second friendly |

`0x200` is IMMUNE_TO_NPC and `0x100` is IMMUNE_TO_PC; the run's own pool census lists a second
friendly as **0x300** (both bits). The scorpions are `0x0`. So `NotAFightFlag = 0x2 | 0x100 | 0x200`
(+0x1E60) is now a hard filter in the scan and in "the target already selected" — a peon is not even
a candidate, and a unit carrying an immune bit is never fought however it got selected. `0x1E68 = 0x10`
and `0x1E5C` stay in the log lines as the next candidates if a friendly ever shows with clean flags.

### Why the ten seconds did not settle it

```
14:23:20.905 …asking the client whether it is an enemy at all
14:23:21.198 TAB confirmed the pool's target is an enemy - the fight carries on     <- wrong
```

TAB on an already-selected unit **keeps that unit**: it has nothing else to move to, so "same GUID
back" proves nothing. The verdict is now the client's own combat state instead:

1. TAB moved to *another* live unit → take it, that is the enemy the client wants;
2. TAB offered *nothing at all* → friendly or blocked, ignore it;
3. TAB kept the same unit → inconclusive, and the real test runs: no damage on it for `PoolVerifyMs`,
   Attack pressed at arm's length more than `MeleeSwingQuietMs` ago, and **the character is not in
   combat** → the client never started a fight, so it is not something this character can fight.

Damage on the target is the proof that ends the trial early (`the pool's target is confirmed by
landing damage on it`).

Two more fixes from the same log:

- **The trial is attached to the unit, not to the fight** (`_unverifiedGuid`). The peon was re-engaged
  four times through `going with the target already selected`, and each re-engage looked like a
  TAB-selected target, so the verdict never ran again. It does now.
- **A refused unit cannot come back through the selection**: `TryCurrentSelection` honours
  `_notViable`, `_tabRefusedUntil` and `NotAFightFlag`. That was the four-times-in-a-row loop.
- **The quiet-round brake no longer cuts an unproven target off** before the verdict can read the
  combat state — the peon fight ended 0.6 s before the trial's own test would have run.

Self-test: `notAFightMask`.

## The bags were full: original CopilotBuddy's selling procedure, adapted

The report was exact: *"ran pretty well. unfortunately inventory was full, the bot needs to get back
to town to sell the loot. Original copilotbuddy had a procedure for this."* It did, in
`Bots/Grind/LevelBot.cs` (~900–1165):

```
need (Me.FreeNormalBagSlots low / NeedToRepair)
  -> BotPoi.Current = new BotPoi(vendor, PoiType.Sell)
  -> the Sell/Repair/Mail POI decorator walks there
  -> WaitContinue(5, ctx => IsVendorFrameOpen())
  -> Vendors.SellAllItems()
  -> MerchantFrame.Instance.Close()
```

Four of those five steps do not exist on build 70291, and the log for each one is in this document
already:

| step | why not | what it is instead |
|---|---|---|
| `Me.FreeNormalBagSlots` | no container/bag offset in `Styx/Offsets/WowBOffsets.cs`; `GetMoney` was a Lua handler and the Lua route is retired | **the number of bodies looted since the last trip** (`SellAfterLoots`, default 20) plus a **Sell now** button. Labelled a proxy in the settings and in the code: when a container offset is found it replaces the count and nothing else changes. |
| `<Vendors>` coordinates | this repository ships **no profile XMLs** (only `Protected Items.xml`, `obfuscar.xml`), and a user's profiles are for another client | the point is **captured where the character stands**, exactly like the hotspot list (`Settings\clickaim-vendors.xml`) |
| `IsVendorFrameOpen()` | the merchant frame is a Lua object | the window is given `VendorWaitMs` (2500) and then the plan runs. Stated as a wait, not a check |
| `Vendors.SellAllItems()` | it is a Lua call (`UseContainerItem`), and `MerchantFrame`'s addresses are HB 3.3.5a | **right-click the items in the bags**, which is what a player does: recorded once, replayed |
| `MerchantFrame.Close()` | same | nothing: the merchant window closes by itself when the character walks away, and the backpack key toggles |

What *is* the original procedure: walk to a point, click the NPC, sell, walk back to the ring. The
trip is taken between fights and never during one — `VendorDue()` refuses while `UnderAttack()`, and
`RunSellPlan` stops at the first step if something starts hitting.

### The route

```
VendorDue()                       bags due (or "Sell now"), nothing attacking
  -> VendorFile.Read()            Settings\clickaim-vendors.xml, nearest point to the character
  -> RouteRunner.Run(mover, one)  the SAME mesh walk and the SAME fight interrupt a hotspot leg uses
  -> TryFindVendorNpc()           a unit that CANNOT be fought: (flags & NotAFightFlag) != 0
  -> walk within VendorReachYd 6  ClickAimKeySteer, then StopAndSettle
  -> TryClickUnit(mustSelect)     right-click the NPC; the client's own target guid confirms the click
  -> Thread.Sleep(VendorWaitMs)
  -> RunSellPlan()                the recorded steps: left click / right click / key / wait
```

`NotAFightFlag` is the same `0x2 | 0x100 | 0x200` word the fight **refuses** targets with, used the
other way round: a vendor is precisely what a mob is not. When nothing at the point carries it, one
living unit within 8 yd is tried as a guess and called a guess in the log.

### Why the sell clicks are recorded instead of computed

A click has to land on an item icon in a bag window. That geometry is UI scale, resolution, addons
and bag count, and every one of those is a guess that ends with a right-click on an item the
merchant window never saw — which **uses** the item instead of selling it. So the plan is captured
from the real thing:

- `Settings\clickaim-vendors.xml` holds, per vendor, the point and a list of `<Step>` elements;
- a step is a **fraction of the client rectangle** (`FX`, `FY`), never a desktop pixel: the window
  moves and is resized, the layout does not;
- `Combat settings -> Selling` has the recorders: **Add vendor here**, **Left click (3 s)**,
  **Right click (3 s)**, **Bags key (B)**, **Clear steps**, **Sell now**;
- three seconds is the countdown because the button is under the cursor and the thing to record is
  in the game window behind it;
- the backpack key is the client's own default (`B`, VK 0x42) and it toggles: record it a second
  time at the end of the plan to close the bags.

A recording of a typical vendor is five steps:

```
1. left click  0.62, 0.44   (the vendor's "Browse goods" gossip option, if it has one)
2. press B                  (open the backpack)
3. right click 0.86, 0.63   (item 1)
4. right click 0.90, 0.63   (item 2)   ... as many as the bag holds
5. press B                  (close the backpack again)
```

It is honoured to the letter, and it is the only part of this feature that needs a human for a
minute. Nothing in the bot invents a position.

### What cannot be claimed

- **The sale is not verified.** There is no money offset and no bag offset, so "sold" means "the
  recorded clicks were sent". The trip says so in its own log line.
- **The merchant window is not read.** `VendorWaitMs` is a wait.
- **Repair is not ported.** `NeedToRepair()` needs `LowestDurabilityPercent` and a repair cost, and
  neither is readable here. The `PoiType.Repair` half of the original tree is left out on purpose
  rather than faked.

Self-test additions (file round trip, no client): `vendorAdd`, `vendorAddClick`, `vendorAddKey`,
`vendorRead`, `vendorReadPoint`, `vendorReadClick`, `vendorReadKey`, `vendorNearest`,
`vendorClearSteps`, `vendorStepsGone`, `vendorReachIsInteractionRange`, `vendorRetryAfterFight`;
settings: `applyAutoSell`, `applySellAfter`, `applyVendorWait`, `applyVendorSearch`,
`clampSellAfter`, `clampVendorWait`, `clampVendorSearch`, `vendorDefaults`.

## Five fixes after the first selling run

### 1. "Sell now" now means now

The trigger sat after the fight, after the rest and *after* the hotspot leg - so pressing **Sell
now** in the window made the bot walk the next leg first, then come back and sell. It is now the
first decision the loop makes after "is the character alive":

```
if (PlayerDead()) break;
if (!_vendorDue) RestIfNeeded(...)                   <- added in the 15:50 round, see below
if (VendorDue()) { VendorTrip(...); continue; }     <- moved here
ApplyBuffsIfNeeded(...)
TryEngage(...)
RestIfNeeded(...)
walk the hotspot
```

A fight still outranks it (`VendorDue()` refuses while `UnderAttack()`), the hotspot index is not
advanced, so the character returns to the ring it left. The bags are due when **either** half of the
count says so: `SellAfterLoots` (default 20) **kills** or the same number of **looted bodies**,
counted separately - an unlootable corpse still fills a slot, and a body cannot be counted twice.
That is the user's own rule: "if 20 monsters are defeated".

A vendor walk cut short by a fight no longer applies a retry delay; only the *interrupt* is quieted
(`InterruptRetryMs`), so "fight, then sell" happens instead of "fight, then wait two minutes".

### 2. Combat settings moved under Load Profile

`Settings` has been greyed out for WowB clients for a while (`btnSettings.IsEnabled = !ClickAimUi.IsWowB`),
and `Combat settings` is the button those users actually press. It now sits in the main column at
`Margin="0,62,8,0"` - directly under **Load Profile** - and `Settings` moved down to `0,89,8,0`.

### 3. The bag count hunt: `+0x???`, and how it gets found

Asked directly: *can we find the bag count offset?* Not from a table, no: there is no container,
money or item offset in `WowBOffsets`, `C_Container.GetContainerNumFreeSlots` is Lua (retired - it
is what crashed the client twice), and the container objects themselves have no unit vtable, no name
pointer and no level word, so identifying one in the pool is a hunt of its own.

What can be done without calling into the client is the trick that found the loot words: read a
window of the player's block before and after an event that moves exactly one bag slot, and keep the
words whose value moves by exactly that much. `Styx/WoWInternals/BagSlotHunt.cs` does that.

* Nine 512-byte windows of the player's data block (`0x0`, `0x1000`, `0x1800`, `0x1A00`, `0x1C00`,
  `0x1E00`, `0x2000`, `0x2200`, `0x2400`) - 128 dwords each, which is the reader's cap, so the whole
  sample is 4.5 KB and it happens **once per looted body**, not per tick.
* A word is a candidate when it moved **down by exactly 1 on every sample where it moved at all** (a
  free-slot count) or **up by exactly 1** (a used-slot count). Anything else - cooldowns, regen, the
  pointer to the next thing - moves by something else or not at all, and is not reported.
* Looting a body and selling at a vendor move the same word in opposite directions, which is the
  second half of the evidence.
* Needs three samples before naming anything, and the report says so instead of guessing.
* `BagSlotHunt = true` in `Settings\clickaim-grind.txt` (default on until the offset is known). The
  report prints at the end of every run:

```
[Bag] bag slot hunt over 6 sample(s): 1 word(s) went DOWN by exactly 1 every time it moved, 0 went UP by exactly 1.
[Bag]   +0x????  down-by-one 5/5  (now 11, started 16)  <- a FREE-SLOT count looks like this
```

If it comes back with nothing, the verdict line says so and says where the next place to look is
(the container objects). That is a real result too: it means the count is not in the player's block
on this build, and the next step is a pool walk over item objects, not a guess at an offset.

**Until then the kill/loot counter stays the trigger**, because a number that means "twenty kills"
is honest and an offset that was made up is not. Nothing in the loop reads a bag slot yet.

### 4. Stuck in a cactus: the watchdog that measures the character, not the mob

The report was precise: the mob was far away with full health, the character was inside a cactus,
and it stayed there for sixty seconds; when it was put in range it attacked and carried on.

The cause is structural, not a missing branch: **every** branch of `Fight` measures the distance to
the *mob*. A chase into a plant leaves that number exactly where it was, so every branch kept
deciding "one more step toward the mob" from the same spot - for a minute.

`ChaseWatch` + `ChaseStep` now measure the **character's own position**:

1. moved at least `StuckMoveYd` (1 yd) since the last reading → fine, keep walking;
2. nothing for `StuckMs` (2.5 s) → escape attempt: walk 5 yd **away from the mob** on attempt 1,
   then **to the right** of it, then **to the left** - steered with the same calibrated keys as every
   other walk, not a hope;
3. three escapes without progress → the target is dropped for `UnreachablePauseMs` (90 s) and the
   fight returns "could not reach ...", the same shape as the existing give-up lines, so the loop
   takes another target and comes back to that one later.

The mark is a **pause, not a blacklist** (`Unreachable()`), it is consulted by `NearestKillable`,
`TryCurrentSelection` and `PullWorthTaking`, and it expires - a mob behind a cactus now is a mob
standing in the open in two minutes.

Self-tests: `stuckWindowIsQuick`, `stuckEscapeIsAStep`, `stuckPauseOutlastsTheEscapes`,
`stuckMoveIsUnderAStep`.

### 5. Vendors and steps can be edited

`Add vendor here` was write-only: several points could be made and nothing could be corrected. The
Selling tab is now two lists side by side:

| list | what it is | what can be done |
|---|---|---|
| **Vendor points** | every captured point, with the distance from the character | **Add vendor here**, **Delete vendor** (confirmed first - it removes the point and every step under it) |
| **The sell plan (in order)** | the steps of the selected point, numbered | **Delete step**, **Up**, **Down**, **Clear steps**, plus the recorders |

`VendorFile.Remove`, `RemoveStep` and `MoveStep` are the file operations behind those buttons; moving
a step rebuilds the order by cloning the element next to its neighbour and removing the original, so
a half-moved step never exists on disk - order is the whole meaning of a sale.

Self-tests: `vendorRemoveStep`, `vendorRemoveStepTookTheRightOne`, `vendorRemoveStepRejectsBadIndex`,
`vendorMoveStep`, `vendorMoveStepOrder`, `vendorMoveStepClamps`, `vendorAddSecondPoint`,
`vendorRemovePoint`, `vendorRemovePointTookTheRightOne`, `vendorRemovePointRejectsBadIndex`.

## The 15:50 run: a stale flag, the meal it skipped, and a stop that was not one

The log (`google_drive/2026-10-31_1550_9652.log`) has one fact at its centre and two symptoms on
top of it: the client's in-combat bit stayed set for the whole walk to the vendor. Every line
below is from that run.

| time | what the log says |
|---|---|
| 15:50:34.130 | `17 of power left and Shadow Bolt needs about 42 … melee from here FOR THE REST OF THIS FIGHT` |
| 15:50:35.379 | `a vendor trip was asked for: it is taken as soon as the character is out of combat` |
| 15:50:43.384 | `selling first, before the next hotspot: vendor 3 … is 201 yd away` |
| 15:50:43.392 | `the character reads as in combat but nothing nearby is fighting - walking on` |
| 15:50:59.261, 15:51:15.627, 15:51:33.416 | the same line, 16 s apart, for the whole 200 yd walk |
| 15:51:48.487 | `Stopping the bot!` |
| 15:51:48.487 → 15:51:57.157 | 28 more `[Keys]` lines, including a 180 deg re-aim at 15:51:55 |
| — | no `ate` and no `drank` line anywhere in 225 lines |

### 1. Selling and eating asked the flag, and the flag was wrong

`VendorDue()` and `RestIfNeeded()` both began with `PlayerInCombat() || UnderAttack()`. The flag
survived the kill by about a minute, so the character walked 200 yd at 17 of 167 power with
Refreshing Spring Water in slot 11 and Tough Jerky in slot 12 - both of them already mapped by
`ClickAimCombat.FindDrink`/`FindFood`, which is why the item lookup was never the bug.

`InRealCombat()` replaces the guess, and `UnderAttack()` is now a call to it:

```
flag clear                                  -> false
flag set, health fell within 8 s            -> true    (damage needs no flags)
flag set, something inside 18 yd is fighting -> true    (any level - a level-8 mob on a
                                                        level-4 character is still a fight)
flag set, neither                           -> false, said once per 15 s
```

`AnyFighting` is the new scan behind the third case. It is deliberately *not* `NearestFighting`:
that one drops everything outside the current level band, which is right for picking a target and
wrong for deciding whether the character is being hit.

### 2. The order: eat, then walk to the vendor

`VendorTrip` returns to the top of the loop on the tick it starts, so a rest check below it was a
rest check that never ran when a trip was due. `RestIfNeeded` now runs before `VendorDue` - except
when `_vendorDue` is set, because "Sell now" is a button somebody is watching and a trip the
counters asked for can afford a meal first.

Two more guards came out of the same run:

* **A meal beside a mob is not a meal.** If something level-viable is inside `RestSafeYd` (12 yd),
  the rest is held and the log says so once per 15 s, instead of sitting down and being interrupted
  (or eaten).
* **"Sell now" pre-empts the corpse, too.** The 8 s between the request and the walk in this log
  were two right-clicks on bodies. With a trip waiting, the body is left on the ground (it keeps;
  the vendor does not go anywhere) and `TryEngage` refuses to pick up a new fight, so the walk
  starts on the next tick.

### 3. Stop is a hand on the wheel

`ClickAimUi.Stop()` set `_stop` and printed `stopping after the current step`, and nine seconds of
arrow keys followed, because the key-steer loop, the pilot loop and every `Thread.Sleep` between
them belonged to nobody that had heard about the stop.

`ClickAimAbort` (`Styx/Logic/Pathing/ClickAim/ClickAimAbort.cs`) is one flag for every long loop:

* `ClickAimKeySteer.Steer` and `.Face` return on entry and inside the tick loop, releasing the
  arrows;
* `ClickAimPilot.WalkTo` and its sidestep stop the mover and let go of the forward key;
* `RouteRunner.Run` checks it beside `StopRequested` at the waypoint, the re-plan and every follow
  point, and `WalkSteps` in the middle of a segment;
* `ClickAimAbort.Wait(ms)` replaces the long sleeps in `GrindRunner` (500, 400, 300, 250, 220 ms), so
  a stop cuts a two-and-a-half second merchant wait down to nothing instead of waiting it out;
* `ClickAimUi.Stop()` requests it *before* it looks at `Running`, and `ClickAimUi.Stop` on a stray
  held key still lets go of it.

`GrindRunner.Stop()` requests it too, `GrindRunner.Start()` and `RouteRunner.Run()` clear it, so a
stop that arrives between runs cannot abort the next one.

It is still not a pre-emption of the client: a key that is already down is released, a click that
has already been sent is already sent. What it is now is one tick, not nine seconds.

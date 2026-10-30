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

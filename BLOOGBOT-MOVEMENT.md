# BloogBot's movement, and why it is the model to copy

`github.com/DrewKestell/BloogBot` drives the 1.12.1, 2.4.3 **and 3.3.5a** clients. Its
movement is two calls. No click-to-move, no camera, no pixels, no scales.

## What it does

`BloogBot/Game/Objects/LocalPlayer.cs`:

```csharp
public void MoveToward(Position pos)
{
    Face(pos);                         // SetFacing(bearing) - write the player's heading
    StartMovement(ControlBits.Front);  // SetControlBit(Front, 1, tick) - walk forward
}

public void SetFacing(float facing)
{
    if (ClientHelper.ClientVersion == ClientVersion.WotLK)
        Functions.SetFacing(Pointer, facing);                 // call the client's own function
    else
    {
        Functions.SetFacing(IntPtr.Add(Pointer, MemoryAddresses.LocalPlayer_SetFacingOffset), facing);
        Functions.SendMovementUpdate(Pointer, SET_FACING_OPCODE);   // 0xDA on TBC
    }
}

public void StartMovement(ControlBits bits) => Functions.SetControlBit((int)bits, 1, Environment.TickCount);
public void StopMovement(ControlBits bits)  => Functions.SetControlBit((int)bits, 0, Environment.TickCount);
```

`Face` is pure arithmetic on two positions - `GetFacingForPosition(pos)` - with a
gradual turn (`amountPerTurn`) rather than a snap, and `MoveToPositionState` re-calls it
every tick while walking (= our `ClickAimPilot`), with a `StuckHelper` for when the
character does not make progress (= our stall watchdog).

The addresses it needs, per client (`BloogBot/Game/MemoryAddresses.cs`):

| | Vanilla 1.12.1 | TBC 2.4.3 | **WotLK 3.3.5a** |
|---|---|---|---|
| `SetFacingFunPtr` | 0x0072EA50 | 0x007B9DE0 | **0x0072EA50** |
| `SendMovementUpdateFunPtr` | 0x007413F0 | 0x0060D200 | **0x007413F0** |
| `SetControlBitFunPtr` | 0x005FBE10 | 0x005343A0 | **0x005FBE10** |
| `SetControlBitDevicePtr` | 0x00D3F78C | 0x00CF31E4 | **0x00D3F78C** |
| `LocalPlayer_SetFacingOffset` | 0x9A8 | 0xBE0 | 0x7A8 |

**Correction (re-read from `BloogBot/Game/MemoryAddresses.cs`, 2026-10-30): the facing
offset was transcribed the wrong way round.** The three blocks in that file are
`WotLK` (0x7A8), `TBC` (0xBE0) and `Vanilla` (0x9A8) - so `0x9A8` is **1.12.1**, and
`0x7A8` is **3.3.5a**. It does not change anything here, because on WotLK BloogBot does
not write the field at all:

```csharp
public void SetFacing(float facing)
{
    if (ClientHelper.ClientVersion == ClientVersion.WotLK)
        Functions.SetFacing(Pointer, facing);          // 0x0072EA50 - a FUNCTION
    else
    {
        Functions.SetFacing(IntPtr.Add(Pointer, MemoryAddresses.LocalPlayer_SetFacingOffset), facing);
        Functions.SendMovementUpdate(Pointer, SET_FACING_OPCODE);
    }
}
```

So the field-plus-opcode path is Vanilla/TBC only, and on 3.3.5a `Face` is one call on
the player pointer. That is the shape to look for on 70291: a function that takes the
player pointer and a heading - not another offset to poke.

**The first four are 3.3.5a build 12340 values, which this project still claims to
support.** They are the missing pieces for the 3.3.5a path in `WoWMovement.cs`: the
`ClickToMove` call there is x86 `__thiscall` and can be replaced outright by
"write facing, set the Front control bit", which is a *field write plus one call*, not a
four-argument ABI problem. That is a smaller job than porting CTM and it is not blocked on
anything.

Note `SetControlBit` is called on a **device pointer read from memory**
(`0x00D3F78C` on 3.3.5a) - this is the same `CInputControl` singleton Winkr documents
for Classic (`RVA_INPUT_CONTROL`, flags at `+0x04`, set-bit and apply helpers), which is
a second independent confirmation that "the input control object + a flag bit" is the
movement primitive on all these clients.

## What it says about our 70291 problem

Every mechanism tried here on 2026-10-29 aimed a **mouse at a pixel**, and all of them
needed the camera's orientation to turn a pixel into a direction. That orientation is not
readable in this client (the readable camera position drifts +4.6 deg per tick with the
character standing still), so all of them failed: 90 yd walked to end 83.7 yd from a
32.7 yd goal, 269 yd walked to end 95.4 yd from a 64.5 yd goal.

BloogBot never has this problem, because it never aims:

* the heading is a **number it computes**, not a direction it has to see;
* the character's facing is a field this project **already reads every tick**
  (`facing=3.59` in every attach log, `WowBOffsets.RecordFacingOffset`);
* so the same model needs nothing new: **write the facing, hold the forward key.**

The two pieces that differ from BloogBot on this client, and why:

| BloogBot | here | why |
|---|---|---|
| `SetFacing` function call | write the facing field (`RecordFacingOffset`) | no address for the function on 70291; the field is already read, and `Human`-style write is available (`PROCESS_VM_WRITE`) |
| `SetControlBit(Front, 1, tick)` | `PostMessage` `WM_KEYDOWN`/`WM_KEYUP` for the forward key | no address for `SetControlBit` on 70291; Winkr's `Movement.md` records that PostMessage key input drives Classic clients from outside, and this project already stops the character that way |

## What the navigation chapter (20) changes here

`drewkestell.us/Article/6/Chapter/20` - the article that Vicxqh credits for making the
tile/mesh mapping click in this very repo - contains three facts that apply directly:

**1. A two-point path is Detour's failure signal.** Quoting it:

> Well, 2 is the minimum that Detour will return. If the library fails to find a path for
> whatever reason, it'll just return those two points, and the navigation path would just
> be a straight line between them.

His test asserts the path is longer than 2 points for exactly that reason. This repo
accepted `path.Length < 2` as failure, i.e. a 2-point result was treated as a **valid
straight line through whatever Detour had just refused to route around**. That is the
reported "walks into walls". Now a 2-point result for a leg longer than 5 yd is refused,
with the reason logged.

**2. `straightPath` means the smoothed path, and that is what to use.** His
`CalculatePath(mapId, start, end, smoothPath, length)` passes the flag down to
`PathFinder::setUseStrightPath`, and: "99% of the time we're going to use a smooth path".
Detour's "straight path" is the string-pulled one - few long segments - while `false`
gives the raw corridor, which is jagged. `Navigator.FindPath(mapId, start, end)` in this
repo already defaults `straightPath: true`, so this side needs no change; the logs
(`5 points, 92.3 yd for a 76.6 yd leg`) are string-pulled output.

**3. Re-plan constantly, not once.** Because "enemies ... are actually constantly moving
around", his `MoveToTarget` calls `CalculatePath` on every `Update`. Paths are cheap
(0.1-0.2 ms here). Our `RouteRunner` plans once and re-plans only when the mover stalls;
for a static quest point that is fine, but any moving goal (a mob, a player, a boat)
needs the BloogBot cadence. Noted, not yet changed.

His follower loop is worth copying verbatim for its radius choices: advance to the next
waypoint at **1 yd**, consider the destination reached at **3 yd**.

## Chapter 18: there are TWO facing fields, and that matters

This one corrects something the first version of `ClickAimKeySteer` got wrong.

> If you remember how we *read* the player's current facing, we used `Pointer + 0x9C4`.
> So when we *set* the player's facing, why do we use `Pointer + 0x9A8`? This is an
> implementation detail of the client/server architecture of WoW. We need two separate
> memory locations to store the player's *actual* facing value (that which comes from the
> single source of truth - the server), and a *different* place to store the outgoing
> facing value (that which we send to the server). ... the client does something called
> "client-side prediction" ... eventually the client and server will do what's called
> "server reconciliation" ... and `FACING_OFFSET` will be overwritten with the true value
> from the server.

(That quote is chapter 18 on 1.12.1: `0x9C4` readable, `0x9A8` outgoing - the
Vanilla offsets in the corrected table above.)

So: writing the **readable** field alone can look like it works and then be reverted, and
the field that actually steers on his build is the **outgoing** one, 0x1C *before* the
readable one.

**That second half did not hold for 70291.** Both fields were written on 2026-10-30 and
the character ignored both, so there is no sign-flip or field-choice left to try - see
"The 12:57 run" below. `ClickAimKeySteer` now:

* writes the readable field first (it is the one the client's own movement code uses, and
  while moving the client is authoritative over its facing);
* if the two calibration probes walk the same bearing - the write did not steer - it says
  so and **retries both probes against the outgoing field** at `readable - 0x1C`;
* reports which field it ended up using in the calibration line.

Chapter 18 also settles the angle convention: `GetFacingForPosition` is
`Atan2(dY, dX)` normalised into `[0, 2pi)`, which is the same convention as this project's
`ClickAimMath.HeadingDeg`. So the calibration should find **slope +1, offset 0** - if it
finds anything else, that is information, not a bug in the arithmetic.

## The one thing not assumed

The client's angle convention. Is facing radians clockwise from north, counter-clockwise
from east, with a sign flip, or scaled? `ClickAimKeySteer.Calibrate` measures it:

```
write f0   -> hold forward -> walked bearing b0
write f0+70 deg (input units) -> hold forward -> walked bearing b1
slope  = (b1 - b0) / 70
offset = b0 - slope * f0
```

A slope near ±1 means the field is the heading; a slope near 0 means the client ignores
the write, and the run says so instead of walking in circles. The map is checked offline
(identity, slope 1, slope −1, a +90 deg offset, and a ×2 scale all invert exactly) and at
attach (`KeySteer self-test`), so a failure in the field is the client, not the arithmetic.

## The 13:11 run: it walks the route

The first time on this client that a requested point was reached by following the mesh,
and the mechanism is the one in the section below: turn keys + a readable facing.

```
[13:11:11] [ClickAim] file request: point (-623.6, -4286.8), tolerance 2.0 yd
[13:11:16] [Route] navmesh available: every leg is routed on the mmaps
[13:11:16] [Route] waypoint 1/1 WalkTo (-623.6, -4286.8, 51.5) - 262.0 yd
[13:11:19] [Route] steering by facing + forward key: turn keys: right arrow -18.0 deg,
           left arrow +36.7 deg per 200 ms (0.184 deg/ms). Facing read 204 deg, walked
           bearing -156 deg - the facing IS the walked direction
[13:11:19] [Route] mesh path: 8 points, 255.9 yd for a 253.8 yd leg (detours around terrain)
[13:12:02] [Route] waypoint 1/1 reached
[13:12:02] [Route] route complete: 1 waypoints walked
```

262 yd, 255.9 yd of it on a Detour path with 8 points, in 43 s. The two halves of the
loop are both confirmed by the run itself: the arrow keys turn the facing, and the facing
is the direction the character walks (204 vs -156 is the same bearing from the other side
of the circle).

Three things in that line are worth keeping, because each one is a mistake that would
have been invisible:

* **the rates are asymmetric -18.0 / +36.7, and that is not a property of the keys.**
  The first keystroke after the calibration's `ReleaseAll` is partly swallowed by the
  client, the same gate that makes the first click after a pause do nothing. Believing
  the halved number makes every turn tap twice as long as it should be. Each key is now
  measured twice and the bigger step kept.
* **the rate is per key.** One arrow turns the facing up, the other down, and they need
  not be equal, so the tap length is computed from the rate of the key actually pressed.
* **the walk was choppy** (the user's word for it, and correct). Two causes, both now
  fixed: `Steer` released the forward key to turn, so every correction stopped the
  character and started it again; and the pilot called `MoveStop` on arriving at every
  intermediate mesh point - 8 stop-starts on this route. A player holds W and taps the
  arrows, and a 20 deg correction at 0.184 deg/ms is a 0.1 s arc of about 2 yd; the
  character now turns while running, and only stops for an error over 60 deg. Between
  mesh points the forward key stays down.

Still outstanding from this run: the goal the reader gave was 40.1 yd off the mesh in Z
(`the target at (-623.6, -4286.8, 0.0) is 40.1 yd off the mesh`), which is the Z the
reader supplies being wrong, not the mesh - the path snapped to (-623.6, -4286.8, 40.1).

## The 12:57 run: the write does not steer, and the click cannot be stopped

Two findings, both decisive.

**1. The facing write is not what the client moves by.** With the probe fixed, and then
again against the outgoing field at -0x1C:

```
[Keys] writing the readable facing field moved nothing (81 deg then 81 deg bearing).
       Trying the OUTGOING field BloogBot writes (readable - 0x1C)
[Route] wrote 81 deg then 151 deg and the character walked bearing 81 then 81 deg
```

81 written, 81 walked; 151 written, 81 walked. The character keeps its own facing and
the write is discarded. So the record field is not the lever - **but it is readable, and
it equals the direction the character walks.** That equality is the loop.

**2. A click whose aim is off is a runaway that cannot be stopped.**

```
[ClickAim] leg ended: 120.4 yd in 17.4 s on a click, it ran out of time
[ClickAim] the click will not stop; giving up on this leg
```

Twice, leaving the character 138 yd from the goal. The reason: a centre click walks to
wherever the camera ray meets the ground - 8 yd looking steeply down, 120 yd looking
level - so its LENGTH is not controllable, only its direction, and we cannot set the
direction. Worse, the stop key did not cancel it, because `TapKey` was posting
`lParam = 0x00010001`: repeat 1, **scan code 0**. A keystroke with no scan code is one a
client can legitimately ignore. `TapKey` now sends the real scan code.

That fix is testable on its own, cheaply, without committing to a walk:
`Tools\StopTest.cmd` (`--clickstop`) clicks at the centre, waits for the walk to begin,
then taps one key at a time - W, arrow left, arrow right, A - and measures how far the
character still travels after each tap. It stops itself at 25 yd and releases everything.
If it reports that a key stopped the walk, a click becomes a *bounded* primitive for the
first time; if not, the click fallback stays disabled and turning the keys is the only
route.

## So: turn keys, and a closed loop on the facing

The remaining lever is the turn keys (arrow left/right). They change the facing the
client obeys, and the facing is readable, so:

```
read facing -> tap the arrow key until facing == wanted -> hold W
```

No memory writes, no camera, no pixels, and no model of the turn rate: the tap duration
is proportional to the remaining error and the result is re-measured every tick. The
step also *learns* - if the error grew or changed sign since the last tick, the tap was
too long, so it halves (and grows back slowly). Modelled across 21 combinations of true
turn rate (0.02-1.0 deg/ms) and assumed rate (0.05-0.5), with the true rate up to 20x
the assumption: **21/21 converge**.

And the click fallback is gone. `RouteRunner` stops and says why rather than falling back
to a mover that walks 120 yd in a random direction and will not stop, and the mover
refuses any click whose aim is more than 25 deg off.

## The 12:04 run: the facing write works, the probe was measuring the previous walk

> **RETRACTED by the 12:57 run above.** The conclusion in this section - that the write
> steers and only the probe was broken - is wrong. Identity between the written heading
> and the walked bearing is also exactly what *ignoring* the write looks like, and once
> the probe was fixed the second, different write was ignored too. Read the 12:57 section
> first; this one is kept because the probe-ordering fix it describes is real.

```
[Route] not steering by facing: wrote 51 deg then 121 deg and the character walked
        bearing 51 then 51 deg - it ignored the field (slope 0.00)
```

Read that again: **51 deg written, 51 deg walked.** The write steered *exactly*, on the
first attempt, with no calibration at all - which is what BloogBot's convention predicts
(`Atan2(dY, dX)`, so bearing == facing). The second write was simply lost because the
character was **still walking from the first probe**, and a client that is mid-move owns
its own facing: writing the field while it walks is overwritten.

So the mechanism was never broken. The probe was. Two changes:

* `Probe` now stops the character and **waits until it is genuinely still** before
  writing, **reads the heading back** and reports a revert as a *different* failure from
  an ignored write, re-writes the heading during the walk, and stops again before
  measuring. A probe that starts on a moving character measures the old walk - that is
  the 51/51 result, exactly.
* `Steer` now **releases and re-presses the forward key** when the heading changes by
  more than 12 deg. If the client reads the facing when a movement key goes *down* -
  which is what the lost second write implies - then a turn has to be: let the key up,
  write the facing, press it again. Small corrections just rewrite the field.

## The other half of 12:04: the drags were walking it backwards

Every click cycle that run spent one click (8-15 yd) and then **three drag legs of 4-5 yd
each**, taken in whatever direction the camera happened to point while the drag ran:

```
52.8 -> 63.6 -> 84.5 -> 108.5 -> 149.4 -> 250 yd from a goal 52.8 yd away
```

That is not a failed walk, it is a walk in the wrong direction - worse than standing
still. `MaxAimDrags` is now **0**: the drags measured 0.03-0.17 deg/px over eight samples
with one in three moving the camera by exactly 0.0, so they could not close a 100 deg
error anyway, and turning is the key-steered path's job now. The click fallback only
clicks, and the pilot's 25 s no-progress watchdog ends a walk that goes nowhere.

## Where it is wired in

Not a side experiment any more. `ClickAimMover.MoveTowards` is the one call the pilot
makes on every tick, so the primitive was swapped inside it:

```
RouteRunner.WalkMesh
  └─ mover.TryEnableKeySteering(...)      calibrated once, before the first waypoint
       └─ ClickAimKeySteer.Calibrate      two probe walks -> slope + offset, or a reason
  └─ ClickAimPilot.WalkTo(waypoint)       unchanged
       └─ ClickAimMover.MoveTowards       key mode: write facing, hold the key,
                                          release within 1.6 yd of the waypoint
```

Everything above the primitive keeps working unchanged - the 12 yd look-ahead, the
stall watchdog, the re-plan, the blackspots. If the calibration fails the mover keeps
clicking and the log says why, so a failure costs one message, not the walk.

The 1.6 yd arrival radius matters: a held key cannot be asked to stop "a bit past" a
point, so the pilot stops the keys when it is close and the next waypoint (at most 12 yd
away) takes over. Simulated with a 7 yd/s character and a 250 ms loop: a 0 deg heading
error ends 1.5 yd from a 12 yd waypoint having walked 10.5 yd, and even a 20 deg error
converges (0.9 yd, 12.2 yd walked), because every tick re-aims from where the character
actually is rather than dead-reckoning.

## Try it

```
Tools\KeyWalk.cmd -623.6,-4286.8
```

or with no argument, which walks 20 yd along the current facing - the smallest thing that
can prove the mechanism. It calibrates first, then walks in 12 yd steps re-steering every
250 ms, and prints:

```
[Keys] facing 0.50 rad walked bearing 148 deg; facing 1.72 rad walked bearing 270 deg.
       So bearing = 1.00 x facing + 98.6 deg, and the client's facing is the player's heading
[Keys] at (-601.4, -4282.0), 18.2 yd to go, heading written 106 deg
...
[Keys] stopped at (-623.4, -4286.1); 0.4 yd from the point (ARRIVED)
```

If it says `it ignored the field`, the facing write does not steer this client and the
next step is the input-control object Winkr documents (flags at `+0x04`), found by the
same signature machinery as `--sigscan`. If it arrives, the mesh path already works and
the mover becomes a loop over waypoints - no clicks anywhere.

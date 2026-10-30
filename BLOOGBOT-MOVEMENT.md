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
| `LocalPlayer_SetFacingOffset` | 0x7A8 | 0xBE0 | 0x9A8 |

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

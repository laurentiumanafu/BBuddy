# WowBRemoteCall — call one game function per run (APC route)

## Why

Three things are now established on this client:

* the OS loader is unusable from injected code (`LoadLibraryW` → 87 for every
  path, `LdrLoadDll` → `STATUS_INVALID_THREAD`), so a normal injected DLL is out;
* an APC delivered to an alertable client thread **does** execute, and a
  manually mapped image works (v7 ran end to end: `ProbeStart` 0, 10 s, `ProbeStop` 0);
* `record+0x60/+0x64/+0x68` is the live player position (verified twice; a
  right-click walk moved it 44 yards in a clean straight line).

What is still missing is a way to *command* movement. The field watch found no
destination field inside the player record — `+0x150` and `+0x6A0` read as
implausible in the second run, so they are volatile, and `+0x7D0` moved 16 yards
with a net drift of 2.6 yards, which looks like a cursor/terrain pick rather
than a commanded destination. So instead of guessing a destination field, this
tool calls engine code directly.

## What it does

Places two small stubs in the client, finds one thread that accepts an APC
(the only reliable route), and then:

* `--read RVA` — hex dump of code at image + RVA (read-only);
* with no arguments — dumps the prologues of the known candidate functions
  (`MoveForwardStart` `0x23C8BE0`, `MoveForwardStop` `0x23C9000`,
  `UnitHealth` `0x1D63190`, `UnitReaction` `0x1D89A00`) so an ABI can be studied
  before anything runs;
* `--call RVA[:a1,a2,a3,a4] --go` — calls one function with up to four integer
  arguments in `RCX, RDX, R8, R9`, records `RAX`;
* `--moves --go` — calls `MoveForwardStart`, waits 1.5 s, calls
  `MoveForwardStop`, and reports the player position before and after. This is
  the objective test: `MOVEMENT CONFIRMED` only if the position actually moved.

Default behaviour is read-only. Anything that executes needs `--go`, because an
unvalidated address can crash the client.

## Run

Read-only reconnaissance (safe, no restart needed):

```
"C:\ghidra-scripts\WowBRemoteCall\WowBRemoteCall.exe"
```

Then, to test whether the movement entry points work (injects, can crash):

```
"C:\ghidra-scripts\WowBRemoteCall\WowBRemoteCall.exe" --moves --go
```

Report: `C:\ghidra-scripts\WowBRemoteCall-<timestamp>.txt`.

**Restart the client after any `--go` run.**

## How to read it

* `prologue MoveForwardStart +0x23C8BE0: 40 55 41 57 …` — a plausible function
  prologue. Bytes of `CC` or `00` mean the RVA is wrong for this build.
* `executionThread=…` — which thread accepted the APC. It varies per session.
* `call … phase=2 result=0x…` — the call completed; `RAX` is recorded.
* `position before=(…) after=(…) moved=N.Nyd` and
  `MOVEMENT CONFIRMED|NOT OBSERVED` — the only claim that matters. A completed
  call with `moved=0.00yd` means the function returned without moving anything,
  which usually means a wrong argument or a wrapper that needs state we did not
  provide.

## Limits

* The stubs pass integers only. A function that takes a float, a struct pointer
  or a `this` pointer will not behave correctly; this tool cannot tell you which
  it is. Wrong arguments can crash the client, which is expected and accepted
  for this local-server research.
* No exception handling is installed in the target; a fault inside the called
  function takes the client down with no report from inside it. The launcher
  report still shows how far it got.
* One call per invocation, executed once, on one thread.
* Functions must be called on the right kind of thread. Running a game-logic
  function on an arbitrary alertable thread is a real risk of deadlock or
  assert; the position check is what tells us whether it worked.
* Cross-compiled for Windows x64 with Zig; the stubs pass offline tests for
  argument registers, result capture and 16-byte stack alignment at the call
  site, but the tool has not been run on Windows.

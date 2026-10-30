# LuaBridge64 — calling Lua inside a native x64 WoW client

The 64-bit client cannot be driven the way the 32-bit one was (no `d3d9!EndScene`
to hook, no `pushad`, no in-process .NET injection on this branch). What it *can*
do is call its own Lua API from a thread we start — this is the managed side of
that, working from outside the process, and it is what the bot can use for reads
today and for actions next.

Everything here was proven live against **wowb.exe 1.60.1, build 70291**
(`hunt/hunt45.ps1`, `hunt/hunt46.ps1` and their logs).

## Files

| file | role |
|---|---|
| `GreenMagic/LuaBridge64.cs` | the bridge: attach, verify, start, `Call(name, arg, results)`, `Stop`. Self-contained (its own P/Invokes), no WinForms/UI dependency. |
| `Tools/LuaBridgeProbe/LuaBridgeProbe.csproj` | x64 console harness, builds the class via a `<Compile Include>` link |
| `Tools/LuaBridgeProbe/Program.cs` | attach → start → print a live snapshot every 500 ms → stop |

`GreenMagic/LuaBridge64.cs` is inside the bot project tree, so `CopilotBuddy.csproj`
compiles it automatically. `Tools/**` is excluded from the bot build (existing rule),
so the harness never interferes with `CopilotBuddy`.

## Build and run the harness

```
dotnet build Tools/LuaBridgeProbe/LuaBridgeProbe.csproj -c Release
Tools\LuaBridgeProbe\bin\Release\net10.0-windows7.0\LuaBridgeProbe.exe --seconds 60
```

`dotnet run --project Tools/LuaBridgeProbe -c Release -- --verbose` works too.

Run `--check` first: it attaches, verifies everything (Lua canary, trampoline shape,
the `jmp qword ptr [rcx]` gadget, `kernel32!Sleep` / `GetTickCount64`) and prints what
it found **without writing into the client and without starting any thread**.

Options: `--pid N`, `--name wowb`, `--seconds 30`, `--interval 500`, `--lua-thread N`,
`--call Name` + `--arg TEXT` + `--nresults 2` + `--rva 0x…` + `--force` (one call, then
stop), `--each Name=arg` (one extra call per round), `--verbose`, `--check`, `--selftest`
(touches nothing).

## The Lua-thread guard — PROVEN LIVE (0.5.0, 2026-10-13)

**Result:** on build 70291 the guard identified the client's Lua thread by itself (tid 13524,
seen inside `module+0x4700000..0x4740000`), froze it for **798 of 798** calls in a 60 s run and
the client survived with zero crashes — 1.139 ms frozen per call, 12 waits and 11 retries (both
signs the guard met a busy VM and retried instead of racing), `[stop]` clean, `[alive]` 70
threads. A second run forcing the tid by hand (0.4.0 `--lua-thread 13524`) ran 791 calls at
1.236 ms frozen with no crash either. Total: ~1,600 guarded calls, no crash, after the unguarded
build died at 287.

**And the first write went through (0.5.0).** `RunScript` was refused by the cold-page gate, the
page was warmed the intended way (`/run print(1)` in the game), and the next call ran
`print('bbuddy: the bridge can write')` **inside the client** — the chat frame showed the
message, `RunScript` returned nil (correct for a statement-only chunk), the client was alive at
the end. Read + write through the guarded bridge, both proven.

**0.5.0 also fixes why the guard kept refusing in a normal run.** `FindLuaThread()` was only ever
called from `Verify()` — i.e. from `--check`. `Start()` never ran the search, so
`--seconds …` always began with an empty pick, printed `guard : freezes tid ` (blank) and
refused on the first call, while `--check` on the same client found the thread and printed a
clean table. `Start()` now identifies the thread **before** it writes anything and throws with
the sampling table plus the fix if it cannot; `UseLuaThread(tid)` (and the guard itself, as a
backstop) reset that state. Related: `Verify()` used to search twice, and the second search
could come back empty *after* the first had succeeded, which would have left the bot's own
`Start()` with nothing to freeze.

Lua in this client runs on **one** thread, and that thread mutates the `lua_State`
hundreds of times per second. Our payload is a foreign thread, so a call that lands
while the client is mid-Lua is a data race on the same `lua_State` — and the way that
ends is a fault inside the Lua cluster. The 2026-10-13 crash report (build 70291,
`ThreadID 9784`) is exactly that: `0xC0000028`, `R10 = module+0x472AEC7`, the
`lua_State` in `XMM1`, after 287 clean calls. The `0xC0000028` itself is our own
doing — the trampoline lives in a page with no `.pdata`, so the client's unwinder
cannot walk it and reports a bad stack instead of the original fault.

The guard removes the race instead of the symptom:

1. before the payload starts, `FindLuaThread()` decides which thread to freeze, from
   **two independent signals**:
   * *CPU time* — the client's frame thread runs the game loop, and the game loop is what
     executes every Lua script, so it is the busiest thread in the process. This signal
     needs nothing but `ProcessThread` and cannot fail.
   * *instruction pointers* — every thread is suspended for a few microseconds, its
     `CONTEXT_CONTROL` is read (~15 µs per thread) and each RIP is counted when it lands
     in the Lua cluster `module+0x4700000..0x4740000` or anywhere in module code. This is
     what turns the guess into a fact.
   Threads seen inside the Lua cluster are the ones the guard freezes; when nobody showed
   up there, the busiest threads are frozen instead and the bridge says the pick was a
   guess. `ThreadReport` (printed by `--check`, by a normal run and again if the guard
   refuses to call) is the sampling table: per thread `tid, cpu+ms, reads, module hits,
   lua hits, first RIPs` plus any `OpenThread`/`GetThreadContext` failures with their
   Win32 error. If no thread could be frozen at all, `Call()` throws with that table
   instead of racing — pass `--lua-thread N` to freeze a thread you picked yourself.
2. before every command: wait until `L->top == L->base` and `L->ci` is back at its
   resting value, twice in a row across a 2 ms sleep;
3. suspend that thread, then re-check quiet *and* that its RIP is not inside the Lua
   cluster. If either check fails, resume, count a retry and try again;
4. run the command (hard cap 1 s while frozen), read the results, resume — from a
   `finally`, so the client's thread is never left frozen, not even on an exception.

Cost: one freeze per call, ~1.5 ms, plus ~10 µs of suspend/resume. `--verbose` prints
the freeze/retry counters; the summary prints the average frozen time per call.

`--no-guard` keeps the old behaviour, which is only useful to reproduce the crash.

Two limits worth knowing: a Lua stack reallocation *during* our call would leave the
frozen thread's saved registers pointing at the old stack (the guard cannot prevent
that; it is why calls stay tiny), and a mutating call (`RunScript`) is a bigger step
than a read and has not been tried yet.

## Invoking anything: `--call` and the cold-page rule (0.4.0)

`--call` runs **one** guarded call, prints what came back, and stops. This is the primitive the
bot's action layer needs (`Call(name, arg, results)` / `CallNumber` / `CallString` /
`CallBoolean` are the same thing in-process):

```
LuaBridgeProbe.exe --call GetTime
LuaBridgeProbe.exe --call UnitHealth --arg player
LuaBridgeProbe.exe --call UnitClass  --arg player --nresults 2
LuaBridgeProbe.exe --call UnitPosition --arg player --nresults 2
```

Before the call it checks one thing: **is the handler's page decrypted in this client right
now?** The client ships its code encrypted and decrypts a page only when the game itself needs
it; until then the page is mapped `NOACCESS`, so `ReadProcessMemory` fails on it. That is the
warm/cold test the hunt scripts have used since hunt37, and the rule has not changed: *a cold
page holds ciphertext, executing it faults or wedges, and a protection change does not decrypt
it.* So:

```
handler     : module+0x1D63190   readable, first bytes 488bc4554157488d68a14881ecb80000 -> WARM
[call]      UnitHealth("player") = 73
[call]      1.42 ms wall, 1.06 ms while frozen, type word 0x103 -> Number
[guard]     1 freezes, 0 waits for the VM to go quiet, 0 retries, 0.284 ms frozen per call
payload     : served 1 command(s) on one resident thread
[stop]      the resident thread exited on command - no TerminateThread was used
[alive]     pid 19516 is still running (70 threads)
```

If the page is unreadable the call is **refused** instead:

```
handler     : module+0x136FFF0   UNREADABLE - still encrypted (cold)   [hunt37/38 found this page cold]

refusing to call 'RunScript': the client's protector has not decrypted that
handler yet, so calling it now would execute ciphertext (fault or wedge).
Warm it by using it in the game itself - for RunScript, type   /run print(1)
in the chat box - then run this command again. ...
```

* the RVAs of the unit/`GetTime`/`GetMoney`/`IsLoggedIn`/`GetFramerate`/`GetZoneText`/
  `FollowUnit`/`UnitPosition`/`TargetUnit`/`RunScript`/`CastSpellByName`/`RunMacro` handlers are
  built into the probe (`hunt/api_handlers.tsv`); `--rva 0x…` supplies one for anything else.
  A name with no RVA gets a warning instead of a check - except the handlers hunt37/hunt38 saw
  encrypted (`TargetUnit`, `RunScript`, `CastSpellByName`, `RunMacro`, `GetNumCompanions`,
  `FnEntityGetGuid`), which are refused outright because there is nothing to verify them
  against;
* `--force` calls anyway - only for a page you know is warm;
* the check reads 16 bytes at the handler *entry*, so a function spanning a page boundary can
  still be partly cold. The read-only set (`Unit*`, `GetTime`, `GetMoney`, `IsLoggedIn`,
  `GetFramerate`, `GetZoneText`, `FollowUnit`) was warm in every session so far.

**The first write — DONE (0.5.0, 2026-10-13).** The page was warmed in the game and the bridge
wrote:

```
(chat)  /run print(1)
        LuaBridgeProbe.exe --call RunScript --arg "print('bbuddy: the bridge can write')"

handler     : module+0x136FFF0   readable, first bytes 40554157488bec4883ec784533ff4889 -> WARM
[call]      RunScript("print('bbuddy: the bridge can write')") = nil
[call]      5.48 ms wall, 0.81 ms while frozen, type word 0x0 -> Nil
[alive]     pid 19516 is still running (70 threads)
```

`bbuddy: the bridge can write` appeared in the client's chat frame. Nil is correct for a
statement-only chunk. It is the same path every later action (`CastSpellByName`, `TargetUnit`, a
`SetCVar`, a macro) takes; those handlers are cold until the game itself has run them.

### Several commands in one session: `--seq` (0.6.0)

`--call` costs a whole process per command (~4 s: attach, the 2 s thread sampling, page,
payload). An action that has to be undone needs its steps milliseconds apart and in one session:

```
LuaBridgeProbe.exe --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
LuaBridgeProbe.exe --seq "RunScript=TargetNearestEnemy() ; sleep=300 ; RunScript=CastSpellByName('Fireball')"
```

Steps are `Name` or `Name=arg`, separated by `;`, with `sleep=MS` between them. Every step goes
through the same cold-page gate, so a refusal stops the sequence *before* an action is left half
done, and one resident payload thread serves the whole list. This is the action-queue shape the
bot will use: an ordered list of guarded calls with explicit pauses.

### Movement — and the wall behind it (2026-10-13)

The page was warmed the intended way (`/run MoveForwardStart()`, `/run MoveForwardStop()`), the
gate let the call through (`module+0x23C8BE0 → WARM`), and the call **did** reach the handler.
It never returned, and the client terminated itself with its own error report:

```
Error:    ASSERT(FrameScript_IsLuaThread())
File:     Engine/Source/FrameScript/FrameScriptEvents.cpp, Line 208
ThreadID: 13572                     <- OUR payload thread
stack:    ... lua_pcall -> MoveForwardStart (module+0x23C8C0C) -> ... -> module+0x1DA4596
          (the FrameScript event dispatcher) -> assert
```

`MoveForwardStart` raises a Lua event, and this client only accepts Lua events **from its own
Lua thread**. From anywhere else it does not error out and it does not ignore the call — it
asserts and dies. So movement (and `TargetUnit`, `CastSpellByName`, `RunMacro`, anything else
that dispatches) cannot be called from an injected thread, ever. That is not a bug in the
bridge and not a protection we can work around by being cleverer with a freeze.

### The answer: `--on-lua-thread` (0.7.0) - commands run ON the client's Lua thread

`FrameScript_IsLuaThread()` asks whether the calling thread is the Lua thread, so the fix is to
make it true. In this mode each command is executed *by that thread*:

```
suspend the Lua thread at a quiet moment (L->top == L->base, L->ci at rest - double checked)
save its full CONTEXT
set RIP = our payload entry, RCX = the control block, RSP = a private 64 KB stack
resume it            -> the payload runs on the VM's own thread, like any C handler would
drive it through the mailbox exactly as before (stage 0x4100 -> CMD 1 -> ack -> CMD 0xFF)
its final `ret` lands on a `pause; jmp $` stub in our page, never in game code
suspend, restore the saved CONTEXT, resume - the game continues where it was
```

```
LuaBridgeProbe.exe --on-lua-thread --seq "MoveForwardStart ; sleep=1200 ; MoveForwardStop"
LuaBridgeProbe.exe --on-lua-thread --call TargetUnit --arg "target"
LuaBridgeProbe.exe --on-lua-thread --call CastSpellByName --arg "Fireball"
```

Properties of this mode:

* events are raised by the Lua thread, which is exactly what the client checks;
* the `lua_State` is only ever touched by its owner, so there is no race to guard against - the
  takeover *is* the synchronisation, and `--timeout-ms N` (default 1000) bounds how long one
  command may take;
* warm the handler first (`/run MoveForwardStart()`), because the cold-page rule is unchanged;
* if a command ever wedges, the bridge does **not** put the context back: resuming a
  half-finished call would corrupt the VM. It says so, the client sits frozen, and you end it
  from Task Manager. Same policy as the frozen-thread path since 0.7.0 (which used to resume -
  that resume is what let the 2026-10-13 corruption happen).
* the non-dispatching work (all the `Unit*`, `GetTime`, `GetMoney`, `IsLoggedIn`, `RunScript`
  reads and writes that are proven over ~1,600 calls) still uses the injected resident thread;
  nothing about that path changed.

### What 0.7.0 got wrong, and why it can no longer be missed (0.8.0)

The first attempt at this mode did not fail in the client - it failed in the bridge, before a
single command was sent:

```
[seq 1]     MoveForwardStart() -> the client's Lua refused it: SetThreadContext failed: 0
```

Both halves of that line were wrong. `SetThreadContext` was **ours** to fail, not the client's Lua,
and the `0` was not an error code:

* `ThreadSetContext` was defined as `0x0040`, which is `THREAD_QUERY_INFORMATION`. The real
  `THREAD_SET_CONTEXT` is **0x0010**. `OpenThread` therefore SUCCEEDED (the mask was legal, so
  there was no error to report) and handed back a handle that may *read* a context but never write
  one - every takeover died at the write. The search itself was fine: the run printed
  `freezing: 3368, 19380` and picked the thread with the Lua RIP hits;
* the message printed the last error *after* `ResumeThread` had already run, so it reported
  whatever that call left behind (`0`) instead of the failing call's own code.

0.8.0 removes the class of problem instead of patching the one line:

* the hijack handle is opened with `SUSPEND_RESUME | GET_CONTEXT | SET_CONTEXT` (0x1A) and falls
  back to `THREAD_ALL_ACCESS`; the rights it got are printed;
* **every candidate is proved before a command depends on it.** A takeover needs the kernel to
  accept a CONTEXT write, so the setup does a real round-trip - suspend, read, write *the same
  context back*, read again, resume - on each candidate from the search, and keeps the first one
  that survives. `--check --on-lua-thread` runs exactly that check without sending anything. If no
  candidate works the mode refuses at `Start()` with a per-thread reason;
* every context write is **verified by read-back**: if the kernel says success but the thread's RIP
  is not the one we asked for, the write did not take, and the attempt is retried - first with
  `FULL`, then with `CONTROL|INTEGER`, which is the takeover minimum: RIP/RSP/segments *and* the
  integer file, because that is where the control-block pointer travels (RCX is not part of plain
  `CONTROL`);
* when the Win32 call says no, the raw **`ntdll!NtSetContextThread`** is called and its NTSTATUS is
  printed (`=0xC0000022` and friends), so "failed: 0" can never be the whole story again;
* a failed takeover raises `LuaTakeoverException`; the probe prints it as `[takeover]`, never as
  "the client's Lua refused it". At that moment nothing in the client's VM has been touched and no
  command has been sent (exit code 8);
* a takeover no longer starts on a moment whose RIP is inside the Lua cluster unless it has waited
  700 ms for a better one: `L->top == L->base` with `L->ci` at rest says no Lua call is in flight,
  and RIP outside the VM cluster says the thread is not mid-mutation either;
* `HijackReport` / `HijackHow` / `HijackAttempts` carry all of it into the probe header, so a log
  says which rights it got, which call took the context, and how many attempts it cost.

If the restored context can no longer be written back at the *end* of a command, the thread is left
suspended in our stub on purpose (never resumed on our registers) and the report says to end the
client - the same wedge policy as always, now applied to the hand-back as well.

### The Lua-level gate: `FORCETAINT_STRONG` (2026-10-13) - and why the C route exists

Running the same function from the **chat box** is not a test of movement, and now we know what it
does instead:

```
(chat) /run MoveForwardStart()
popup: *** FORCETAINT_STRONG*** has been blocked from an action only available to the Blizzard UI
```

That is the client's own Lua-level gate: `MoveForwardStart` is a **forbidden** function - not merely
protected - so no addon, macro or `/run` line may ever call it, no matter what surrounds the call.
Two useful facts fall out of it:

* the popup is *also* proof that the handler page was decrypted by that call (the same session's
  gate reports `module+0x23C8BE0 -> WARM`): the block happens inside the handler's call path, after
  its first instruction has run. So `/run Name()` remains the warming step; it just cannot be the
  action step;
* it is the reason this project goes in at the C level at all. `FrameScript_IsLuaThread()` is a
  **thread** check, and `--on-lua-thread` satisfies it by making the caller *be* the Lua thread. The
  taint/forbidden check is a *script-context* check, and a C call made by the client's own VM thread
  from its own idle point has no tainted script context to judge. Whether the handler accepts that
  is exactly what the next run answers - it is a yes/no with a `[seq]` line either way, and both
  fallbacks are already named: the engine-level movement call underneath the handler, or
  `DX11PresentHook64` with synthetic input.

The same question applies to `ToggleAutoRun`/`StartAutoRun`/`StopAutoRun` (all in the movement
table with verified RVAs - see the probe's built-in table): autorun is movement without holding a
key, so if the client lets *that* handler run from a C call, the bot has a forward primitive today.

### Seeing inside: `--dump` (0.8.0)

The pages that matter for movement are the ones the client has decrypted **by running them**, and
those are exactly the pages the offline image still holds encrypted. So the probe can copy them out
live, read-only:

```
LuaBridgeProbe.exe --dump 0x23C5500:0x400,0x23C8BE0:0x400,0x23C9000:0x400,0x23DBE00:0x400,0x23DD400:0x400,0x1DA4500:0x400,0x58CF2B0:0x3A0 --out movement.bin
```

Same `ReadProcessMemory` the cold-page gate already uses: no thread is started, nothing is written
into the client, pages that are still cold come back as zeros and are counted in the manifest
(`movement.bin.txt`), and it prints the md5 of what it wrote. `--dump-text` prints the bytes to the
console instead (for a range small enough to paste). The ranges above are the movement handler, its
neighbours in the same registration table, the dispatcher chain from the crash stack, and the
movement table itself.

### Changelog

* **0.8.2** — the 2026-10-13 crashes decoded and closed. Both reports have the same shape:
  `ACCESS_VIOLATION write` at `0x...31B0` from `RIP 0x...0807` (= our page + 0x807, the payload's
  entry + 7, i.e. its very first store) for the Lua thread, with `RBX = RCX = R10 = 0x7FF8E17730D0`
  — **the client's own register value, not our control block**. The Lua thread was parked in a
  kernel wait (its RIP was in `ntdll`) when the context was written, and Windows applies a context
  only in part to such a thread: **CONTROL took** (RIP/RSP were ours — the fault proves the payload
  ran on our stack) but the **INTEGER file did not**, so the payload began with the client's stale
  `RCX` and `mov [rbx+0xE0], 0` (the call counter) landed in a client code page.
  Fixes: `HijackBegin` now refuses a moment whose RIP is outside the game module and waits for one
  inside it (`HijackWindowMs`, default 5000 ms; parked-thread moments are skipped and the count is
  reported in the takeover diagnostic); the read-back proves **RIP, RSP and RCX** (all three the
  payload dereferences through) instead of RIP alone; the whole hijack path now throws the distinct
  `LuaTakeoverException`, so a bridge refusal is never again printed as a client Lua refusal.
  Also `--dump`: ranges below the image base are read as module RVAs (`rva:`/`va:` force either
  reading), chunks end on 4 KB boundaries so one cold page can no longer lose an entire range, and a
  page that refuses as a whole is retried in 64-byte blocks with per-range readability counts.
  *Lesson recorded:* a same-context round-trip self-test proves **rights and alignment only** — it
  cannot detect a partial application, which is why the dry run was green while the command crashed.

* **0.8.1** — build fix for 0.8.0 on the user's machine: `_hijackSkippedBusyMoments` was declared
  `bool` and used as a counter (`++`, `> 0`), which is CS0023/CS0019. It is an `int` now; nothing
  else changed. The class of mistake is what `Tools/lint_cs_types.py` checks for (declaration vs
  operator, no compiler needed) - it is the check that should have run before 0.8.0 was packaged.
* **0.8.0** — the hijack mode's own machinery is fixed and made self-proving: `THREAD_SET_CONTEXT`
  is now **0x0010** (0.7.0 asked for `0x0040`, `THREAD_QUERY_INFORMATION`, so the handle could never
  write a context - the reason the first movement run died with `SetThreadContext failed: 0` before
  its first command); Win32 errors are captured **before** anything else can overwrite them and the
  raw `ntdll!NtSetContextThread` NTSTATUS is printed alongside; every candidate thread from the
  search is proved with a real CONTEXT round-trip at setup (and by `--check --on-lua-thread`),
  the first survivor is used, and a mode with no survivor refuses at `Start()` instead of failing
  inside a command; context writes are verified by read-back and retried with `CONTROL|INTEGER`;
  failed takeovers raise `LuaTakeoverException` and the probe reports them as `[takeover]`/exit 8
  rather than blaming the client's Lua; a moment with RIP inside the Lua cluster is avoided when a
  better one is coming; `HijackReport`/`HijackHow`/`HijackAttempts` expose the whole story. New
  `--dump va:len,...` (read-only, `--out`, `--dump-text`, `.txt` manifest, md5) so the decrypted
  live pages can be copied out for offline analysis, and the whole movement/binding set
  (`MoveForwardStop`, `MoveBackward*`, `Turn*`, `Strafe*`, `Pitch*`, `JumpOrAscendStart`,
  `ToggleRun`, `ToggleAutoRun`, `StartAutoRun`, `StopAutoRun`, `Mouselook*`, `SetMoveEnabled`,
  `SetTurnEnabled`) is in the built-in RVA table from hunt31's live walk.
* **0.7.0** — Lua-thread hijack execution mode (`--on-lua-thread`, `LuaExecutionMode`), the
  answer to `ASSERT(FrameScript_IsLuaThread())`: each command is executed by the client's own
  Lua thread via suspend + saved CONTEXT + RIP/RSP/RCX redirect, with the payload returning into
  a `pause` stub in our page and the context restored afterwards. Mode is opt-in and orthogonal
  to the guard (no freeze is used in it). Wedge policy hardened in **both** modes: a command that
  times out now leaves the client's thread frozen/parked and the pages untouched, never resumes a
  half-finished call, and says so in the log. `--timeout-ms N`, `HijackTimeoutMs`, `HijackActive`,
  `StoppedFrozen`. `WriteCommandBlock`/`ReadResults` are shared by both paths so they cannot
  drift; name/argument writes are bounds-checked against their page slots. The movement section
  above replaced the "try `/run` then call it" advice with what actually happened.

* **0.6.1** — the cold-page refusal is now **name-aware**: it prints the exact chat line that
  warms *that* function (`/run MoveForwardStart()` + the matching `/run MoveForwardStop()` for a
  `*Start`, `/run TargetUnit('player')`, `/run CastSpellByName('Fireball')`, `/run RunMacro('1')`,
  `/run print(1)` for `RunScript`) instead of always suggesting `RunScript`'s line, and it says
  why a bound key does not warm a movement handler. `--seq` now **continues past a Lua error**
  (the client refusing a call - the command was served, so the payload is healthy; a movement
  pair then reports both halves) while still aborting on a cold-page refusal and on a timeout.
  `--call` reports a Lua refusal as an answer rather than a bridge failure, and still stops the
  thread and checks the client. `MoveForwardStart`/`MoveForwardStop` joined the cold-survey list
  after this run refused them.
* **0.6.0** — `--seq "A ; sleep=1200 ; B"`: several guarded calls in **one** session, in order,
  through the same cold-page gate (a refusal stops the sequence before the action is left half
  done). One attach, one thread pick, one resident payload thread for the whole sequence — the
  shape the bot's action queue will take, and the only sane way to test an action that has to be
  undone (walk for a moment, then stop). Each step prints its result and timing. Also: the
  cold-survey tag on a handler now distinguishes "still cold" from "the game has since decrypted
  it", and `MoveForwardStart` (module+0x23C8BE0) is in the built-in RVA table.
* **0.5.0** — **verified live: 798/798 calls guarded, zero crashes; and the first write
  (`RunScript("print(...)")`) executed inside the client** (see "The Lua-thread guard" above).
  Fixes the reason every `--seconds` run refused while `--check` worked:
  `Start()` never called `FindLuaThread()` (only `Verify()`/`--check` did), so a normal run
  began with an empty pick, printed a blank thread id and threw on the first call. `Start()`
  now runs the search **before** it allocates the page or starts a thread, and on failure
  throws with the whole sampling table and the three fixes (`--check`, `--lua-thread <tid>`,
  or taking the race deliberately). `EnsureGuardThread()` is the cached search used by both
  `Start()` and the guard (public, for the bot), `GuardReady`/`GuardFailureHelp()` expose the
  state and the refusal text, `UseLuaThread()` clears a previous failure, `Verify()` searches
  once instead of twice (the second search could empty a good pick), and the `Process`/
  `ProcessThread` objects the sampling opens are disposed instead of accumulating handles.
* **0.4.0** — the bridge gets a verb: `--call NAME [--arg TEXT] [--nresults 1|2]` runs one
  guarded call and prints the result, the raw type word, the frozen-vs-wall timing and the
  guard counters, then stops. It **refuses** to call a handler whose page this client has not
  decrypted yet (`ReadProcessMemory` fails on it; the protector maps cold pages `NOACCESS`) and
  says how to warm it - `--force` overrides, `--rva 0x…` supplies a handler RVA for names that
  are not in the built-in table. `LuaBridge64` gained `CanRead`/`ReadHex` for that check. The
  old per-round extra call moved from `--call` to `--each` (the one-shot mode took the name).
  Nothing else changed: same payload, same guard, same 641 bytes.

* **0.1.1** — fixed: the thread start address was `base + gadget` where `gadget` was already
  absolute, so the injected thread began outside the module (non-canonical) and died on its
  first instruction while the payload never ran (`stage 0x0`). `FindGadget()` now returns the
  absolute address and `Start()` uses it as-is, with a module-range check, a `ff 21` re-read
  and an error that reports the thread's exit code instead of a bare stage number. Also added
  `Verify()` / `--check`. **If 0.1.0 was run, the client almost certainly died with that
  thread — restart it.**
* **0.3.0** — thread identification is now two-signal (CPU time + RIP sampling) and
  reported: `ThreadReport` is a per-thread sampling table (`--check`, a normal run, and
  the guard's refusal message all print it), `--lua-thread N` freezes a thread you choose,
  and if the sampling cannot open a single thread handle the table names the Win32 error
  instead of leaving a blank thread id. Reason: the first 0.2.0 run printed
  `guard : freezes tid ` — FindLuaThread() had found nothing, so the bridge correctly
  refused to call, but the log did not say *why*. Behaviour of the guard itself is
  unchanged: quiet-check, freeze, re-check, run, resume in a `finally`.
* **0.2.0** — the Lua-thread guard (above), on by default; `--no-guard` to disable it;
  `Verify()`/`--check` now also identify the Lua thread and report whether the VM is
  quiet; the error path checks whether the client is still alive before advising.
* **0.1.0** — first port of the hunt45/hunt46 protocol.

Run `--check` first: it attaches, samples the threads, verifies everything and prints
what it found **without writing into the client and without starting any thread**.

Expected output, in-game (real run, 0.5.0, build 70291):

```
client      : pid 19516   image base 0x7FF73B080000
lua_State   : 0x21CDB3B6358
trampoline  : 0x7FF73F79A983   (module+0x471A983, `e8 ... c3` verified live)
bridge page : 0x21D3B950000   gadget 0x7FF73C4BA7DA
payload     : 641 bytes, resident, waiting for commands
guard       : freezes tid 13524 (seen executing Lua when sampled) for the duration of every call

[   1] GetTime=211469.5      Dw         level 2   hp 72    target=True  Player-70-00000008 class "ROGUE"/"Rogue"
        last call 0.70 ms, payload has served 7 commands, guard: 7 freezes, 0 waits, 0 retries
...
[stats]     114 rounds, 798 calls in 60.5 s  ->  13.2 calls/s
[stats]     per round: min 21.49 ms, p50 23.92 ms, avg 26.78 ms, max 45.80 ms (7 calls each)
guard       : 798 freezes, 12 waits for the VM to go quiet, 11 retries, 1.139 ms frozen per call
[stop]      the resident thread exited on command - no TerminateThread was used
[alive]     pid 19516 is still running (70 threads)
```

`13.2 calls/s` is the probe's `--interval 500` pacing, not the bridge: a round of 7 calls takes
~24 ms, of which ~8 ms is the 1.1 ms-per-call freeze. `--interval 0` measures the ceiling.

## How it works (the protocol)

One injected thread runs a 641-byte payload and serves commands from a mailbox in
a page the driver allocates inside the client (`cmd`/`seq`/`ack` handshake, so a
result is never read half-written):

| block field | meaning |
|---|---|
| `+0xC8 CMD` | 0 = idle, 1 = run the command, 0xFF = exit |
| `+0xD0 SEQ` / `+0xD8 ACK` | the payload copies SEQ to ACK once the results are complete |
| `+0xE0 calls`, `+0x118 idles` | the payload's own counters |
| `+0xF8 tickFn`, `+0x100 sleepFn` | `kernel32!GetTickCount64` / `kernel32!Sleep` (verified against the client's own mapping) |
| `+0x108 deadline` | the payload exits by itself when this passes — a dead driver cannot leave a spinner behind |
| `+0x00..+0xC0` | per-command: function name/length, argument/length, nargs, nresults, results (value + type word) |

Per command the payload does `lua_getfield(globals, name)` → `lua_pushlstring(arg)`
→ `lua_pcall(L, nargs, nresults, 0)` and parks the results. Two things are load
bearing and both were established by hunt42–hunt45:

* the thread is started at a warm `jmp qword ptr [rcx]` gadget in **module code**
  (a thread started at our own page is rejected);
* `lua_pcall` is entered through a two-hop return whose return address is a module
  `ret` with the `E8` call shape (`module+0x471A983`) — every call path in this
  client hands its return address to the protector at `module+0x1D8E80`, which
  range-checks it and shape-checks `byte[X-5] == 0xE8`, then deliberately
  dereferences null (`0x1D9115`) if it does not like what it sees.

## Things that will bite

* **Type words carry flags.** `UnitHealth("player")` comes back as `tt=0x103`
  (`0x103 & 0xFF = 3` = number) while `GetTime()` comes back as `tt=3`. Always mask
  with `0xFF` — `LuaValue.Type` does, and `RawType` keeps the original.
* **Build-specific addresses.** `lua_State` slot `module+0x7B76AB8`, `lua_getfield`
  `+0x471A370`, `lua_pushlstring` `+0x471BC20`, `lua_pcall` `+0x471B5C0`,
  trampoline `+0x471A983`, gadget list in `GadgetRvas`. `Attach()` verifies the
  `lua_gettop` prologue (`488b512848b8ab…`) before doing anything, so a wrong client
  fails loudly instead of writing into the wrong place.
* **The Lua stack must be left clean.** After a call the payload's frame leaves
  `nresults` slots above `L->top`; the driver restores `L->top` to its pre-call
  value (only when `top_after` matches the expected arithmetic, otherwise it warns
  and leaves it alone).
* **No `TerminateThread`, ever** (hunts 40/41: it takes the client down) and the page
  is never freed while a payload thread might live in it (hunt43). If a command does
  not complete, the bridge marks itself stuck, refuses further calls and leaves the
  client alone.
* **This is still a foreign thread inside their VM.** Reads are safe; a mutation
  (`RunScript`, `CastSpellByName`) is the next thing to prove, and the step after
  that is running the same payload from an in-process hook
  (`DX11PresentHook64`) so it executes on a game-owned thread.

## Measured (hunt46, build 70291)

* 180 calls through one thread in 0.73 s → **245 calls/s**, p50 1.06 ms, max 14.9 ms
* idle polling at ~650 Hz costing **0.78 % of one core** (`GetThreadTimes`)
* failsafe: an unused payload thread exited by itself at its deadline with the
  sentinel exit code `0x4200CAFE`; a commanded exit is immediate
* 11/11 read-only APIs validated: `GetTime`, `UnitHealth`, `UnitLevel`, `UnitExists`,
  `UnitGUID`, `UnitName`, `UnitClass` (two results), `GetMoney`, `IsLoggedIn`

## 2026-10-30 — the first *action* killed the client, and what changed

The grind loop reached its first hotspot and the client died. The crash report
(`2026-10-30_14.38.14_Error_16036.txt`, 134 840 bytes) says:

```
Error: ASSERT(FrameScript_IsLuaThread())
File:  d:\BuildServer2\...\Engine\Source\FrameScript\FrameScriptEvents.cpp
Line:  208
ProcessID: 16036   ThreadID: 11228
DBG-ADDR<00007ff67d9c4b86>("WowB.exe")
...
```

The bot log lines up to the millisecond:

```
[14:38:14.762] [Route] waypoint 1/1 reached
[14:38:14.762] [Route] route complete: 1 waypoints walked
        (client crash report timestamp: 14:38:14.993)
```

Nothing was being walked, nothing was being written. The next thing `TryEngage`
did was call `TargetNearestEnemy()` — and for the first time in the session there
was a mob for it to select. Every earlier call had been a *question*
(`UnitName`, `UnitLevel`, `UnitHealth`, `UnitExists`, `UnitIsDead`, `HasAction`,
`IsUsableAction`, `GetTime`) and every one of them answered; they raise no event.
Selecting a target raises `PLAYER_TARGET_CHANGED`, the client tried to dispatch it,
found itself on the payload's thread instead of its Lua thread, and terminated.

So the earlier conclusion in this file — "reads are safe, a mutation is the next
thing to prove" — was right, and the thing that made the mutation unsafe is not
the mutation's *content* but the *thread*. `ExecutionMode.ServerThread` cannot
ever send `UseAction`, `CastSpellByName` or `TargetNearestEnemy`.

### What the bot does now

`Styx/Logic/Pathing/ClickAim/GameLua.cs`:

1. **Tries `LuaExecutionMode.LuaThreadHijack` first** — the command runs on the
   client's own Lua thread, which is exactly what `FrameScript_IsLuaThread()`
   tests. The first command (`GetTime`) is the proof of the takeover, and the
   report line says which way it went, e.g.
   `execution LuaThreadHijack; the client's own Lua thread can be taken over
   (SetThreadContext(flags 0x10001F)=ok, 1 quiet moment(s)) - events may be raised`.
2. If the takeover **setup** fails (thread handle, CONTEXT round-trip), it falls
   back to the resident thread in a **read-only** state and says so:
   `READ-ONLY: questions only, because the client asserts FrameScript_IsLuaThread() and dies
   when an event is raised from our thread`.
3. If the setup is green but the first takeover cannot run a command, it keeps the
   bridge for reads and refuses every action. It never falls back to sending the
   action through the other thread.
4. Independently of the mode, `GameLua.Call` refuses `UseAction`, `CastSpell`,
   `CastSpellByName`, `TargetNearestEnemy`, `TargetUnit`, `AttackTarget`,
   `RunMacroText`, `StartAttack` and `StopAttack` unless `EventsAllowed` is true,
   and returns the reason instead of sending the command. That check is what makes
   a repeat of this crash impossible even if the mode logic is wrong.

`Styx/Logic/Pathing/ClickAim/GrindRunner.cs` follows the same rule at its call
sites (`EventsAllowed`, not `Live`), and gained a targeting order that can engage
without Lua at all:

1. `TargetNearestEnemy()` — only when `EventsAllowed`.
2. TAB ×3 — the client's own hostility filter, no memory, no Lua.
3. Right-click the nearest unit — no hostility filter, so it is last; the client
   selects it and starts auto-attack itself, which is a click-to-move mechanic
   applied to a unit and cannot trip the assert.

### What the next log will say

Either

```
[Grind] Lua: pid …, payload resident (641 bytes), execution LuaThreadHijack; the client's
  own Lua thread can be taken over (…, N quiet moment(s)) - events may be raised
[Grind]   events ALLOWED - commands run on the client's own Lua thread
```

and combat presses the bar, or

```
[Grind] Lua: … READ-ONLY: questions only, …
[Grind]   events BLOCKED - …
```

and combat is the right-click attack (`the right-click attack does damage:
health 86 -> 71`) until the takeover works. There is no third, silent case.

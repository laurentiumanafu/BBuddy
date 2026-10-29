# Standalone 10-second injected writer probe — experimental (v7)

Evidence that shaped this build:

* v1-v3: a bare remote `LoadLibraryW` returned `0x0`; the stub then reported
  `phase=0` (never ran). v3 also queued 74 APCs at once (my bug).
* v4: ran against an un-restarted client and had an uninitialised method flag.
  Only useful line: a `kernelbase!Sleep` start routine does execute.
* v5: fresh client. `CreateRemoteThread` did not run our stub; APC did (16 ms).
  `LoadLibraryW` returned NULL + **87** for both `System32\version.dll` and the
  probe DLL — so the block was not specific to our image.
* v6: `callVerify` returned the real PID, proving the call mechanism and the
  reported errors are genuine. `bogusLoad` of a non-existent path gave **87**
  instead of the expected 126, i.e. `LoadLibraryW` never reached path
  resolution. `ntdll!LdrLoadDll` returned **0xC000071C = STATUS_INVALID_THREAD**.

v7 therefore stops trying to use the OS loader:

1. read-only `codeDiff` of `kernel32!LoadLibraryW`, `kernelbase!LoadLibraryW`,
   `kernel32!LoadLibraryExW`, `ntdll!LdrLoadDll`,
   `ntdll!LdrGetProcedureAddress` and a control function, comparing live target
   bytes with the same functions in this process. Any difference means the
   target copy is patched, and the byte dump shows the hook shape.
2. the probe DLL is now built **without the C runtime** (its only import group
   is `KERNEL32.dll`, 11 symbols) and is **manually mapped**: allocate at the
   preferred base when possible, copy headers and sections, apply DIR64
   relocations if any are needed, resolve every import against modules already
   loaded in the client, apply per-section protections, flush the instruction
   cache.
3. only then is the image entry point called once, followed by `ProbeStart`,
   the 10-second observation and `ProbeStop`. The loader is never involved, so
   `87` and `STATUS_INVALID_THREAD` cannot block it.

Still: one execution per never-reused slot, restart guard active, watcher only
after a verified `ProbeStart` return.

## Run

1. Extract the ZIP into `C:\ghidra-scripts`, leaving the EXE and DLL together
   in `C:\ghidra-scripts\WowBCtmQuickProbe`. **Both files changed in v7** — the
   DLL no longer uses the C runtime. Enter the world in a safe area.
2. In CMD run:

   ```cmd
   "C:\ghidra-scripts\WowBCtmQuickProbe\WowBCtmQuickProbe.exe"
   ```

   If multiple WowB processes are running, add `--pid 1234` using the correct
   PID. No .NET build is needed. If access is denied, run at equivalent
   privileges to your local test client.
3. The EXE may spend up to ~90 seconds in diagnostics before anything is
   watched (Sleep test, execution test, control load, probe load). If the client
   freezes during this, close it and send the report. When the console says
   **ACTIVE**, return to WoW and right-click different ground destinations; the
   observation ends automatically after 10 seconds.
4. Upload the timestamped `C:\ghidra-scripts\WowBCtmQuickProbe-*.txt` report,
   even if the client crashes or injection fails. If the console reports an
   error and no report exists, share the console output instead.
5. **Restart the game client after the run**, even if cleanup appears successful.

## Scope, safety and interpretation

* This is invasive: it allocates/writes remote memory, launches remote threads,
  loads the supplied DLL and applies `PAGE_GUARD` to the player-position page.
  The whole page is affected, not just three floats. It can severely slow,
  freeze or crash the client. The preceding experiment produced a client report
  labelled `Security Crash`; its cause remains unknown and a repeat is possible.
  Back up test data. Do not use on systems you do not own/control.
* The loader reads the local GUID and pool slots and rechecks the player record
  before injection. It requires an AMD64 PE and a matching 16-byte UnitHealth
  sample; that is only a partial build check. If validation fails it writes an
  error report and does **not** start the probe. No old process address is reused.
* The APC route queues an asynchronous procedure on one client thread at a
  time. If that thread never becomes alertable the APC never runs; if it does,
  the stub executes on a game thread and may call `LoadLibraryW` while that
  thread holds internal locks, which can freeze the client. A queued APC can
  still run **after** this tool has exited. Restart the client afterwards.
* It watches around `record+0x60` (the observed player position), **not a
  confirmed CTM destination**. Those writes may be ordinary movement updates;
  lack of events does not disprove CTM. No game functions or movement actions
  are called and game code is not patched.
* PAGE_GUARD faults occur before the access. A vectored handler re-arms the
  guard using single-step; other threads may access while the guard is absent.
  Reuse, non-atomic lookups, wide writes and the 512-event cap can miss or
  misattribute data. Report RIPs are before-write; sampled code bytes and the
  module list are read separately and may change.
* The local launcher samples the physical right-button state only while WoW is
  the foreground window. A logged transition does not prove a game message or
  ground click was accepted; polling and focus changes can miss transitions.
* Events are written to the report during the observation, so some evidence
  may survive a client crash, but the latest/in-flight events can still be lost.
* Cleanup attempts to restore page protections. The DLL, handler, TLS slot and
  shared memory intentionally remain loaded until client exit, to avoid a
  handler use-after-free. A timeout or crash can leave guards behind: **restart
  the client regardless of the reported cleanup status**.
* The native EXE/DLL were cross-compiled for Windows AMD64 using Zig
  0.12.0-dev.1286. The actual player-lookup code passed synthetic Linux tests;
  PE architecture/export checks passed. No Windows API, injection, timing, or
  live-game execution was tested here. Source and synthetic tests are included.

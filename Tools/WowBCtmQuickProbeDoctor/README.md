# WowBCtm Quick Probe loader diagnostic

This diagnostic investigates why the probe returned immediately. It does not
open, debug, or inject into WoW and does not call `ProbeStart`. It loads the
existing probe DLL in its own process and performs a remote LoadLibrary test
only in a disposable helper process that it launches and closes itself.

Extract the ZIP into `C:\ghidra-scripts`, so the diagnostic EXE sits next to
your **existing** `WowBCtmQuickProbe.dll` in
`C:\ghidra-scripts\WowBCtmQuickProbe\`. Then in CMD:

```
"C:\ghidra-scripts\WowBCtmQuickProbe\WowBCtmQuickProbeDoctor.exe"
```

No clicks or gameplay actions are required. The diagnostic prints a report and
also saves `C:\ghidra-scripts\WowBCtmQuickDoctor-*.txt`. Share that report or
paste the console output. Your game can remain open for the diagnostic. Before
any later live-probe experiment, restart the client to clear the previous
attempt's remote allocations.

The report distinguishes file/metadata problems, normal DLL loading in the
diagnostic process, and the same remote loader technique applied to the
helper. A successful helper test does not prove why the game rejected or failed
the load; a game-specific policy, client health, or another issue could still
be involved. A remote thread exit code is only 32 bits, not a full x64 DLL
address. No client security controls are disabled or bypassed.

The EXE was cross-compiled for Windows x64 using Zig 0.12.0-dev.1286 and its PE
architecture/imports were checked. It has not been run on Windows here; send
any failure report. Source is included in the ZIP.

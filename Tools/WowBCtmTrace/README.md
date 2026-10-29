# WoW 70291 experimental capture + write trace

**Invasive, experimental, and not Windows-built or live-tested yet.** Use only
on your own local/test client. This attaches the Windows debugger, changes thread
debug registers, and pauses all client threads at debug events. It can severely
slow, freeze, or crash the client; take backups. It requires 64-bit Windows and
.NET 10 SDK. Exit/restart the client after an abnormal stop. Client integrity
checks, existing debuggers, privilege boundaries, or anti-debugging may block it.

This first approach does **not** inject a custom DLL or patch game instructions.
It uses `DebugActiveProcess`, per-thread x64 hardware data watchpoints, and
`DebugBreakProcess` to request target breaks for arming/cleanup. It does not call
game functions, automate clicks, or write gameplay memory. It changes debug
registers and debugger state; it is not a read-only operation.

## One batched run

Extract the ZIP under `C:\ghidra-scripts`, giving
`C:\ghidra-scripts\WowBCtmTrace\WowBCtmTrace.csproj`. Put the character in a
safe, open area, keep the same process and character for the whole run, then in
CMD:

```
dotnet run -c Release --project "C:\ghidra-scripts\WowBCtmTrace\WowBCtmTrace.csproj"
```

If more than one WoW process exists, append `-- --pid 1234` using the actual
PID. If attach/access fails, try a CMD with equivalent privileges to the local
test client. Do not use this against a client/server you do not control.

1. Follow the three prompts: idle, right-click destination A, then right-click
   a different destination B. Return to the console and press Enter after each.
   Prefer distant clicks and an open area, and switch promptly. The six
   sequential snapshots can use about 768 MiB RAM and are incomplete.
2. The capture report is saved first. If it finds no candidates the tool stops
   there. Otherwise press Enter at the debugger warning. Wait for `READY`.
3. Type `C` and press Enter. Wait for `ARMED`, switch to the client and manually
   click a new ground destination. Return to the console; type `D` and Enter,
   switch back and click a different destination. Return, type `K` and Enter,
   then move using only the keyboard. Finally return, type `Q` and Enter.
   Phase labels mark console commands, not exact click timestamps.
4. Share **both** timestamped `WowBCtmCapture-*.txt` and
   `WowBCtmWriteTrace-*.txt` from `C:\ghidra-scripts`. If no trace file exists,
   share the capture and console error. Reports contain process addresses,
   registers, code bytes and raw stack words; review before sharing.

The tracer stops after 90 seconds or 3,000 watchpoint events, possibly before
all three phases if a field is very busy. It records the first detailed hit for
up to 200 distinct phase/thread/RIP/slot signatures and counts repetitions. It
attempts to restore each modified debug-register slot while the debugger has
stopped the client, then detaches. A cleanup failure is explicitly reported;
**restart the client if cleanup is uncertain, the debugger exits unexpectedly,
or the client behaves oddly**, regardless of a local-server crash being safe.
Do not close the debugger console mid-trace if you can use `Q` instead.

## Interpretation and limits

* The fresh read-only capture is capped at 128 MiB and excludes mapped/read-only
  pages; six snapshots are sequential and can drop failed chunks. It applies
  stability and float filters and keeps at most 200 sampled candidates. Existing
  memory may be reused between capture and tracing. Zero candidates, writers or
  hits does not rule out CTM.
* The tracer heuristically chooses up to four **non-overlapping** candidate
  triples, giving preference to plausible values near the checked player record.
  It watches only the component that changed most between A and B. It is not a
  destination filter or proof; it may watch unrelated values or miss the field.
* Four hardware slots are available per x64 thread. It uses free slots only,
  handles observed new-thread events, and reports arm failures. Existing debug
  slots or failures can reduce per-thread coverage. Heavy writes stall the game
  and may consume the 3,000-event budget before clicks occur.
* For a normal x86/x64 data watchpoint, the recorded `RIP-after-write` is after
  the writing instruction; raw stack qwords are **not** an unwind. In Ghidra,
  inspect instruction boundaries around the recorded RIP, not RIP as a proven
  function entry. Compare C/D against the keyboard-only phase and seek repeatable
  semantic evidence before treating anything as CTM. Writer code, pointers,
  calling conventions, thread affinity and control operations remain unproven.
* Standalone source syntax and an offline ABI/bit-layout model were checked in
  the sandbox; this is **not** a Windows build, debugger API test, or live test.
  `--selftest` covers only offline capture/selection logic, not debugger APIs.
  Never promote the candidate addresses or issue gameplay calls from this report.

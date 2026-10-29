# WoW 70291 debugger + injected writer trace — experimental

**Runs invasive code in your local client.** The native DLL and loader were
cross-compiled for Windows x64, but this bundle has not been Windows-built,
Windows-run, or tested against WoW. Client stalls, crashes, lost progress, and
incomplete cleanup are possible. Back up your client/server data. Use only on a
client you control. Requires .NET 10 SDK, Windows x64 and privileges to attach.

The last trace showed two coordinate-like candidates being copied by the same
SIMD copy instruction in a non-WoW module. They are not established CTM data.
This tool looks for fresh candidates, uses the debugger to recognize that *exact*
copy pattern and compare paired 4-byte source/destination values, then injects
an x64 research DLL to trace writes near fresh source addresses. If that exact
pattern is absent, it falls back to fresh candidate addresses. No old PID or
absolute address from the last report is reused. The loader verifies the
process name and exact creation time before injecting.

## One batched run

Extract the ZIP into `C:\ghidra-scripts`, so the project is
`C:\ghidra-scripts\WowBCtmTraceInject\WowBCtmTraceInject.csproj`. Enter a safe
open area on the local test client. In CMD run:

```
dotnet run -c Release --project "C:\ghidra-scripts\WowBCtmTraceInject\WowBCtmTraceInject.csproj"
```

For multiple clients, append `-- --pid 1234` using the correct PID. Keep the
same process and character in-world throughout:

1. Follow the **idle, A, B** prompts. Right-click ground for A and a different
   destination for B, then return to the console and press Enter each time.
2. The capture report is saved first. If there are no candidates, it uses the checked player-position fields when
   available (not proof of CTM); otherwise it stops. Press Enter at the
   debugger warning. When it says `READY`, type `C`, press Enter and wait for
   `ARMED`. Return to the client and click one or more ground destinations with
   the mouse. Return to the console, type `Q`, then Enter.
3. If the debugger detached cleanly, press Enter at `INJECTED STAGE`. Wait for
   `INJECTED WATCH ACTIVE`, return to the client and right-click several
   different ground locations. The injected probe stops after 25 seconds.
4. **Restart the client after the test**, even when the report says cleanup
   succeeded. Share the timestamped `WowBCtmCapture-*.txt`,
   `WowBCtmWriteTrace-*.txt`, and `WowBCtmInjected-*.txt` files from
   `C:\ghidra-scripts`. If a stage fails or the client crashes, share whichever
   reports exist and the console error. Files include process addresses,
   registers, code bytes and potentially sensitive raw stack values.

## What it changes and what results mean

* First stage is bounded read-only sampling. The debugger stage changes per-
  thread hardware debug registers and pauses the process at debug events.
  It attempts to restore slots before detaching. If that fails, injection is
  skipped; restart the client. Its `RIP-after-write` points after the store.
* The injected loader writes a DLL path and configuration to remote process
  memory, starts remote threads for `LoadLibraryW`, `ProbeStart`, and
  `ProbeStop`, and sets `PAGE_GUARD` on up to four deduplicated candidate pages.
  A vectored exception handler observes faults and uses per-thread single-step
  rearming. It captures up to 512 nearby write faults, their pre-write RIP and
  registers. Code bytes and module names are sampled at the **end**, not at the
  fault time. It does not issue gameplay calls or patch game instructions.
* PAGE_GUARD covers an entire 4 KiB page, including reads, and can severely
  perturb execution. The OS clears guard on the first access; a competing
  thread can access the page before the handler rearms it. Fault address is
  not necessarily the beginning of a logical structure, and wide writes and
  cross-page operations can be missed. Counters include nearby writes only;
  logs are capped. Client page reuse and code re-encryption may confuse results.
* The probe attempts to restore page protections when it stops. To avoid
  unloading code while a handler/single-step may still be running, its DLL,
  handler, TLS slot, and configuration intentionally remain until client exit.
  **A successful stop is not a clean unload. Restart the client.** A timeout,
  failure or crash can leave a guard active until restart. No kernel or disk
  driver is installed, and nothing persists across client restart.
* Up to 128 MiB is sampled and six scans need roughly 768 MiB plus overhead.
  Candidate filtering, only four watched components, thread coverage, rearming
  races and the 25-second cap can miss the true CTM data. Mouse-only labels do
  not provide an independent keyboard-movement control. A reported writer or
  `RIP-before-write` is an investigation lead, not a CTM function, offset, or
  callable ABI. Compare in Ghidra/rea/Gemini before making gameplay changes.

`native-source/` contains the native source. The bundled binaries were built
from it with Zig 0.12.0-dev.1286 `zig cc -target x86_64-windows-gnu -O2 -s`
(the DLL additionally uses `-shared`). The C# controller was syntax-parsed but
has not been compiled in this environment. No Windows live execution has been
performed for the injected probe.

## Hold after 2026-10-28 crash

Do not rerun the original injected/debugger sequence while this crash is being
investigated. In the user's 22:19 run, the client reported a `Security Crash`
at 22:19:25.296; the injector report was timestamped 22:19:32.712 and says the
remote DLL was not found, `loaded=0 started=0 stopped=0`. `ProbeStart` and the
page-guard handler were not called in that loader path. The time ordering
assumes both files used the same unchanged system clock. The crash's underlying
cause and relation, if any, to earlier debugging are not established. PID/name/
creation-time checks do not establish that a process is healthy: a process can
remain alive while reporting a crash. Prefer offline analysis until understood.

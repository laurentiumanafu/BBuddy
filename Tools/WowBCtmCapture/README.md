# WowB CTM differential capture v2 (research only)

This stand-alone x64 Windows console project takes six **read-only** memory
samples: two while idle, two after clicking destination A, and two after
clicking destination B. It looks for finite, 4-byte-aligned 3-float values that
are stable within each pair but differ between A and B. It does not write to,
suspend, inject into, or call functions in the game. It requires .NET 10 SDK.
The exact manual click position is not assumed known. No game automation is done.

## Run

Extract the supplied ZIP under `C:\ghidra-scripts`, replacing the earlier version if present, so that the project file is
`C:\ghidra-scripts\WowBCtmCapture\WowBCtmCapture.csproj`. Keep WoW in-world on
the same character and PID in an open, safe area. Run in Windows CMD:

```
dotnet run -c Release --project "C:\ghidra-scripts\WowBCtmCapture\WowBCtmCapture.csproj"
```

If multiple WowB processes are open, add `-- --pid 1234` with the actual PID.
There are three prompts: stay still for idle, then click a distant destination
A and switch back to press Enter, then a different distant destination B and
switch back to press Enter. Use the client's normal manual ground click; nothing
is injected. If possible, keep the character moving during the A and B scans.
The tool records scan durations: if movement ends before sampling, the data may
be misleading or uninformative. After the final capture, analysis runs offline.

Upload only the timestamped `C:\ghidra-scripts\WowBCtmCapture-*.txt` report.
It contains process addresses and sampled float values: review it before sharing.
If building or running fails, paste the error instead; no report is expected.

## Limits and interpretation

* 128 MiB of writable memory is selected: small windows around a GUID/slot-
  checked local player record and verified player unit if found; image pages;
  then private chunks sampled across the allocation list, with remaining space
  filled from private/image pages. The scan is still highly incomplete and may
  omit the correct allocation entirely. If the player lookup fails, selection
  falls back to image and stratified private pages.
  Mapped/read-only pages and allocations created after enumeration are omitted.
  A scope cap and failed full-chunk reads are reported. A changed/freed region
  is dropped, but the same allocation address can be reused (ABA).
* Six snapshots require up to about 768 MiB plus overhead. They're sequential,
  non-atomic, scanned in selection order, and not synchronized to the click. Objects may
  change while sampling; no thread suspension. Candidate triples must lie fully
  in a chunk (1 MiB maximum); unaligned fields, non-floats, pointers, encoded
  values, and triples straddling chunk boundaries are missed.
* When the GUID/slot checks succeed, the player position at record+0x60 is
  sampled after each scan. Approximate distances to A1/B1 are comparison aids,
  not proof of a destination, and sampling times differ. ABA reuse can still
  occur. Pointer validation and the reported player object are read-only.
* Within-pair component tolerance is 0.05; between A/B Euclidean difference
  must be at least 3. Candidate magnitudes are limited to 40000; such bounds
  can exclude a real destination. Idle is only a basic stationary control;
  there is no repeated-identical-click or independent movement control yet.
* A maximum of 200 candidates is sampled uniformly with a fixed random seed;
  total counts/caps are shown. Candidates may be player positions, animation,
  cursor/camera projections, stale or unrelated data—not CTM offsets. Address
  and values are not authorization to write, call, or dereference them later.
  Zero candidates do **not** disprove CTM.
* A tiny `--selftest` exercises offline comparison logic only, not OS reads or
  live correctness. For example:
  `dotnet run -c Release --project "C:\ghidra-scripts\WowBCtmCapture\WowBCtmCapture.csproj" -- --selftest`

Version 1 compiled and ran on the user machine. Version 2 has not been compiled
or tested against the live client in the sandbox. Do not change the game version or use legacy 3.3.5a offsets.

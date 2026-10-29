# WowBSigScan — apply a proven signature table to our client

## v7 — the report contradicted itself at 29 % coverage

v6 fixed the phantom hit and then printed both of these, one after the other:

```
Only 29% of the executable bytes could be read ... UNSEEN, not absent.
All executable bytes were readable, so a NOT FOUND here is real evidence.
```

The reassuring line was an unguarded `else` that fired whenever coverage had not
*fallen*, which includes "never rose above 29 %". It is now behind
`lastPct >= 99`, and a flat-coverage watch gets its own sentence: nothing became
readable, watching longer did not convert any UNSEEN pattern into a result, scan
once right after play instead.

Two smaller inconsistencies went with it:

- `patterns with any match: 5 of 19` per pass counted the positive-control slot;
  `summary: 4 of 19` was right. The per-pass count now stops at `g_sigCount`.
- `NOT FOUND (29% of executable bytes readable, retry in progress)` promised a
  retry that cannot arrive: `--watch` keeps a union of the pages it read, and a
  re-locked page is not re-read. It now reads
  `UNSEEN (only 29% of executable bytes were readable)`.

Nothing about scanning changed. The same four UNIQUE matches at the same RVAs
(`LUAL_LOADBUFFER 0x048A1A50`, `HANDLE_TERRAIN_CLICK 0x025D6670`,
`LUAH_NEW 0x0472A3D0`, `WOWGUID_TO_STRING 0x047B96A0`) are what a correct report
looks like.

## v6 — a phantom site at rva 0, and coverage that falls instead of climbing

The first v5 field report (69 KB, 26 passes) exposed two things.

**1. My own bug.** Every repeated match was reported twice, the second time at
`rva=0x00000000` with all-zero bytes:

```
LUAL_LOADBUFFER   len=45  matches=2   AMBIGUOUS
    rva=0x048A1A50 prev=0xCC bytes=40 53 48 83 EC 30 49 83 F8 03 72 19
    rva=0x00000000 prev=0x00 bytes=00 00 00 00 00 00 00 00 00 00 00 00
```

`results_scan` added the new match count to `counts[p]` but wrote every pass's hits
from index 0, so after two passes the count said 2 while slot 1 had never been
written. The report then printed an uninitialised slot as a second site and called
a unique function `AMBIGUOUS`. Four patterns showed the phantom, and the summary
said "5 of 19 matched" when 4 had. v6 appends at `counts[p]` (`sc_scan_into`) and
the accumulation is unit-tested: the second pass must store a *real* duplicate,
not zeros, and the dedupe must collapse it back to one.

So the earlier v5 numbers were: **4 of 19 patterns matched**, at the same RVAs as
before —

| pattern | rva |
|---|---|
| `LUAL_LOADBUFFER` | `0x048A1A50` |
| `LUAH_NEW` | `0x0472A3D0` |
| `WOWGUID_TO_STRING` | `0x047B96A0` |
| `HANDLE_TERRAIN_CLICK` | `0x025D6670` |

**2. Coverage falls across passes, not rises.** The premise for `--watch` was that
pages get decrypted as code executes, so playing would raise coverage. The data
says the opposite:

```
pass 1:  readable 32.6%   unreadable 0x316843C
pass 5:  readable 31.9%   unreadable 0x31EB43C
pass 14: readable 30.4%   unreadable 0x32FB43C
pass 26: readable 28.3%   unreadable 0x348143C
```

That is about 29 pages lost per pass, steadily. The client is **re-locking** pages
as we read them, and repeated passes are counter-productive. Two consequences,
both now stated in the report instead of the old optimistic note:

* **the best moment to scan is right after the client has been used**, once. Pass-1
  coverage differs a lot between runs (20.6% in one, 32.6% in another), so this is
  not a fixed property of the build.
* `--watch` still works, but as a **union**: pages already read stay scanned, so
  its coverage line cannot fall back to pass-1's number when a page is taken away.
  It is not a way to reach 100%.

If a run ends below 100% coverage, the report now says plainly that the
NOT FOUND patterns are **unseen, not absent**, and adds the coverage trend.


## v5 — a NOT FOUND at 20% coverage is not an absence

The v4 live run (pid 7208) settled what the tool may and may not claim:

```
  .text  rva=0x00001000 size=0x0494343C unreadable=0x03A2C43C EXEC
      protections: readable=0xF17000 noaccess=0x3A2C43C guard=0x0 ...
UNITHEALTH_PROLOGUE copies=7  known site 0x1D63190 PRESENT
MAPPING CONFIRMED
summary: 4 of 19 patterns matched; 4 in executable code; 0 only inside data
```

Only **0x0F17000 of 0x0494343C** executable bytes could be read — **20.6%**, and
0x3A2C43C bytes are `PAGE_NOACCESS` right now. The mapping was confirmed, so the
four matches are real RVAs:

| pattern | rva | note |
|---|---|---|
| `LUAL_LOADBUFFER` | `0x048A1A50` | preceded by `0xCC` |
| `LUAH_NEW` | `0x0472A3D0` | preceded by `0xCC` |
| `WOWGUID_TO_STRING` | `0x047B96A0` | preceded by `0xCC` |
| `HANDLE_TERRAIN_CLICK` | `0x025D6670` | **same RVA as the earlier process scan**, unique, in `.text` |

That last row matters twice. It means the signature *does* match on build 70291
(the Winkr source says it has not matched since 68887 — that is their build, not
ours), and it is still not movement: `HANDLE_TERRAIN_CLICK` is the `Spell_C`
ground-target finaliser in their code. Two independent process scans now agree on
`0x025D6670`, which is the strongest location evidence we have for that function.

The other 15 patterns — `WORLD_INTERSECT`, `WORLDFRAME_PROJECT_POINT`,
`SPELL_STOP_TARGETING`, `INTERACT_UNIT`, the Lua secret gates — were **not seen**,
which is not the same as absent, because 79% of the code could not be read.

v5 therefore reports coverage instead of pretending:

* every `NOT FOUND` now carries the coverage: `NOT FOUND (21% of executable bytes
  readable)`.
* `--watch SECONDS` rescans until the time is up, accumulating hits. Pages are
  decrypted as they execute, so coverage should climb while you play; the report
  prints coverage per pass, whether it improved, and on which pass each pattern
  first appeared (`only became readable on pass 7 of 12`).
* results are deduplicated by RVA after every pass, so a unique function is not
  reported `AMBIGUOUS` merely because five passes found it five times. That
  dedupe is unit-tested (`sc_dedupe_hits`), because a bug there would invent a
  collision out of the tool's own repetition.
* the closing note is now conditional: at 100% coverage a `NOT FOUND` is evidence;
  below that it says plainly that the patterns are **UNSEEN, not absent**.

```
WowBSigScan.exe --watch 600
```
Play or idle in game for the ten minutes; then look at which patterns appeared and
which pass they appeared on.

**Later passes only re-read pages that were unreadable before.** The first version
of `--watch` rescanned the whole 76 MB image every five seconds, which is why it
looked hung; a failed `ReadProcessMemory` is cheap, but scanning 76 MB against 19
patterns is not. Each pass now iterates the pages still marked unreadable, reads
them one at a time, and scans only the ones that succeeded — with an 80 byte
lead-in from the previous page so a match starting just before the boundary is
still found. The report prints how many pages came back and how long the pass took,
and coverage is recomputed from the page map, so `readable 20.6% → 23.1%` between
passes is a measurement, not an impression.

## v4 — a packed file must not produce "results"

The `--file` run on `C:\Users\brutaIIes\Desktop\wowb-decoded.exe` reported
`1 unique match` and `MAPPING NOT CONFIRMED`. Both numbers were noise, and the PE
header says why:

| section | virtual | raw on disk | ratio |
|---|---|---|---|
| `.text` | 76,821,564 | **1,024** | 0.000 |
| `.data` | 44,156,225 | 6,907,392 | 0.156 |
| `.rdata` | 15,341,684 | 15,342,080 | 1.000 |

A decrypted image stores its code. Here `.text` is 1 KiB on disk for 76 MiB of
virtual space — **that file is the encrypted original**, exactly what the
reference notes warn about ("entropy 7.96, zero x64 prologues, scores 0 signatures").

Every hit landed in `.data`: all 57 control copies and the "UNIQUE"
`HANDLE_TERRAIN_CLICK` at `0x07726470` are inside the compressed blob, i.e. bytes
that merely look like a prologue.

v4 refuses to launder that into a result:

* **structure check** per section: `raw/virtual` ratio, entropy, prologue density
  and int3 density, with a verdict. `raw/virtual < 0.25` on a multi-MB section, or
  entropy > 7.5, gets called out as packed/encrypted.
* **mapping candidates**: PE, then flat (`file offset == RVA`), scored by whether
  the control site lands at `0x1D63190`. If none confirms, it says so and refuses
  to present offsets as RVAs.
* **lookalike labelling**: a match inside a non-executable section is reported as
  `ONLY IN DATA (lookalikes, not code)` and never counted as a hit.
* `--flat` for headerless dumps.

Thresholds are unit-tested on synthetic data: x64-looking code scores entropy 3.4
with 197k prologues/MB and 197k int3/MB, uniform noise scores 8.0 with 128
prologues/MB, all-zero scores 0.0 with none. Code is not flagged as encrypted;
ciphertext is; and zeroes are caught as "high entropy is not the only test".

## v3 — scan the decoded image instead of the live process

Two runs of v2 on the **same process**, minutes apart, disagreed:

| | run A | run B |
|---|---|---|
| `.text` unreadable | `0x36AA43C` | `0x3A0143C` |
| control copies | 9 | 5 |
| `HANDLE_TERRAIN_CLICK` | UNIQUE `0x25D6670` | NOT FOUND |

So `.text` is only partly resident and process-mode results are **racy**: a
NOT FOUND there is not evidence of absence, and one run even found the same
pattern the other could not see. v2's protection histogram also printed
`readable=0`, which was an absolute/relative offset bug of mine - fixed now.

v3 adds `--file PATH`, which scans a decoded image on disk: the whole file, once,
no target process, no races, deterministic. It parses the PE section table when
the file has a usable one, otherwise treats it as flat (`file offset == RVA`), and
calibrates the mapping from the known control site.

```
"C:\ghidra-scripts\WowBSigScan\WowBSigScan.exe" --file "C:\ghidra-projects\WowBLive\wowb-decoded.exe"
```

That is the answer to trust for `WORLD_INTERSECT` / `WORLDFRAME_PROJECT_POINT`.
The report states `MAPPING CONFIRMED` only when the control pattern is found at
`0x1D63190`; if the file is flat with a shifted base it prints the candidate shift
instead of silently reporting wrong RVAs.

## v2 — the first run failed its own control, and it was the tool's fault

v1 reported `UNITHEALTH_PROLOGUE matches=0 ... SCAN SUSPECT` and stopped at
`scannedBytes=0x3A3C000` of a `0x863C000` image. Two defects, both mine:

1. it read the image in 4 MB chunks and did `if (got < CHUNK) break;`, so the
   **first partial read aborted the whole scan** — and a 4 MB span crossing an
   unreadable gap is routine in a live image;
2. it dropped the last `maxlen-1` bytes of every chunk, so any pattern straddling
   a chunk boundary was invisible **by construction**.

The client was never at fault: the tool's own identity check reads those same 16
bytes at `+0x1D63190` and passed before the scan started.

v2 scans the **PE section table** instead — each section is read whole (one bulk
read, falling back to page-by-page with a validity map) and scanned once, so
there is no chunk arithmetic left and no boundaries to straddle. The scan core
now lives in `scancore.h` and is unit-tested against exactly this bug class:
patterns at the first and last byte of a buffer, a match inside an unreadable
hole, wildcards, and capacity overflow.

This is precisely why the control exists. Without it, v1's `0 of 19 patterns
matched` would have read as "the signatures do not apply to our build", and we
would have abandoned the most promising lead available.

## Why

Winkr-Public ships byte signatures for the functions a bot needs, and byte
signatures describe *code*, not addresses — so unlike their RVA table they can
survive a build change. Nineteen of their entries are scannable (the other 83 are
`skip_scan=1` stubs that just use a hardcoded RVA).

The two that matter for click-to-move:

| Signature | What it is |
|---|---|
| `WORLD_INTERSECT` | the client's own raycast against world geometry |
| `WORLDFRAME_PROJECT_POINT` | world → screen projection |

Together those replace a click-to-move *offset* entirely: project the destination
to a screen point, then send a right-click there. The client already walks when it
sees a right-click (measured: 44 yd and 76 yd walks, 31 ms after release), so
nothing else is needed.

`HANDLE_TERRAIN_CLICK` is **not** what it sounds like. Reading
`dll/bridge/dinkr_bridge_actions.c` shows it is the ground-target *finaliser for
spells* (`Spell_C` terrain click: checks `targetingFlags & 0x60`, writes the
position into the spell object, completes a pending AOE cast). It does not move
the character. Recorded so nobody chases it for movement.

## Run

```
"C:\ghidra-scripts\WowBSigScan\WowBSigScan.exe"
```

Report: `C:\ghidra-scripts\WowBSigScan-<timestamp>.txt`.

Read-only: `PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE`. No writes, no
allocation in the target, no threads, no DLL. Client untouched, no restart.

It scans the **in-memory** module image, because the on-disk executable of these
clients is encrypted (Winkr's patch-day notes: entropy 7.96 with zero x64
prologues on disk, 5.7–6.7 with hundreds in memory). Reading the live image
sidesteps that completely — no dump needed.

## How to read it

```
sections=8  (name  rva  vsize  raw  characteristics)
  .text    0x00001000 0x0... ... EXEC
...
  .text    rva=0x00001000 size=0x0... unreadable=0x00000000
scannedBytes=0x... unreadableBytes=0x0 (module virtual size 0x863C000)

=== POSITIVE CONTROL ===
UNITHEALTH_PROLOGUE matches=1 rva=0x1D63190 expected 0x1D63190  SCAN OK
```

The `unreadable=` figure per section matters: if a section reports unreadable
bytes, patterns living there cannot be found and a `NOT FOUND` for those is not
evidence of absence.
This is a check on the *tool*, not on the client: the 16 bytes at `+0x1D63190`
are already confirmed, so the scan must find exactly one match at exactly that
RVA. If it says `SCAN SUSPECT`, every other result in the file is worthless.

```
=== RESULTS ===
WORLD_INTERSECT               len=40  matches=1   UNIQUE
    rva=0x0147EC20 prev=0xCC bytes=48 8B C4 48 89 58 10 48 89 68 18 56 57  (preceded by padding/tail: plausible function start)
```

* `UNIQUE` — one match in the whole image. Best case.
* `AMBIGUOUS` — several matches; the pattern is too generic for this build.
* `NOT FOUND` — the bytes do not exist here, so that function must be found
  another way (Ghidra xrefs, or `wow_offsets.txt`-style named tables).
* `prev=` is the byte before the match. `0xCC`/`0xC3`/`0x00` means it sits after
  padding or a function tail, i.e. it looks like a real function entry rather than
  the middle of one.

## Honest limits

* The patterns were derived on **Classic Era build 69547 `WowClassic.exe`**, not
  on wowb.exe 70291. A unique match is a strong candidate, **not proof**. Before
  calling anything, look at the body in Ghidra and confirm it reads like the same
  function.
* Only 19 patterns are scannable, and none of them is a data global — globals have
  no signature by design and still need anchored disassembly.
* The scan covers the module image only, not heap or stack.
* The positive control proves the scanner can find a known pattern at a known
  place. It does not prove the other patterns are correct for this build.
* Offline tests check the scan core (buffer edges, unreadable holes, wildcards,
  capacity, pattern table well-formedness, the control pattern itself) — not the
  patterns' correctness on our client.
* Sections larger than 192 MB are skipped with a note rather than buffered.

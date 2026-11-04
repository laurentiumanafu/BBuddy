# Experimental 1.60.1.70790 port — 2026-11-04

## Input and status

The user supplied `Wow-decoded.zip` (Drive ID `1VM3hp_fLx5u6BvnLvhO7zlWW14p-OMo_`; it replaced the older archive under the same ID). Archive size 35,094,159 bytes; MD5 `d628e0bbaaa5579a24367c0690c6179b`. The single decoded PE is 86,498,000 bytes, AMD64 (`0x8664`), image base `0x140000000`, version-resource string **1.60.1.70790**, SHA-256 `a4ac58d2998386fe93ce77dda0d2df6d775231d078c849b017876e11879db620`. The executable is not committed.

The 70755 reference executable was not available in this sandbox; the Drive archive had been replaced. Comparison used the known decoded **70291** reference (SHA-256 `8f864e73e73ad67319765063a7c491a420dbe2d0714ce427106e39cd9f109192`), 70755 source/recorded marker bytes, bounded byte searches and Capstone disassembly. Patterns masked RIP-relative displacements and relative branch operands, and matching sites were checked in context. These are static candidates, **not a Windows build or live 70790 validation**. Pattern matches and instruction markers do not authenticate the entire image or prove semantics.

70790 is registered and highest-priority default; 12340, 70291, 70707 and 70755 stay registered. Exact-build reader, selector and inventory/marker routing are separate. The historical `IsExperimental70707` denotes the 70707-style shared family; use `IsExperimental70790` for this precise image. Shared gameplay code remains experimental. Object, unit, combat, movement, aura, loot, corpse and other layouts inherited from 70707/70755 were not comprehensively verified by these static anchors. In particular the unit vtable is inferred from method alignment/shared layout, not independently established. Incorrect fields can cause incorrect reads, movement, interactions or clicks. The user must build and test locally.

## New image-relative candidates

All values are RVAs, not absolute virtual addresses. Runtime uses the actual loaded module base.

| Candidate | 70790 RVA | Static evidence / qualification |
|---|---:|---|
| Player GUID | `0x51B3E70` | Two matching reference sites from 70291 |
| Player name | `0x51B3E80` | Independent name-like references including `0x1DF5F81` and `0x1DF60FB`, plus primary/secondary name contexts; not merely guessed from GUID adjacency |
| Target GUID | `0x6BC5BF8` | 27 matching reference sites |
| Mouseover GUID | `0x526F698` | Two matching sites; transition semantics still untested |
| Object manager | `0x6DC8178` | Three matched references |
| Handle pools | `0x48BFBE0` | Two matching references; inherited pool layout unproven |
| Map root | `0x6DC80F8` | Three matching references; inherited `+0x20` map candidate unproven |
| Camera frame | `0x4FEB5F0` | Ten matching references; `0x28D6754` statically follows frame+`0x480`, camera+`0x274` |
| Stored action array | `0x6DE68C0` | 33 matching references; `0xA6B06D` resolves to it |
| Player / unit / game-object vtables | `0x427CAF8` / `0x4290AD8` / `0x4284398` | Correlated 70291 method tables: player top candidate matched 14 method slots, game object 22; unit candidate shares eight common methods and a plausible first method. Shared methods have other matching tables, so unit especially is weak and all require live verification. |
| Inventory handler / lookup | `0x27696E0` / `0x21FA4C0` | Handler at `0x27697C9` passes context+`0x9460` to lookup; result+`0x90`. It does not prove a free-slot count. |
| Container identity table | `0x6DE9900` | Helper `0x2605350` uses RIP-relative table and index×16. Identity and enumeration semantics remain candidate-only. |

Partial gameplay guard sites: faction `0x211826C` resolves to `0x6B2C240` and reads candidate `+0x1E54`; health `0x2142757` reads `+0x1E28`; power `0x2931DA5` reads `+0x1BC0`; action `0xA6B06D` resolves the action array. At least two readable matching markers and no readable mismatch are required in addition to player, map, action and other guards. An unreadable marker is not a match. Some inventory helper checks retain the prior compromise allowing unreadable code pages only with independent build/player checks; readable mismatches reject the observation. These partial checks do not establish full executable identity or gameplay safety.

## Bag and selling path

The 70790 helper at `0x2606E3B` calls identity helper `0x2605350`; at `0x2606E66` it reads container `+0xAC0`, then high-six bits of qwords at `+0x490` in 16-byte steps. Its player-range branch reads candidate `+0x9480`, size `+0x7D04`, start `0x23`, and array/context `+0x9460`. Markers for the separate capacity path are `0xD8E69C`, `0xD8E6C0`; helper markers are `0x2606E66`, `0x2606E74`, `0x2606E80`; player-range markers `0x2606EB6`, `0x2606EC9`, `0x2606ED4`. These are structurally consistent with the existing candidate model, but no live 70790 inventory observations have verified the model, completeness, overlap or specialty-bag identity.

70790 reuses the 70755 ordinary-bag policy, including user-confirmed per-character and per-full-bag-GUID classifications in `Settings/clickaim-bag-kinds-70755.txt`; this preserves the user's existing classifications only for the same character and exact bag instance. Newly observed or unclassified bags remain unknown. Confirm the old categories are still correct on the new client. Stable zero table identities are not proof of complete enumeration. The two-zero monitor and bounded startup wait are wired to 70790, but **the real subtotal, automatic trip and sale are untested on 70790**. Disable automatic selling until stopped research snapshots match visible ordinary slots and a controlled non-stacking change; then supervise the first trip. Recorded clicks are window-relative and sales are not verified.

## User-observed Grind and unresolved bags: 23:07–23:12

In user log `2026-11-04_2307_3624.log`, the user reports Grind works and **17 visible free bag slots**. Build 70790 attached successfully; player vtable `0x427CAF8` was observed, all four gameplay marker checks matched, and the log records movement, target engagement and declining target health. It sent five loot interactions, but the log does not verify item acquisition. This is meaningful user validation of that Grind scenario, not proof of every feature.

At 23:08:23 the automatic bag subtotal was **unavailable** and selling waited; startup confirmation timed out at 23:08:37 and Grind continued. The stopped 23:12:09 snapshot had a 20-entry player range with **0 zero-tags**, while all four nonzero container candidates were unresolved. Each showed one observed identity-matching header, but another pointer-like field had an unreadable candidate header (1, 1, 2 and 1 respectively); `record+0xD8` had small pointer-like values such as `0x145D8`. All listed inventory and container markers matched. The user-reported 17 slots therefore **cannot be confirmed or ruled out from the subtotal**, and the log does not establish which candidate is ordinary or specialty. Unknown candidates continue to fail closed; no automatic trip is authorized from this snapshot.

For 70790 stopped-bot research only, a subsequent diagnostic reports bounded size, zero-tags and raw entries for a single observed identity-matching header when *other pointer-like headers are unreadable*. It independently rereads the player, record, source pointer, header, size and slot data. These are exploratory observations, not resolved bags: they do not populate classification choices, contribute to a subtotal or enable selling. Multiple matching headers, changing data or unreadable fields remain unavailable. Compare a fresh stopped snapshot to visible bag contents and controlled single-item moves; never assume an unreadable pointer is harmless or infer bag type from size or index. Automatic selling remains unavailable until the unresolved issue is safely addressed and the ordinary-bag subtotal is validated.

### Stopped exploratory snapshot: user reports 17 free slots

In `2026-11-04_2317_10844.log` (23:18:06), the user reported 17 visible free bag slots. The player-range candidate size is 20 with **0** high-qword zero-tags. Four unresolved container candidates each have a single observed identity-matching header at record `+0x28`; their exploratory candidate size / zero-tag counts are **4/1**, **6/5**, **6/6**, and **6/6**. Their combined exploratory count is **18**, not 17. The three six-slot candidates alone total **5 + 6 + 6 = 17**; omitting the four-slot candidate would match the user's report, but neither its bag type nor the slot semantics have been established. A four-slot bag, its candidate index or a one-slot difference do not by themselves prove it is specialty. The user needs to identify the actual bag and a controlled move can test correspondence.

All five container helper markers and the three special-index markers were unreadable in this later snapshot, unlike the matched markers in the earlier log; unreadable is not a match. The lookup and handler markers matched. Each container still has one or two unreadable pointer-like alternatives, the table has one zero identity, and `0xD8` contains small values. The exploration is read-only and produces no accepted subtotal or classification choices; automatic selling must continue to wait. Counts/identities could be incomplete, overlapping, unstable or misinterpreted. Do not claim the 17 ordinary slots are confirmed or exclude a bag by size alone.

## Verification and user test plan

The synthetic `Tools/WowBReaderTests/Experimental70790Tests.cs` covers distinct profile routing, full identity checks, candidate world snapshot, marker mismatch, mixed specialty/ordinary/unclassified bags, unstable subtotal, guarded session and action-bar revocation. It runs production code against synthetic memory, not a client. The 70755 and older fixtures remain. The isolated Roslyn synthetic reader and vendor-due fixtures passed; eight Python inventory tests, two build-default tests and four approach-structure tests passed. C# lint scripts passed except the existing `GreenMagic/LuaBridge64.cs:1752` heuristic; modified C# and XAML passed syntax/XML parsing. There was no full Windows/WPF build and no live 70790 test.

Run `python Tools/check_wow70790_image.py path/to/wow-decoded.exe` to verify the documented exact SHA-256, 14 marker byte strings, selected relative references and vtable slots; it passed against the supplied image and rejected the 70291 reference. This static check cannot prove runtime behavior or that other layouts match.

On Windows, build x64 with `dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64`. Attach to 70790 while stationary; inspect the log for local identity, snapshot, map, signatures and action bar. With auto-selling disabled, take a stopped Bag research snapshot; confirm each ordinary/specialty bag, compare its candidate subtotal to the visible ordinary free slots, then use a controlled non-stacking item and reverse the move. Only after those checks, test STOP, small movement, target/nearby scan, combat and loot, gathering, buffs, corpse recovery and reviewed vendor replay in safe, supervised steps. Do not treat a logged click as proof of a sale. If a guard refuses, a field is implausible, or behavior is wrong, stop and capture a targeted log rather than removing the guard.

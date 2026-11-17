# Experimental 1.60.1.70959 port

## Input and status

The supplied Drive archive (file ID `1Q8slwi3Ca988r68E-M5CmYkIdWlKOOGx`, `wow-decoded.zip`) is
34,689,931 bytes, MD5 `93b8f87873c9915d4af699f1069f2b5d`, and contains a single decoded x64 PE,
`wow-decoded.exe`, 80,689,360 bytes, SHA-256
`dd2f820f68a825cd1b3ab62c2efe7e1993bbe974be6537f3ad2312de9d3012df`, image base `0x140000000`,
build string `1.60.1.70959`. The archive and executable are **not** committed.

For comparison the previously supplied 70938 image was retrieved from the same Drive account
(`Wow-decoded.zip`, 35,181,465 bytes, MD5 `4657842fa8b59d89a37cade7b1b3e862`) and its SHA-256
`436a1b0dc17006895d46b1619dff45c84b683d37e2b24a458cdec333f2accb3a` matches the value recorded in
`hunt/WOW_70938_PORT.md`, so this port had the real old image available rather than only reports
about it. Build strings confirm the pair: `1.60.1.70938` → `1.60.1.70959`.

70959 is now the **default registered build** (`ClientAttachPolicy` prefers the highest registered
build) and the target for future changes. It has its own offset profile, build markers, inventory
markers, container markers and mail candidate table. 70938 and every earlier registered build stay
registered and selectable; unknown builds are still rejected. This is a **candidate port, not
evidence of feature parity**: no Windows build and no live-client test happened here.

## Method: carry the reference, do not shift the number

`Tools/WowBBuildPort/derive_build_anchors.py` (new) ports each recorded 70938 anchor by code
cross-reference instead of by applying a delta:

1. Scan the 70938 `.text` for every RIP-relative data reference and every relative call.
2. For a recorded target, keep the sites whose resolved displacement equals it, and export each
   site as a byte window with **all** displacements masked (the one being ported, any neighbouring
   RIP-relative or call displacement, since those move too).
3. Find that window in the 70959 `.text` — all occurrences, not just the first — and resolve the
   displacement again at each match.
4. Report how many independent sites were carried and whether they agree. Several sites agreeing is
   strong; one unique site is a single lead; disagreement or no match is reported as unknown and
   nothing is invented.

The procedure was validated before any 70959 number was taken from it: run against the 70938 image
it reproduced three values that were recorded independently — the action array `0x6D674C0`, the
container identity helper `0x257E080` and the mail attachment table `0x4E6CA90`.

`python3 Tools/WowBBuildPort/derive_build_anchors.py --selftest` exercises the mechanics on a
synthetic PE pair (a global and a call target deliberately moved); it passes.

## Candidates

| 70938 | 70959 | evidence |
|---|---:|---|
| PlayerGuid `0x4D2EAE0` | `0x4C28A30` | 6 independent sites agree |
| PlayerNamePrimary `0x4D2EAF0` | `0x4C28A40` | 1 unique site; keeps the recorded `+0x10` from the GUID |
| PlayerTargetGuid `0x6B467F8` | `0x6A407B8` | 59 sites |
| MouseoverGuidCandidate `0x4DEA308` | `0x4CE4258` | 6 sites |
| ObjectManager `0x6D48D78` | `0x6C42D38` | 30 sites |
| HandlePools `0x483CBE0` | `0x4736BE0` | 634 sites |
| MapRoot `0x6D48CF8` | `0x6C42CB8` | 174 of 175 unique sites; one outlier discarded. Keeps the recorded `+0x80` distance from ObjectManager |
| CameraWorldFrameGlobal `0x4B661F0` | `0x4A60170` | 219 sites |
| PlayerVtable `0x41F8070` | `0x40F23A8` | 2 sites |
| UnitVtable `0x420C0D8` | `0x4106190` | 2 sites; weakly inferred, as it was for 70938 |
| GameObjectVtableCandidate `0x41FFF48` | `0x40FA278` | 2 sites |
| StoredActionArrayCandidate `0x6D674C0` | `0x6C61480` | 36 sites **and** the marker's `lea` independently resolve to the same value |
| ContainerIdentityTableCandidate `0x6D6A500` | `0x6C644C0` | 11 sites |
| InventoryLookup `0x21796E0` | `0x207EF20` | 83 call sites |
| InventoryHandler `0x26E1230` | `0x25DDD60` | no call sites in either image; the recorded 15-byte prologue is unique in 70959 |
| PlayerGetterCandidateRva `0x1F6EC10` | `0x1E74780` | 2265 call sites |

Vtable identity is **not** established by these addresses; a vtable is a table of function
pointers and matching two referencing sites says nothing about which object type it belongs to.

### Member offsets: unchanged, and re-read rather than assumed

The supplied player-state member offsets (uploaded `newoffsets.txt`) were re-derived by porting the
functions that read them and comparing the displacement bytes **inside** those functions:

| field | offset | 70959 evidence |
|---|---:|---|
| CurrentTarget | `0x2C0` | same byte position in the carried attack-begin helper |
| AutoAttack | `0x2C9` | same byte position; `488B81C0020000` / `488D99C9020000` read back at `0x1EB5B70` |
| ShapeshiftFlags / flag | `0x2D0` / `0x10000000` | `F780D0020000...0010` read back at `0x231EAC0` |
| Shapeshift | `0x1EEF` | `4438A0EF1E0000` read back at `0x231EAC0` |
| AttackTarget | `0x1D40` | same byte position in the carried reader |
| StateFlags `0x250C`, AttackEffect `0x25C8` | | same byte positions in the carried state-flags writer |
| ComboPoints | `0x1E1D` | **weakest on this list.** The two recorded reader functions have no direct call sites in either image, so they were not ported. The `0x1E1D` field encoding still exists in the 70959 image (six `movsx` and four `movzx` forms), which shows the field survived but does not prove those readers are the ones used |

### Markers (image-relative, so each build needs its own)

| purpose | 70938 | 70959 |
|---|---|---|
| faction / health / power / action | `0x209774C`, `0x20C1C37`, `0x28A8105`, `0xA6017D` | `0x1F9D48C`, `0x1FC7547`, `0x27A2B65`, `0xA3695D` |
| container identity set | `0xD6C5BC`…`0x257FC04` | `0xD1ACBC`, `0xD1ACE0`, `0x247E886`, `0x247E894`, `0x247E8A0`, `0x247E8D6`, `0x247E8E9`, `0x247E8F4` |
| inventory probe markers | `0x257FBE6`, `0x257FBF9`, `0x257FC04` | `0x247E8D6`, `0x247E8E9`, `0x247E8F4` |
| mail attachment table / marker | `0x4E6CA90` / `0x26B4F78` | `0x4D66A60` / `0x25B1AA8` |
| cooldown research anchors | `0x1F51104`, `0x1F51133`, `0x1F51166`, `0x1F51364` | `0x1E56C94`, `0x1E56CC3`, `0x1E56CF6`, `0x1E56EF4` |

The 70938 faction marker's surrounding code changed shape, so the 70959 faction marker was placed
from the longest byte context that is still unique in the new image (99 bytes) rather than from the
recorded pattern; the field access itself, `mov edx,[rbx+0x1E54]`, is unchanged. All other markers
were confirmed by reading the recorded bytes back at the new address. One 70938 cooldown anchor
(`0x15DF02A`) is a 14-byte window whose 5-byte `E8` tail matches 29 places in the new image; it is
**not** portable and was deliberately left out rather than guessed.

`python Tools/check_wow70959_image.py path/to/wow-decoded.exe` checks the SHA-256, 21 markers and 5
relative references. **Executed** against the supplied image: pass. Run against the 70938 image it
fails on the hash, as it should.

## What changed in the code

- `Styx/Offsets/Wow70959Offsets.cs` (new): the candidates above, each with its evidence in comments.
- `ClientAttachPolicy`: `ExperimentalBuild70959 = 70959`, registered → it becomes the default.
- `WowBReader`: `IsExperimental70959`; `Uses70938Features` (70938 || 70959) for behaviour that the
  byte-level comparison supports; `Uses70888Features` and `Uses70862Features` extended;
  `ExperimentalBuildNumber`; constructor flag; all RVA chains branch to `Wow70959Offsets` first.
- `ObjectManager` selects the profile; `ProcessSelectorWindow` reads the 70959 player name.
- `WowBGrindFactions`, `WowBContainerResearchProbe`, `WowBInventoryProbe`, `WowBMailAttachmentResearch`,
  `WowBMailCandidateReader`: their own 70959 marker sets, tables and expected bytes.
- 87 behaviour gates that previously tested `IsExperimental70938` now test `Uses70938Features`, so
  every 70938 feature — Grind, Gather, combat defence, buffs, totem macro, skinning, vending, loot,
  death checkpoints, corpse recovery, ghost relocation, mail, Ghost Wolf travel, aura candidates —
  reaches 70959. Exact-build checks remain only where the value really is per-image (offsets,
  markers, tables). `Tools/test_wow70959_port.py` asserts that separation and fails if a behaviour
  gate ever goes back to exact-build.

Settings need no migration: bag classifications live in one shared file
(`Settings/clickaim-bag-kinds-70755.txt`, the name is historical) and vendor plans in
`Settings/clickaim-vendors.xml`; neither is keyed by build.

## Local checks actually run

- `python3 Tools/WowBBuildPort/derive_build_anchors.py --selftest` → pass.
- Same tool on the real pair (old → new) → the table above; 1,304,650 RIP-relative references and
  1,313,936 relative calls scanned in the old image.
- `python Tools/check_wow70959_image.py` on the supplied image → pass; on the 70938 image → rejected.
- `python Tools/test_wow70959_port.py` (new, 7 tests) → pass; `Tools/test_wow70938_port.py` and
  `Tools/test_wow70888_port.py` updated for the family property → pass.
- 79 of 81 Python suites pass; the two failures are the pre-existing missing-binary ones
  (`Tools/MapFiles/test_mapdump.py`, `Tools/NavMeshBuilder/test_legacy_mmaps.py`).
- Changed and new C# files parse (`Tools/CsParseCheck`). `Tools/lint_cs_types.py` on the new offsets
  file and `ClientAttachPolicy.cs`: no findings; on `WowBReader.cs`: two pre-existing heuristic
  findings unrelated to this change.
- The C# fixtures under `Tools/` were **not executed** — this environment has no .NET SDK. Their
  stub readers were given the `Uses70938Features` property so the sliced production code still
  compiles, but that is a source edit, not a test run.

## What is not established

No Windows compile and no live client. A resolved address is a code cross-reference, not a
validated field; an unreadable address is not a match. Nothing here proves object/unit identity,
vtable type, health/power/faction/level/ghost layout, aura completeness, bag identity or
specialty-bag separation on 70959 — those are inherited from 70938, where several of them were
themselves unvalidated hypotheses. Build 70959 is a different image size (80.7 MB vs 81.8 MB), so
not every function stayed in place; the container helper body in particular differs even though the
markers around it match.

## Supervised validation for the user

Build on Windows x64: `dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64`. Confirm the log
reports build 70959 and that the offsets were accepted. Start **stopped** in a safe area with
automatic selling disabled. Check attach, character name/GUID, map, coordinates and target; verify
select-free nearby scan; test STOP and a short movement before combat. Then Grind, Gather, loot,
buffs, death/release/corpse return, mail and background focus one at a time. Keep selling off until
stopped ordinary-bag snapshots match the visible slots and a controlled item move between an
ordinary slot and an identified specialty bag behaves as it did on 70938. If a value or guard
disagrees, capture a log rather than removing the guard: a logged attempt is not a success.

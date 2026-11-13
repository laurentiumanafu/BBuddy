# Experimental 1.60.1.70938 port

## Input and status

The supplied Drive archive (file ID `1VM3hp_fLx5u6BvnLvhO7zlWW14p-OMo_`, version 12) is 35,181,465 bytes, MD5 `4657842fa8b59d89a37cade7b1b3e862`. Its single decoded x64 PE reports FileVersion `1.60.1.70938`, image base `0x140000000`, and SHA-256 `436a1b0dc17006895d46b1619dff45c84b683d37e2b24a458cdec333f2accb3a`. The image and archive are not committed. This Drive ID was reused and the prior 70888 image was unavailable for direct comparison; the earlier 70888 anchor records and the decoded 70291 image (SHA-256 `8f864e73e73ad67319765063a7c491a420dbe2d0714ce427106e39cd9f109192`) were used instead.

70938 is the default registered build, with its own candidate RVAs, build markers, inventory markers, and exact-build identity. It uses the shared 70862/70888 feature and safety paths, including Grind, Gather, movement, combat defence, buffs and totem macro, skinning, vending, loot, and 70888-specific death checkpoints/safety pause and target-change guard. Earlier registered builds remain distinct; unregistered builds do not inherit this profile. This is a **candidate port, not evidence of feature parity**. Code was not built on Windows or tested against a live client. Most runtime member layouts are inherited, not validated by matching a few instructions.

## Static evidence

`python Tools/check_wow70938_image.py path/to/wow-decoded.exe` checks this exact SHA-256, 14 code byte sequences, 12 relative references/calls and selected candidate vtable slots. It confirms those bytes in this image only, not their semantics or the live image. Do not replace a candidate by applying a blanket delta: data sections moved by different amounts.

| Candidate | 70938 RVA | Evidence / limits |
|---|---:|---|
| Player GUID / name | `0x4D2EAE0` / `0x4D2EAF0` | GUID-size store at `0x1D75F20`, subsequent byte copies and name-selection code at `0x1D76B51`, matching 70291 context; semantic live check still needed. |
| Target GUID | `0x6B467F8` | Several matching 70291 code contexts including `0x5B871D`. |
| Mouseover GUID | `0x4DEA308` | 16-byte store at `0x21FB216` corresponds to 70291 mouseover code; hovering behavior not validated. |
| Object manager / pools | `0x6D48D78` / `0x483CBE0` | References at `0x2036CD` and `0x269152`; structure remains inherited. |
| Map / camera | `0x6D48CF8` / `0x4B661F0` | Multiple 70291-context matches, including `0x203520` and `0x2C8D23`. |
| Action array | `0x6D674C0` | Several 70291-context matches and indexed access at `0xA6017D`. |
| Player / unit / game-object vtables | `0x41F8070` / `0x420C0D8` / `0x41FFF48` | Exact code-window matches to 70291 method slots gave 26, 23 and 26 matching slot votes respectively. Shared methods and compiler changes mean votes do not prove object type; runtime identity still must be checked. |
| Inventory handler / lookup | `0x26E1230` / `0x21796E0` | Handler calls lookup at `0x26E1320`; matching context includes `+0x90` after the call. |
| Container identity table | `0x6D6A500` | Helper `0x257E080` is called at `0xD6C5BC` and accesses the indexed table at `0x257E0A3`. |

Gameplay markers: faction `0x209774C`, health `0x20C1C37`, power `0x28A8105`, action `0xA6017D`. As before, gameplay signature guard requires at least two readable matches and no readable mismatch; subsequent identity, map and action checks still apply. Inventory loops, capacity and special-index anchors retain the previous partial matching guard. A passing check does not establish health, combat, ghost, movement, pool, action-bar, inventory, cursor or aura layout correctness.

The 70888 aura header bounds (28 slots/36 capacity) and speculative effect mapping carry over to 70938 with **no 70938 trace**. Pointer semantics, completeness, effect IDs and macro execution remain unverified. Combat defence remains a best-effort heuristic and can select a friendly, totem or unrelated NPC. The safety checkpoint never treats unreadable as death; a recovered corpse requires the guarded saved checkpoint and confirmation. None of this proves PvP death handling works on this build.

## Supervised validation

Build on Windows x64: `dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64`. Start stopped, verify attach, character name/GUID, map, coordinates, target and nearby scan. Check STOP and short movement before combat, then test Grind, Gather, loot, buffs, death and corpse return separately. Keep automatic selling disabled until stopped ordinary-bag snapshots match visible slots and a controlled item move; supervise any recorded vendor clicks and independently confirm the sale. If values or guards are wrong, stop and capture a log rather than removing guards. A log of an attempted action does not establish success.

## Local checks

The decoded image passed the exact-image checker; the 70291 reference was rejected by its hash check. Python source-wiring and offline analyzer suites passed. The new C# synthetic fixture covers build routing and reuses the guarded death-checkpoint and inventory scenarios, but it was **not executed** because this environment has no `dotnet` SDK. Changed C# files were syntax-parsed, and the available C# heuristic linters reported no new findings; `lint_cs_types.py` still reports its pre-existing heuristic finding in `GreenMagic/LuaBridge64.cs:1752`. These checks do not replace a Windows build or live testing.

## Gather stuck report: 2026-11-13

In `2026-11-13_0847_336.log`, the gatherer was key-steering and had already walked and attempted a Copper Vein. At 09:25:14 the three fixed 7 yd perpendicular sidesteps were refused by mesh validation (one ray reported `hit=0.6533313`, the others could not be validated). The route later reported that no click had been dispatched; that message was misleading for key steering, which normally sends no clicks. The next resource approaches failed from the same area, each node was put on retry cooldown, and the hotspot-only relocation routine was never invoked. The log does not establish the physical obstacle, actual key delivery, or whether any candidate on the real mesh is reachable.

Mesh-owned sidesteps now stop first and search bounded 7, 4 and 2.5 yd points with varying lateral, diagonal and reverse bearings. They send movement only for a freshly revalidated mesh segment, and check STOP, route interruption, attachment, position and facing; they turn in place and stop on measured deviation from the validated segment. A jump is attempted after forward is held, not before turning. Attempted escape destinations are not repeated within the same walk, and a step is only reported as making progress if the position changes by at least 0.75 yd; exhausted searches return to the route owner sooner. Mesh validity is not proof of physical clearance, and no unsafe blind escape is attempted if all candidates fail.

Standalone GatherBuddy now also tries the existing up-to-five mesh-validated relocation locations when a resource approach fails, without letting the same resource immediately interrupt that relocation. STOP, death, map, attachment and applicable combat checks still run. An arrived alternate must be followed by a fresh eligible same-GUID/position check before retrying the old node location; normal in-range checks remain before any interaction. After three failed resource approaches and relocation attempts ending near the same place, Gather stops rather than cycling indefinitely. If no safe mesh candidate can be found, it still cannot guarantee escape. Key-steered stalls do not blacklist a polygon based merely on the absence of clicks.

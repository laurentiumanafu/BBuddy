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

## Ranged defence report: 2026-11-13 09:36

In `2026-11-13_0936_13604.log`, GatherBuddy entered node defence at 09:43:19, fought several selected units logged at approximately 3.6–3.7 yd, and by 09:43:38 had lost the last selection. From 09:43:41 until STOP at 09:44:00 it repeatedly reported that no nearby defensive TAB selection was eligible and held position. The log does not capture the alleged ranged monster's GUID or distance, so it cannot establish which unit attacked, that the monster was selected at that instant, or that a particular chase would have succeeded.

The selection logic could repeatedly exhaust nearby TAB attempts without considering a selected non-preferred remote NPC, and the old remote probe was available only when there were no nearby live/unreadable NPCs and the normal-pull faction filter was enabled. On 70938 only, after preferred-faction and nearby selection checks are exhausted, defence may consider a currently selected live NPC between 12 and 40 yd or try a bounded, rotated list of remote candidates. A candidate only guides facing: two stable snapshots for a current selection or an exact live TAB match after fresh identity checks are still required. Nearby preferred or unreadable NPCs prevent this fallback. A non-preferred nearby NPC may still be the real attacker, and TAB, faction, combat state and proximity do not prove hostility; this best-effort defence can therefore choose the wrong NPC, including a friendly.

The existing combat movement uses a complete validated mesh path, rechecks attachment, combat, selection, identity and STOP at each guarded step, and stops/reconsiders if the selected target changes, combat ends, a nearby preferred/unreadable unit appears or a path cannot be validated. The normal pull faction filter and pre-existing preferred-target priority are not removed. The fallback is not a guarantee of escape or of finding the attacker, especially if the client cannot TAB-select it or it is outside the 40 yd bound. Unsuccessful checks now include a fresh selected GUID/distance/field summary when available.

## Mining skill and remaining-node report: 2026-11-13 09:59

`2026-11-13_0959_12720.log` recorded the manually configured mining skill as 150 at 10:21:38, 140 at 10:25:33 and 125 at 10:26:07. Gold Vein (entry 1734, GUID `0x2C6B40000001B18000459F000076BA44`) was nevertheless selected at those settings. The shared WotLK node table lists Gold as 115, so the skill comparison was working against the wrong game's requirement. At skill 150 the bot posted four interactions and kept observing that same GUID, then imposed only a 20-second retry; it later selected the same GUID again. At skill 125 it began a hover search but STOP prevented a click. The log does not prove whether a cast or loot succeeded.

For 70938, common mining nodes now use Classic-era candidate thresholds rather than the shared WotLK thresholds: Copper 1, Tin 65, Silver 75, Iron 125, Gold 155, Mithril 175, Truesilver/Dark Iron 230, Small Thorium 245 and Rich Thorium 275. See the [Classic Era guide](https://wowforever.be/tradeskills/classic/mining/) and [Classic mining reference](https://classic-wow-archive.fandom.com/wiki/Mining). These are external references, not a live read of 70938 game data; unknown entries keep their existing table value. Herbs and other builds retain their existing thresholds. The user's manually entered value continues to be used; the bot does not read the profession skill from the game.

At the user's updated request, for 70938 only, allow five posted interactions, each followed by a completed uninterrupted wait and successful scan still observing the exact GUID, before blacklisting it for the current Gather or Grind-gather session. The count is per GUID across short retries; a scan that no longer observes the node clears its old count. STOP, combat, attachment/map changes, hover misses and unreadable scans do not count as a completed attempt. The blacklist is by GUID, not entry or position, and is cleared when a new gathering session starts; it is not a permanent profile blackspot. A remaining node does **not** prove a failed harvest: it could be a multi-hit vein or the client might keep the object visible. Other builds retain their prior bounded four-attempt behavior.

## Mining cast research and wait policy: 2026-11-13

Two 25-second 70938 Combat research recordings, labeled `mining cast` and `no mining cast`, completed 100 samples at approximately 250 ms each. The mining trace has four candidate cycles: player `+0x03C0` begins changing at about 2.25, 6.76, 11.01 and 15.51 seconds, with matching `+0x03EC` and timing pair present by 2.50, 6.76, 11.26 and 15.51 seconds. Both ID-like words carry decimal **3564** and clear around 5.76, 10.01, 14.26 and 18.77 seconds. The `+0x0418/+0x041C` pairs differ by 3200, and `+0x03E8` increments once per cycle. Split starts show that samples are non-atomic. The idle trace had no changes at these offsets; its changes at `+0x0474` and `+0x1C10` also appeared in the mining recording. Historical sources identify [3564 as Expert Mining with a 3.2-second cast](https://vanilla-twinhead.twinstar.cz/?spell=3564), consistent with but not proof of the field semantics.

In `2026-11-13_1051_3980-2.log`, this same candidate was logged for **all 10 posted interactions** across three Tin Veins (2, 4 and 2 interactions) and one Iron Deposit (2 interactions). Each of those four GUIDs was no longer observed after its last wait. The final Copper Vein's hover search timed out without posting a click, and the log ends during the old fallback wait: it supplies no Copper cast result. This log records whether the candidate appeared during each six-second wait, not the exact start, clear, completion, or loot.

For 70938 ore interactions, the experimental read-only observer now drives a bounded wait: require an idle-like baseline, check for the exact ID-3564/3200 pair at 250-ms intervals, then wait for two idle-like reads and an additional one-second loot grace. A two-and-a-half-second all-idle start window is a **candidate absence**, not proof of failure. Three such posted interactions with safe, uninterrupted waits and the same node still observed session-blacklist that exact GUID. Completed no-start counts survive interruptions; a detected cast, uncertain result or missing node clears them. If a cast candidate is detected and clears, the existing five-still-present interaction limit remains, because a vein can take multiple hits. These policies can blacklist harvestable nodes due to missed observations, lag, a different mining rank or multiple hits. They do not confirm the cast was caused by the click, completed, or yielded items; the fixed one-second loot grace does not guarantee auto-loot finished. A 12-second active/uncleared limit defers without counting a failure. Herbs and other client builds retain their old waits and handling.

The observer double-reads uncached candidate fields and rechecks session, player GUID, backpointer, record and vtable. Partial, unstable, unreadable and other candidate cast-cluster states are unknown, never a no-cast observation: unknown cases use the prior six-second fallback and do not count toward the three missing starts. STOP, combat, map, attachment and relevant Grind/Gather checks interrupt the wait. Polling may miss short events; identity reads are not atomic. Instant spells, other ranks, skinning, channels, interruptions and natural completion remain unverified. The old `WoWUnit` casting offsets are 3.3.5a offsets, not proof of 70938 semantics.

For 70938, a bounded hover search miss or a hover-away GUID that remains on the node causes up to three separate alternate approaches. Candidate positions around the node require a complete validated mesh path, a real change in bearing, a fresh in-range same-GUID scan, a safe face, a readable idle-like cast observation, and repeated STOP, combat, attachment, map and mode-specific safety checks. The original viewpoint and each arrived alternate are searched; if all three alternate searches also miss and the GUID is still observed, it is session-blacklisted. A failed mesh query, no validated alternate, unreadable state, interruption or lost node causes defer/return, not a hover-failure blacklist. Mesh and viewport checks cannot guarantee physical or visual clearance. Other builds retain their previous hover handling.

Offline reproduction: `python Tools/analyze_combat_trace.py MINING_TRACE --control IDLE_TRACE --spell-id 3564`. `Tools/WowBGatherCastTests/run.py` and `Tools/GatherHarvestTests/run.py` provide synthetic fixtures when .NET 10 is available; neither is a live test.

## Ghost Wolf travel observations: 2026-11-13

Two 70938 Combat research recordings, `no ghost wolf` and `ghost wolf`, each have 100 completed samples from the same player and the same first bar; slot 9 is raw spell ID 2645. The no-wolf control included four mining-like casts and had 19 occupied external aura-candidate slots, with no ID 1238640. In the wolf-labeled recording, at approximately 2.003 s candidate slot 3 gained ID **1238640**, active count increased 19→20, and that candidate remained through the end of the recording. Player `+0x03C0` briefly carried 1238640 just beforehand. The same code in the [Forever spell database](https://www.wowhead.com/forever/spell=1238640/ghost-wolf) is named Ghost Wolf; 2645 is the first-bar spell. The trace does not itself record keypresses, whether the character was visually in form, a cancellation/removal or the start and end of a normal three-second cast. Both runs are from one character/session; the array layout and candidate absence are still hypotheses.

The opt-in **Use movement skill when travelling** checkbox and selected first-bar slot now also cover Ghost Wolf on **70938 Grind hotspot legs**, including profile-free roaming, and continue covering standalone GatherBuddy travel to resources and hotspots. A 70938-only controller recognizes first-bar spell 2645 or 1238640 and uses the externally/inlined aura candidate ID 1238640. It validates the full bounded slot/count representation twice, rechecks local identity, map, session, health, ghost, combat, cast-like fields, bar and the configured action, and does not press the form while candidate presence is observed or state is unknown. No Lua, target changes or client writes are used. Non-wolf travel abilities and earlier builds retain their previous behavior; this does not add Grind movement-form handling for those abilities.

When a stable candidate appears absent and at least 100 candidate mana is available (an estimate based on the trace and [Forever spell listing](https://www.wowhead.com/forever/spell=1238640/ghost-wolf)), it stops, rechecks, posts the configured key, holds still for up to four seconds, then checks again. STOP, combat, death/ghost, map, bar, session and Grind's fight/vendor priority can interrupt the hold; attempts are limited to one per 15 seconds. While travelling, the route checks at most every three seconds and yields to the existing outer loop if a fresh absent candidate and enough mana suggest reapplication is needed. Unknown state, insufficient mana, combat or an unsupported slot simply withhold the key; movement can continue without Ghost Wolf. A missed/incorrect aura reading could still lead to a form being toggled off or left inactive. A present aura ID is not proof of visual form, speed or success. For 70938, Ghost Wolf is excluded from the generic timed buff scheduler even if manually marked Buff, so the movement checkbox exclusively controls automatic attempts; this prevents the two controllers from toggling it against one another. Restart the bot after changing the setting. The action bar must still pass the existing guard after shapeshifting.

### Release transition after death

In a drowning log the checkpoint was saved and one foreground Release Spirit
click was posted, but the ghost candidate was not confirmed within 30 seconds.
A posted Windows message is not proof that the client accepted it; that log
cannot distinguish an ignored click from an unreadable/incorrect ghost flag.
The 70938 experimental path now records bounded state/read diagnostics while
waiting. After a clean button-down/up post it may make at most two additional
attempts, five seconds apart, only after repeated dead observations and a fresh,
unique, fully guarded release-dialog validation. An unknown or alive observation
permanently vetoes retries during that transition; an unconfirmed button-up also
vetoes retries. A missing dialog or failed guard never posts a click and vetoes further retries. No corpse
movement is permitted until the ghost candidate is confirmed. Other builds retain
the single-click policy. These guards and the inherited ghost candidate require
live validation; underwater checkpoint positions and mesh availability can still
prevent a subsequent return, which this particular log never reached.

### Totem macro blocked by a grown aura header

In `2026-11-13_1317_6764.log`, Grind had combat buffs enabled and the key 2
macro configured for Mana Spring (two presses). It repeatedly withheld the
macro because the inline header had length 29, capacity 36 and the external
reader's inherited 70888 limit was 28. The earlier drowning log shows the same
rejection after earlier macro attempts. The user's 70938 recordings captured
external 26-slot buffers with capacity 36; the 29-slot report records a header,
not the corresponding bytes or proof of completeness. The earlier GatherBuddy
log reports macro attempts but cannot prove delivery or totem casts.

The 70938-only experimental bound is now 29 slots (not the full capacity 36);
older build limits are unchanged. The full readable region and header still
must be stable, and the nonzero, unique candidate IDs must match the separate
active count. Missing, unstable or inconsistent data is unknown and must not
trigger a macro or a potentially toggle-like Ghost Wolf key. With valid data,
existing combat, mana, identity, selection, STOP, Earth reservation and retry
checks still apply. The same candidate reader also serves shield and 70938
Ghost Wolf decisions, so the widened bound affects those features too. A
stable 29-slot layout and effect absence remain unverified until traced live;
this change is not proof that the macro, aura mapping or casting works.

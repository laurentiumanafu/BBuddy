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

### Hotspot exhaustion and death watch

In `2026-11-13_1330_952.log`, the route failed at 14:42:46 and Grind
immediately ended after its fourth consecutive failed hotspot. There is no
zero-health sample, checkpoint or dialog observation in that ending, so the
log alone cannot establish when the reported PvP death occurred or why death
was not seen during the route. A failed route is not evidence of death.

For 70938, four failed hotspots now release held movement and keep the run
alive in a no-input death watch, polling the local HP/ghost checkpoint tracker
until it produces an eligible checkpoint, STOP, attachment/session loss or the
configured run time expires. If alive or unreadable, it holds position instead
of walking the same failing ring; it does not fight while in this hold. A
confirmed checkpoint transfers to the existing recovery path, which checks the
release dialog, map, player identity and mesh before release and requires a
confirmed ghost before corpse travel. After successful revival, the hotspot
failure count resets. A zero-health read without the existing recent matching
alive evidence cannot invent a checkpoint: the watch remains paused and logs
why. This is experimental; it does not guarantee release, dialog recognition,
ghost detection or corpse navigation. STOP remains authoritative.


## Grind merchant identity after restart (experimental)

The 15:59 log shows the recorded town and three-step plan loaded and a 991 yd
mesh trip to town; the later lookup found no exact confirmed live GUID. The log
does not contain the saved GUID or the NPC's current GUID, so it does not prove
whether the low half changed, the NPC was absent/unreadable, or another guard
failed. The saved confirmation count is not proof that a current GUID matched.

70938 now prefers the exact recorded full GUID, and only if none of the saved
GUIDs is currently observed may it use a single non-player candidate whose
high 64 GUID bits match a saved GUID, within the configured search radius of
both the recorded town and player, and within 5 yd vertically of town. It
counts even unreadable or dead matching candidates as ambiguous. The live GUID,
object/record, map, combat and uniqueness are rechecked on approach and replay.
Other client builds remain exact-only. The high-half correlation and merchant
identity are unverified; a different NPC could share the high bits, so supervise
and verify the first interaction and sale. The recorded plan still replays
screen clicks, and the merchant window and item sale are not verified. The
failure log now includes saved and nearby non-player GUIDs for diagnosis;
matching is limited to client-loaded snapshots. Existing confirmations require
no re-recording. Moving a town point intentionally clears its confirmations.

## Riverglades mesh diagnosis

The `2026-11-13_1922_9296.log` reports candidate map 0 and player position
`(-9196.181, -4799.7695, 69.073)`. Detour's read-only experimental query found
no polygon within its 5 yd horizontal / 40 yd vertical search box, and
profile-free Grind refused to start without a verified nearby mesh layer. The
startup inventory's map 1 is only the configured NavGrid default, not a live
map read; its 988 files do not establish anything about map 0. The log did not
report which map 0 tile files exist, load, or contain geometry.

Using the existing loader tile calculation, that position lies in `(49,40)`,
about 0.23 yd from the `(49,41)` boundary. Depending on naming convention the
center file is `0004940.mmtile` or `0000_49_40.mmtile`; neighboring tile files
can matter at the border. The candidate map and position layouts remain
experimental. Missing tiles, incompatible params/tile format, mismatched game
terrain and holes or filters are all possible; this log cannot choose among
them. Do not mix separately built mesh sets under one map or bypass the mesh
check to force movement.

Startup's count now explicitly identifies the configured default as such. If
the experimental probe finds no polygon, or profile-free roaming is refused
with a stable map, read-only diagnostics log the candidate map and position,
map parameter filenames and lengths, 3x3 tile filenames and lengths, and
native loaded flags/counts. Existence or loading still does not prove correct
or walkable geometry. These changes diagnose but do not create or repair a
mesh, and require a Windows build and live verification.


### Supplied Riverglades mesh: version-16 map header

Drive `1MgVb1dK3WA0U2x8ZK7YiUKcDwFn_l8yw` ZIP MD5
`4fae3da57f49eba61bd6411170b293a0` contains `0000.mmap` and two tiles matching
the recorded lengths. SHA-256: map params
`11f29fcf6940331baab4221832dd3c60cafd7b8946d3f7503bbfaffc66695226`, tile
`49,40` `f68c358b03d0854c0b3f043553d6fd95afbde49487deded1fbad314f0b3c906e`,
tile `49,41` `9a0c340ee409cad9b78d74af5fdaefbd7c15e5b5d440b9e7a1f5b4c9fd357eb2`.
The 40-byte map file begins with MMAP magic and version 16 before the 28-byte
Detour parameters, with four extra trailing bytes. The prior loader read the
header as parameters and did not check the read or bound the allocation.

A common bounded parser now handles the old bare params or this observed v16
header, validates finite dimensions and tile count, and rejects unknown versions
or truncated data before initialization. Detour uses 64-bit refs and ignores
the captured maxPolys sentinel `0x80000000`. The updated x64 Navigation DLL is
cross-compiled and export-checked; it has not been exercised on Windows here.
The user's original files were not changed. The Linux checker built from the
same Detour and parser accepted both supplied tiles and snapped the 19:22 and
19:30 player coordinates to nearby polygons. On-demand query between the two
positions returned a complete 45-polygon, 241.4 yd path; this is an offline
mesh result, not proof of live movement. Legacy and versioned synthetic fixture
coverage is separate from Windows/client validation.

Separate audit note: the native optional sliced-path API treats map ID 0 as an
inactive sentinel in update/finalize. No callers of that API were found outside
its managed wrapper. This is not evidence for the nearest-poly failure and was
not changed by the map-header fix; if a future feature uses sliced paths on map
0, it needs a separate active-state fix and tests.


### 21:28 corpse-recovery report (70938)

The log recorded a death at `(-9179.1,-4532.8,16.2)`, release, a ghost
route, four unproductive scout attempts and a fallback that stopped without a
click. The fallback's old "prompt not visible" message was misleading: it
also printed when its ground/state guard failed before any image check, and
the log does not identify which guard failed. The user confirms the prompt was
visible and provided a horizontally cropped screenshot.

Offline Detour queries on the previously supplied version-16 `0000_49_40`
mesh returned polygon area **11** at the logged corpse position and at each
cardinal 8, 16 and 32 yd sample. The existing recovery ground guard accepted
only 0, 1 or 4 and therefore rejects these particular queried polygons. Trinity
v16 `MMapDefines.h` (TrinityCore commit `49e6ed29efbb7689a04a2830c811bd5a6848a907`,
`src/common/mmaps_common/MMapDefines.h`) identifies 11 as ground, 10 as steep,
9 as water and 8 as magma/slime; in the older HB enum, 11 instead denotes Alliance portal. The
actual stopped ghost position and installed tile hashes are not recorded, so
this is a concrete incompatibility consistent with, but not proof of, the
specific failed check in the log.

Recovery now permits 11 only when the map parameters and the selected loaded
single tile both match the observed version-16 format, using the same native DLL
folder and classic-before-modern filename priority. Near tile boundaries,
unknown or mixed formats, inaccessible files, other v16 area IDs and unloaded
tiles fail closed. Bare legacy parameter maps retain the prior 0/1/4 policy.
The ghost/session, map, ground-snap/range, NPC preference/four-attempt policy,
visual prompt, hover/focus/STOP, and alive-confirmation guards remain. No map
files should be changed or mixed while the process is running. Other roam and
relocation area policies have not been changed by this corpse-specific fix.

An offline independent replay of the pixel matcher found unique heading and
Accept candidates from the unscaled screenshot crop on a synthetic client
canvas. The crop lacks full client geometry and is not a live capture or click.
The C# fixture includes that anonymized crop and format-policy cases; C# tests,
Windows build and in-game recovery were not run here. The fallback log now
separates a failed safety check from an inspected frame without a matching
button; ground-query failures report throttled reasons.

### Badlands profile-free roaming stop (23:50 log)

`2026-11-13_2350_23308.log` shows map 0 tiles and paths working. At 23:55
and again 00:06, the client released, returned, accepted resurrection, and
reported alive/action-bar confirmation before Grind resumed. At 00:03:25 and
00:07:00, Grind stopped after the profile-free sampler failed to choose a local
mesh destination five times. The old sampler did not log which of random query,
anchor/distance, snap/layer, area or format rejected those samples, so the log
does not prove a particular rejection reason. In the later run, those five
failures were not consecutive: earlier failed samples accumulated across combat
and a subsequent death/revival.

Corpse recovery already used the version-aware ground policy, but MeshRoam and
MeshRelocate still treated the polygon area as a legacy HB enum. In particular
those paths rejected area 11 unconditionally even when the selected map and tile
are the captured Trinity v16 layout where area 11 denotes ground. Both now use
the same format-aware loaded-tile check as corpse recovery; this does not treat
11 as ground for bare-parameter legacy maps, and unknown/mixed files, water,
lava, border ambiguity and failed mesh checks are still refused. Bounds,
map/session checks, STOP, valid snap, recent-spot avoidance and complete route
validation remain. We have not inspected the actual Badlands tile contents, so
whether that incompatibility explains the specific sampling failures is not yet
confirmed. The sampler now reports rejection counts and area IDs when it finds no
destination. A completed fight or confirmed resurrection resets the Grind roam
failure streak; five fresh failed sampling rounds still stop rather than moving
without a verified destination. C# fixture code models both formats, interrupts
and relocation, but no .NET SDK/Windows client was available here to execute it.

### 07:59 Badlands follow-up: missing sampler diagnostics

`2026-11-14_0759_17548.log` shows a profile-free map 0 run continuing through
16 fights, with movement/mesh paths and unverified loot/skin interactions, then
five failed local-destination attempts at 08:10:22–08:10:30. It contains no
`sampling rejections`, `sampling unavailable`, or `mesh sampling failed`
details from the 2ed0f75 sampler. This is consistent with either an older
running build or an unlogged early return in that sampler (player X/Y or Z
unreadable, or a transient reader/map check); it does not prove which.

Add an explicit format-aware sampler revision marker when profile-free roaming
starts, and log each formerly silent X/Y, Z, or transient map/reader early
return. The checks remain fail-closed and do not authorize movement on an
unreadable position, unknown map, non-ground polygon, STOP or invalid snap.
This change is diagnostic only: it does not establish the reason for the latest
failure. Confirm the new marker is in the Windows log and capture the next
failure's rejection details before changing more navigation behavior.

### 08:40 Badlands: corner following near a ravine

The 2026-11-14 08:40 log contains the guarded-input-diagnostics-v1 sampler
marker. The user reports repeated falls at the same ravine edge, followed by
mesh loading and escape. The log does not record the exact edge/corner or prove
whether the cause was waypoint advancement, steering, mesh geometry, or a
collision mismatch. Several legs and chases have multi-point paths.

The key-steered mesh route used a 2.5 yd intermediate arrival radius and retained
forward movement across corners; combat/pool chases advanced within 2 yd. Both
could cut inside a corner. For directional bends of at least approximately 10
degrees, use a 0.9 yd corner tolerance and release forward at the corner; steer
in place on key-steered mesh legs and chases when facing needs correction. Keep
ordinary intermediate/final tolerances, combat/STOP and facing guards, and
restore the mover flags after the leg. Intermediate corner braking only releases
the key, avoiding the click-walk cancellation tap. This can add pauses, and
inaccurate positions or delayed input may still result in deviation.

This is not a clearance-aware path planner: Detour's path may itself hug or
cross a hazardous edge, and mesh rays don't prove physical cliff clearance.
The change is an experimental reduction in corner cutting, not proof that the
reported pit is safe. Observe a supervised retest, particularly the same ravine;
if it still occurs, save the log and coordinates or a screenshot. Do not claim a
Windows or live-game validation from source/fixture tests.

### 2026-11-14: skinning cast recording ended on target change

The user requested waiting for skinning cast completion instead of the current
six-second post-click hold. `20261114-085052-3d11bab3a555419786bb14fa2245d5c6.txt`
is labelled skinning cast but ends at sample 2 (0.499 s), because the target GUID
changed; the next sample was discarded. The observed player mining-candidate
cluster (+0x3C0/+0x3EC/+0x418/+0x41C) is zero in its baseline and has no
changes in the three retained samples. The analyzer reports zero cast-like
patterns. The separate `20261114-085116-6ed2fa4cf5bb47c6ace0c5f867d241b9.txt`
control ran 100 samples. The first trace neither captures a demonstrated cast
start/clear nor establishes that skinning uses those mining offsets. Do not
infer a skinning spell ID, duration, completed cast, or skin loot from it.

Combat Research now offers an opt-in, default-off player-only trace mode. It
records no selected target window and allows selection changes, while retaining
build, attachment, player identity, stopped-bot, action-bar and cancellation
checks. The original target-tracked mode is unchanged. Collect a new full trace
with a manually initiated skinning cast and a quiet control, preferably another
cast for comparison. The gameplay skinning wait is intentionally unchanged
until a reproducible skinning start/clear candidate is supported by evidence;
even an observed clear does not prove cast success or loot.

### 2026-11-14: 70938 single skinning cast candidate

The player-only `20261114-090130-e060f397e03e4f38af627879a9372e01.txt`
recorded 100 samples. In the one manually reported skinning interaction, at
6.00 s the player +0x3C0 and +0x3EC words became 10768, while +0x418 and
+0x41C became 0x00A8D1A8 and 0x00A8DD60 (difference 3000). The four fields
cleared at 9.01 s. These transitions were absent in the previous no-cast
control. This is one trace, not authoritative proof of spell identity, success,
interrupt behavior, other ranks, haste, items, or offsets across builds.

The 70938-only cast observer now recognizes that exact double-read pattern and
keeps other or partial clusters Unknown. Post-loot and nearby skinning require
a stable idle/player baseline before the guarded click; after it they watch for
the same candidate, then a stable clear following at least 2.6 s of observed
active time and a 1 s loot-settle hold. Any uncertain reading, early clear or
candidate change prevents a completion inference and retains the six-second
fallback once the candidate is clear. If an observed candidate never clears,
the wait is bounded at 12 seconds and selection cleanup is withheld; that limit
does not establish that the cast stopped. Other builds and unknown baseline use
the old six-second behavior. Combat, STOP, map/attachment, death and vendor
guards remain active during the wait. Candidate clearance is not proof of
completed skinning or received items; this needs a supervised live test.

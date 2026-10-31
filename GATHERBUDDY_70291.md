# Gatherbuddy on wowb.exe 70291: offset audit

## Status

**Not functional yet on 70291.** The existing 3.3.5a Gatherbuddy is not an x64
implementation: its behaviour tree uses the legacy `WoWGameObject`, `WoWUnit`,
`StyxWoW.Me`, targeting, loot-frame and bag wrappers. The wowb UI intentionally
does not start that tree. Do not select it or call its old native functions on
this client. The legacy implementation is unchanged.

A ground-only Gatherbuddy can reuse the 70291 profile parser, `RouteRunner`,
mesh routing and its dynamic stuck-point blackspots, the action-bar combat code and Stop. Profile-defined blackspots need a separate audit; they must not be assumed to work merely because dynamic blackspots do. The
missing observations below must be established before a resource-focused loop
can safely decide to walk to and right-click a node. Walking a profile without
detecting nodes, or using Grind's nearby-mob interrupt, is **not** gathering.

## Required observations

| Need | Current 70291 status | Why it matters |
|---|---|---|
| World object enumeration | Handle pools and full 128-bit GUID records are available; `WowBWorldSnapshot` intentionally keeps only verified unit/player vtables. | Need a validated game-object snapshot, not the unit-only snapshot. Pool membership alone is not a node. |
| Game-object identity and position | Two copper veins and a Peacebloom matched `record+0xD8`, position `record+0x60`, and vtable RVA `0x50B1838`; 4-byte object alignment is possible. Other resource types and lifecycle still need validation. | Detect nearby resources and route to them; revalidate full GUID immediately before interaction. |
| Resource entry/type | The copper and Peacebloom samples agree on `object+0x90` and `(GUID.High >> 6) & 0x7FFFFF`, with legacy IDs 1731 and 1618. Other entries, cache and lock types remain unverified. `WowBGuid.EntryId` is not verified for modern game objects. | Avoid treating doors, mailboxes or friendly NPCs as resources. `Bots/Gatherbuddy/GatherableNodes.cs` supplies legacy IDs and required skill levels, but IDs and coverage must be verified for the current client. |
| Gatherability / depleted state | No verified game-object state, dynamic flags or lootability offset. | Avoid approaching depleted/inaccessible nodes indefinitely; temporary GUID/position cooldowns and a bounded interaction attempt can supplement, not prove, success. |
| Profession and skill level | No verified 70291 mining/herbalism profession/skill offsets. Legacy `WoWGameObject.CanLoot` relies on legacy skill, object cache and Lock.dbc. | Choose only trained professions and attainable nodes. A UI-selected profession and skill level can be a fallback if clearly labelled manual; never guess from unverified memory. |
| Interaction / outcome | No verified node mouseover identity, interaction address or gather/loot-frame state. Candidate mouseover RVA `0x6283898` is **unverified**. Ground right-click projection is approximate and can issue a movement order when it misses. | A safe window-input-only gather requires a verified hover/identity or another reliable positive check, stop-and-settle, and bounded retry/backoff. A sent click is not proof of gathering. Do not use Lua or injection. |
| Combat / defence | Player `+0x1E60` bit `0x80000` matched one labelled idle → combat → idle cycle; other combat situations still need testing. `0x800` is present while idle. Hostility/reaction and the attacker's identity are not established. | Stop gathering to defend only when a real attacker can be identified; recent health loss is an imperfect fallback, not proof that the nearest mob attacked. Absorbs, heals and the first hit can mask it. |
| Bags / vendor | 70291 bag contents and counts are unresolved; recorded vendor clicks work when configured by the user. | Full bags and the presence of a tool cannot currently be detected reliably. |

## Read-only data collection from the window

With wowb attached, stop the bot and stand near a visible **herb**. Press
**Gather probe**. The probe first logs a bounded census of nearby structurally
matching non-unit pool records; after that it announces an eight-second hover
window. Move the cursor over the herb in the game and leave it there until the
probe finishes. It never clicks, moves, writes game memory, or uses Lua. Repeat
with a known **ore node** and then with **empty ground**, ideally without moving
the character. Save/share the log and identify which sample was which. The
mouseover candidate, any raw words, candidate vtables and locations are
unverified hypotheses, not a functional gather detector. If the candidate is
empty, stale or not in the pool, the census still helps and the candidate must
not be promoted to a real offset. Header validation may miss differently
aligned or differently structured objects.

Once object identity, position and a usable resource classification are
validated on multiple examples, the remaining implementation is a separate
70291 gather loop: load the profile's hotspot order; check for nodes at and
between mesh legs; use the same navigation and blackspots as Grind; defend on
credible attack evidence without proactively grinding; stop and interact with
eligible herbs/ores; and retry/back off without declaring success on a click.
The interaction, skill, depletion, defence and bag limitations must remain
visible until validated on the live client.

## Combat fallback in this change

The previous `InRealCombat` checked the player's `0x800` bit and then treated
any nearby unit with that same bit as fighting. The repository's own samples
include idle mobs with `0x800`, so that combination can stick true forever.
The earlier fallback sampled health loss. The labelled 18:02 probe now provides
a stronger player-only candidate: `+0x1E60 & 0x80000`. Grind uses this candidate
for rest and vendor guards, and treats an unreadable flag as unknown/unsafe.
This is still one controlled cycle, not proof for every class, pet, target or
PvP state, and it cannot identify the attacker.

## Copper and herb samples (user report, 17:53–17:55)

These are promising **candidates**, not yet an automated gathering contract:

* Hovering a copper vein changed `0x6283898` from zero to the full GUID
  `2C0118002001B0C0:0000000000001295`. The matching fam3 record was 1.8 yd
  away, at the reported vein position, with object vtable RVA `0x50B1838`.
  The dword at object `+0x90` was `0x6C3` = **1731**, the legacy Copper Vein
  entry in `GatherableNodes.Minerals`.
* The reported herb resolved to GUID
  `2C01180020019480:0000000000001282` and record position
  `(-237.70, -4733.99, 30.96)`. Its mouseover value was **already set before**
  the hover window and unchanged afterwards; that does not independently prove
  the pointer is a live mouseover, nor that the object was the herb.
* For these two examples `(GUID.High >> 6) & 0x7FFFFF` yields **1731** and
  **1618** respectively. The latter matches the legacy Peacebloom ID, but the
  actual herb name was not reported. The GUID low 24 bits (4757 and 4738) are
  not these IDs. The copper object's `+0x90` independently agrees with its
  candidate entry; the herb's `+0x90` has not yet been sampled.
* The herb record's pointer at `+0xD8` was `0x1DB110E525C`, 4-byte aligned.
  The previous diagnostic rejected this alignment, explaining its absence
  from the census and failure to print its header. The probe now tests either
  4- or 8-byte alignment and demands a record backpointer, full GUID and
  pointer re-read. This is **diagnostic only**, not proof that all matching
  objects are harvestable or that a pointer remains valid.

The improved **Gather probe** lists plausible legacy entry matches in the
nearby pool and shows candidate `object+0x90` for the hovered record. Please
compare a fresh hover on a known herb, a copper vein, empty ground, and a
non-resource object. Move the pointer away between samples so a stale candidate
can be distinguished from a real transition. Exact resource skill, node state,
client interaction confirmation and game-object type are still unresolved;
no autonomous gathering or click-on-ground fallback has been enabled.

## Finding a combat indicator without Lua

The **Combat probe** button takes read-only player-memory samples and prints
changed dwords/XORs compared with the previous sample for the same process,
player GUID and unit address. It reports readable and comparable chunk counts
and caps output; it does not assert that any change represents combat.

Stop the bot and press **Combat probe** when safely idle. Then manually engage
one mob and press it while the character is actually in combat. After killing
it, wait until the game itself exits combat and press it again. Repeat at least
one more idle → combat → idle cycle, and share the full log with each sample
labelled. A bit that turns on/off consistently across cycles is a candidate;
check target changes, auto-attack, PvP, pets and damage independently before
using it. Player `+0x1E60` bit `0x80000` now matched one controlled cycle in the
18:02 probe, and should be tested again in other combat states. The probe examines the unit's `0x0000..0x23FF` and record's first `0x100`
bytes, so a flag outside those ranges will not be discovered by this probe.
No Lua, process write, movement or combat input is used by either probe.


## New 18:02–18:09 samples and scanner

* Combat samples labelled by the user show `unit+0x1E60`: `0x808` idle,
  `0x80808` fighting, `0x8` idle again. The difference `0x80000` fits combat
  for this cycle; `0x800` does not. Grind now uses the candidate player bit,
  with unknown reads conservatively blocking rest and selling. Additional
  cycles and PvP/pet/target-only controls are still needed.
* A second copper vein at 1.4 yd has `object+0x90 = 1731`, GUID entry 1731,
  vtable RVA `0x50B1838`, and a 4-byte-aligned object. Peacebloom at 1.7 yd
  has `object+0x90 = 1618`, GUID entry 1618 and the same vtable. A copper
  node also appears 43.1 yd away in the same census. The user confirmed the
  hovered herb was Peacebloom. Matching two resource types does not establish
  that every legacy ID or required skill matches the new client.
* `GatherNodeScanner.TryCapture` provides read-only, 100-yd observations to the
  existing Gather probe: it requires a matching legacy ID from both GUID and
  object, the observed vtable, full GUID, backpointer, plausible transform,
  pool-slot and identity rechecks. Both object alignments are handled. The
  returned list is non-atomic and may be incomplete; its nodes are not proven
  harvestable and do not authorize mouse clicks. An empty scan is not proof
  that no nodes exist.
* Remaining work before Gatherbuddy is usable: verify hover **changes** on and
  off resources and whether posted window mouse moves update it; verify
  interaction/harvest success and depletion; provide profession/skill settings
  and integrate hotspot traversal, mesh routing, defence and blackspots.


## Copper harvested in the 18:24 observation

Before manual mining, the scanner found copper GUID
`2C0118002001B0C0:0000000000001295` at 2.0 yd and Peacebloom GUID
`2C01180020019480:0000000000001282` at 43.3 yd. After the user mined and
looted the copper, the next scan found the Peacebloom but not that copper
GUID. Mouseover on the former copper location was zero. This supports pool
disappearance as **one** observed depletion signal; a failed/partial scan,
streaming, or phase changes can also make a GUID disappear. A missing GUID
does not by itself prove a successful harvest. Herb depletion and partially
mined multi-use veins have not been measured.

The character has **Mining only**, not Herbalism. Resource-selection groundwork
now takes explicit profession/skill values and applies the legacy default
blacklist; mining-only must reject the nearby Peacebloom. Profession offsets
remain unknown, and those options are not yet connected to an autonomous bot.

## Window-only hover experiment

`Gather hover test` is an opt-in window button. With the bot stopped, press it,
hover a nearby resource within 8 yd, and keep the actual cursor still during
the test. It posts an away and a return `WM_MOUSEMOVE` to the game window and
logs the candidate GUID before, away and returned. It sends no mouse buttons,
keys or process writes, and does not move the desktop cursor. A clear
resource → different GUID → same resource transition is useful evidence that
window-only hover can identify a pixel before any right-click; without that
transition, the implementation must not assume a click will hit the resource.
The check remains non-atomic, and a transition does not prove harvesting.

## 2026-10-31: hover transition and experimental loop

A user test on Copper Vein entry 1731 (2.4 yd) saw the candidate hover GUID
change from the node to zero on a posted mouse move away, then back to the same
node on a posted move to the recorded client coordinate. This supports a
window-only hover search; it does not show that a posted right-click gathers,
that auto-loot completes, or that herbs disappear when gathered.

The experimental 70291 GatherBuddy uses manually configured professions and
skills, searches the node pool at and between mesh-routed hotspots, and tries a
bounded window-only hover search before a single right-click. It rechecks the
same GUID immediately before button-down and refuses the click if combat or
memory state is unknown. Keep the real mouse inside the game and still during
hover searches. Nodes missed by the search or still present are retried after a
cooldown; disappearance from a non-atomic scan is not proof of loot. It is not
yet tested on the live client. The old bot's loot, bag-full, vendor, flight,
and automatic profession/skill functions are not implemented here.

**Combat limitation:** the candidate player combat flag does not identify the
attacker. The loop stops moving and holds position when it sees that flag;
defend manually. It does not select an arbitrary nearby unit as the attacker.
This is not automatic defence. Profile blackspots are used to exclude nodes and
hotspots and marked costly on the navmesh, but Detour can still route through a
costly polygon, and loaded-tile marks can be lost. Strict blackspot avoidance
is not established.

## Follow-up: route polling and defensive combat

The supplied 18:47 log shows the mesh query returning two points and the route
using its existing short straight-line fallback. The gather interrupt was only
checked before a route leg or between mesh points: it was not checked while
that fallback (or a long mesh segment) was being walked. The excerpt alone
cannot establish that a valid mining node was nearby. Gather now opts into
polling while those walks run; scans log recognized/eligible/excluded/cooldown
counts, and hover failures report their stage. A scan is still non-atomic.

The real desktop cursor no longer has to be inside the game; it must remain
still during the posted-hover search. The hover search uses an additional
interleaved grid but may still miss a small or obscured object. A right-click
is sent only after the matching GUID is rechecked; neither the click nor
absence in a scan proves loot. This remains untested in the live client.

At the user's request, the gatherer now invokes the existing Grind fight
rotation when the provisional combat flag is set, after TAB changes the
selection to a nearby live non-player NPC. It stops if combat becomes clear or
unreadable, or the selection changes. **TAB does not identify the attacker:**
it can select another hostile NPC, and if TAB retains an existing target the
bot will hold position rather than assume it is valid. Supervise testing; this
is not reliable attacker-only defence. The combat candidate remains based on
one observed idle/combat/idle sample. Previous paragraph describing manual-only
defence was accurate for the earlier revision, not this one.

## 19:14–19:17 gathering and defence follow-up

The reported run found and approached copper, but the log said the final hover,
reach, combat, cursor or click check failed; the old combined message did not
identify which check. In that logged attempt the bot reported no right-button
down, while the user observed a mining cast that was promptly interrupted.
The exact source of that cast is not established by the log. A separate code
hazard was found: the generic movement stop sends mouse button-up events, a
centre left-click and a W key tap, including at the resource. The gather mover
now uses key-release-only stops instead of this panic sequence; it still uses
the normal key-steering calibration before a walk.

The extra mouse-move immediately before the final hover check was removed;
there is now a settle wait followed by separate diagnostic reasons and the
same GUID recheck before button-down. After any attempted interaction, even if
no down was posted, the gather loop remains still for six seconds unless Stop
or combat intervenes. It re-scans and makes up to four attempts on a node that
remains observed, then retries later rather than immediately suppressing it for
90 seconds. Cast state and inventory/loot are not readable, so neither a sent
click nor the wait or disappearance proves a successful gather. Nodes may
require multiple hits; retries remain bounded to avoid getting stuck.

In the combat report, TAB selected a level-7 NPC 28.8 yd away, not the nearby
monster the user says was attacking. Defence now faces nearby pool candidates
and only starts the Grind rotation when TAB changes to that same live NPC
within 12 yd; it will not chase that target past 15 yd. If already selected,
it uses Grind's default F1 self-target key and verifies self was selected before
asking TAB again. **Proximity and TAB are heuristics, not attacker identity**;
a different nearby hostile could still be attacked, a ranged attacker may be
missed, and F1 must retain its default binding. The combat bit is provisional.
These changes have not been validated on a live client.

## 20:50 herb observations and 70291-only catalog

The user reports mining with the preceding revision works well. Their herb
probe identified a hovered object with GUID high `0x2C0118000280C800`, whose
GUID-derived candidate entry and object `+0x90` both equal `656160`, at 1.8 yd
with game-object vtable RVA `0x50B1838`. The user says the hovered object is a
herb; the probe did not test harvesting it. Nearby records with GUID-derived
entry `656161` shared the vtable, but that entry was not itself hovered or
checked at object `+0x90` in the supplied log.

For the 70291 scanner only (not the 3.3.5a Gatherbuddy catalog), add these
candidates as herbs, using a provisional minimum Herbalism skill of 1:

- `656160`: Wilted Peacebloom — name supported by [this external database
  report](https://github.com/advocaite/TrinityCore/pull/81) and the user's
  hovered-herb observation.
- `656161`: Stunted Silverleaf — name supported by the same external report;
  its in-game type and hover have not yet been independently confirmed here.

A [Forever herbalism guide](https://kami-labs.fr/en/guide-herboristerie-wow-forever/)
places both starting-herb variants under skill 1. This is a provisional catalog
value, not a verified 70291 lock/skill offset. Unknown IDs are not automatically
considered herbs, and the special Cultivation duplicate `657454` mentioned by
external sources has not been added: its interaction/profession rules here are
unverified. All normal object validation and final matching-hover checks still
apply. These additions do not establish a complete catalog, successful herb
interaction, or herb depletion/loot behavior. Enable Herbalism and enter the
character's actual skill manually before testing; mining-only settings must
continue to skip both herbs.

## 21:00 herb test: observations and repeat avoidance

The user reports the bot gathered the first two herbs successfully: a Wilted
Peacebloom and a Stunted Silverleaf. The log shows a posted right-click and
then no matching scan observation after six seconds for each. It does not
independently verify bag contents. The bot then went to another Wilted
Peacebloom which the user says had been gathered earlier, and its hover search
stopped after 19 seconds with the combined reason "stopped, client changed, or
real cursor moved". Stop was logged three seconds *after* that message, and the
user reports they did not move the mouse. That combined reason cannot establish
whether a desktop cursor position actually changed, GetCursorPos failed, or
the client/window changed; OS or game cursor movement is not evidence of human
interference. The old gather logs did not include GUIDs, so they cannot establish
whether this was the exact same object as either earlier herb. There remains no
validated depleted-state offset and a pool record is not proof of availability.

The loop now logs each target's full GUID and coordinates. A GUID on which a
button-down was posted and which is subsequently not observed is deferred for
120 seconds, not labelled successfully harvested. A hover search has a 45-second
budget; if no matching hover is found after that bounded search, the bot defers
only that exact GUID for 120 seconds rather than repeating the whole search
four times. A search miss is not proof of depletion: the object may be too
small, obscured, or outside the searched pixels. A cursor/client/read failure is
reported separately and is **not** treated as a hover miss; the existing
stationary wait and bounded retries remain. Changed desktop coordinates report
both positions without attributing them to the user, and still prevent clicks.
Neither cooldown is persistent or based on position, so a new GUID is not
blocked, and gathering a node before the bot starts remains unknowable without
additional observations or a verified depletion field.

The log also shows navmesh queries finding a polygon hundreds of yards below the
character and falling back to a direct short walk. The navmesh was therefore not
actually used for those specific legs; the map/tile/layer mismatch is not fixed
by this change.

## 21:11 overlapping herbs and map mismatch

The user reports that the bot eventually harvested nodes, with visible hover
flashing, and that they moved the mouse once when returning to the game. The
linked file `2026-10-31_2111_11476.log` is a log, not a video. In it the bot
searched for Wilted Peacebloom GUID ending `109C` for 45 seconds without a
match, then found Stunted Silverleaf GUID ending `109D` at the same logged
position `(-6264.5,179.1,416.6)` and posted an interaction. The overlapping
positions are evidence of a plausible explanation for the flashing, not proof
which GUID was under the mouse during every flash. Other searches were cancelled
when Windows cursor coordinates changed; the log cannot identify who or what
moved them. The user independently confirms successful harvests.

The hover search may now accept a *different* eligible, structurally validated
node within 4 yd of the intended node, but only when its GUID is actually under
the posted cursor and differs from the initial away reading. Profession/skill,
blacklist and cooldown checks apply; player-to-node distance must be within
5 yd; the full GUID and eligibility are checked again immediately before the
right-button down. The originally intended GUID is deferred briefly after a
substitute click, not declared depleted. Unknown game objects and untrained
herbs are never accepted as substitutes. Hover search remains visible, can miss
nodes, and a posted interaction is not independent proof of loot.

MeshProbe reported tiles for maps 0 and 1 at the same X/Y and queried map 1,
then found only polygons hundreds of yards below the character. The loaded
profile is labelled Elwynn Forest, but the log did not independently record a
live map ID, so the wrong-map explanation is strongly suggested, not proven.
The client has a previously observed 70291 map root at RVA `0x7B7B4F8`, with
map ID at root `+0x28`. The code now prefers a stable, uncached live map-ID
read (zero is valid) for routing, blackspot marking and MeshProbe instead of
choosing map 1 merely because its tile exists. If that read fails on 70291, routing refuses to guess from overlapping tiles;
the legacy 32-bit map heuristic remains unchanged. Navmesh pathing can
still fail if the correct map has missing/outdated tiles or different terrain;
a short straight key-steered fallback is not a mesh path. The new reader and
routing behavior have not been validated against the live client.

## Optional gathering while grinding

Open **Gather & tools** under Settings, then **Gather settings**. The new
**Gather nearby resources while grinding** checkbox is off by default and is
saved with the manually configured professions and skill levels. It applies to
70291 Grind sessions with a `<GrindArea>`; a plain hotspot-only profile is
currently run as a one-shot route by the existing UI, not as the Grind loop.

The Grind thread and mover remain in control; no second GatherBuddy loop is
started. Combat and vendor requests take priority. Eligible nodes are searched
within 100 yd before and during hotspot legs, using the same profession, skill,
GUID, cooldown and profile-blackspot checks as GatherBuddy. A node detour uses
the same mesh and exact-hover-confirmed interaction, and resumes the interrupted
hotspot afterward. Before and during the detour, hover search, and cast wait,
combat, Stop, vendor requests, client/map reads and nearby units are rechecked.

The client has no verified hostility/reaction field. To honour “no monsters in
range” without treating a friendly NPC as a confirmed monster, this option
conservatively refuses to gather if *any* living non-player NPC is observed
within 100 yd, or a nearby NPC's health is unreadable. Friendly or neutral
NPCs and pets can therefore block gathering; snapshot coverage is non-atomic
and does not prove the absence of a threat. The combat flag remains provisional.
The option, the new panel, and their safety behavior require a Windows build and
live verification; fixture checks are not proof of either.

The subsequent `2026-10-31_2125_12804.log` reports client map 0, 23 nearby
ground polygons and a successful 40-yd mesh probe; it also contains 21 logged
mesh paths and 17 posted node interactions followed by those GUIDs no longer
being observed. Two early hover attempts sent no button-down after desktop
cursor coordinates changed, and a later retry posted the click. The user reports
that the run harvested successfully; the log by itself does not verify loot.

## 21:54 short-leg mesh, recovery and hover improvements

The user reports becoming lost on an Elwynn leg and missing food/drink recovery.
The excerpt shows successful short straight key-steered fallback walks after
successful Detour results with two points were discarded. It does not prove a
mesh was missing, nor does it show the obstacle or how the character got lost.
A successful two-point result can be a straight path as well as an ambiguous
failure-shaped answer. Gather legs now refuse an unverified direct fallback.
A two-point result must pass a separate mesh raycast with a complete status,
no boundary hit, the actual goal polygon reached, and readable non-blackspot
visited polygons. Both path endpoints and partial status are checked, and large
snap corrections / unreadable player height are refused. A validated mesh ray
is still not a guarantee against dynamic objects, terrain changes, stale
meshes or a wrong client-to-mmap map assignment. If validation fails Gather may
stop rather than navigate around the obstruction; the new logs give raycast
status, hit fraction, and polygon details to investigate.

GatherBuddy now invokes Grind's existing food/drink recovery between combat and
choosing the next resource/hotspot. Set thresholds and food/drink action-bar
slots in **Combat settings**. The loop checks for combat or unreadable nearby
units, releases movement, holds still while resting, and responds to Stop.
A pressed key is not proof an item was consumed. Power maximum remains an
unverified candidate; a session peak is used and starting already low with no
reliable maximum can make the percentage misleading. The bag inventory is not
read, so this does not automatically find food in bags.

Herb searches try the last successful herb screen point, the original center
point, and a few approximate projected points before the existing grid. The
projection and remembered pixel are only hover hints: full GUID, eligibility,
range, client/cursor, combat and final click checks remain mandatory. Mining's
search order and the six-second cast/loot wait remain unchanged. These changes
are untested on the live client; hints can miss, and a mesh ray cannot detect
moving objects that are not represented in the mmap.

The mesh follower previously folded away short path corners and then skipped
additional points less than 12 yd away, even in key-steering mode. Both could
cut across the mesh corridor. Key-steered mesh paths now retain every corner;
this is a plausible navigation issue but the excerpt alone cannot establish
that it caused this particular incident.

### 22:06 run follow-up (experimental)

The 22:06 log recorded a Silverleaf (GUID ending `1D3D`) aborting four searches because desktop cursor coordinates changed. No right-button down was posted on those attempts; the source of the coordinate changes is unknown. On revisiting that GUID, the first sampled point matched and button messages were posted; its subsequent absence was observed but loot was not independently checked. This is not evidence of depletion, or proof of who or what moved the cursor. Other herbs included a 34-point search, so small/occluded hover targets may still be slow.

Key-steered walking now checks arrival before turning toward a mesh point that is already in tolerance, and turn adaptation distinguishes a changing target bearing from overshoot. The turn scale is reset conservatively at each mesh point; mesh corners and the fail-closed path requirement remain. Gather sidesteps now require a short ray validated on the current map and terrain layer and poll the route interruption callback, including combat and Stop. Mesh raycasts do not detect dynamic collisions such as unmeshed trees; this is not a guarantee the sidestep is physically clear.

A cursor-coordinate abort still prevents button-down. After the existing six-second possible-cast hold and node recheck, Gather waits for 1.5 seconds of cursor stability (up to 15 seconds, interruptible for combat/Stop) and restarts the search with a fresh away transition and all original GUID, node and cursor checks. These aborts do not consume normal gather attempts; at most three quiet-cursor recoveries are allowed before the exact GUID is deferred briefly. Mouse/cursor failures and hover misses do not prove depletion. Mining uses the same safety recovery, but its hover point order is unchanged. These changes and fixtures require Windows build and live validation.

### Dense-hotspot transition timing (22:28 run)

The supplied Elwynn profile contains 668 hotspots (median adjacent horizontal separation approximately 8.3 yd). In the log, 71 ordinary transitions from completed route to next hotspot began in 507–525 ms, median 510 ms. The Gather loop explicitly waited 500 ms after every successful hotspot. It now selects the next hotspot immediately after the existing map, combat, recovery and resource checks, with a new mesh path still required. The mover still releases forward at the endpoint; this change does not blindly continue into an unvalidated next leg. Consecutive sub-100 ms completed hotspots are throttled only after a whole short profile or eight fast points, preventing a busy loop on coincident routes. The existing blackspot-skip pause is unchanged. Stop, combat, resource and cast-wait behavior are unchanged. Needs Windows build and live verification.

### Profile-free local mesh roaming (experimental)

In **Gather & tools → Gather settings**, enable **Roam on the mesh when no profile is loaded**. The option defaults off and applies to both GatherBuddy and Grind only when no profile is loaded; loaded profiles retain their existing behavior. To switch away from a loaded WowB profile, stop the bot and use **Gather & tools → Clear loaded profile**. Start GatherBuddy with its trained professions/skills selected, or Start Grind; enable **Gather nearby resources while grinding** separately to permit Grind gathering when its existing conservative combat/NPC safety gate allows it. Grinding still uses the existing mob discovery, level, TAB/attack, defence and recovery logic. Without a profile there are no profile-specific level bands or profile blackspots; existing level-viability checks remain. No Lua is used.

This is **not exact zone confinement**: the 70291 zone/area offsets are unverified, and a map can be a continent. Random destination samples are restricted to legacy ground, ground or road mesh polygons near the player, within the configured 100–3000 yd radius from the starting point (250 yd by default); this is a local sampling bound, not a zone boundary or a hard leash. Combat, resource approaches and mesh detours may extend beyond it, or cross an actual zone boundary. Review the surroundings before starting. A live map ID and nearby correctly layered mesh are required; a changed/unreadable map stops roaming. Destinations are routed through the existing validated mesh walker with no direct fallback for roaming; failures are bounded and stop rather than spin forever. Mesh data cannot detect unmeshed/dynamic obstacles. A Pause/Resume restarts profile-free roaming from the then-current position. This mode requires a Windows build and live verification, including the native random-point query.

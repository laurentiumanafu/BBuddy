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

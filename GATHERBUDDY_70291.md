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
| Game-object identity and position | `record+0xD8` and position `record+0x60` are verified for units; their applicability, game-object vtable(s), pointer layout, lifecycle and transforms are **not verified for nodes**. | Detect nearby resources and route to them; revalidate full GUID immediately before interaction. |
| Resource entry/type | No validated entry, game-object cache, lock type or herb/mineral discriminator for 70291. `WowBGuid.EntryId` is not verified for modern game objects. | Avoid treating doors, mailboxes or friendly NPCs as resources. `Bots/Gatherbuddy/GatherableNodes.cs` supplies legacy IDs and required skill levels, but IDs and coverage must be verified for the current client. |
| Gatherability / depleted state | No verified game-object state, dynamic flags or lootability offset. | Avoid approaching depleted/inaccessible nodes indefinitely; temporary GUID/position cooldowns and a bounded interaction attempt can supplement, not prove, success. |
| Profession and skill level | No verified 70291 mining/herbalism profession/skill offsets. Legacy `WoWGameObject.CanLoot` relies on legacy skill, object cache and Lock.dbc. | Choose only trained professions and attainable nodes. A UI-selected profession and skill level can be a fallback if clearly labelled manual; never guess from unverified memory. |
| Interaction / outcome | No verified node mouseover identity, interaction address or gather/loot-frame state. Candidate mouseover RVA `0x6283898` is **unverified**. Ground right-click projection is approximate and can issue a movement order when it misses. | A safe window-input-only gather requires a verified hover/identity or another reliable positive check, stop-and-settle, and bounded retry/backoff. A sent click is not proof of gathering. Do not use Lua or injection. |
| Combat / defence | Player health and unit snapshots exist. `0x800` is present on idle units, so it is not proof of combat or an attacker. Hostility/reaction, unit target GUID and a reliable combat flag remain unverified. | Stop gathering to defend only when a real attacker can be identified; recent health loss is an imperfect fallback, not proof that the nearest mob attacked. Absorbs, heals and the first hit can mask it. |
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
The current fallback samples **raw player health**, regardless of that bit,
and holds rest/vendor actions for eight seconds after a detected drop. An
unreadable health value is treated conservatively as unknown; it is not
converted to safe. The fallback cannot establish all combat states or identify
the attacker. It is intentionally not presented as a fully reliable defence
mechanism for Gatherbuddy.

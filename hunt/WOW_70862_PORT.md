# Experimental 1.60.1.70862 port

## Input and status

The user's Drive ID `1VM3hp_fLx5u6BvnLvhO7zlWW14p-OMo_` was reused: the current `Wow-decoded.zip` (version 8) is 34,440,529 bytes, MD5 `b61f8f7477fda93821ecfa67350d6337`. Its one executable is 80,627,408 bytes, AMD64 PE32+, image base `0x140000000`, resource version **1.60.1.70862**, SHA-256 `b332526a904931e81dcbcf1f95a42d64e842177b37ab5af52ab1f27b875fd0af`. Neither executable nor archive is committed. The older 70790 executable was no longer available through that ID; direct comparison used the available 70291 decoded reference (`8f864e73e73ad67319765063a7c491a420dbe2d0714ce427106e39cd9f109192`), the documented 70790 anchors, bounded masked instruction matches and Capstone disassembly. The new image was not executed.

70862 is the default registered build and routes to the existing experimental 70707-family gameplay, settings and safeguards with its own image-relative candidates. Earlier registered builds remain supported. This is a **port of shared implementation, not proof that every feature or structure works**. No Windows/WPF compile or live client validation has been done here. Unknown versions are not assumed compatible; no Lua is used. Any new action or layout should be supervised, particularly combat, healing, vendoring, corpse recovery and skinning.

## Static candidates (RVAs)

| Candidate | 70862 RVA | Static basis |
|---|---:|---|
| Player GUID / name | `0x4C1A770` / `0x4C1A780` | Three corresponding 70291 references to the GUID; the adjacent byte-write sequence at `0x1C712DB` targets the name candidate. Name semantics still need a live comparison. |
| Target GUID | `0x6A32478` | 35 corresponding 70291 xrefs voted for this candidate; `0x5ABB58` and `0x23C76D` provide separate references. |
| Mouseover GUID | `0x4CD5F98` | Two corresponding 70291 xrefs, including `0x21C900A`; live cursor behavior unverified. |
| Object manager / pools | `0x6C349F8` / `0x4728BE0` | Three and two corresponding 70291 references respectively; inherited pool record layout remains a hypothesis. |
| Map root | `0x6C34978` | Three corresponding 70291 references; actual map offset remains inherited and unverified. |
| Camera frame | `0x4A51EB0` | Fourteen matching 70291 references; frame/camera member offsets are inherited and unverified. |
| Action array | `0x6C53140` | 36 corresponding references and indexed array use at `0xA2B9FD`. Key bindings and packing remain experimental. |
| Player / unit / game object vtables | `0x40E6498` / `0x40FA510` / `0x40EECB0` | Correlated 70291 virtual-method slots (player two, game object five); the unit candidate shares 47 of the first 60 player slots and has plausible corresponding initial methods. Other similar tables exist. Especially the unit identification is an inference, not a live observation. |
| Inventory handler / lookup | `0x25D4E10` / `0x2074700` | `0x25D4EF9` uses `+0x9460` and calls lookup at `0x25D4F00`; observed `+0x90` after the call. Semantics are still unverified. |
| Container identity table | `0x6C56180` | Helper `0x2473D60`, called at `0xD0FD5C` and `0x247584B`, computes this RIP-relative table with index ×16. Completeness is unproven. |

Gameplay code marker sites are faction `0x1F9298C`, health `0x1FBCA47`, power `0x27997E5`, action `0xA2B9FD`. Their selected bytes are checked independently, with the existing minimum-two-matches/no-readable-mismatch policy. The container loop at `0x2475876` accesses `+0xAC0`, `+0x490`, strides 16 and tests high bits; player-range markers `0x24758C6`, `0x24758D9`, `0x24758E4` match inherited `+0x9480`, `+0x7D04`, `0x23` behavior. They are partial static evidence, not proof of the slot model or safety.

`Tools/check_wow70862_image.py` checks the exact PE hash, marker bytes, RIP-relative targets and selected table slots; a successful run does not validate behavior. Unit vtable, health/power/faction semantics, ghost, aura, input, pool, object and interaction fields, and complete bag enumeration remain unverified for 70862.

## Bags and live verification

The existing ordinary-bag policy, two-zero monitor, startup priority and read-only stopped research are routed to 70862. The 70790 free-memory/neighbor-record handling is reused with the same conservative checks. Saved classifications are keyed by exact player and bag GUID in `Settings/clickaim-bag-kinds-70755.txt`; a changed character or bag remains unknown. Confirm existing classifications still correspond to ordinary/specialty bags on this client. Zero identities cannot prove a complete inventory. Keep auto-selling disabled until stopped snapshots match visible ordinary slots and a controlled non-stacking item changes the subtotal correctly; supervise the first zero-trigger trip and check the sale independently.

Build Windows x64 with `dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64`. In a safe area start stopped, inspect attach/build, character, coordinates, target, map, action bar and nearby scan; check STOP and short mesh movement before supervised combat/gather/loot/buffs/death recovery. Test individual modes separately and send a log if a guard refuses or values disagree. A logged action is not proof it succeeded. See `HANDOFF_FUTURE_WOW_BUILDS.md` for the full live validation procedure and limitations.

## Checks performed

The supplied PE passed `Tools/check_wow70862_image.py`; the old 70291 reference was rejected by its exact-hash check. The production-linked Roslyn synthetic reader suite passed for 70707, 70755, 70790 and 70862, including 70862 marker mismatches, bag classifications, unreadable alternatives and mapping changes. Gather radius/interaction, memory-region, bag-sell, remote-defence, buff, Shock/Attack and power fixtures passed; eight Python inventory and two build-default checks passed. C# lint scripts completed with the existing heuristic at `GreenMagic/LuaBridge64.cs:1752`; changed C# parsed, project/XAML XML parsed, and `git diff --check` passed. These are not a full Windows application build or live validation.

## 2026-11-09 bag snapshot follow-up

The supplied 70862 log had four nonzero container identities. Three had a single observed identity-matching header but were unresolved because each scan also encountered `0x258000000E0` as an unreadable pointer-like value; the fourth was resolved as a user-classified ordinary bag. The scan windows overlap other occupied pool records, but field ownership and record extents are not established. The log did not identify which candidate was newly equipped and did not show the Windows state of the unreadable address. Exploratory zero tags are not verified free slots.

The resolver now also excludes an unreadable alternative only when `VirtualQueryEx` confirms the complete 32-byte header is in a single `MEM_RESERVE` region on 70862: reserved pages are uncommitted and cannot currently hold a header. The range is rechecked during the candidate scan and before publishing the result. `MEM_FREE` handling is unchanged; committed pages (including inaccessible/guard pages), split ranges, failures, instability and multiple identity-matching headers remain unknown. Other builds retain the prior free-only policy. Mapping and inventory reads are non-atomic; this is still experimental and does not prove bag completeness or tag semantics.

If the address was reserved, this can restore candidate resolution. If it was committed or the query fails, it will remain unresolved; a stopped Bag research snapshot now logs the state/protection or a failed/split query for the remaining unreadable alternatives. Do not guess bag classification from candidate index or size. Keep automatic selling disabled until the snapshot and a controlled item move agree with the visible ordinary bag counts. This change has not been built on Windows or tested live.

## Best-effort defence with faction bypass

On 70862 only, the user chose to bypass Allow/Blacklist faction checks in combat defence. Voluntary pulls still use the configured faction filter. Nearby and 12–40 yd remote defence use the existing selection heuristic; player combat state, proximity and TAB do **not** identify the attacker or prove hostility. Consequently defence can target a different NPC, including a friendly, and is not guaranteed to select the actual attacker. Supervise and use STOP. The player combat flag is itself a build candidate, not independently verified in every situation.

Defence retains the combat-state, target identity, selected GUID, non-player/live/flags, range, attachment, action-bar and STOP checks. Remote defence also retains the nearby-unit veto and guarded mesh movement. On 70862, a faction key being blocked or unreadable no longer vetoes these defensive candidates; unrelated refusal and safety checks still apply. Faction-free defence stops when the combat candidate ends or becomes unreadable. Unknown factions remain blocked for voluntary pulls. The prior blocked-NPC research probe is no longer scheduled by the defence loop; its GUID matches did not establish attacker identity.

## 2026-11-09 damaged-target defence loop

In the supplied `2026-11-09_1831_13868.log`, after a Shock and two totem-macro attempts, a pull yielded to defence. The already-damaged selected target remained about 21.0 yd away; the focus selector admitted it within its 28 yd search radius, while the subsequent near-only fight rejected it beyond the selected 20 yd Shock range. The same target was re-entered and rejected 33 times until STOP. The trace does not itself establish the character's facing, actual attacker identity, or why it went past the NPC.

On 70862, a focused, already-damaged selected target outside the 12 yd nearby radius now uses the existing bounded remote-defence fight and its revalidated mesh movement guard. Another live or unreadable nearby NPC, a paused-unreachable target, changed identity/selection, STOP, combat end or other guard can still withhold or interrupt the approach. The attacker and hostility remain unverified, and the user-approved defence-only faction bypass remains. This does not authorize chasing an arbitrary selected NPC: the existing focused GUID/damage and range checks still apply.

The combat buff and totem macro paths now release held chase movement only when an attempt is due and recheck combat, target, player, bar and aura before pressing. Stoneclaw also releases movement before its final recheck. Previously held forward movement could persist during each 1.8 s post-input wait; that could contribute to overshoot, but the log does not prove it caused this instance. These changes have not been compiled on Windows or live-validated; supervise movement and STOP in a controlled fight.

### Nearby combat selection (2026-11-09 log)
The 19:01 log recorded repeated failure to confirm a nearby defensive TAB selection after 19:03:42; it did not identify the attacker, hostility, selected NPC, or the cause of each failed confirmation. The old loop required TAB to match the exact NPC it faced, so a different valid nearby TAB selection could be discarded. The 70862 path now checks an already-selected nearby NPC before turning, tries TAB before turning, and validates whichever nearby NPC TAB actually selects using two fresh snapshots plus combat, STOP, attachment, selection, identity, fields and range checks. Failed searches use fewer direction/TAB attempts and a two-second retry delay, while a valid current selection remains immediately eligible. Older-build selection behavior and guarded rotations remain in place. As previously approved, faction is bypassed during 70862 combat defence; proximity/selection are not proof of hostility or attacker identity and may choose an unrelated or friendly NPC. These changes need live testing and a Windows build.

### Ranged pull and faction preference (19:13 log)
At 19:19:25 Frost Shock coincided with target health 863→704. At roughly 20.4 yd the voluntary fight then yielded because the remote-pull policy compared distance with its estimated 20 yd spell range even for an already engaged target; defence reacquired it, then the generic nearby-NPC veto interrupted the remote approach. The log does not identify that nearby NPC or show the selected totem; the user observed a totem and stationary caster in game.

On 70862 a voluntary target observed losing health is now retained through the combat transition, subject to matching GUID, unit/record addresses, live pull/profile/faction checks, unreachable cooldown, a 43 yd bound, and freshly guarded combat movement. The damage source remains unknown. If a damaged voluntary target has to re-enter defence, nearby NPCs alone do not veto its guarded approach. The Shock/Attack rotation defers optional combat buffs while closing on a damaged target outside melee; healing and Stoneclaw keep priority, and buffs remain combat-only.

Defence now first prefers explicit faction IDs from the active profile or the saved manual allow-list, including preferred remote candidates within 40 yd. It tries a bounded number of selections before the pre-existing 70862 nonpreferred nearby fallback. For preferred remote approaches, a nearby preferred candidate blocks the approach, but an unknown/nonpreferred nearby NPC no longer does; this could ignore an actual attacker. A preferred faction is not proof of hostility, and fallback can still choose a totem, friendly, or unrelated NPC. The previous blanket nearby-veto descriptions above are superseded for these narrowly scoped 70862 cases. Synthetic fixtures and source checks are not live or Windows build validation.

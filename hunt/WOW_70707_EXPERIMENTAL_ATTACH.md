# Experimental attachment and single-target Grind trial: build 70707

Build 70707 is discovered on normal startup; no special launch argument is needed.
The port is **not validated as a complete bot**. The raw candidates, image
signatures and synthetic tests are not evidence of correct combat, hotkey
bindings, movement, death detection, or safety in the live client. No Lua is
used. Builds 70291 and 12340 retain their existing paths.

## Supervised first-fight trial

- Build CopilotBuddy for x64 and attach to the logged-in 70707 client.
- In Combat Settings, set Factions to **Allow** and include the candidate key
  for the specific monster you intend to fight. The observed Trogg candidate
  was 36; it was not added to anyone's allowlist automatically. The faction key
  is not a hostility or attackability verdict. Select a known hostile monster,
  not a friendly NPC. The selection-free nearby scan remains available while
  stopped and is still research-only.
- For the observed shaman, the trial requires action slots 1 Attack (6603),
  2 Lightning Bolt (403), and 3 Healing Wave (331), the observed mana-type-0
  array, a readable living player and target, matching partial code signatures,
  and the selected monster within 15 yards. Confirm the real key bindings before
  use; an action array does not establish them. Start Grind using a grind-area
  profile, or enable profile-free roaming in Gather settings to use its mesh
  initialization without a profile.
- The trial admits **only the exact full GUID selected at start**, and rechecks
  its record, vtable and allowlisted key before combat inputs. It does not use
  TAB or pixel searching to replace a lost target. It stops after one fight or
  if the target cannot be engaged, rather than continuing to roam. A 60-second
  outer-loop budget does not preempt an in-progress fight; the fight has its own
  timeout and STOP remains necessary. Reads and inputs are non-atomic: races
  remain possible. If already in combat or the candidate combat flag is set,
  the trial stops without attempting defence.
- Auto-healing is **disabled** in the trial because its F1/TAB retargeting is
  not verified and could select a different unit while auto-attack is active.
  Rest, buffs, automatic looting, gathering and selling are also disabled.
  Unlisted attackers cannot be identified or defended against. Do not leave the
  character unattended: use STOP and manually take over if anything looks wrong.
  STOP releases movement and revokes trial reads, but it does not guarantee the
  game's already-enabled auto-attack is switched off; verify it manually.

Experimental gameplay reads are bound to the startup/Grind thread and revoked
on STOP, start failures, and worker exit. The standalone GatherBuddy and
70291 gathering scanner remain blocked on 70707. Other modes do not receive
70707 candidate gameplay values from the trial.

## Diagnostics and evidence

`[70707 candidates]` logs candidate GUIDs, player name, pointers, map and camera
zoom. Player and selected-record logs include candidate raw health, level,
faction and power; `[70707 action research]` prints the first twelve candidate
qwords. These can contain identifying information and process addresses.

The reported 171/171 mana matched candidate array index zero; raw action slots
1–3 matched the abilities above. The selected Trogg's candidate key was 36 and
its level was reported as 2. These are single observations, not proof of live
transitions or binding semantics. Nearest mesh polygon checks snapped the player
and target to within 0.18 and 0.20 yards respectively, but did not calculate a
route or demonstrate actual movement. Health scaling, zero/death transitions,
combat flags, facing, route execution and attacker identity remain unverified.

The 70707 client must be x64 and its version resource must be readable.
Discovery includes `Wow`, `Luacct`, `WowB` and `Wow-decoded`; `/pid=1234` may
optionally select a differently named process by its actual PID.

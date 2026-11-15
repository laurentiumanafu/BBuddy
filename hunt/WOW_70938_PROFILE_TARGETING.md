# 70938 profile target-level conflict

The supplied `2026-11-15_1103_12356.log` starts at 11:03:04 and ends at
11:03:42. It does not record the later hotspot cycling described by the user.
The supplied `riverglades.xml` was loaded with player band 1–59, target band
34–40, two hotspots and factions 7, 38 and 16. The player candidate level was
43; several alive pool units had faction 38 and levels 36–38. The bot activated
the profile allowlist, so this log does not indicate faction 38 was excluded.

A pre-existing generic voluntary-pull cutoff also required targets to be no
more than six levels below the character. Thus a level-36 NPC, seven levels
below 43, could pass the profile target band but fail the generic check at
pool scanning, TAB selection, approach and final pull. The log shows TAB
selection repeatedly moved to self using F1 for GUID ending 97525, which was
later observed as a living level-36 faction-38 defensive target. The old log
did not print the rejection reason, so it does not independently prove every
F1 event had this cause. One earlier F1 cleared a nearby corpse. F1 was a
verified selection dismissal, not a heal attempt. The log also shows a level-38
faction-38 voluntary target acquired, followed by a switch to nearby combat
defence; it does not prove the voluntary target was damaged or that the defence
selection was the attacker.

The last Maelstrom change did not change faction scanning or TAB rules. It did
change the normal 70938 Lightning Bolt opener: the log shows Bolt withheld
because the five-stack candidate or another safety condition was not confirmed,
then a mesh approach for the 20-yard Earth Shock. This can change timing and
whether a distant target is damaged before combat defence intervenes; a key-1
Start Attack attempt is not proof that the distant target was hit.

When a valid active profile explicitly supplies `TargetMinLevel`, its configured
lower target level now overrides only the generic six-below cutoff. The active
profile's level band, player band, faction allowlist, hotspot radius, map,
AvoidMobs, flags, health and identity checks remain mandatory; the generic
three-above safety limit remains. Profile-free Grind and profiles using an
inferred/fallback minimum retain the six-below cutoff. This behavior applies
consistently to voluntary pool scans, selected targets and approach rechecks;
combat defence remains separate. The F1 dismissal safeguards are unchanged.
Rejections now include throttled diagnostic readings, not a hostility verdict.

Synthetic policy fixtures and source checks do not establish actual client
behavior. The fix needs a local 70938 in-game run, including confirmation that
level-36 targets are actually selected and attacked and that no unintended NPC
is attacked. A faction template alone does not establish hostility.

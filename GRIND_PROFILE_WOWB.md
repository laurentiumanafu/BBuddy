# WowB Grind profiles

For a profile containing `<SubProfile>` / `<GrindArea>` entries, choose Grind,
load the profile and press Start. Each area needs nonempty numeric `<Factions>`,
a target level range and valid hotspots. A `<SubProfile>`'s `<MinLevel>` and
`<MaxLevel>` choose the area for the *player's* level; `<TargetMinLevel>` and
`<TargetMaxLevel>` restrict the *monster's* level. The bot checks for a level
change approximately every ten seconds and switches to a matching area; if
bands overlap it chooses the closest matching area. If the player level is
unknown or no area matches, it will not start arbitrary profile pulls. It does
not guess that a nearby or out-of-band area is correct.

For voluntary pulls, only the active area's faction IDs are used (the manual
faction setting remains in effect for profile-free roaming); the target also
needs to be within 100 horizontal yards of at least one hotspot in that area
and within the normal 100-yard scan radius of the character. Those constraints,
the level and faction are rechecked for a selected/approached NPC and before
attack input. Unknown levels/keys fail closed. The existing player-relative
level viability and other anti-friendly safeguards still apply; this may narrow
the profile's range. Profile `<AvoidMobs>` entries are also excluded using the
experimental GUID-entry candidate; a matching candidate is not proof of NPC
identity. A level change or unreadable player level temporarily holds new pulls.
TAB may momentarily select an unsuitable NPC, but it must pass the checks before
being attacked. Only streamed client objects can be found.

In combat, defence remains separate. On 70862 the explicitly approved heuristic
can bypass faction/radius/level restrictions and may select the wrong NPC,
including a friendly; those restrictions describe **voluntary pulls**, not a
hostility guarantee. Profile-free Grind is unchanged. STOP, attachment, death,
mesh and action-bar safety remain in effect.

The sample profile does not identify a map on each area/hotspot. The current
map is bound for the run, but cannot be inferred from the coordinates; start on
the correct map and keep monitoring navigation. Profile `<Blackspots>`,
vendors, trainers, mail, and sale/mail instructions are **not applied** by this
profile path. Existing separately recorded vendor selling still applies. Do not
assume the bot avoids those listed hazards. Candidate faction IDs/entry layouts
may differ on this client, and a matching ID is not proof of hostility. These
changes require a Windows build and live testing; no Lua is used.

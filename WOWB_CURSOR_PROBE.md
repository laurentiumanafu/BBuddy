# 70291 NPC cursor: read-only experiment

The user reports that an attackable NPC changes the cursor to a dagger, while
friendly town NPCs do not. This is a promising **observation**, not yet a
proven attackability test. The relationship-table offsets are still useful, but
a template-only comparison does not incorporate reputation, overrides or the
other checks in the full attack predicate.

The experimental **Gather & tools → NPC cursor probe** captures the Windows
`GetCursorInfo` flags and handle and, if available, a SHA-256 fingerprint of
`GetIconInfo`'s mask/color bitmaps and hotspot. It never stores those bitmap
pixels or screen images. It also records full selected and mouseover-*candidate*
GUIDs and whether they match, with a user-supplied label. It does not move the
pointer, send mouse or keyboard messages, call client functions, use Lua, write
game memory, interact, select or attack. It is disabled while the bot is
running or paused. **Grind, targeting, the existing failsafe and selling are
unchanged.**

## How to collect one controlled set

1. Build, attach, stop the bot and routes. In game, select a living friendly
   town NPC. The target portrait should show that NPC.
2. Open **Gather & tools → NPC cursor probe**, press **Friendly town NPC**,
   switch back to the game, and place the pointer over the **same selected NPC**
   before the eight-second timer expires. Keep it still. Do not click or attack.
   Switch back to the tool to read the result.
3. Repeat for an attackable yellow monster and an aggressive monster, using the
   matching buttons. Select each monster *before* starting its timer. Use a safe
   location; the tool itself never attacks. Record separately whether you actually
   saw a dagger and if any sample said the mouseover did not match or the Windows
   cursor was hidden. Repeat examples if convenient; one example each is only a
   feasibility check.
4. Privately share `Settings\wowb-cursor-samples.jsonl` from the output folder.
   The file appends and is gitignored. It contains player/target/mouseover GUIDs,
   target-entry hints, cursor handles, image fingerprints and labels; it does
   not contain screenshots or raw cursor bitmaps. It can identify characters or
   world objects, so do not share publicly.

The tool requires the foreground window to be the attached game and the desktop
pointer to be over its client window. It rechecks foreground, desktop coordinates,
cursor flags/handle and selected object and rereads the candidate mouseover GUID.
It samples the cursor image twice and omits the fingerprint if it changes or a
read fails. These checks reduce, but cannot eliminate, races, changes-away-and-back,
stale GUIDs, overlays or a mismatch between the user's pointer and a 3D target.
The 70291 partial signature and observed mouseover RVA are not full version or
semantic verification. A matching GUID is only a candidate; the code saves an
inconclusive result when it doesn't match rather than pretending it is the target.

`GetCursorInfo` observes the **desktop cursor**, not necessarily what the game
renders. Hardware/software cursor settings, fullscreen mode, custom or animated
cursors, overlays and focus can make it hidden, unavailable, identical for
visibly different in-game pointers, or variable between machines. Handles may
change across sessions; fingerprints may change with resolution, theme or
rendering. A hover can also be impossible due to occlusion, distance, camera or
UI, and a dagger alone has not been proven to cover immunity, reputation,
PvP or other edge cases. Posted/background mouse moves may not update the
foreground desktop cursor. No absence or presence of a dagger authorizes a
combat action in this build.

Offline (no game input):

```
python Tools/WowBCursorAnalysis/analyze.py path/to/wowb-cursor-samples.jsonl
python -m unittest discover -s Tools/WowBCursorAnalysis -v
```

The analyzer reports fingerprint overlap only for visible Windows cursors with a
matching candidate GUID and available fingerprint. User labels are not proof of
the visually displayed dagger or attackability; separate notes are needed. An
observed separation is a lead to test, not a general classifier.

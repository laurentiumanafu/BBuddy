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

## First sample set (2026-11-01)

The shared file contained one friendly, one attackable-neutral and one
aggressive labeled observation. All three had a visible Windows cursor, an
available stable-within-capture image fingerprint, and a mouseover-candidate
GUID matching the selected target. All three fingerprints were different.
This is promising but does not establish which image is a dagger, whether the
fingerprint follows hover rather than some other factor, or whether the
candidate mouseover is current. The labels are manually supplied; visual
cursor appearance was not independently recorded. There was no off-target
control, no repeat of the same target, and only one target per category. No
classifier or gameplay change follows from these results.

## Follow-up: user-observed dagger and glove

The user reports that the aggressive example was a dark dagger because it was
far away, while the yellow boar showed a highlighted dagger close up. They
also report a glove when not hovering the boar, and that friendly NPCs have
multiple cursor shapes. Thus the two observed dagger images are **not** a way
to distinguish aggressive from neutral, nor is an arbitrary other image proof
of friendliness.

A second file has one highlighted-dagger boar observation with the same image
fingerprint as the first boar, but a different selected boar and cursor handle.
It also has one user-reported off-target glove observation with a different
fingerprint and an empty mouseover candidate. Both Windows cursors were shown.
The control's selected boar was **not the same GUID** as the hovered boar, so
this is not a same-target before/after comparison. User visual reports are not
independently captured by the probe. The dark dagger still has one observation.

The diagnostic now reports `observed_highlighted_dagger` or
`observed_dark_dagger` only when the cursor is visible, the selected GUID
matches the candidate, and the fingerprint equals one of the two images from
this installation. Off-target controls and everything else report `unknown`,
**never friendly**. These hints can fail under other resolutions, client or
cursor settings, and the candidate can be stale. They do not authorize input
or change Grind, the existing failsafe, navigation or selling.

A further obstacle remains: these observations are of the real Windows desktop
cursor while the user manually hovered a foreground game. Grind's existing
pool-selection search uses posted mouse messages, often with the game in the
background. Whether that changes the Windows cursor in the same way, and ties it
to the intended game object, is not established. Treating a current desktop
cursor as the result of a posted hover could associate the wrong NPC and is
unsafe. A separate, explicitly opted-in test is required before considering any
gameplay integration.

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
4. To check whether the desktop cursor actually follows hover, keep the same NPC
   selected and press **Off-target control**. Return to the game and hover empty
   world, away from NPCs and UI (not another interactable object). Keep still
   until capture finishes. Repeat an on-target sample and an off-target control
   without changing target. The control is intentionally off-target: its
   mouseover candidate can be empty, different, or even stale and matching;
   confirm visually where the pointer was. Avoid camera movement and interaction.
5. Privately share `Settings\wowb-cursor-samples.jsonl` from the output folder.
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

For NPC labels, the analyzer reports fingerprint overlap only for visible
Windows cursors with a matching candidate GUID and available fingerprint. For
off-target controls, it does not require a candidate GUID match: pointer
position is user-reported and the candidate may be stale. A match between an
off-target and on-target fingerprint is evidence against relying on that
fingerprint in the observed situation. Distinct fingerprints are not proof of
a dagger or attackability; separate visual notes and broader tests are needed.

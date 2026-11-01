# 70291 NPC cursor diagnostics

The user reports that an attackable NPC changes the cursor to a dagger, while
friendly town NPCs do not. This is a promising **observation**, not yet a
proven attackability test. The relationship-table offsets are still useful, but
a template-only comparison does not incorporate reputation, overrides or the
other checks in the full attack predicate.

The passive buttons in **Gather & tools → NPC cursor probe** capture the Windows
`GetCursorInfo` flags and handle and, if available, a SHA-256 fingerprint of
`GetIconInfo`'s mask/color bitmaps and hotspot. It never stores those bitmap
pixels or screen images. It also records full selected and mouseover-*candidate*
GUIDs and whether they match, with a user-supplied label. These passive buttons
do not send mouse or keyboard messages. A separate, explicitly started
background experiment posts two mouse-move messages, as described below; it
sends no mouse clicks or keyboard messages. Neither mode calls client functions,
uses Lua, writes game memory, interacts, selects or attacks. Both are disabled
while the bot is running or paused. **Grind, targeting, the existing failsafe
and selling are unchanged.**

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
unsafe. The separate, explicitly opted-in test below investigates this before any
gameplay integration is considered.

## Background posted-hover experiment

This is a **separate opt-in test**, not part of normal bot operation. It posts
at most two `WM_MOUSEMOVE` messages to the game: one to an automatically chosen
point away from the recorded point, then one back. It does not post mouse buttons
or keys, directly move the desktop pointer, call game functions, or select or
attack. The game can still respond to hover messages (for example, show a
tooltip); these are real messages, not a read-only test. If a guard fails after
the first post, the second is not sent.

1. Build, attach to wowb.exe and stop the bot. Use windowed or borderless mode;
   do not minimize the game. Choose a safe, stationary living boar and select
   it in the game. Don't change character position, target, camera, game window
   position or size during the test. Avoid UI covering the boar in the game.
2. Open **Gather & tools → NPC cursor probe**, press **1. Prepare foreground
   hover**, return to the game, hover the *selected boar* with no buttons held,
   and hold still for at least 10 seconds. The capture begins after 8 seconds.
   Return to the tool and check that it says **Foreground hover prepared**. If
   not, repeat step 1. The preparation is in memory only and expires in two
   minutes; do not restart the app between steps.
3. Bring the **NPC cursor diagnostics** window to the foreground (for example,
   with Alt-Tab) and release Alt. Press **2** on the main keyboard or numpad,
   or click **2. Test posted hover in background**. The `1` key likewise starts
   preparation. These shortcuts work only while the diagnostic window has
   keyboard focus; they are not global hotkeys. Moving the desktop mouse between
   the foreground capture and this step is allowed. Once step 2 starts, release
   all mouse buttons and leave the desktop pointer still. Three seconds after
   the key or button press, the diagnostic takes its desktop-position baseline
   and records the
   foreground desktop cursor and mouseover candidate; it posts an away move,
   waits 1.25 seconds and records again, then posts a return move, waits and
   records again. It stops if focus, attachment, bot state, desktop pointer,
   mouse buttons, Stop generation, game window, selected unit, or player/target
   observed identity or position change. It also requires readable, nonzero player
   and target health and a clear provisional player combat bit (`0x80000`),
   which is not proof that the area is safe. Position tolerance is 0.5 yards; player
   facing is checked. Camera changes and all change-away-and-back races cannot
   be ruled out. Pressing Stop during the experiment prevents subsequent posts
   at the next guard check; it does not start or stop the bot.
4. When it reports **Saved background hover test**, privately share
   `Settings\wowb-posted-hover-samples.jsonl` from the output folder. The file
   appends and is gitignored. If it cancels instead, share the displayed reason;
   rerun from step 1. The result describes the specific guard that stopped it;
   the NPC wandering, a changed game window or a pressed Stop can also cancel the
   test even if the physical mouse did not move. Don't move the cursor until it finishes.

The file contains player, selected and mouseover-candidate GUIDs, cursor handles,
flags and image fingerprints, but not screen images, cursor pixels, desktop
coordinates or the recorded hover point. Share it privately. The optional
analysis command is:

```
python Tools/WowBCursorAnalysis/analyze_posted.py path/to/wowb-posted-hover-samples.jsonl
```

A candidate change away from the selected GUID and back is a useful transition,
not proof of exact hover or attackability. The automatically chosen away point
may be on a different unit, empty world, or game UI. No change is inconclusive,
including if the background client doesn't process the messages quickly enough.
Windows reports the cursor of the actual foreground desktop, which may be the bot
window rather than the background game; even matching a foreground dagger hash
can be a stale or unrelated desktop cursor. Bitmap stability and identity checks
are non-atomic, and the partial build signature is not full build verification.
This diagnostic makes no gameplay decisions and does not enable dagger targeting.

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

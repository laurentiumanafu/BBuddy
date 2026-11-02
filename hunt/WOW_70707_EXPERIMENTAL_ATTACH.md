# Experimental attachment to build 70707

Build 70707 is **not yet ported**. It is discovered automatically at the user's
request, and uses a small set of statically identified global candidates. The
remaining 70291 layouts and offsets are unverified for 70707: snapshots, player
location, targeting, movement, combat, selling, STOP behavior and other
functions may fail or act incorrectly. Automatic attachment does not validate
those candidates, disable existing safety checks, or enable Lua.

1. Build CopilotBuddy for x64 as usual (`Tools\Build.cmd`).
2. Start the 70707 game and log in, then launch `CopilotBuddy.exe` normally.
   No special launch argument is needed. Discovery includes `Wow`, `Luacct`,
   `WowB` and `Wow-decoded`. To select a differently named process, optionally
   pass `/pid=1234` with its actual Windows PID.
3. The log identifies the experimental build and warns that layouts are not
   validated. A successful attachment does not establish that the bot can run.
   `[70707 candidates]` logs candidate GUIDs, player name, pointers, a proposed
   map field and camera zoom. `[70707 player record]` and `[70707 selected
   record]` log read-only pool observations and candidate raw health, level and
   faction values, not gameplay authorization. These logs contain identifying
   information and process addresses. With the bot stopped, Combat Settings →
   Factions → Scan nearby also logs a selection-free 70707 research report;
   candidate keys and approximate distances do not establish attackability.
4. The attach snapshot recognizes the observed 70707 player and selected-unit
   vtables, but these are still candidates. Record transforms are displayed for
   research only; player position, map ID and camera position remain unavailable
   to gameplay code. Legacy 70291 health, level, flags and faction reads are
   disabled on 70707. Grind refuses to start on 70707 in every faction mode,
   including Off, until its required movement and combat layouts are validated.
   Gathering also refuses to start and the 70291 node scanner rejects 70707.
   The existing 70291 faction-signature and identity checks are unchanged.

The 70707 client must be x64 and its version resource must be readable. The
selector, explicit PID and automatic attachment apply the same build policy;
12340/70291 paths and the existing version/name exceptions for other builds
remain unchanged.

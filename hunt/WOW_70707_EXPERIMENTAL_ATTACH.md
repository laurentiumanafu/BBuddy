# Experimental attachment to build 70707

Build 70707 is **not supported or ported**. This opt-in attaches it to the
existing x64 path at the user's request, selecting a small set of statically
identified 70707 global candidates. The remaining 70291 layouts and offsets
are unverified for 70707: snapshots, player location, targeting, movement,
combat, selling, STOP behavior and other functions may fail or act incorrectly.
This switch does not validate those candidates, disable existing safety checks,
or enable Lua. Do not use it where incorrect input or memory access would be
unacceptable.

1. Build CopilotBuddy for x64 as usual (`Tools\Build.cmd`).
2. Launch the resulting `CopilotBuddy.exe` with `/unsafe-wow70707`, for example
   by adding a space and `/unsafe-wow70707` to a Windows shortcut's Target.
3. Start the actual 70707 game process and connect as usual. Auto-discovery
   includes `Wow`, `Luacct`, `WowB` and `Wow-decoded` with this switch. To choose
   another named process, also pass `/pid=1234` with its actual Windows PID.
4. The log identifies the experimental build and warns that offsets mismatch.
   A successful attachment is not evidence that the bot can operate correctly.
   The `[70707 candidates]` line logs candidate GUIDs, player name, pointers and
   camera zoom; it may contain identifying information. The map ID and camera
   position remain unavailable because their 70707 layouts are not established.
   Grind's 70291 faction-signature checks are still enforced.

The switch is per launch, case-insensitive, and only opts in build 70707; normal
12340/70291 behavior is unchanged. The 70707 client must be x64 and its version
resource must be readable. Without the switch a process reporting 70707 is
rejected even if named `WowB`. The selector and automatic attachment apply the
same rules. Existing version/name exceptions for other builds are unchanged.

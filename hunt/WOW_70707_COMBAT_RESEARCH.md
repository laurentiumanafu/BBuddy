# 70707 combat-state research: initial stage 3

This is a research tool, not an implemented cooldown, cast or aura reader. It does not enable new combat actions. All observations below are version-specific; retain the 70707 mana-type and other gameplay gates. No Lua, game function calls, process writes, input injection, or automatic spell presses are used.

## What was found statically

The user's 70707 decoded executable (SHA-256 `2cfec8708b648efd8b78db4cd52da78ede44de39ce229a0175a572b749c81a89`) contains registration sequences associating the following names and handler RVAs. These are reverse-engineering anchors, **not callable APIs or verified data offsets**. The registration handlers have obfuscated, overlapping control flow; naive linear disassembly can decode junk or unreachable paths as real code. The handler names do not prove a particular internal data layout.

| Registered name | Name-reference instruction RVA | Handler RVA |
| --- | ---: | ---: |
| UnitCastingInfo | `0x182832F` | `0x17E1E00` |
| UnitChannelInfo | `0x1828363` | `0x17E3FE0` |
| GetActionCooldown | `0xA5CAD9` | `0xA44450` |
| GetSpellCooldown | `0x15E5325` | `0x15CA110` |
| GetUnitAura | `0x16F8E30` | `0x16EA070` |
| GetUnitBuff | `0x16F8E6E` | `0x16ECD40` |
| GetUnitDebuff | `0x16F8EAC` | `0x16EF9E0` |
| UnitPowerType | `0x1828FE1` | `0x18168F0` |
| GetComboPoints | `0x1827E69` | `0x17CE850` |

At `0x17CEA23..0x17CEA3F` a path in the `GetComboPoints` handler calls `0x20AC3B0` with `DL=4` and reads a dword at `[rbx+rax*4+0x20C8]` if the returned index is not `-1`. This is a **static lead only**: the meaning of the index, `rbx`, alternate paths and live representation must be established with actual combo-point observations. Do not use this as a gameplay reader or treat all `+0x20C8` values as combo points. A Shaman or Mage alone cannot validate energy, rage, combo points or pets.

## Manual trace procedure

1. Attach normally to build 70707 and enter the world. Stop Grind, Gather and routes. Open **Combat Settings → Combat research**.
2. Start with a harmless self-buff if available. Enter a label including character class, spell name/rank and scenario, e.g. `Mage Frost Armor rank 1; self`. Select a durable NPC first only if an NPC target window is needed; no target is needed for self-buff research.
3. Press **Record 25 seconds**, wait until the status says **Recording**, return focus to the game, wait approximately five seconds, then press the spell key **once yourself**. Do not start the bot or change character/target during the trace. Record the approximate elapsed time, the spell, displayed result and duration/cooldown, if any. Wait until recording finishes.
4. Repeat separately for Shaman and Mage. A cast-time spell on a durable, safely engaged target is useful for cast-state research. A spell with a visible cooldown is useful for cooldown research. Don't assume either character has a particular spell. If a target dies or disappears, the recorder stops and retains earlier valid samples; report that fact.
5. Share the specific `Logs/CombatResearch/*.txt` file and the notes privately. The file contains raw memory, including GUIDs, process addresses and potentially sensitive state; inspect it before sharing. It does not record keyboard input, screen images or game text, but raw memory may still contain sensitive data.

Use one action per trace, and include a separate no-action control trace. Changing multiple things makes comparisons ambiguous. A 25-second trace can show an aura being applied but cannot prove an aura has expired if it lasts longer; use a later separate trace and note the actual timing. The user can stop recording with the button or close the window. Starting/pausing a bot, changing target/player/attachment or the decodable action-bar preview, or losing identity stops the trace; the inconsistent sample is discarded. Completed earlier samples are retained and labeled incomplete.

## Data format and limitations

The recorder checks at least two of four partial build signatures with no readable mismatch, takes a pool snapshot, validates player and optional selected NPC identity (GUID, back-pointer, vtable), reads 12 candidate action-bar slots as a preview, and checks identity and current selection before **and after each sample**. The recorder only reads known unit memory windows `+0x0000..+0x23FF` in bounded 256-byte chunks; it does not dereference any arbitrary captured pointers. Up to 100 samples are taken at an intended 250 ms interval. Actual start/end times and UTC are recorded because process reads and file writes take time. Reads are non-atomic, ABA changes can be missed, and each sample is not an exact instant.

Each chunk starts with a hex-byte `baseline`. Later lines list changed little-endian dwords as `+0xOFFSET:OLD>NEW`. An unreadable chunk is labeled unreadable, not zero; when it becomes readable it receives a new baseline. The final status and sample count are in the file. Changes can be caused by unrelated state, memory reuse, animation or timing; correlation is not proof. The relevant cast, cooldown or aura data may live **outside these windows**, so lack of changes proves nothing. Action-bar preview is descriptive and never grants gameplay access.

Suggested analysis: `python Tools/analyze_combat_trace.py path/to/trace.txt --around 5 --radius 2 --spell-id 133` (replace the time and ID with the actual observations). This reports raw changes around an event and value matches; neither proves semantics. For validation, compare several repetitions, controls, expiration and different spells on both characters. Only then consider adding guarded read-only field decoders and later integrating them into combat.

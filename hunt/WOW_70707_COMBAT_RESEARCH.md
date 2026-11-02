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

## First Mage traces (2026-11-02)

The supplied trace labels are **Mage Fireball**, **Nothing**, and **Frost Armor**. All three completed 100 samples at roughly 250 ms intervals. They use the same Mage but the Fireball trace has a selected NPC and subsequent combat; the other two have no selected target. The idle trace is therefore a player-side control, not a target/combat control. Times below are relative to recording start, with 250 ms sampling uncertainty and non-atomic reads. No raw GUIDs or process addresses are reproduced here.

- **Fireball:** the action preview has spell `133`. At approximately 5.77 s player `+0x03C0` and `+0x03EC` both changed `0 → 133`; player `+0x0418/+0x041C` changed from zero to a pair differing by 1200. All four cleared at about 7.02 s. The same pattern occurred again around 22.54–24.04 s, but both are in the same combat trace and are not independent character/client validations. The second timing pair also changed while active, so it is not safe to assume timings cannot be adjusted. `+0x02D8` bit `0x40` was set and cleared in those same sampled intervals. These are promising **cast-state candidates**, not a reliable general cast or GCD reader.
- **Possible target effect:** at about 7.77 s, the selected target's `+0x06F0` changed `0 → 133`, `+0x06FC` `0 → 4000`, and `+0x0700` `0 → 0x020E8C49`; all cleared around 11.78 s. At the first sample, the target's `+0x0700` was about 3,943 above the player's concurrently changing `+0x1C10` word; at clearing, the latter exceeded it by about 116. This is consistent with an effect ID, duration and expiration in millisecond-like units, but one trace cannot establish aura layout, timebase, ownership, slot capacity, or behavior with multiple effects. A different ID, `6136`, appeared in that same area after the first cleared; its identity and cause were not independently established. The target's candidate health fell `120 → 100` at approximately 7.77 s, followed by two single-point decreases; these are correlation, not proof of individual spell effects.
- **Frost Armor:** the user confirmed Frost Armor was already active before the press, so this trace is a refresh attempt rather than a clean application. At about 7.01 s the player power-array candidate `+0x1BC0` changed `550 → 490`, consistent with spending power. The instant action did not produce the Fireball cast-like transitions in the sampled player window. Although some baseline words numerically equal `168` (the action-bar Frost Armor ID), the same values were already present in the Fireball and no-action traces and did not change on this press. They do **not** validate a Frost Armor aura.
- **Control and power:** the no-action player trace changed only `+0x0474` and `+0x1C10`, so those changes alone are not spell evidence. In the Mage Frost Armor trace, player `+0x20C8` took values `535 → 490 → 544`; it is plainly not a validated fixed combo-point counter. This reinforces that the indexed static path above must not be simplified to a fixed `unit+0x20C8` read.

## Shaman comparison (2026-11-02)

The two subsequent user-supplied traces are labeled **Shaman Nothing** and **Shaman Lightning Bolt**; each completed 100 samples. They use the same Shaman but different selected NPCs. Different characters do not need to share a target, and these traces support a player-side comparison; differences between the two targets are not controlled.

- Lightning Bolt is action-bar ID `403`. Player `+0x03C0` and `+0x03EC` both changed `0 → 403` around 6.01 s and cleared around 7.51 s; the corresponding `+0x0418/+0x041C` pair differed by 1500 and cleared at the same time. The same pattern recurred around 19.03–20.53 s in this combat trace. `+0x02D8` bit `0x40` also set and cleared with both. At each clearing, `+0x1BC0` changed `141 → 126`, consistent with 15 power spent. This is a second class and different spell supporting the same cast-like candidates as Mage Fireball, but the two occurrences in one recording are not independent trials; no claim is made about how many keys were pressed or whether a cast succeeded solely from these fields.
- The Shaman **Nothing** trace did not show the Lightning Bolt pattern. It was not a perfectly static control: around 10.27 s the candidate maximum health changed `118 → 148`, and a raw player word `+0x1280` changed `0 → 1243`, with subsequent health increases. Their cause is unknown; the no-action label alone does not establish why these changes happened. They illustrate why a control can contain external or passive events.
- The no-action and Lightning Bolt NPCs differ; comparing their target memory directly would confound NPC-specific activity with spell effects. The available traces do not establish a general cooldown, universal GCD, aura layout, cancellation or channel behavior. A fixed raw `+0x20C8` still must not be used as a combo counter.

No candidate above has been wired into combat or used to remove the existing mana-type gate. For additional read-only validation, compare another cast-time spell, a cancelled cast and a channel if available; record visible behavior and note interruptions. A self-buff trace starting with the buff absent and another after expiration would help distinguish application from refresh.

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

Suggested analysis: `python Tools/analyze_combat_trace.py path/to/trace.txt --control path/to/control.txt --around 5 --radius 2 --spell-id 133` (replace the time and ID with the actual observations). The optional control comparison lists only player offsets that changed in the test but not in the control; it does not assume the targets are the same or prove that differing changes are spell-related. This reports raw changes around an event and value matches; neither proves semantics. For validation, compare several repetitions, controls, expiration and different spells on both characters. Only then consider adding guarded read-only field decoders and later integrating them into combat.

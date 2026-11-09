# Experimental 70862 Shaman totem effects

With **Apply self-buffs** and **Re-cast supported buffs when they run out during combat** enabled in Combat settings, Grind and GatherBuddy defence can maintain these exact ranks **only during combat**, using the experimental player-effect candidate:

| Totem spell | Player effect candidate | Rank / evidence |
| --- | --- | --- |
| Strength of Earth Totem, 8160 | 8162 | Rank 2; inferred from the user's effect traces and public spell data |
| Healing Stream Totem, 5394 | 5672 | Rank 1; inferred from the user's effect traces and public spell data |
| Mana Spring Totem, 5675 | 5677 | Rank 1; in the user's labelled no-totem, active and on/off/on traces, 5677 follows Mana Spring; 5675 also appears on the action bar. Earth effect 8162 also changed during the on/off/on trace; this is correlation, not captured proof of the actual cast. |

Other ranks and spells are **not** mapped to these effect IDs. The trace does not prove a cast-to-effect association or the general aura layout. Mana estimates are 65 for Earth and 40 for either Water totem; costs, modifiers, mana type and cast success are not verified.

## One user-made macro key

Open **Combat settings → Spells → Shaman totem macro…**, enable it, select key 1–9, the number of presses per burst (1–9), and the effects to check. Save the dialog and then **Save** Combat settings. The option is off by default, and changes are only allowed while stopped. Create the macro in WoW yourself, put it on the normal first action bar at that key, bind that key normally, and test it manually. Include only the selected exact totem ranks in a cast sequence and set the press count accordingly. The bot cannot inspect the contents, binding, current cast-sequence step, cooldown, or successful application. A macro may recast an already-active totem because it cannot skip to the specific missing step. No Lua is injected or called by the bot.

**Healing Stream and Mana Spring are both Water totems; choose at most one.** They cannot normally coexist. The dialog refuses both selected; invalid hand-edited settings also withhold the macro. If both individual Water spells are eligible on the action bar, both are withheld to avoid replacing each other; remove one or set its role to Ignore. When the macro maintains a Water totem, direct automatic attempts for both Water totems are suppressed. A selected Earth spell is likewise managed only by the macro, not the individual buff path. Other individual buffs keep their usual behavior.

The macro key cannot be key 1 while key 1 is reserved for the Start Attack macro. The macro slot is reserved from automatic spell/item selection while enabled. A nonempty, stable action-bar slot is required but does **not** establish that it contains the intended macro or is actually bound to that key. A missing, changed or all-zero action bar fails closed.

In combat, if any selected player effect is read as absent, the bot attempts the configured number of macro presses separately, with at least the normal 1.8-second cast/global-cooldown spacing, while allowing normal fight processing and heal/STOP/target safety to take priority. It waits **at least 10 seconds after the last press** before starting another burst, then checks again and starts one only if an effect is still missing. If combat ends, guards fail, mana is too low or the aura becomes unknown, a press is withheld. A burst interrupted for 10 seconds is abandoned; when eligible, a fresh burst can start after the retry gate. Since the next macro step is unknown, each press requires the most expensive selected rank's estimated mana (65 if Earth is selected, otherwise 40). These are estimates, not verified affordability. The bot does not wait 10 seconds blocking combat.

After an automatic Stoneclaw attempt, the entire opaque totem macro and individual Strength of Earth are withheld for 20 seconds to avoid immediately replacing it; see `STONECLAW_70862.md`. The hold is estimated, not a detection of an active Stoneclaw totem. Make sure your macro has no other Earth spells that could replace it; manual casts remain outside the bot's control.

## Individual keys

Alternatively, put the exact spells in normal first-bar slots 1–12 with their expected bindings. Assigning Ignore prevents individual attempts. If key 1 is reserved for the Start Attack macro, use other slots. The player's effect, not a duration, is checked in combat. A consistently readable candidate saying the effect is absent permits a key attempt; a detected active effect does not. Attempts for each spell are spaced at least 10 seconds apart. There are no attempts out of combat or travel interruptions for these totems. STOP, combat ending, target/action-bar validation and other safety gates can delay or prevent an attempt. **Recheck is not a guarantee of a successful cast.** The combat-buffs option is off by default.

An effect can disappear when you leave the totem's range even if the totem still exists. If missing during combat, the bot may attempt a replacement. This can consume mana or replace another totem. Turning buffs off disables this behavior.

The external layout remains experimental. The reader is restricted to 70862, bounded to at most 24 candidate slots and a committed readable region, double-reads its candidate data and header, and requires stable active-count/slot agreement and no duplicate IDs. Unknown, unreadable, unstable or unsupported layouts **withhold** totem and macro keys, including in combat following startup or resurrection. Guards reduce but cannot eliminate non-atomic-read, pointer reuse, incorrect layout, or ABA risks. Other buff scheduling is unchanged.

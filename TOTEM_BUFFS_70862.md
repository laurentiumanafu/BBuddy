# Experimental 70862 totem effect buffs

With **Apply self-buffs** and **Re-cast supported buffs when they run out during combat** enabled in Combat settings, Grind and GatherBuddy defence can maintain two specific first-action-bar spells **only during combat**, using the experimental player-effect candidate:

| Totem spell on bar | Player effect candidate | Rank mapping |
| --- | --- | --- |
| Strength of Earth Totem, 8160 | 8162 | Rank 2, suggested by the user's effect trace and public spell data |
| Healing Stream Totem, 5394 | 5672 | Rank 1, suggested by the user's effect trace and public spell data |

Put these exact spells in normal first-bar slots 1–12 with their expected key bindings. If key 1 is reserved for the Start Attack macro, use other slots. Other ranks and spells are **not** mapped to these effect IDs; the traces did not include the casting keys, and a live 70862 cast-to-effect association still needs confirmation. Assigning Ignore in the Spells tab prevents attempts.

The player's effect, not a duration, is checked in combat. A consistently readable candidate saying the effect is absent permits a key attempt; a detected active effect does not. Attempts for each spell are spaced at least 10 seconds apart, and effect checks resume at the next eligible combat tick. There are no attempts out of combat or travel interruptions for these totems. The normal fight/target safety checks and estimated mana gates (65 and 40, respectively) must pass; costs and modifiers are not verified. STOP, combat ending, action-bar validation and other safety gates can delay or prevent an attempt. **Recheck is not a guarantee of a successful cast.** The combat-buffs option is off by default.

After an automatic Stoneclaw attempt, Strength of Earth is withheld for 20 seconds to avoid immediately replacing it; see `STONECLAW_70862.md`. The hold is estimated, not a detection of an active Stoneclaw totem.

An effect can disappear when you leave the totem's range even if the totem still exists. If the effect is missing during combat, the bot may cast a replacement in that case. This can consume mana or replace another totem of the same element. Turning buffs off disables this behavior.

The external layout remains experimental. The reader is restricted to 70862, bounded to at most 24 candidate slots and a committed readable region, double-reads its candidate data and header, and requires stable active-count/slot agreement and no duplicate IDs. Unknown, unreadable, unstable or unsupported layouts **withhold** these totem keys, including in combat following startup or resurrection. It does not follow other pointers, use Lua, or write game memory. Guards reduce but cannot eliminate non-atomic-read, pointer reuse, incorrect layout, or ABA risks. Other buff scheduling is unchanged; the new totem mappings only run on 70862.

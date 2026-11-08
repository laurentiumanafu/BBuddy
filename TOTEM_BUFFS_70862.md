# Experimental 70862 totem effect buffs

With **Apply self-buffs** enabled in Combat settings, Grind and GatherBuddy can maintain two specific first-action-bar spells using the experimental player-effect candidate:

| Totem spell on bar | Player effect candidate | Rank mapping |
| --- | --- | --- |
| Strength of Earth Totem, 8160 | 8162 | Rank 2, suggested by the user's effect trace and public spell data |
| Healing Stream Totem, 5394 | 5672 | Rank 1, suggested by the user's effect trace and public spell data |

Put these exact spells in normal first-bar slots 1–12 with their expected key bindings. If key 1 is reserved for the Start Attack macro, use other slots. Other ranks and spells are **not** mapped to these effect IDs; the traces did not include the casting keys, and a live 70862 cast-to-effect association still needs confirmation. Assigning Ignore in the Spells tab prevents attempts.

The player's effect, not a duration, is checked. A consistently readable candidate saying the effect is absent permits a key attempt; a detected active effect does not. Attempts for each spell are spaced at least 10 seconds apart, and effect checks resume at the next eligible tick. The bots also check while walking long hotspot/resource legs and yield to their normal loop if a totem needs attention. Combat, selling, death, stop, action-bar validation and other safety gates can delay a check or cast. **Recheck is not a guarantee of a successful cast.**

With **Re-cast supported buffs when they run out during combat** enabled, the two totems are also attempted during Grind/defence only when the normal fight/target safety checks pass and estimated mana gates (65 and 40, respectively) are satisfied. Costs and modifiers are not verified; out-of-combat attempts use the same estimates. The combat option is off by default.

An effect can disappear when you leave the totem's range even if the totem still exists. In accordance with the requested policy, the bot may cast a replacement in that case. This can consume mana or replace another totem of the same element. Turning buffs off disables this behavior.

The external layout remains experimental. The reader is restricted to 70862, bounded to at most 24 candidate slots and a committed readable region, double-reads its candidate data and header, and requires stable active-count/slot agreement and no duplicate IDs. Unknown, unreadable, unstable or unsupported layouts **withhold** these totem keys, including on startup and after resurrection. It does not follow other pointers, use Lua, or write game memory. Guards reduce but cannot eliminate non-atomic-read, pointer reuse, incorrect layout, or ABA risks. Other buff scheduling is unchanged; the new totem mappings only run on 70862.

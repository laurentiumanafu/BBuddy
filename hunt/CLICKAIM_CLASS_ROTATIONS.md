# ClickAim low-level hotbar priorities (stage 1)

This is a small set of experimental **WowB Grind/Gather-defence** priorities, not Singular or a specialization engine. Choose **Combat settings → Spells → Rotation class**. The setting is saved in `Settings/clickaim-grind.txt`. `Auto` (default) looks for known class-specific action-bar IDs; if it finds no class evidence or evidence for more than one class it uses `Generic`, the previous first-spell selection. Choose a class explicitly when Auto cannot infer one. Class inference is not a player-class memory read and is not proof of the class, spell rank or client version.

The priorities use only recognized spells on mapped action-bar keys. The regular healing threshold, minimum heal power, DoT toggle and refresh interval, buff setting and role overrides remain in force. An explicitly overridden unknown spell can fill an otherwise empty damage, DoT or heal slot. `Ignore` vetoes a spell, including Attack. `Generic` retains the old selector and can therefore include abilities that class profiles intentionally omit. Check the per-fight `hotbar rotation` log to see the resolved profile and selected actions.

| Profile | Basic priority in addition to healing, configured buffs and auto-attack |
| --- | --- |
| Druid | Moonfire / Insect Swarm DoT, Wrath / Starfire filler. No automatic form skills. |
| Hunter | Serpent Sting, Arcane Shot with an estimated interval; Raptor Strike when in melee. No automated Auto Shot toggle or pet management. |
| Mage | Frostbolt / Fireball filler; Fire Blast in range after damage is observed. |
| Paladin | Heal, configured buffs/seal, auto-attack and Crusader Strike if present. Judgement is withheld because a seal cannot be verified. |
| Priest | Shadow Word: Pain, Smite, then Mind Blast in range after damage is observed. |
| Rogue | Sinister Strike and auto-attack; no combo-dependent finishers. |
| Shaman | Flame Shock, Lightning Bolt filler, Earth Shock in range after damage is observed. Shocks share a conservative timer. |
| Warlock | Corruption / Immolate / Curse of Agony and Shadow Bolt. |
| Warrior | Rend in melee, Heroic Strike and auto-attack. |
| Death Knight | Explicit profile only: auto-attack and explicitly overridden actions; rune abilities are withheld. No DK spell IDs are inferred. |

Within a list the first **available** action with the required effective role wins, not the first hotbar slot. Healing is handled before damage. Only one configured DoT is selected; application and duration are not read. If a class has no matching direct damage, the DoT or existing melee/Attack fallback can still be used. The Shaman, Mage and Priest short-range instant can be the only direct-damage fallback; it still uses an estimated interval. A failed optional instant does not force the main spell into melee fallback.

The cooldowns and Hunter minimum range are conservative estimates, not verified client values. Timers start on a keypress, not confirmation, and are retained across fights and stop/start in the same process. Learned power-cost estimates and the existing cast verification are also imperfect: target damage could come from another source. Auto-detection and spell IDs are based on the existing WotLK name table, which is not verification of 70707 spell behavior. No specialization, live cooldown, aura, casting, rune, form, combo, pet or proc state is read. Explicit overrides can defeat the conservative omissions and are the user's responsibility. Default keybindings are assumed.

Existing faction, target identity, attachment, session/action-bar, STOP, movement, healing and resource checks remain in the fight path. On 70707, the existing experimental session still admits only verified mana-type characters; having a Warrior, Rogue or Death Knight profile does **not** enable rage, energy or runes there. The 70291 power type is also not verified. The separate 12340 bot path is not replaced or converted to this WowB action-bar engine.

`Tools/WowBReaderTests/RotationTests.cs` exercises synthetic hotbars and timers, not a Windows build or a live game. Verify the selected keys, spells, range and behavior in the target client; future stages need validated spell/client state before expanding these priorities.

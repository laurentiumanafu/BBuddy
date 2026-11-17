# Heal priority, and where the 1.8 s went

The user's question on 2026-11-17: *"when healing is there a delay of 1.8 seconds
before retrying ? if character is under 50% and it has enough mana healing should
have priority"*.

The 1.8 s is `ClickAimCombat.CastSettleMs`, the client's global cooldown. Before this
change every combat key recorded the same `lastCombatKeyMs`, so auto-attack, Start
Attack and the key-1 macro follow-up charged the heal a cooldown they never started.
There are now two clocks:

* `NoteSpellKey()` -> `lastCombatKeyMs` -> `CastSettleMs` (1800 ms). Only real spells:
  the heal, the DoT/Shock, the cast, Stoneclaw, in-combat buffs and the melee ability.
* `NoteCombatInput()` -> `lastInputKeyMs` -> `KeySettleMs` (250 ms). Auto-attack,
  Start Attack and the key-1 macro: long enough for the client to have processed the
  keypress, not long enough to cost a heal.

`GlobalCooldownClear()` requires both gaps.

Under `ClickAimCombat.HealUrgentPercent` (50 %) **with power to pay for it**, healing
outranks `HealRetryMs` (4000 ms): the next attempt waits for the cooldown only, and no
damage key is sent while it waits. Above that line the fight keeps going and only the
cooldown itself is waited out, as before.

The heal is never held. The gates are still the configured health line, power, the
retry spacing and the shared cooldown; see
`Tools/WowBReaderTests/HealPolicyTests.cs`.

`python3 Tools/WowBHealPriorityTests/test_heal_priority.py` is a static wiring check
of the two clocks, the call-site classification, the urgent retry and the absence of
any hold. It does not run the game, cast a heal or prove the client's cooldown.

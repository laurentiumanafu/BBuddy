# Supplied player-state leads (diagnostic only)

The user supplied leads from another agent's `wow-decoded.exe`; its hash and
build were not included. They cannot yet be assumed to match 70938. Claimed
player getter RVA `0x1F6EC10`; form getter RVA `0x241DCC0`; combo getter VA
`0x1417E4AE0`; attack-begin VA `0x141FAFFA0`; state helper VA `0x141FB4880`;
alternate target helper VA `0x1420BA540`. `0x1471210` was supplied as the
StartAttack guard but its address format is ambiguous; it has not been fixed up
or called. This implementation has not independently disassembled the stated
executable or executed a getter. No code executes client functions or uses Lua.

The bot's existing pool scan finds a local record and its player-vtable unit
pointer, `UnitAddress`. We do **not** know that this is the object returned by
`0x1F6EC10`; pool identity, vtable, plausible health, and a second health read
do not establish equivalence. Nor do they prove the supplied addresses match
70938. Therefore the candidates are reported in the existing read-only Combat
research trace on 70938 only; they do not influence attacks, targets, forms,
combo-point rotations, movement, or any other gameplay. Other builds are
unchanged. Missing bytes are reported as unreadable, not zero. The 64-bit
candidate target values are reported as raw qwords, not a full 128-bit WowBGuid.
The attack-effect pointer is reported but never followed. The form flag and raw
form byte are both printed; a clear flag does not prove the character is in no
form. Combo points are printed as both raw byte and signed value, reflecting the
reported `movsx` without deciding what negatives mean.

## Collecting evidence

Stop the bot and use **Combat research**; select **Player only** so target
changes do not stop the recording. Record separate traces with clear labels:
1. No attack, start auto-attack manually, then stop it; target a durable NPC.
2. Without a target, select one manually and clear it; do not fight.
3. Druid: change between normal and each available form manually.
4. Rogue or appropriate class: build and spend combo points manually.

Wait for the Recording message before acting. The trace has 250 ms samples and
may miss short changes; each sample prints these candidates plus the existing
health and maximum-health candidate with a fresh same-base reread. A health
mismatch can reflect a real health change, not necessarily an invalid pointer.
The values are non-atomic and may change for unrelated reasons; even perfect
correlations do not prove pointer identity. Logs under `Logs/CombatResearch`
contain GUIDs, process addresses and other sensitive data. Captures require
stable player identity and attachment, and never post inputs.

To promote these leads to gameplay, first identify the exact executable by hash
and independently verify its instructions, derive the getter's returned object
without unsafe guessing, and compare that actual pointer to `UnitAddress` in
live sessions. Then validate transitions against user-observed attack/form/combo
state, including unknown/unreadable states and target changes. Do not invoke a
client function or infer an exact 128-bit GUID from the supplied qwords.

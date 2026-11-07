# 70862 attacker-reference research

This is diagnostic only, **not a defence implementation or proof of attacker identity**. During Grind defence, if there is no permitted nearby candidate but blocked NPCs are present, 70862 can log up to four nearby blocked NPCs' exact local-player-GUID matches in bounded record/unit memory windows. The observation is throttled to once per ten seconds and each pass has a 250 ms sampling budget. Reads are non-atomic, sections may be unreadable or partial, and a GUID may represent any relationship. No matching offsets are used to select, move or attack.

The log can help identify leads for a later controlled comparison of the same NPC while idle, targeting someone else, and actually attacking the player, alongside non-attacking NPCs. Even an NPC targeting the player does not itself prove it is attacking. Do not turn an observed offset into an authorization without separate evidence and appropriate live checks.

Run `python Tools/WowBAttackerResearchTests/run.py` with .NET 10 SDK for synthetic GUID, overlap, interruption and revalidation tests. These do not test real game memory or hostility.

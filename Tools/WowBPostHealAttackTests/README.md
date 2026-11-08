# 70862 post-heal Start Attack binding

The post-heal checkbox is unchecked by default. Its remembered key selection does not enable it. The user must bind it in the game to a non-toggle **Start Attack** action and manually confirm repeated presses do not stop attacking. The bot cannot inspect game bindings or swing state. This uses normal key input, not Lua injection. It queues at most one guarded keypress after each heal with an observed net health gain or power drop; those changes do not prove a heal landed. No toggle retry is inferred from a silent target.

`python Tools/WowBPostHealAttackTests/test_post_heal_structure.py` checks integration wiring only. Production policy synthetic checks are in `Tools/WowBReaderTests/HealPolicyTests.cs` and require the .NET 10 SDK. Neither test establishes the behavior of a live 70862 keybinding, heal, or auto-attack.

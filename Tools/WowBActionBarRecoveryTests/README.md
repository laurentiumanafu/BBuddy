# 70862 action-bar recovery

`python Tools/WowBActionBarRecoveryTests/test_recovery_structure.py` checks source wiring only. The synthetic `Tools/WowBReaderTests` C# fixture covers a bar restored after the old 1.2-second window, persistent zero/unreadable bars, player identity changes, attachment changes, and STOP; it needs a .NET 10 SDK. Neither establishes why a live client returned all zeros, whether the game is connected, or whether recovery will succeed in game.

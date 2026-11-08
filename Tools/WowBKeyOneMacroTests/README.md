# Key 1 macro checks

`python3 -m unittest discover -s Tools/WowBKeyOneMacroTests -p 'test_*.py'` checks source wiring only. `Tools/WowBReaderTests/RotationTests.cs` exercises production rotation policies if the .NET test project is run. Neither test verifies bindings, a WoW macro, cast success, or swing state.

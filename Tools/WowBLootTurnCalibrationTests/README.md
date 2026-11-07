# Loot turn calibration tests

Run `python Tools/WowBLootTurnCalibrationTests/run.py` with the .NET 10 SDK. The fixture compiles the production turn-only calibration with synthetic facing, key taps and safety checks. It is not a game, real input or navigation test.

A recent corpse that requires a mesh approach may use a two-tap calibration if both 200 ms measurements are strong, opposite and similar in magnitude. Weak, inconsistent or implausible samples use the normal four-tap calibration instead. Existing valid calibration for the same window is reused. Other calibration callers retain the full procedure, and loot identity, reach, mesh, combat and click checks remain in place. Neither symmetry nor strength guarantees the measurements are accurate in a real client.

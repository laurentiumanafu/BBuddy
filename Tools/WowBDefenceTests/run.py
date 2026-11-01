#!/usr/bin/env python3
"""Exercise actual Grind defence, approach and dismissal against deterministic stubs.

Requires a .NET 10 SDK. No game, native input, or production project build is used.
"""
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "Styx/Logic/Pathing/ClickAim/GrindRunner.cs").read_text(encoding="utf-8-sig")
fixture = (Path(__file__).parent / "Fixture.cs.txt").read_text(encoding="utf-8")
# Include the exact production methods, not a second implementation of the policy.
level = source[source.index("    private static bool LevelViable("):source.index("    /// <summary>\n    /// Whether the walk should stop here.")]
defence = source[source.index("    private static bool DefenceFieldsValid("):source.index("    private static bool InRealCombat()")]
selection = source[source.index("    private static bool SelectionFieldsValid("):source.index("    // Do not treat a failed read or a corpse")]
current_selection = source[source.index("    private static bool TryCurrentSelection("):source.index("    private static bool ShouldApproachPoolCandidate(")]
dismissible = source[source.index("    private static bool IsDismissibleSelection("):source.index("    // F1 is the client's default self-target key")]
pull = source[source.index("    private static bool PullWorthTaking("):source.index("    /// <summary>\n    /// Stands at a hotspot")]
nearest = source[source.index("    private static bool NearestKillable("):source.index("    /// <summary>\n    /// Player-only 70291 candidate")]
approach_policy = source[source.index("    private static bool ShouldApproachPoolCandidate("):source.index("    private static bool PreferNearerPoolCandidate(")]
approach = source[source.index("    private static string ApproachPoolCandidate("):source.index("    /// <summary>\n    /// Drops a target the fight could not walk to")]
dismiss = source[source.index("    private static void DismissRejectedSelection("):source.index("    /// <summary>\n    /// Whether a mob of")]
chase = source[source.index("    private sealed class ChaseWatch"):source.index("    // A pool unit's position is only a navigation lead")]
# Guard the control-flow wiring as well: approach is before pixel search and
# both outer loops retry after it, rather than treating movement as a fight.
engage = source[source.index("    private static bool TryEngage("):source.index("    private static bool TryCurrentSelection(")]
assert engage.index("ShouldApproachPoolCandidate(have, haveNearest, nearestYd)") < engage.index("FindUnitPixel(")
assert "report = ApproachPoolCandidate(hwnd, nearest);" in engage
assert source.count("_combatHold || _poolApproachHandled") == 2
assert "out near, out nearYd, out live2, out unread2, null, false" in source
fight = source[source.index("    private static string Fight("):source.index("    private static", source.index("    private static string Fight(") + 20)]
assert fight.count("!defenceOnly && !FactionPullStillAllowed(") == 2
assert "expectedPull: target" in engage
start_profile = source[source.index("    public static bool Start("):source.index("    internal static bool StartRoaming(")]
start_roaming = source[source.index("    internal static bool StartRoaming("):source.index("    private static", source.index("    internal static bool StartRoaming(") + 30)]
assert "WowBGrindFactions.PrepareForGrind" in start_profile and "WowBGrindFactions.PrepareForGrind" in start_roaming
assert fixture.count("// __PRODUCTION_METHODS__") == 1
fixture = fixture.replace("// __PRODUCTION_METHODS__", level + "\n" + selection + "\n" + current_selection + "\n" + dismissible + "\n" + pull + "\n" + nearest + "\n" + approach_policy + "\n" + defence + "\n" + approach + "\n" + dismiss + "\n" + chase)
# Read constants from production so boundary and movement tests cannot pass
# against stale copies of the thresholds or watchdog limits.
constants = []
for name in ("LevelAbovePlayer", "LevelBelowPlayer", "SearchYd", "PullYd", "GiveUpYd", "PoolApproachYd",
             "PoolApproachTimeoutMs", "GatherDefenceRadius", "TabVk", "TabSettleMs", "NotAFightFlag",
             "StuckMoveYd", "StuckMs", "StuckEscapes", "ChaseNoClosingMs"):
    matches = re.findall(r"^\s*private const (?:int|uint|double) " + name + r"\s*=[^;\r\n]+;", source, re.MULTILINE)
    if len(matches) != 1:
        raise ValueError("Expected exactly one production constant " + name)
    constants.append(matches[0].strip())
assert fixture.count("// __PRODUCTION_LEVEL_CONSTANTS__") == 1
fixture = fixture.replace("// __PRODUCTION_LEVEL_CONSTANTS__", "\n        ".join(constants))
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix="wowb-defence-tests-") as directory:
    directory = Path(directory)
    (directory / "Fixture.csproj").write_text(project, encoding="utf-8")
    (directory / "Program.cs").write_text(fixture, encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project", str(directory / "Fixture.csproj")], check=True)

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
click_unit = source[source.index("    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,\n"):source.index("    /// <summary>\n    /// Finds the screen pixel")]
restore = source[source.index("    private static bool TryRestoreHealTarget("):source.index("    /// <summary>\n    /// A unit's health straight out of its data block")]
vendor_policy = source[source.index("    private static bool WaitVendorSafe("):source.index("    /// <summary>\n    /// Walks to one corpse")]
chase = source[source.index("    private sealed class ChaseWatch"):source.index("    // A pool unit's position is only a navigation lead")]
# Guard the control-flow wiring as well: approach is before pixel search and
# both outer loops retry after it, rather than treating movement as a fight.
engage = source[source.index("    private static bool TryEngage("):source.index("    private static bool TryCurrentSelection(")]
assert engage.index("ShouldApproachPoolCandidate(have, haveNearest, nearestYd)") < engage.index("FindUnitPixel(")
assert "report = ApproachPoolCandidate(hwnd, nearest);" in engage
assert engage.index("report = ApproachPoolCandidate(hwnd, nearest, FactionApproachYd)") < engage.index("ClickAimInput.TapKey(hwnd, TabVk")
assert engage.index("if (GrindFactionSettings.Rules.Enabled && !have)", engage.index("ClickAimInput.TapKey(hwnd, TabVk")) < engage.index("FindUnitPixel(")
assert "if (!GrindFactionSettings.Rules.Enabled && TryPullOnTheWay(hwnd, me))" in source
assert source.count("_combatHold || _poolApproachHandled") == 2
assert "out near, out nearYd, out live2, out unread2, null, false" in source
fight = source[source.index("    private static string Fight("):source.index("    private static", source.index("    private static string Fight(") + 20)]
assert fight.count("(!defenceOnly || defenceFactionFilter) && !FactionPullStillAllowed(") == 2
assert fight.count("!FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter)") == 4
assert "unverified melee right-click withheld" in fight
assert "filtered pool check: no in-fight TAB" in fight
assert "_running && GrindFactionSettings.Rules.Enabled" in source[source.index("    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,\n"):source.index("    private static bool FindUnitPixel(")]
assert "grindMode && !WowBGrindFactions.AllowsPull(reader, selected" in defence
assert "ClickAimInput.TapKey(hwnd, 0x70" not in defence
assert "selected.Guid != candidate.Guid" in defence
assert fight.index("ClickAimHealPolicy.StillOnBar(heal, ClickAimCombat.ReadBar(reader))") < fight.index("ClickAimInput.TapKey(hwnd, 0x70")
assert fight.index("HealPowerOk(freshHeal.Power, freshHeal.MaxPower)") < fight.index("ClickAimInput.TapKey(hwnd, 0x70")
assert fight.index("HealPowerOk(finalHeal.Power, finalHeal.MaxPower)") < fight.index("ClickAimInput.TapKey(hwnd, heal.KeyVk")
assert "expectedPull: target" in engage
assert "if (GrindFactionSettings.Rules.Enabled) poolPick = true;" in engage
assert engage.count("ClearNonSelfFactionSelection(hwnd,") >= 3
start_profile = source[source.index("    public static bool Start("):source.index("    internal static bool StartRoaming(")]
start_roaming = source[source.index("    internal static bool StartRoaming("):source.index("    private static void RunGuarded(")]
assert "WowBGrindFactions.PrepareForGrind" in start_profile and "WowBGrindFactions.PrepareForGrind" in start_roaming
assert fixture.count("// __PRODUCTION_METHODS__") == 1
vendor_due = source[source.index("    private static bool VendorDue("):source.index("    /// <summary>\n    /// The original CopilotBuddy selling procedure")]
vendor_trip = source[source.index("    private static bool VendorTrip("):source.index("    private static bool WaitVendorSafe(")]
assert "RouteRunner.RequireMeshDuringSession = true" in vendor_trip
assert "TryClickRecordedMerchant(hwnd, point, npc" in vendor_trip
assert "ChaseStep(hwnd, vendorWatch, npc" in vendor_trip
assert "TryClickUnit(hwnd, npc" not in vendor_trip
assert "return _lootsSinceTrip >= after;" in source
fixture = fixture.replace("// __PRODUCTION_METHODS__", vendor_due + "\n" + vendor_trip + "\n" + vendor_policy + "\n" + restore + "\n" + level + "\n" + selection + "\n" + current_selection + "\n" + dismissible + "\n" + pull + "\n" + nearest + "\n" + approach_policy + "\n" + defence + "\n" + approach + "\n" + dismiss + "\n" + click_unit + "\n" + chase)
# Read constants from production so boundary and movement tests cannot pass
# against stale copies of the thresholds or watchdog limits.
constants = []
for name in ("LevelAbovePlayer", "LevelBelowPlayer", "SearchYd", "PullYd", "GiveUpYd", "PoolApproachYd", "FactionApproachYd",
             "PoolApproachTimeoutMs", "GatherDefenceRadius", "TabVk", "TabSettleMs", "NotAFightFlag",
             "StuckMoveYd", "StuckMs", "StuckEscapes", "ChaseNoClosingMs", "VendorReachYd", "SellPlanMaxSteps", "VendorWalkMs", "VendorRetryMs", "VendorRetryAfterFightMs", "VendorNpcAttempts", "InterruptRetryMs"):
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

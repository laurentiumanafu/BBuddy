#!/usr/bin/env python3
"""Synthetic tests of production buff controller and both runner integrations.

Requires the .NET 10 SDK. No native input, game, client data or aura validation.
"""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
controller = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs').read_text(encoding='utf-8-sig')
schedule = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimBuffSchedule.cs').read_text(encoding='utf-8-sig')
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text(encoding='utf-8')
grind = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
gather = (root / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
grind_recover = grind[grind.index('    private static bool RecoverIfPending('):grind.index('    private static int _presses;')]
gather_recover = gather[gather.index('    private static bool RecoverIfPending('):gather.index('    public static void Stop()')]
gather_reset = grind[grind.index('    internal static void ClearGatherFightAfterRecovery('):grind.index('    internal static string RestForGather(')]
assert grind_recover.index('if (!recovered)') < grind_recover.index('Buffs.Reset()')
assert gather_recover.index('if (!recovered)') < gather_recover.index('GrindRunner.ClearGatherFightAfterRecovery()')
assert 'Buffs.Reset()' in gather_reset
assert 'Buffs.Reset()' in grind[grind.index('    private static void ReportMemory()'):grind.index('    private static', grind.index('    private static void ReportMemory()') + 30)]
assert 'Buffs.Reset()' in grind[grind.index('    internal static void PrepareGatherDefence()'):grind.index('    internal static void ClearGatherFightAfterRecovery()')]
assert 'Buffs.TryApply(hwnd, () => _stop, () => StopForBuff(hwnd, mover), () => _presses++)' in grind
assert gather.count('GrindRunner.BuffForGather(hwnd, mover, () => _stop)') == 1
gather_loop = gather[gather.index('    private static void Run(List<RoutePoint>'):]
assert gather_loop.index('if (fighting)') < gather_loop.index('GrindRunner.RestForGather(hwnd)') < gather_loop.index('GrindRunner.BuffForGather(hwnd, mover, () => _stop)') < gather_loop.index('GatherAtNode(hwnd, node)')
assert 'TryQuietPlayer' in controller and 'TryReadGhostFlagCandidate' in controller and 'SameSlot' in controller
assert 'reader.ReadTargetGuid() != player.Guid' in controller
fight = grind[grind.index('    private static string Fight('):grind.index('    private static bool TryRestoreHealTarget(')]
assert fight.index('TryRestoreHealTarget(') < fight.index('Buffs.TryApplyInCombat(') < fight.index('/* ---- 1b. the damage-over-time spell')
assert '() => FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter)' in fight
assert 'report = Fight(hwnd, player.Location.X, player.Location.Y, false, true, target.Guid, target, grindMode)' in grind
combat_method = controller[controller.index('    internal bool TryApplyInCombat('):controller.index('    // Returns true only if at least one buff key')]
assert '0x70' not in combat_method and 'TabVk' not in combat_method
assert 'CombatManaEstimate(action.Id)' in combat_method and 'reader.IsExperimental70707' in combat_method
assert 'spellId == 324' in controller and 'spellId == 168' in controller
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-buff-tests-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project, encoding='utf-8')
    (directory / 'Program.cs').write_text(fixture, encoding='utf-8')
    (directory / 'Controller.cs').write_text(controller, encoding='utf-8')
    (directory / 'Schedule.cs').write_text(schedule, encoding='utf-8')
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

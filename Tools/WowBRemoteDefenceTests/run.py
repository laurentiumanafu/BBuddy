#!/usr/bin/env python3
"""Production-extracted defensive selection and movement-guard fixtures.

Synthetic snapshots/input only; cannot identify live attackers or prove hostility.
"""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
handoff = (root / 'Styx/Logic/Pathing/ClickAim/WowBDefenceHandoff.cs').read_text(encoding='utf-8-sig')
def section(start, end):
    a = g.index(start)
    return g[a:g.index(end, a)]
near = section('    private static bool HasNearbyOtherNpc(', '    // Proximity is a heuristic')
fields = section('    private static bool DefenceFieldsValid(', '    private static bool TryTakeDefenceHandoff(')
defend = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
remote = section('    private static string DefendRemote(', '    // Revalidate the remote fight')
movement = section('    private static bool RemoteDefenceMovementAllowed(', '    private static bool InRealCombat()')
fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
chase = section('    private static bool ChaseStep(', '    // A pool unit\'s position')
assert 'nearby.Count == 0' in defend and 'GrindFactionSettings.Rules.Enabled' in defend
assert 'HasNearbyOtherNpc(snap, player, default, reader)' in defend
assert 'FightDefenceWithHandoffs(hwnd, reader, true, player, selected, true)' in remote
assert 'remoteDefence ? RemoteDefenceRadius + 3.0' in fight
assert 'myMana >= needForCast' in fight and 'if (canCast && d > selectedRange)' in fight
assert 'if (d > MeleeEngageYd)' in fight
assert fight.count('combatMoveAllowed: defensiveMovement') == 3
assert 'remoteDefence = false; // changed selections' in g
assert chase.count('combatMoveAllowed != null && !combatMoveAllowed()') >= 2
assert 'RouteRunner.TryCombatMeshPath' in chase and 'no complete validated mesh path' in chase
assert '(!TryReadGatherCombat(out bool fighting) || !fighting)' in fight
assert 'grindMode && !reader.IsExperimental70862, remoteDefence' in g
assert 'FactionPullStillAllowed(ObjectManager.WowB, expectedPull, target)' in fight
assert 'FactionPullStillAllowed(reader, expectedPull, target)' in fight
assert fight.count('!defenceOnly || defenceFactionFilter, defenceOnly') == 8
assert 'target.UnitAddress != expectedPull.UnitAddress || target.RecordAddress != expectedPull.RecordAddress' in fight
assert 'FactionPullStillAllowed(reader, expected, expected)' in g
assert 'requireCombat && (!TryReadGatherCombat(out bool activeCombat) || !activeCombat)' in g
assert '(!requireCombat || (TryReadGatherCombat(out bool stillFighting) && stillFighting))' in g
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text(encoding='utf-8')
for marker, content in [('__HAS_NEARBY__', near), ('__DEFENCE_FIELDS__', fields), ('__DEFEND_NEARBY__', defend), ('__DEFEND_REMOTE__', remote), ('__MOVE_GUARD__', movement)]:
    fixture = fixture.replace('        // ' + marker, content)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-remote-defence-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Handoff.cs').write_text(handoff)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)
print('Remote fight movement and resource integration assertions passed (static only).')

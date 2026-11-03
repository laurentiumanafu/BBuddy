#!/usr/bin/env python3
"""Exercise production corpse search/fallback against synthetic routes and state.

Requires .NET 10 SDK. Does not test a real client, mesh, dialog, or input.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / 'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')
methods = source[source.index('    // Mesh-only movement'):source.index('    private static bool TryDialogClick(')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text(encoding='utf-8')
assert fixture.count('// __SEARCH_METHODS__') == 1
fixture = fixture.replace('// __SEARCH_METHODS__', methods)
constants = source[source.index('    private const int PromptWaitMs'):source.index('    private static bool Allowed(')]
assert fixture.count('// __RECOVERY_CONSTANTS__') == 1
fixture = fixture.replace('// __RECOVERY_CONSTANTS__', constants)
assert fixture.count('// __RECOVER_METHOD__') == 1
fixture = fixture.replace('// __RECOVER_METHOD__', source[source.index('    internal static bool TryRecover('):source.rindex('\n}')])
spot = source[source.index('    private static bool SpotSafe('):source.index('    // True means a guarded image')]
assert 'TryNpcSnapshot' in spot and 'MaxClickRadius' in spot and 'TrySnapSafeSpot' in spot
assert 'requireNpcClearance && !WowBCorpseSpotPolicy.ClearEnough' in spot and 'State.Ghost' in spot
click = source[source.index('    private static bool TryDialogClick('):source.index('    internal static bool TryRecover(')]
assert 'SpotSafe(memory, reader, hwnd, checkpoint, running, requireNpcClearance)' in click
assert 'SuitableForClick(dialog, release' in click and 'ConfirmedLeftClick' in click and 'FinalCheck' in click
recover = source[source.index('    internal static bool TryRecover('):]
assert 'TryDialogClick(memory, reader, hwnd, checkpoint, running, false, !fallback' in recover
assert 'aliveReads >= 2 && WowBGrindFactions.Experimental70707BarSafe(reader)' in recover
assert 'new bool[WowBCorpseSpotPolicy.DirectionCount]' in recover and 'out fallback' in recover
assert 'TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true' in recover
policy = (root / 'Styx/Logic/Pathing/ClickAim/WowBCorpseSpotPolicy.cs').read_text(encoding='utf-8-sig')
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-corpse-recovery-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project, encoding='utf-8')
    (directory / 'Program.cs').write_text(fixture, encoding='utf-8')
    (directory / 'Policy.cs').write_text(policy, encoding='utf-8')
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

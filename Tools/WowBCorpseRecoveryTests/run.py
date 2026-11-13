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
mover = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimMover.cs').read_text(encoding='utf-8-sig')
mover_methods = mover[mover.index('    internal bool TryEnableRecoveryKeySteering('):mover.index('    // Gather uses only key steering.') ]
assert fixture.count('// __MOVER_METHODS__') == 1
fixture = fixture.replace('// __MOVER_METHODS__', mover_methods)
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
assert recover.index('!RouteRunner.MeshReady()') < recover.index('TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true')
assert recover.index('state == State.Ghost') < recover.index('TryEnableRecoveryKeySteering(hwnd, SafeGhost') < recover.index('WalkGhostWithRelocation(memory, reader, hwnd, checkpoint')
relocation = source[source.index('    private static bool WalkGhostWithRelocation('):source.index('    // Mesh-only movement')]
assert 'WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, true)' in relocation
assert 'MeshRelocate.TryPick(checkpoint.Map' in relocation and 'ReadState(memory, reader, hwnd, checkpoint, running' in relocation
assert 'RequireFacingForKeyMode = true' in recover and 'mover.EndRecoveryKeySteering()' in recover
assert 'out rem, RequireFacingForKeyMode' in mover
assert 'EnsureChaseTurnKeys(hwnd, safe, out report)' in mover_methods
assert 'TryEnableKeySteering(' not in mover_methods  # no walking calibration
# The owning Grind loop must hand a death checkpoint to recovery without
# overriding an explicit STOP, then return to the loop after confirmed revival.
grind = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
owner = grind[grind.index('    private static bool RecoverIfPending('):grind.index('    private static int _presses;')]
assert 'MayBeginRecovery' in owner and '_deathStopGeneration, _stopGeneration' in owner
assert 'WowBCorpseRecovery.TryRecover(candidate, mover' in owner and 'Buffs.Reset()' in owner
loop = grind[grind.index('            bool recovered;'):grind.index('        catch (Exception ex)', grind.index('            bool recovered;'))]
assert 'if (CheckDeathCheckpoint()) break;' in loop
assert '!watchCancelled && _recoveryReady && !ClickAimAbort.Requested' in loop and 'RecoverIfPending(mover)' in loop
assert 'while (recovered && !_stop && !ClickAimAbort.Requested' in loop
policy = (root / 'Styx/Logic/Pathing/ClickAim/WowBCorpseSpotPolicy.cs').read_text(encoding='utf-8-sig')
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-corpse-recovery-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project, encoding='utf-8')
    (directory / 'Program.cs').write_text(fixture, encoding='utf-8')
    (directory / 'Policy.cs').write_text(policy, encoding='utf-8')
    (directory / 'ReleaseTransition.cs').write_text((root / 'Styx/Logic/Pathing/ClickAim/WowBReleaseTransition.cs').read_text(encoding='utf-8-sig'), encoding='utf-8')
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

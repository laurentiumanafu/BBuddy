#!/usr/bin/env python3
"""Exercise the production corpse-route relocation loop with synthetic state/input."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = (root / 'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')
method = source[source.index('    private static bool WalkGhostWithRelocation('):source.index('    // Mesh-only movement')]
walk = source[source.index('    private static bool WalkGhostTo('):source.index('    // The five-location recovery')]
assert 'RouteRunner.PollInterruptDuringWalk = true' in walk and 'RouteRunner.RequireMeshDuringSession = true' in walk
assert 'RouteRunner.ShouldInterrupt = oldInterrupt' in walk and 'ClickAimKeySteer.ReleaseHeld()' in walk
assert 'ReadState(memory, reader, hwnd, checkpoint, running' in walk and 'RouteLimitMs' in walk
assert 'MeshRelocate.TryPick(checkpoint.Map' in method and 'attempt <= MeshRelocate.Attempts' in method
recover = source[source.index('    internal static bool TryRecover('):]
assert 'WalkGhostWithRelocation(memory, reader, hwnd, checkpoint, running, mover, goal, routeStarted)' in recover
assert '!WowBCorpseSpotPolicy.InRange(ghostPos, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)' in recover
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __METHOD__', method)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-ghost-relocate-') as folder:
    folder = Path(folder)
    (folder / 'Fixture.csproj').write_text(project)
    (folder / 'Program.cs').write_text(fixture)
    subprocess.run(['dotnet', 'run', '--project', str(folder / 'Fixture.csproj')], check=True)

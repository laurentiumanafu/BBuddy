#!/usr/bin/env python3
"""Execute relocation and Gather's recovery loop with stubbed navigation/input (.NET 10 SDK required)."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim'
gather = (source / 'GatherRunner.cs').read_text(encoding='utf-8-sig')
method = gather[gather.index('    private static bool RelocateAndRetry('):gather.index('    // No movement, turns, stop-key taps')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // RELOCATION_METHOD', method)
loop = gather[gather.index('                    bool walked = Walk(mover, next, true);'):gather.index('                if (!Ready(node)) continue;')]
assert 'if (!walked && roam == null)' in loop and 'RelocateAndRetry(mover, next)' in loop
assert 'if (++failures >= 3)' in loop  # unchanged roam failure handling
assert 'RouteRunner.LastRunInterrupted' in loop
node = gather[gather.index('                    bool reached = Walk(mover, destination, false);'):gather.index('                ClickAimKeySteer.ReleaseHeld();', gather.index('                    bool reached = Walk(mover, destination, false);'))]
assert 'RelocateAndRetry(mover, destination, false, NodeStillValid)' in node
assert 'stationaryNodeFailures >= 3' in node and 'RouteRunner.LastRunInterrupted' in node
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='mesh-relocate-tests-') as folder:
    folder = Path(folder)
    (folder / 'Fixture.csproj').write_text(project)
    (folder / 'Fixture.cs').write_text(fixture)
    (folder / 'MeshRelocate.cs').write_text((source / 'MeshRelocate.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(folder / 'Fixture.csproj')], check=True)

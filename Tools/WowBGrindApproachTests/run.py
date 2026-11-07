#!/usr/bin/env python3
"""Run production faction TAB-miss branch with synthetic snapshots and movement (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
predicate = source[source.index('    private static bool ShouldApproachPoolCandidate('):source.index('    private static bool PreferNearerPoolCandidate(')]
start = source.index('        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;', source.index('        for (int i = 0; i < TabTries'))
branch = source[start:source.index('        // Do not waste dozens of pixel clicks', start)]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __PREDICATE__', predicate).replace('            // __BRANCH__', branch)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-grind-approach-') as folder:
    folder = Path(folder)
    (folder / 'Fixture.csproj').write_text(project)
    (folder / 'Program.cs').write_text(fixture)
    subprocess.run(['dotnet', 'run', '--project', str(folder / 'Fixture.csproj')], check=True)

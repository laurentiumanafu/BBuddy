#!/usr/bin/env python3
"""Execute extracted Stoneclaw production code with synthetic inputs; requires .NET 10."""
from pathlib import Path
import subprocess, tempfile
root = Path(__file__).resolve().parents[2]
src = root / 'Styx/Logic/Pathing/ClickAim'
g = (src/'GrindRunner.cs').read_text(encoding='utf-8-sig')
a = g.index('    // Shared across successive fights in the session')
b = g.index('    // Key 1 is an explicit, user-tested', a)
fixture = (Path(__file__).parent/'Fixture.cs.txt').read_text().replace('  // METHODS', g[a:b])
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-stoneclaw-') as d:
    d = Path(d)
    (d/'Fixture.csproj').write_text(project)
    (d/'Program.cs').write_text(fixture)
    (d/'Policy.cs').write_text((src/'WowBStoneclawPolicy.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet','run','--project',str(d/'Fixture.csproj')],check=True)

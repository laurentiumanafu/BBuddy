#!/usr/bin/env python3
"""Execute the actual GatherAtNode loop against synthetic interaction/scan inputs (.NET 10 required)."""
from pathlib import Path
import subprocess, tempfile
root = Path(__file__).resolve().parents[2]
g = (root/'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
a=g.index('    private static void GatherAtNode('); b=g.index('    // Grind owns the thread',a)
f=(Path(__file__).parent/'Fixture.cs.txt').read_text().replace('        // __METHOD__',g[a:b])
proj='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='gather-harvest-') as d:
 p=Path(d);(p/'Fixture.csproj').write_text(proj);(p/'Program.cs').write_text(f)
 subprocess.run(['dotnet','run','--project',str(p/'Fixture.csproj')],check=True)

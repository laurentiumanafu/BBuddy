#!/usr/bin/env python3
"""Exercise production hotspot watch with simulated state/time (requires .NET 10)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='hotspot-death-watch-') as temp:
    path = Path(temp)
    (path / 'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (path / 'Program.cs').write_text((Path(__file__).parent / 'Fixture.cs.txt').read_text())
    (path / 'Watch.cs').write_text((root / 'Styx/Logic/Pathing/ClickAim/WowBHotspotDeathWatch.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(path / 'Test.csproj')], check=True)

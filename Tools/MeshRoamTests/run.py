#!/usr/bin/env python3
"""Run production MeshRoam with fake navigation; requires .NET 10 SDK."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim/MeshRoam.cs'
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='mesh-roam-tests-') as path:
    d = Path(path)
    (d / 'Fixture.csproj').write_text(project)
    (d / 'Fixture.cs').write_text((Path(__file__).parent / 'Fixture.cs.txt').read_text())
    (d / 'MeshRoam.cs').write_text(source.read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(d / 'Fixture.csproj')], check=True)

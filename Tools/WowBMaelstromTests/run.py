#!/usr/bin/env python3
"""Run synthetic production reader fixtures; requires .NET 10, no game access."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-maelstrom-') as directory:
    path = Path(directory)
    (path / 'Fixture.csproj').write_text(project)
    (path / 'Fixture.cs').write_text((Path(__file__).parent / 'Fixture.cs.txt').read_text())
    (path / 'WowBMaelstromWeapon.cs').write_text((root / 'Styx/Logic/Pathing/ClickAim/WowBMaelstromWeapon.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(path / 'Fixture.csproj')], check=True)

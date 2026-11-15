#!/usr/bin/env python3
"""Run the production learning policy fixture; requires the .NET 10 SDK."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='clickaim-turn-rate-') as d:
    path = Path(d)
    (path / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (path / 'Program.cs').write_text((Path(__file__).parent / 'Fixture.cs.txt').read_text())
    (path / 'Policy.cs').write_text((root / 'Styx/Logic/Pathing/ClickAim/ClickAimTurnRateLearning.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(path / 'Fixture.csproj')], check=True)

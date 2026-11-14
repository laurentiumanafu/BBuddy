#!/usr/bin/env python3
"""Run retry policy with synthetic observations; requires .NET 10 SDK."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='resurrection-retry-') as temp:
    p = Path(temp)
    (p / 'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p / 'Program.cs').write_text((Path(__file__).parent / 'Fixture.cs.txt').read_text())
    for name in ['WowBResurrectionRetry.cs', 'WowBReleaseTransition.cs']:
        (p / name).write_text((root / 'Styx/Logic/Pathing/ClickAim' / name).read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p / 'Test.csproj')], check=True)

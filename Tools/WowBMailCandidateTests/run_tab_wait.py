#!/usr/bin/env python3
"""Run synthetic production tab wait checks; requires .NET 10, no game access."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='mail-tab-wait-') as directory:
    p = Path(directory)
    (p/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p/'Fixture.cs').write_text((Path(__file__).parent/'TabWaitFixture.cs.txt').read_text())
    (p/'Wait.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBMailTabWait.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p/'Fixture.csproj')], check=True)

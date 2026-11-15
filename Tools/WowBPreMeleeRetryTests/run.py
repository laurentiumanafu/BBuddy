#!/usr/bin/env python3
"""Run the production retry policy fixture; requires the .NET 10 SDK."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='pre-melee-retry-') as folder:
    folder = Path(folder)
    (folder/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (folder/'Program.cs').write_text((Path(__file__).parent/'Fixture.cs.txt').read_text())
    (folder/'Policy.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBPreMeleeRetry.cs').read_text())
    subprocess.run(['dotnet','run','--project',str(folder/'Fixture.csproj')], check=True)

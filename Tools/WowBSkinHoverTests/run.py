#!/usr/bin/env python3
"""Run production hover-only search with synthetic mouseover values; requires .NET 10."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='skin-hover-') as d:
 p=Path(d)
 (p/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Fixture.cs').write_text((Path(__file__).parent/'Fixture.cs.txt').read_text())
 (p/'Search.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBSkinHoverSearch.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Fixture.csproj')],check=True)

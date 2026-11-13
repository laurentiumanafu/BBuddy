#!/usr/bin/env python3
"""Execute the production read-only cast candidate logic with synthetic inputs (.NET 10 required)."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
source=(root/'Styx/Logic/Pathing/ClickAim/WowBGatherCastResearch.cs').read_text(encoding='utf-8-sig')
fixture=(Path(__file__).parent/'Fixture.cs.txt').read_text(encoding='utf-8')
project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='gather-cast-research-') as d:
 p=Path(d);(p/'Fixture.csproj').write_text(project);(p/'Fixture.cs').write_text(fixture);(p/'Candidate.cs').write_text(source)
 subprocess.run(['dotnet','run','--project',str(p/'Fixture.csproj')],check=True)

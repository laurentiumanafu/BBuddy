#!/usr/bin/env python3
"""Run production travel-form controller against synthetic data (.NET 10 required)."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='ghost-wolf-travel-') as d:
 p=Path(d); (p/'test.csproj').write_text(project)
 (p/'Fixture.cs').write_text((Path(__file__).parent/'Fixture.cs.txt').read_text())
 for name in ('WowBGhostWolfTravel.cs','GatherMovementSkill.cs'):
  (p/name).write_text((root/'Styx/Logic/Pathing/ClickAim'/name).read_text(encoding='utf-8-sig'))
 subprocess.run(['dotnet','run','--project',str(p/'test.csproj')],check=True)

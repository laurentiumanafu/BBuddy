#!/usr/bin/env python3
"""Compile the pure production mail policy with synthetic inventory evidence."""
from pathlib import Path
import subprocess, tempfile
root=Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='mail-policy-') as tmp:
 p=Path(tmp); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Fixture.cs').write_text(Path(__file__).with_name('Fixture.cs.txt').read_text())
 for src in ('Styx/Logic/Pathing/ClickAim/WowBMailBatchPolicy.cs','Styx/WoWInternals/WowBGuid.cs'):
  (p/Path(src).name).write_text((root/src).read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

#!/usr/bin/env python3
"""Execute the production return-stage method with simulated safety and route states."""
from pathlib import Path
import subprocess, tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
start=s.index('    private static bool WalkBackToProfile(')
end=s.index('    private static bool WalkToRecordedMailbox(',start)
f=Path(__file__).with_name('Fixture.cs.txt').read_text().replace('        // __PRODUCTION__',s[start:end])
with tempfile.TemporaryDirectory(prefix='profile-return-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Fixture.cs').write_text(f)
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

#!/usr/bin/env python3
"""Compile the production final observation guards with simulated memory."""
from pathlib import Path
import subprocess, tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs').read_text()
a=s.index('    private sealed class Candidate\n'); b=s.index('    private static Memory? _previousMemory;',a)
c=s.index('    private static bool TryBuildMailObservation('); d=s.rindex('\n}')
f=Path(__file__).with_name('Fixture.cs.txt').read_text().replace('        // __CANDIDATE_TYPES__',s[a:b]).replace('        // __PRODUCTION__',s[c:d])
with tempfile.TemporaryDirectory(prefix='mail-observation-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Fixture.cs').write_text(f); (p/'Guid.cs').write_text((root/'Styx/WoWInternals/WowBGuid.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

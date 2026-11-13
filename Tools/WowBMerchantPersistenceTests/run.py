#!/usr/bin/env python3
"""Compile and run the production high-half matching helper using .NET 10."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'Styx/Logic/Pathing/ClickAim/WowBKnownMerchants.cs').read_text(encoding='utf-8-sig')
helper=s[s.index('    public static bool Same70938HighCandidate('):s.index('    public static bool IsRecordedLive(')]
fixture=(Path(__file__).parent/'Fixture.cs.txt').read_text().replace('        // __PRODUCTION__',helper)
with tempfile.TemporaryDirectory(prefix='vendor-match-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Program.cs').write_text(fixture)
 (p/'WowBGuid.cs').write_text((root/'Styx/WoWInternals/WowBGuid.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

flow=(Path(__file__).parent/'FlowFixture.cs.txt').read_text()
g=(root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
methods=g[g.index('    private static bool TryFindVendorNpc('):g.index('    private static bool TryClickRecordedMerchant(')]
flow=flow.replace('        // __HIGH_MATCH__',helper).replace('        // __PRODUCTION__',methods)
with tempfile.TemporaryDirectory(prefix='vendor-flow-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Program.cs').write_text(flow)
 (p/'WowBGuid.cs').write_text((root/'Styx/WoWInternals/WowBGuid.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

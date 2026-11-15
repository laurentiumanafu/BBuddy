#!/usr/bin/env python3
from pathlib import Path
import tempfile, subprocess
root=Path(__file__).resolve().parents[2]
s=(root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
start=s.index('    private static ClickAimAction? TownReturnAction(')
end=s.index('    private static bool WalkBackToProfile(',start)
fixture=Path(__file__).with_name('Fixture.cs.txt').read_text().replace('        // __PRODUCTION__',s[start:end])
with tempfile.TemporaryDirectory(prefix='town-flow-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Fixture.cs').write_text(fixture)
 (p/'Progress.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBTownCastProgress.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

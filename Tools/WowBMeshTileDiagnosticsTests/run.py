#!/usr/bin/env python3
"""Exercise the actual logging helper with synthetic tile files; requires .NET 10."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'Styx/Logic/Pathing/ClickAim/ClickAimDiagnostics.cs').read_text(encoding='utf-8-sig')
method=s[s.index('    internal static void ReportMeshTileDiagnostics('):s.index('    /// <summary>\n    /// The map id to route on.')]
fixture=(Path(__file__).parent/'Fixture.cs.txt').read_text().replace('        // __PRODUCTION__',method)
with tempfile.TemporaryDirectory(prefix='mesh-diag-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Program.cs').write_text(fixture); (p/'TileIdentifier.cs').write_text((root/'Tripper/Navigation/TileIdentifier.cs').read_text())
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

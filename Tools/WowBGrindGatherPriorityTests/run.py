#!/usr/bin/env python3
"""Run actual Grind gathering decision/approach code with synthetic stubs (.NET 10)."""
from pathlib import Path
import subprocess, tempfile
root = Path(__file__).resolve().parents[2]
s = (root / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
methods = s[s.index('    private static void ReportGrindCommitment('):s.index('    private static void RunGuarded(')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __PRODUCTION__', methods)
with tempfile.TemporaryDirectory(prefix='grind-gather-') as d:
 p=Path(d); (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
 (p/'Program.cs').write_text(fixture)
 subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

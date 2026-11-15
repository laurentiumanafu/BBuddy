#!/usr/bin/env python3
"""Run synthetic C# candidate table fixtures with an installed .NET 10 SDK."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='mail-candidate-') as d:
    p = Path(d)
    (p / 'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p / 'Fixture.cs').write_text(Path(__file__).with_name('Fixture.cs.txt').read_text())
    for name in ('Styx/Logic/Pathing/ClickAim/WowBMailCandidateTable.cs', 'Styx/WoWInternals/WowBGuid.cs'):
        (p / Path(name).name).write_text((root / name).read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p / 'Test.csproj')], check=True)

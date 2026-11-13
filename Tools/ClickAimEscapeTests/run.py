#!/usr/bin/env python3
"""Run production candidate geometry and sidestep with synthetic inputs; requires .NET 10."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim'
pilot = (source / 'ClickAimPilot.cs').read_text(encoding='utf-8-sig')
method = pilot[pilot.index('    private static bool Sidestep('):pilot.index('    /// <summary>The 26-check report')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // SIDESTEP_METHOD', method)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='clickaim-escape-tests-') as folder:
    folder = Path(folder)
    (folder / 'Fixture.csproj').write_text(project)
    (folder / 'Fixture.cs').write_text(fixture)
    (folder / 'ClickAimEscape.cs').write_text((source / 'ClickAimEscape.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(folder / 'Fixture.csproj')], check=True)

#!/usr/bin/env python3
from pathlib import Path
import subprocess, tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='death-btn-') as d:
    p = Path(d)
    (p / 'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p / 'Fixture.cs').write_text(Path(__file__).with_name('Fixture.cs.txt').read_text())
    (p / 'DeathButtons.cs').write_text((root / 'Styx/Logic/Pathing/ClickAim/WowBDeathButtonCalibration.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p / 'Test.csproj')], check=True)

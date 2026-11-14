#!/usr/bin/env python3
"""Run the production cast progress state machine against synthetic cases (.NET 10 required)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='bbuddy-skin-cast-') as d:
    p = Path(d)
    (p/'Fixture.csproj').write_text(project)
    (p/'Fixture.cs').write_text((Path(__file__).parent/'Fixture.cs.txt').read_text())
    (p/'Progress.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBSkinCastProgress.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p/'Fixture.csproj')], check=True)

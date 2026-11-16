#!/usr/bin/env python3
"""Run synthetic disappearance checks; requires .NET 10 and does not access the game."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='mail-send-observation-') as directory:
    p = Path(directory)
    (p/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p/'Fixture.cs').write_text((Path(__file__).parent/'SendObservationFixture.cs.txt').read_text())
    (p/'Observation.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBMailSendObservation.cs').read_text())
    (p/'Guid.cs').write_text((root/'Styx/WoWInternals/WowBGuid.cs').read_text())
    subprocess.run(['dotnet', 'run', '--project', str(p/'Fixture.csproj')], check=True)

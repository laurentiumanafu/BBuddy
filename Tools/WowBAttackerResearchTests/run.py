#!/usr/bin/env python3
"""Test read-only player-GUID research with stubbed snapshots and memory (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim'
g = (source / 'GrindRunner.cs').read_text(encoding='utf-8-sig')
probe = (source / 'WowBAttackerResearch.cs').read_text(encoding='utf-8-sig')
assert 'WowBAttackerResearch.Observe(' in g and 'blockedForResearch.Add(unit)' in g
assert 'Math.Min(blocked.Count, MaxUnits)' in probe and 'ProbeLimitMs = 250' in probe
assert 'NOT attacker/target proof' in probe and 'ClickAimInput' not in probe
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-attacker-research-') as folder:
    folder = Path(folder)
    (folder/'Fixture.csproj').write_text(project)
    (folder/'Fixture.cs').write_text((Path(__file__).parent/'Fixture.cs.txt').read_text())
    (folder/'Research.cs').write_text(probe)
    subprocess.run(['dotnet','run','--project',str(folder/'Fixture.csproj')],check=True)

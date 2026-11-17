#!/usr/bin/env python3
"""Run the production ghost relocation loop with synthetic walks (.NET 10 required)."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim'
text = (source / 'WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')
method = text[text.index('    private static bool WalkGhostWithRelocation('):text.index('    // Mesh-only movement')]
# The retry budget and the jump key come from production, so the fixture cannot
# pass against a stale copy of either.
constants = text[text.index('    private const int PromptWaitMs'):text.index('    private static bool Allowed(')]
fixture = (Path(__file__).parent/'Fixture.cs.txt').read_text()
assert fixture.count('        // RELOCATION_METHOD') == 1
assert fixture.count('        // RECOVERY_CONSTANTS') == 1
fixture = fixture.replace('        // RELOCATION_METHOD', method).replace('        // RECOVERY_CONSTANTS', constants)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='bbuddy-corpse-relocation-') as folder:
    folder = Path(folder)
    (folder/'Fixture.csproj').write_text(project)
    (folder/'Fixture.cs').write_text(fixture)
    (folder/'Regions.cs').write_text((source/'WowBCorpseRelocationRegions.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(folder/'Fixture.csproj')], check=True)

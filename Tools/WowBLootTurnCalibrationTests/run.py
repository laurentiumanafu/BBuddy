#!/usr/bin/env python3
"""Run production turn-key calibration against synthetic facing/input (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = (root / 'Styx/Logic/Pathing/ClickAim/ClickAimKeySteer.cs').read_text(encoding='utf-8-sig')
methods = source[source.index('    internal static bool EnsureChaseTurnKeys('):source.index('    /// <summary>\n    /// One steering tick')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __METHODS__', methods)
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
loot = g[g.index('    private static bool ApproachRecentCorpse('):g.index('    private static int TryLootOne(')]
assert 'quickLootTurnCalibration: true' in loot and 'RecentCorpseCurrent(' in loot and 'ChaseStep(' in loot
chase = g[g.index('    private static bool ChaseStep('):g.index('    // A pool unit\'s position')]
assert 'EnsureChaseTurnKeys(' in chase and 'out string calibration, quickLootTurnCalibration' in chase
assert 'RouteRunner.TryCombatMeshPath(' in chase and 'TryReadGatherCombat(' in chase
assert g.count('quickLootTurnCalibration: true') == 1
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-loot-turn-calibration-') as folder:
    folder = Path(folder)
    (folder / 'Fixture.csproj').write_text(project)
    (folder / 'Fixture.cs').write_text(fixture)
    subprocess.run(['dotnet', 'run', '--project', str(folder / 'Fixture.csproj')], check=True)

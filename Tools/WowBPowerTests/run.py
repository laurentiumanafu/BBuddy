#!/usr/bin/env python3
"""Compile production mana-wait/resource gates with synthetic player snapshots (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
methods = g[g.index('    private static bool TryReadLocalFields('):g.index('    /// <summary>\n    /// One half of the "eating and drinking:" line')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __METHODS__', methods)
# Verify integration, not merely the helper policy.
fight = g[g.index('    private static string Fight('):g.index('    /// <summary>\n    /// A unit\'s health straight out of its data block')]
assert 'unmeasuredRage = reader.IsExperimental70707 && mine.PlayerPowerTypeCandidate == 1' in fight
assert 'ClickAimSettings.UseDamageOverTime && !unmeasuredRage' in fight
assert 'meleeHit != null && !unmeasuredRage' in fight
assert fight.count('if (!ResourceInputStillAllowed(reader)) continue;') >= 3
auto = fight[fight.index('            if (!keyOneMacro && auto != null)'):fight.index('            else if (!keyOneMacro && !swinging && !pressPending')]
assert 'ClickAimInput.TapKey(hwnd, auto.KeyVk' in auto and 'ResourceInputStillAllowed' not in auto
assert 'HealPowerOk(freshHeal.Power, freshHeal.MaxPower)' in fight
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-power-tests-') as directory:
    directory = Path(directory)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Fixture.csproj').write_text(project)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

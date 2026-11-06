#!/usr/bin/env python3
"""Production-extracted candidate ranking plus voluntary-pull wiring checks."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
policy = (root / 'Styx/Logic/Pathing/ClickAim/WowBPullPreference.cs').read_text(encoding='utf-8-sig')
prefer = g[g.index('    private static bool PreferFullPull('):g.index('    private static bool SelectionFieldsValid(')]
nearest = g[g.index('    private static bool NearestKillable('):g.index('    /// <summary>\n    /// Player-only 70291 candidate.')]
engage = g[g.index('    private static bool TryEngage('):g.index('    /// <summary>\n    /// Whether the client')]
approach = g[g.index('    private static string ApproachPoolCandidate('):g.index('    /// <summary>\n    /// Drops a target')]
assert engage.index('if (combatNow)') < engage.index('preferFullHealth: true')
assert engage.count('preferFullHealth: true') >= 3
assert engage.count('PreferFullPull(') >= 3
assert engage.index('if (TryCurrentSelection(') < engage.index('PreferFullPull(selReader, target, nearest)')
assert engage.index('PreferFullPull(esp, target, preferred)') < engage.index('report = Fight(')
assert 'TryReadGatherCombat(out bool preferenceCombat)' in engage
assert 'preferFullHealth: true' in approach and 'WowBPullPreference.Full(fields.Health, fields.MaxHealth)' in approach
assert 'TryReadGatherCombat(out bool fighting)' in approach and 'WowBGrindFactions.AllowsPull' in approach
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __PREFER__', prefer).replace('        // __NEAREST__', nearest)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-pull-preference-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Preference.cs').write_text(policy)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)
print('Voluntary-pull integration assertions passed (static, not a live fight).')

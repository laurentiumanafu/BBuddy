#!/usr/bin/env python3
"""Compile the production recent-loot flow against synthetic client stubs (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
src = root / 'Styx/Logic/Pathing/ClickAim'
g = (src / 'GrindRunner.cs').read_text(encoding='utf-8-sig')
loot = g[g.index('    private static void PollRecentLootAfterFight('):g.index('    // ------------------------------------------------------------------ selling')]
approach = g[g.index('    private static bool RecentCorpseCurrent('):g.index('    // Selection-free nearby scan.')]
click = g[g.index('    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,\n                                     bool mustSelect, out string how, out bool pixelConfirmed'):g.index('    /// <summary>\n    /// Finds the screen pixel')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('  // METHODS', loot + approach + click)
# Verify integration paths as well as the executed production methods.
assert 'MarkFocusedAttack(target)' in g
assert 'RecentLoot.Remember(unit, map, Environment.TickCount)' in g
start = g.index('                // Do not wait through a long meal')
outer = g[start:g.index('                RoutePoint next;', start)]
assert outer.index('LootNearby(hwnd, 3, recentOnly: true)') < outer.index('GatherRunner.GatherForGrind') < outer.index('TryEngage(hwnd, out combatReport)')
engage = g[g.index('    private static bool TryEngage('):g.index('    /// <summary>\n    /// Whether the client\'s current selection')]
assert engage.index('if (combatNow)') < engage.index('LootNearby(hwnd, 3, recentOnly: true)') < engage.index('WaitForManaBeforePull')
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-recent-loot-') as folder:
    folder = Path(folder)
    (folder/'Fixture.csproj').write_text(project)
    (folder/'Fixture.cs').write_text(fixture)
    (folder/'WowBRecentLoot.cs').write_text((src/'WowBRecentLoot.cs').read_text())
    subprocess.run(['dotnet','run','--project',str(folder/'Fixture.csproj')],check=True)

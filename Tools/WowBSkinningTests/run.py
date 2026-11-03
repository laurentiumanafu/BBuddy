#!/usr/bin/env python3
"""Compile production skinning code and its loot integration with stubbed client/input (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
src = root / 'Styx/Logic/Pathing/ClickAim'
gather = (src / 'GrindRunner.cs').read_text(encoding='utf-8-sig')
methods = gather[gather.index('    private static bool SkinQuiet('):gather.index('    /// <summary>The click found no point on the body at all.')]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // METHODS', methods)
loot = gather[gather.index('    private static string LootNearby('):gather.index('    // ------------------------------------------------------------------ selling')]
assert loot.index('TryLootOne(hwnd, corpse, out uint lootMap, out bool lootPixelConfirmed)') < loot.index('_lootsSinceTrip++') < loot.index('TrySkinLootedBeast(hwnd, reader, corpse, lootMap)')
assert 'result == 0 && _running && GatherSettings.Skinning' in loot
assert loot.index('if (!lootPixelConfirmed)') < loot.index('WowBSkinning.IsConfirmed') < loot.index('TrySkinLootedBeast(hwnd, reader, corpse, lootMap)')
loot_one = gather[gather.index('    private static int TryLootOne('):gather.index('    // Skinning is separate')]
assert loot_one.index('DismissRejectedSelection(hwnd, lootReader, lootPlayer.Guid, corpse.Guid)') < loot_one.index('out how, out lootPixelConfirmed')
assert 'pixelConfirmed = true;' in gather
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-skinning-tests-') as folder:
    folder = Path(folder)
    (folder/'Fixture.csproj').write_text(project)
    (folder/'Fixture.cs').write_text(fixture)
    (folder/'WowBSkinning.cs').write_text((src/'WowBSkinning.cs').read_text(encoding='utf-8-sig'))
    (folder/'GatherSettings.cs').write_text((src/'GatherSettings.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(folder/'Fixture.csproj')], check=True)

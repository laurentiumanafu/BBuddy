#!/usr/bin/env python3
"""Structural checks; the accompanying C# fixtures exercise the real policy with .NET 10."""
from pathlib import Path
import gzip
import unittest

root = Path(__file__).resolve().parents[2]
source = root / 'Styx/Logic/Pathing/ClickAim'

class GroundFormatStructure(unittest.TestCase):
    def test_corpse_ground_check_and_click_guards_remain(self):
        recovery = (source / 'WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')
        spot = recovery[recovery.index('    private static bool TrySnapSafeSpot('):recovery.index('    private static bool WalkGhostTo(')]
        self.assertIn('nav.GetPolyArea', spot)
        self.assertIn('MmapGroundPolicy.IsGround(map, snapped, area', spot)
        self.assertIn('nav.IsTileLoaded(map, x, y)', spot)
        self.assertIn('MaxClickRadius', spot)
        fallback = recovery[recovery.index('    private static bool FindFallbackPrompt('):recovery.index('    private static bool TryDialogClick(')]
        self.assertIn('prompt was not checked here', fallback)
        self.assertIn('TryPromptAtSpot', fallback)
        click = recovery[recovery.index('    private static bool TryDialogClick('):recovery.index('    internal static bool TryRecover(')]
        for guard in ('SpotSafe(', 'SuitableForClick(', 'ConfirmedLeftClick(', 'FinalCheck(', 'Safety()'):
            self.assertIn(guard, click)

    def test_format_policy_is_fail_closed_and_fixtures_linked(self):
        text = (source / 'MmapGroundPolicy.cs').read_text()
        for guard in ('GetModuleHandleW("Navigation.dll")', 'OpenPreferred(', 'mapFile.Length != 40',
                      'area != 11', 'fx < 1', '!tileLoaded(x, y)', 'size + 20 != tile.Length',
                      'r.ReadUInt32() == DetourMagic', 'catch { return false; }'):
            self.assertIn(guard, text)
        project = (root / 'Tools/WowBReaderTests/WowBReaderTests.csproj').read_text()
        self.assertIn('MmapGroundPolicy.cs', project)
        self.assertIn('Resurrect70938.bgra.gz', project)
        fixture = (root / 'Tools/WowBReaderTests/MmapGroundPolicyTests.cs').read_text()
        for case in ('legacy area semantics', 'v16 ground requires loaded tile', 'tile boundary ambiguous',
                     'classic mixed-format tile takes priority', 'unsupported tile version', 'truncated tile'):
            self.assertIn(case, fixture)
        crop = root / 'Tools/WowBReaderTests/Fixtures/Resurrect70938.bgra.gz'
        self.assertEqual(len(gzip.decompress(crop.read_bytes())), 220 * 50 * 4)

if __name__ == '__main__':
    unittest.main()

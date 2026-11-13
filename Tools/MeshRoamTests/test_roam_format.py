#!/usr/bin/env python3
"""Structural regression checks; run.py executes the production C# with .NET 10."""
import unittest
from pathlib import Path
root = Path(__file__).resolve().parents[2]
class RoamFormat(unittest.TestCase):
    def test_shared_format_policy_and_safeguards(self):
        roam = (root / 'Styx/Logic/Pathing/ClickAim/MeshRoam.cs').read_text()
        relocate = (root / 'Styx/Logic/Pathing/ClickAim/MeshRelocate.cs').read_text()
        policy = (root / 'Styx/Logic/Pathing/ClickAim/MmapGroundPolicy.cs').read_text()
        self.assertIn('MmapGroundPolicy.IsGround(map, position, area, tileLoaded)', roam)
        self.assertIn('SafeDestinationArea(_map, endSnap, area, (tx, ty) => nav.IsTileLoaded(_map, tx, ty))', roam)
        self.assertIn('MeshRoam.SafeDestinationArea(map, snap, area, (tx, ty) => nav.IsTileLoaded(map, tx, ty))', relocate)
        for guard in ('!MapCurrent()', 'ClickAimAbort.Requested', 'WithinAnchor(endSnap.X', 'end.IsValid', 'snapRejected', 'areaRejected', 'rejected area IDs='):
            self.assertIn(guard, roam)
        for guard in ('area != 11', '!tileLoaded(x, y)', 'mapFile.Length != 40', 'LegacyGround(area)', 'fx < 1'):
            self.assertIn(guard, policy)
        for file in ['GrindRunner.cs', 'GatherRunner.cs']:
            src = (root / 'Styx/Logic/Pathing/ClickAim' / file).read_text()
            self.assertIn('could not find a local mesh destination after five tries; stopping', src)
    def test_combat_and_revival_reset_old_failures(self):
        src = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()
        fight = src[src.index('                if (TryEngage(hwnd, out combatReport))'):src.index('                if (combatReport.Length > 0)')]
        self.assertIn('roamSampleFailures = 0;', fight)
        self.assertIn('if (recovered) roamSampleFailures = 0;', src)
    def test_fixture_cases_present(self):
        fixture = (root / 'Tools/MeshRoamTests/Fixture.cs.txt').read_text()
        for case in ('v16 area 11 sampled', 'v16 water denied and counted', 'legacy area 11 portal denied',
                     'legacy ground accepted', 'unloaded tile denied', 'stop during sampling', 'map change during sampling'):
            self.assertIn(case, fixture)
if __name__ == '__main__': unittest.main()

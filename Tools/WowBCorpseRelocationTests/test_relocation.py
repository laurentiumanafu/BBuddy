"""Source-level regression checks; not a C# or live navigation test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT/'Styx/Logic/Pathing/ClickAim'
REC = (SRC/'WowBCorpseRecovery.cs').read_text(encoding='utf-8-sig')
REGIONS = (SRC/'WowBCorpseRelocationRegions.cs').read_text(encoding='utf-8-sig')
MESH = (SRC/'MeshRelocate.cs').read_text(encoding='utf-8-sig')

class CorpseRelocation(unittest.TestCase):
    def test_local_reanchoring_and_budgets(self):
        loop = REC[REC.index('private static bool WalkGhostWithRelocation('):REC.index('// Mesh-only movement')]
        for s in ('WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, true)',
                  'ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint current) != State.Ghost',
                  'regions.TrySelect(current.X, current.Y', 'region.Attempts >= MeshRelocate.Attempts',
                  'MeshRelocate.TryPick(checkpoint.Map, region.X, region.Y, region.Tried',
                  'region.Tried.Add(alternate)', '++region.Attempts',
                  'unchecked(Environment.TickCount - started) >= RouteLimitMs', 'if (Interrupted()) return false'):
            self.assertIn(s, loop)
        self.assertLess(loop.index('regions.TrySelect(current.X, current.Y'), loop.index('MeshRelocate.TryPick(checkpoint.Map'))
        self.assertIn('MaxRegions = 12', REGIONS)
        self.assertIn('RegionRadiusYd = 40.0', REGIONS)
        self.assertIn('region.Attempts', loop)
        self.assertIn('region.Tried', loop)
        self.assertIn('foreach (Region region in regions)', REGIONS)
        self.assertIn('regions.Count >= MaxRegions', REGIONS)
        self.assertIn('RadiusYd = 50.0', MESH)
        self.assertIn('ClickAimMath.Len(snap.X - originX, snap.Y - originY) > RadiusYd', MESH)
        self.assertIn('RouteRunner.TryCombatMeshPath', MESH)

    def test_walk_preserves_existing_recovery_guards(self):
        walk = REC[REC.index('private static bool WalkGhostTo('):REC.index('// The five-location recovery')]
        for s in ('Allowed(memory, reader, hwnd, running)', 'RouteLimitMs', '!= State.Ghost',
                  'RouteRunner.PollInterruptDuringWalk = true', 'RouteRunner.RequireMeshDuringSession = true',
                  'ClickAimKeySteer.ReleaseHeld()', 'mover.MoveStop()'):
            self.assertIn(s, walk)

if __name__ == '__main__': unittest.main()

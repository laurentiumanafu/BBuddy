"""Recovery source wiring checks; not a C# execution or a live mesh test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]

def read(path): return (ROOT / path).read_text(encoding='utf-8-sig')
def section(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class EscapeWiring(unittest.TestCase):
    def test_bounded_diverse_candidates(self):
        geometry = read('Styx/Logic/Pathing/ClickAim/ClickAimEscape.cs')
        for item in ('90.0, 135.0, 180.0, 45.0', '-90.0, -135.0, 180.0, -45.0',
                     '180.0, 135.0, -135.0, 90.0, -90.0', '7.0, 4.0, 2.5',
                     'double.IsFinite', 'length < 1', 'attempt > 3'):
            self.assertIn(item, geometry)

    def test_sidesteps_are_guarded_and_observe_displacement(self):
        pilot = read('Styx/Logic/Pathing/ClickAim/ClickAimPilot.cs')
        method = section(pilot, 'private static bool Sidestep(', '    /// <summary>The 26-check report')
        for item in ('mover.MoveStop()', 'ClickAimKeySteer.ReleaseHeld()', 'RouteRunner.StopRequested',
                     'SessionChanged()', 'interrupt != null && interrupt()', 'ClickAimEscape.Candidates',
                     'RouteRunner.ValidateGatherSidestep(px, py, candidate)',
                     'RouteRunner.ValidateGatherSidestep(nowX, nowY, to)', 'ClickAimMath.Len(nowX - px, nowY - py) > 0.5',
                     'mover.RequireFacingForKeyMode = true', 'mover.RequireFacingForKeyMode = oldRequireFacing', 'mover.TurnInPlaceForKeyMode = true',
                     'mover.TurnInPlaceForKeyMode = oldTurnInPlace', 'cross > 0.75',
                     'ClickAimKeySteer.KeyDown', 'moved >= 0.75', 'physical clearance is unverified'):
            self.assertIn(item, method)
        self.assertLess(method.index('mover.MoveStop()'), method.index('ClickAimEscape.Candidates'))
        self.assertLess(method.index('ValidateGatherSidestep(nowX, nowY, to)'), method.index('mover.MoveTowards(to)'))
        self.assertLess(method.index('mover.MoveTowards(to)'), method.index('ClickAimKeySteer.KeyDown'))
        self.assertIn('sidesteps >= MaxSidesteps', pilot)
        # When no segment validates (off-mesh or broken mesh), the sidestep
        # falls back to an unvalidated jump instead of withholding and stopping
        # the walk. The first unstuck attempt is a forward jump step toward the
        # goal (most stuck characters clear with a forward hop); later attempts
        # use the sidestep directions (side, left, right, back).
        for item in ('firstUntried', 'haveUntried', 'validated = false',
                     'fall back to an unvalidated jump',
                     'The first unstuck attempt is a forward jump step toward the goal',
                     'if (attempt == 1)', 'double fdx = goal.X - px, fdy = goal.Y - py'):
            self.assertIn(item, method)
        self.assertLess(method.index('haveUntried'), method.index('mover.MoveTowards(to)'))
        # The forward jump is tried before the sidestep-direction fallback.
        self.assertLess(method.index('if (attempt == 1)'), method.index('if (!found && haveUntried)'))
        route = read('Styx/Logic/Pathing/ClickAim/RouteRunner.cs')
        self.assertIn('if (mover.KeyMode || mover.Clicks == 0)', route)
        self.assertIn('no polygon blacklisted. Returning to the route owner for recovery', route)
        self.assertIn('ValidateStraightMeshSegment(nav, map, startSnap, endSnap, false)', route)

    def test_resource_and_hotspot_recovery_and_stale_node_guard(self):
        gather = read('Styx/Logic/Pathing/ClickAim/GatherRunner.cs')
        recovery = section(gather, 'private static bool RelocateAndRetry(', '    // No movement, turns, stop-key taps')
        for item in ('MeshRelocate.Attempts', 'MeshRelocate.TryPick', 'Interrupt(false)', 'MapStillCurrent()',
                     'Walk(mover, alternate, checkNodes)', 'targetStillValid != null && !targetStillValid()',
                     'Walk(mover, original, checkNodes)', 'RouteRunner.LastRunInterrupted'):
            self.assertIn(item, recovery)
        self.assertLess(recovery.index('targetStillValid != null && !targetStillValid()'), recovery.index('Walk(mover, original, checkNodes)'))
        self.assertIn('RelocateAndRetry(mover, next)', gather)
        resource = section(gather, 'bool reached = Walk(mover, destination, false);', '                ClickAimKeySteer.ReleaseHeld();')
        for item in ('RelocateAndRetry(mover, destination, false, NodeStillValid)', 'fresh.Guid == requestedGuid',
                     'Ready(fresh)', 'requestedPosition.Z', 'stationaryNodeFailures >= 3',
                     'RouteRunner.LastRunInterrupted', 'ClickAimPositions.TryRead'):
            self.assertIn(item, resource)

if __name__ == '__main__': unittest.main()

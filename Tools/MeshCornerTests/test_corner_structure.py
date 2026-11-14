"""Integration/source checks, not an execution of C# or a game movement test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
ROUTE = (ROOT / 'Styx/Logic/Pathing/ClickAim/RouteRunner.cs').read_text()
GRIND = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()
PILOT = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimPilot.cs').read_text()
STEER = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimKeySteer.cs').read_text()

class CornerIntegration(unittest.TestCase):
    def test_route_preserves_corners_and_brakes(self):
        self.assertIn('internal const double MeshCornerTolerance = 0.9', ROUTE)
        follow = ROUTE[ROUTE.index('WoWPoint[] follow = Resample(path, FollowStepYd);'):ROUTE.index('if (complete) return true;')]
        for fragment in ('IsMeshCorner(follow, p)', 'corner ? MeshCornerTolerance : StepTolerance',
                         'mover.TurnInPlaceForKeyMode = true', 'mover.RequireFacingForKeyMode = true',
                         'if (corner) ClickAimKeySteer.ReleaseHeld()', 'finally',
                         'mover.TurnInPlaceForKeyMode = oldTurnInPlace',
                         'mover.RequireFacingForKeyMode = oldRequireFacing', 'StopRequested || ClickAimAbort.Requested'):
            self.assertIn(fragment, follow)
        self.assertIn('false, true, !lastPoint, InterruptDuringWalk', follow)
        self.assertIn('if (!keepMoving) mover.MoveStop()', PILOT)
        self.assertIn('if (turnInPlace || !forward || Math.Abs(error) > TurnWhileRunningDeg) SetForward(hwnd, false)', STEER)

    def test_chase_brakes_before_advancing_and_checks_safety(self):
        chase = GRIND[GRIND.index('private static bool ChaseStep('):GRIND.index('private static string ApproachPoolCandidate(')]
        for fragment in ('RouteRunner.IsMeshCorner(follow, watch.Point)', 'RouteRunner.MeshCornerTolerance : 2.0',
                         'if (corner) ClickAimKeySteer.ReleaseHeld()', 'out remaining, true, true',
                         'ClickAimAbort.Requested', 'combatMoveAllowed', 'TryCombatMeshPath'):
            self.assertIn(fragment, chase)
        self.assertLess(chase.index('if (corner) ClickAimKeySteer.ReleaseHeld()'), chase.index('watch.Point++;'))

    def test_corner_geometry_selftest_covers_bends_and_straight(self):
        method = ROUTE[ROUTE.index('internal static bool IsMeshCorner'):ROUTE.index('private static WoWPoint[] Resample')]
        for fragment in ('i <= 0', 'i >= path.Length - 1', 'double.IsFinite(a)', 'a < 0.001', '0.984807753'):
            self.assertIn(fragment, method)
        self.assertIn('IsMeshCorner(corners, 1) && IsMeshCorner(corners, 2)', ROUTE)
        self.assertIn('!IsMeshCorner(new[]', ROUTE)

if __name__ == '__main__':
    unittest.main()

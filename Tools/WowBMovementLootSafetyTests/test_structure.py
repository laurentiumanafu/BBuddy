"""Static integration checks, not Windows or live-client execution."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
S = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimKeySteer.cs').read_text()
I = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimInput.cs').read_text()
G = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()

def section(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class MovementLootSafety(unittest.TestCase):
    def test_turn_key_released_when_aborted_or_post_fails(self):
        tap = section(I, 'internal static bool TapMovementKey(', 'public static void ReleaseAll(')
        self.assertIn('ClickAimAbort.Requested', tap)
        self.assertIn('ClickAimAbort.Wait(ms)', tap)
        self.assertIn('finally', tap)
        self.assertLess(tap.index('finally'), tap.index('WM_KEYUP'))
        steer = section(S, 'public static bool Steer(', 'public static bool Face(')
        face = section(S, 'public static bool Face(', 'public static void ReleaseArrows(')
        for body in (steer, face):
            self.assertIn('TapMovementKey', body)
            self.assertIn('ClickAimAbort.Wait(', body)
        calibration = section(S, 'public static bool Calibrate(IntPtr hwnd, double', 'public static bool StopAndRest(')
        self.assertIn('finally { SetForward(hwnd, false); }', calibration)
        self.assertIn('ClickAimAbort.Wait(ProbeWalkMs)', calibration)

    def test_no_blind_held_walk(self):
        steer = section(S, 'public static bool Steer(', 'public static bool Face(')
        self.assertIn('!ClickAimPositions.TryRead(out px, out py)) { ReleaseHeld(); return false; }', steer)
        facing = section(steer, 'if (!TryReadFacing(out facingRad))', 'double facingDeg')
        self.assertIn('ReleaseHeld();', facing)
        self.assertIn('return false;', facing)
        self.assertNotIn('SetForward(hwnd, forward)', facing)
        forward = section(S, 'public static void SetForward(', 'public static void Release(')
        self.assertIn('down && ClickAimAbort.Requested', forward)
        self.assertIn('ClickAimInput.HoldKey(hwnd, ForwardVk, false)', forward)

    def test_face_rates_are_local_and_direction_specific(self):
        face = section(S, 'public static bool Face(', 'public static void ReleaseArrows(')
        for part in ('double posRate', 'double negRate', 'lastVk == _turnPosVk && lastDelta > 0',
                     'lastVk == _turnNegVk && lastDelta < 0', 'vk == _turnPosVk ? posRate : negRate'):
            self.assertIn(part, face)
        self.assertNotIn('_turnPosRate =', face)
        self.assertNotIn('_turnNegRate =', face)

    def test_70938_melee_does_not_use_projected_right_click(self):
        melee = section(G, '/* ---- in melee: swing, do not steer ----', '/* The melee half of the bar')
        self.assertIn('else if (!reader.IsExperimental70938 && !keyOneMacro', melee)
        self.assertIn('TryClickUnit(hwnd, target', melee)
        self.assertIn('reader.IsExperimental70938 ||', melee)
        self.assertIn('unverified melee right-click withheld', melee)
        self.assertIn('if (!keyOneMacro && auto != null)', melee)
        self.assertIn('MayPressConservativeAttack', melee)

    def test_recent_70938_hover_only_and_click_checks(self):
        click = section(G, 'using var uncachedClick =', 'if (strictClick && clickReader != null && clickReader.ReadTargetGuid()')
        self.assertIn('clickReader?.IsExperimental70938 == true && clickMemory != null && safeToClick != null', click)
        self.assertNotIn('GatherSettings.Skinning', click)
        self.assertIn('FindCorpseHover', click)
        self.assertIn('hoverSafe()', click)
        self.assertIn('finalHover == unit.Guid', click)
        self.assertIn('ConfirmedRightClick', click)
        self.assertIn('return false;', click)
        self.assertIn('return true;', click)
        loot = section(G, 'private static int TryLootOne(', 'private static bool FindCorpseHover(')
        self.assertIn('!lootReader.IsExperimental70938', loot)
        self.assertIn('!lootClicked && fastRecentProbe && searchExhausted', loot)
        self.assertIn('recentSafe()', loot)
        self.assertIn('RecentLootInReach(afterTurnCorpse', loot)

if __name__ == '__main__':
    unittest.main()

"""Source integration checks; not C# execution or proof of a client cooldown."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]
ROT = (ROOT/'Styx/Logic/Pathing/ClickAim/ClickAimClassRotation.cs').read_text(encoding='utf-8-sig')
FIGHT = (ROOT/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
TEST = (ROOT/'Tools/WowBReaderTests/RotationTests.cs').read_text(encoding='utf-8-sig')
class Stormstrike(unittest.TestCase):
    def test_opt_in_role_and_estimate(self):
        for text in ('StormstrikeId = 17364', 'StormstrikeInterval70938Ms = 8000',
                     'StormstrikePowerFloor70938 = 125', 'experimental70938 = false',
                     'plan.Class == ClickAimRotationClass.Shaman && plan.Melee == null',
                     'Find(bar, new[] { "Stormstrike" }, ClickAimSpellRole.Damage)',
                     'plan.Melee.IsDamage && plan.Melee.IsMelee && plan.Melee.KeyVk != 0',
                     'plan.MeleeGroup = StormstrikeCooldownGroup70938'):
            self.assertIn(text, ROT)
        for text in ('Ignore suppresses Stormstrike', 'older build rotation retains old behavior',
                     'Shock does not defer Stormstrike group', 'unbound ability is withheld',
                     'Shock and auto attack stay enabled'):
            self.assertIn(text, TEST)
    def test_melee_cast_rechecks_every_guard_and_separate_timer(self):
        self.assertIn('ClickAimClassRotation.Build(ClickAimSettings.RotationClass, bar, reader.IsExperimental70938)', FIGHT)
        self.assertIn('shockAutoPlan || (reader.IsExperimental70938 && meleeHit != null && meleeHit.Id == ClickAimClassRotation.StormstrikeId && meleeHit.IsDamage)', FIGHT)
        start=FIGHT.index('bool stormstrike70938 ='); end=FIGHT.index('/* Nothing was sent this pass',start)
        s=FIGHT[start:end]
        for text in ('ClickAimClassRotation.Ready(_rotationCooldowns, rotation.MeleeGroup',
                     'ClickAimClassRotation.StormstrikePowerFloor70938', 'ClickAimCombat.CastSettleMs',
                     'meleeHit.KeyVk != 0', 'rotation.MeleeGroup == ClickAimClassRotation.StormstrikeCooldownGroup70938',
                     'stormstrike70938 || !swinging || sinceDamageMs >= MeleePressMs',
                     'FightAttackInputAllowed', 'ResourceInputStillAllowed', 'hitPlayer.Guid != me.Guid',
                     'hitTarget.Guid != defenceTarget', 'hitTarget.UnitAddress != expectedPull.UnitAddress',
                     'hitTarget.HasPlayerVtable', 'DefenceFieldsValid(hitMob.Health',
                     'hitSelf.Health == 0', 'hitSelf.PlayerPowerTypeCandidate != 0', 'hitSelf.Power < meleePower',
                     'double.IsFinite(hitDistance)', 'hitDistance > ClickAimCombat.MeleeRangeYd',
                     'ClickAimInput.TapKey(hwnd, meleeHit.KeyVk, 40)', 'rotation.MeleeIntervalMs',
                     'client cooldown and cast success unverified'):
            self.assertIn(text,s)
        self.assertLess(s.index('hitSelf.Power < meleePower'),s.index('ClickAimInput.TapKey(hwnd, meleeHit.KeyVk, 40)'))
        self.assertLess(s.index('ClickAimInput.TapKey(hwnd, meleeHit.KeyVk, 40)'),s.index('ClickAimClassRotation.Defer'))
        self.assertNotIn('Thread.Sleep(8000)',s)
        self.assertNotIn('ClickAimKeySteer.Face(',s)
    def test_reset_is_a_narrow_guarded_override_not_a_general_ready_read(self):
        policy = (ROOT/'Styx/Logic/Pathing/ClickAim/WowBStormstrikeReset.cs').read_text()
        research = (ROOT/'Styx/Logic/Pathing/ClickAim/WowBCooldownResearch.cs').read_text()
        for text in ('MinClearSpacingMs = 100', 'elapsed >= WindowMs', 'previousStarts.Contains(node.Start)',
                     'node.Duration != 8000', 'node.SameExceptDuration(active)', 'same != 1',
                     'if (nodes == null', 'ResetCandidate = true', 'Address != other.Address'):
            self.assertIn(text, policy)
        for text in ('TryReadStormstrike', 'TemporaryCacheState(false)', 'first != second',
                     'IsCommittedReadableRange64', 'visited.Add(node)', 'watch.ElapsedMilliseconds > BudgetMs'):
            self.assertIn(text, research)
        guard = FIGHT[FIGHT.index('private static bool TryStormstrikeCandidate'):FIGHT.index('private static string Fight(')]
        for text in ('reader.IsExperimental70938', 'reader.ExperimentalGrindActive', 'ObjectManager.Wow != memory',
                     'beforeMap != map', 'afterMap != map', 'TraceIdentity70707', 'ClickAimAbort.Requested',
                     'RouteRunner.StopRequested', 'TryReadStormstrike'):
            self.assertIn(text, guard)
        self.assertGreaterEqual(guard.count('TraceIdentity70707'), 2)
        self.assertIn('stormReset.Observe(readable ? nodes : null', FIGHT)
        self.assertIn('if (!stormReset.ResetCandidate) return false;', FIGHT)
        self.assertIn('ObserveStormReset(); // Fresh', FIGHT)
        self.assertIn('stormReset.Begin(stormBaseline', FIGHT)
        self.assertIn('stormstrike70938 && StormResetReady()', FIGHT)
        self.assertIn('&& !StormResetReady())', FIGHT)
        self.assertIn('ClickAimAbort.Wait(Math.Min(125, remaining))', FIGHT)
        self.assertNotIn('_rotationCooldowns.Remove', FIGHT)
if __name__ == '__main__': unittest.main()

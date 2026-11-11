"""Static regression checks for the 70862 defence-only faction bypass.

These do not execute C# and do not establish attacker identity or game behaviour.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')


def section(start, end):
    i = SOURCE.index(start)
    return SOURCE[i:SOURCE.index(end, i)]


class DefenceScopeTests(unittest.TestCase):
    def test_bypass_is_70862_only(self):
        policy = section('    private static bool DefenceFactionAllowed(', '    private static bool TryTakeDefenceHandoff(')
        self.assertIn('!grindMode || reader.Uses70862Features || WowBGrindFactions.AllowsPull', policy)

    def test_nearby_remote_and_handoff_use_defence_policy(self):
        handoff = section('    private static bool TryTakeDefenceHandoff(', '    private static string FightDefenceWithHandoffs(')
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        remote = section('    private static string DefendRemote(', '    // Revalidate the remote fight')
        self.assertIn('DefenceFactionAllowed(reader, grindMode, selected)', handoff)
        self.assertIn('DefenceFactionAllowed(reader, grindMode, unit)', near)
        self.assertIn('DefenceFactionAllowed(reader, grindMode, selected)', near)
        for unit in ('unit', 'fresh', 'readyUnit', 'selected'):
            self.assertIn(f'DefenceFactionAllowed(reader, true, {unit})', remote)
        self.assertIn('!reader.Uses70862Features', section('    private static bool RemoteDefenceMovementAllowed(', '    private static bool InRealCombat()'))

    def test_fight_propagates_bypass_only_to_defence(self):
        handoff = section('    private static string FightDefenceWithHandoffs(', '    private static string DefendNearby(')
        fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('grindMode && !reader.Uses70862Features, remoteDefence', handoff)
        self.assertIn('(!defenceOnly || defenceFactionFilter) && !FactionPullStillAllowed', fight)
        self.assertEqual(10, fight.count('!defenceOnly || defenceFactionFilter, defenceOnly'))
        self.assertIn('target.UnitAddress != expectedPull.UnitAddress', fight)
        self.assertIn('target.RecordAddress != expectedPull.RecordAddress', fight)
        self.assertIn('reader.Uses70862Features) && GrindFactionSettings.Rules.Enabled', fight)

    def test_damaged_focus_uses_bounded_remote_chase(self):
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        self.assertIn('reader.Uses70862Features && focusedDistance > GatherDefenceRadius', near)
        self.assertIn('!Unreachable(focused.Guid) && !RemoteDefenceBlocked', near)
        self.assertIn('player, focused, focusedRemote)', near)
        fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('defenceOnly && remoteDefence', fight)
        self.assertIn('RemoteDefenceMovementAllowed(reader, fightMemory, expectedPull)', fight)
        self.assertIn('defenceOnly && remoteDefence && d > GatherDefenceRadius && RemoteDefenceBlocked', fight)
        self.assertIn('combatMoveAllowed: defensiveMovement', fight)

    def test_combat_buff_stops_chase_before_cast(self):
        fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('beforeAttempt: ClickAimKeySteer.ReleaseHeld', fight)
        stone = section('    private static bool TryStoneclaw(', '    // Key 1 is an explicit')
        self.assertLess(stone.index('ClickAimKeySteer.ReleaseHeld()'), stone.index('ClickAimInput.TapKey'))
        buff = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs').read_text(encoding='utf-8-sig')
        combat = buff[buff.index('    internal bool TryApplyInCombat('):buff.index('    // Returns true only if at least one buff key')]
        macro = buff[buff.index('    private bool TryTotemMacro('):buff.index('    internal bool TryApplyInCombat(')]
        for body in (combat, macro):
            self.assertLess(body.index('beforeAttempt?.Invoke()'), body.index('ClickAimInput.TapKey'))
            self.assertIn('CombatPlayerSafe(', body[body.index('beforeAttempt?.Invoke()'):])
            self.assertIn('targetSafe()', body[body.index('beforeAttempt?.Invoke()'):])

    def test_pull_still_checks_faction(self):
        select = section('    private static bool TryCurrentSelection(', '    // A fresh voluntary pull')
        verify = section('    private static bool FactionPullStillAllowed(', '    // Re-read the selected GUID')
        self.assertIn('WowBGrindFactions.AllowsPull(reader, target, out _)', select)
        self.assertIn('WowBGrindFactions.AllowsPull(reader, actual, out _)', verify)

    def test_70862_actual_selection_is_double_checked_before_turn_and_after_tab(self):
        selected = section('    private static bool TrySelectedNearbyDefence(', '    private static bool TryFightSelectedNearby(')
        for guard in ('pass < 2', 'TemporaryCacheState(false)', 'ObjectManager.Wow != memory',
                      'ObjectManager.WowB != reader', 'RouteRunner.StopRequested',
                      'TryReadGatherCombat(out bool fighting)', 'TryReadGatherCombat(out bool stillFighting)',
                      'TryGetLocalPlayer', 'TryGetSelectedTarget', 'currentTarget.HasPlayerVtable',
                      'reader.ReadTargetGuid() != currentTarget.Guid', 'reader.ReadTargetGuid() == selected.Guid',
                      'DefenceFieldsValid', 'DefenceFactionAllowed', 'double.IsFinite(distance)',
                      'distance > maxDistance', 'currentTarget.RecordAddress != firstTarget.RecordAddress',
                      'currentTarget.UnitAddress != firstTarget.UnitAddress'):
            self.assertIn(guard, selected)
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        self.assertLess(near.index('"already-selected"'), near.index('ClickAimKeySteer.Face('))
        self.assertIn('for (int attempt = -1;', near)
        self.assertIn('RemoteDefenceRadius, true', near)
        self.assertIn('continue; // never fall through to an unvalidated 70862 selection', near)
        self.assertIn('NearDefenceRetryMs', near)
        self.assertIn('_nearDefenceCursor = (startCandidate + candidates) % nearby.Count', near)
        self.assertIn('reader.Uses70862Features ? 1 : 3', near)

    def test_70862_preserves_damaged_voluntary_pull_and_prioritises_allow_lists(self):
        focus = section('    private static bool IsDamagedVoluntaryFocus(', '    private static bool ShouldYieldRemotePull(')
        for guard in ('reader.Uses70862Features', '_damagedVoluntaryTarget.Guid', 'target.UnitAddress == _damagedVoluntaryTarget.UnitAddress',
                      'target.RecordAddress == _damagedVoluntaryTarget.RecordAddress', '!Unreachable(target.Guid)'):
            self.assertIn(guard, focus)
        fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('if (!defenceOnly) _damagedVoluntaryTarget = target', fight)
        self.assertIn('IsDamagedVoluntaryFocus(reader, target) && double.IsFinite(d) && d <= RemoteDefenceRadius + 3.0', fight)
        self.assertIn('combatNow && !keepDamagedPull && ShouldYieldRemotePull', fight)
        self.assertIn('DamagedPullMovementAllowed(reader, fightMemory, expectedPull)', fight)
        self.assertIn('!closingShockTarget && ClickAimSettings.UseBuffsInCombat', fight)
        self.assertIn('_focusedDamageObserved && d > MeleeEngageYd', fight)
        movement = section('    private static bool DamagedPullMovementAllowed(', '    private static bool InRealCombat()')
        for guard in ('RouteRunner.StopRequested', 'ObjectManager.Wow != memory', 'TryReadGatherCombat',
                      'selected.UnitAddress != expected.UnitAddress', 'selected.RecordAddress != expected.RecordAddress',
                      'DefenceFieldsValid', 'RemoteDefenceRadius + 3.0',
                      'FightAttackInputAllowed(reader, memory, expected.Guid, expected, true, true, true)'):
            self.assertIn(guard, movement)
        self.assertIn('FactionPullStillAllowed(reader, expectedPull, target)', fight)
        self.assertIn('ProfileTargetAllowed(reader, target)', fight)
        policy = section('    private static bool DefencePreferred(', '    // On 70862 a TAB')
        self.assertIn('GrindFactionSettings.Rules.ContainsAllowedKey(key)', policy)
        self.assertIn('_defenceManualRules.ContainsAllowedKey(key)', policy)
        self.assertIn('PreferredDefenceCandidates(reader, snap, player, target.Guid, GatherDefenceRadius', policy)
        self.assertIn('return HasNearbyOtherNpc(snap, player, target.Guid, reader)', policy)
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        self.assertLess(near.index('PreferredDefenceCandidates(reader, snap, player, default'),
                        near.index('no preferred faction NPC confirmed by TAB'))
        self.assertIn('Math.Min(preferred.Count, 3)', near)
        self.assertIn('RemoteDefenceRadius, true', near)

    def test_stop_combat_and_selection_checks_remain(self):
        guard = section('    private static bool FightAttackInputAllowed(', '    // End the old rotation')
        self.assertIn('RouteRunner.StopRequested', guard)
        self.assertIn('requireCombat && (!TryReadGatherCombat(out bool activeCombat) || !activeCombat)', guard)
        self.assertIn('!requireCombat || (TryReadGatherCombat(out bool stillFighting) && stillFighting)', guard)
        self.assertGreaterEqual(guard.count('reader.ReadTargetGuid()'), 2)
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        self.assertIn('!TryReadGatherCombat(out bool fighting) || !fighting', near)
        self.assertIn('selected.Guid != candidate.Guid', near)
        self.assertIn('DefenceFieldsValid', near)


if __name__ == '__main__':
    unittest.main()

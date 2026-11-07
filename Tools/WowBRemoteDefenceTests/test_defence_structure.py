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
        self.assertIn('!grindMode || reader.IsExperimental70862 || WowBGrindFactions.AllowsPull', policy)

    def test_nearby_remote_and_handoff_use_defence_policy(self):
        handoff = section('    private static bool TryTakeDefenceHandoff(', '    private static string FightDefenceWithHandoffs(')
        near = section('    private static string DefendNearby(', '    // Out-of-melee combat fallback.')
        remote = section('    private static string DefendRemote(', '    // Revalidate the remote fight')
        self.assertIn('DefenceFactionAllowed(reader, grindMode, selected)', handoff)
        self.assertIn('DefenceFactionAllowed(reader, grindMode, unit)', near)
        self.assertIn('DefenceFactionAllowed(reader, grindMode, selected)', near)
        for unit in ('unit', 'fresh', 'readyUnit', 'selected'):
            self.assertIn(f'DefenceFactionAllowed(reader, true, {unit})', remote)
        self.assertIn('!reader.IsExperimental70862', section('    private static bool RemoteDefenceMovementAllowed(', '    private static bool InRealCombat()'))

    def test_fight_propagates_bypass_only_to_defence(self):
        handoff = section('    private static string FightDefenceWithHandoffs(', '    private static string DefendNearby(')
        fight = section('    private static string Fight(', "    /// <summary>\n    /// A unit's health straight out of its data block")
        self.assertIn('grindMode && !reader.IsExperimental70862, remoteDefence', handoff)
        self.assertIn('(!defenceOnly || defenceFactionFilter) && !FactionPullStillAllowed', fight)
        self.assertEqual(8, fight.count('!defenceOnly || defenceFactionFilter, defenceOnly'))
        self.assertIn('target.UnitAddress != expectedPull.UnitAddress', fight)
        self.assertIn('target.RecordAddress != expectedPull.RecordAddress', fight)
        self.assertIn('reader.IsExperimental70862) && GrindFactionSettings.Rules.Enabled', fight)

    def test_pull_still_checks_faction(self):
        select = section('    private static bool TryCurrentSelection(', '    // A fresh voluntary pull')
        verify = section('    private static bool FactionPullStillAllowed(', '    // Re-read the selected GUID')
        self.assertIn('WowBGrindFactions.AllowsPull(reader, target, out _)', select)
        self.assertIn('WowBGrindFactions.AllowsPull(reader, actual, out _)', verify)

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

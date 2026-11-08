"""Static safety/integration checks; not a C# or real-client gameplay test."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
R = Path(__file__).resolve().parents[2]
def read(p): return (R / p).read_text(encoding='utf-8-sig')
A = read('Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs')
B = read('Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs')
C = read('Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs')
G = read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
H = read('Styx/Logic/Pathing/ClickAim/GatherRunner.cs')
X = read('UI/ClickAimCombatSettingsWindow.xaml')

def section(text, start, end):
    i = text.index(start)
    return text[i:text.index(end, i)]

class TotemBuffs(unittest.TestCase):
    def test_exact_observed_mapping_and_roles(self):
        self.assertIn('castId == 8160 ? 8162 : castId == 5394 ? 5672 : 0', A)
        self.assertIn('{ 8160, "Strength of Earth Totem" }', C)
        self.assertIn('{ 5394, "Healing Stream Totem" }', C)
        self.assertIn('{ "Strength of Earth Totem", ClickAimSpellRole.Buff }', C)
        self.assertIn('{ "Healing Stream Totem", ClickAimSpellRole.Buff }', C)
        self.assertIn('reader.IsExperimental70862 && WowBTotemAuraCandidate.EffectForCast', B)
        self.assertIn('if (!TotemSupported(reader, action)) continue;', B)

    def test_unknown_absent_and_active_are_distinct(self):
        for item in ('!reader.IsExperimental70862', 'ReadExternalCandidate(memory, player.UnitAddress',
                     'count != BitConverter.ToUInt32(after, 0)', 'ids.Count == count', '!ids.Add(id)',
                     'count == 0', 'ExternalMaxSlots', 'IdWithinSlot = 0xE8'):
            self.assertIn(item, A)
        self.assertIn('aura?.Active(action.Id) == false', B)
        self.assertNotIn('duration', A.lower())
        self.assertIn('!active.HasValue ? "unavailable; cast withheld"', B)

    def test_backoff_and_safety_in_combat_and_quiet(self):
        self.assertIn('TotemRetryMs = 10_000', B)
        self.assertIn('_schedule.PastAttemptBackoff(action.Id, now, TotemRetryMs)', B)
        self.assertIn('fields.PlayerPowerTypeCandidate == 0', B)
        self.assertIn('spellId == 8160 ? 65U : spellId == 5394 ? 40U', B)
        combat = section(B, 'internal bool TryApplyInCombat(', '    // Returns true only if at least one buff key')
        self.assertLess(combat.index('WowBTotemAuraCandidate.Capture'), combat.index('ClickAimInput.TapKey'))
        for text in ('CombatPlayerSafe(', 'targetSafe()', 'SameSlot(', 'TotemManaEnough(',
                     'TotemDue(', 'ClickAimSettings.UseBuffsInCombat', '_schedule.RecordAttempt'):
            self.assertIn(text, combat)
        quiet = section(B, 'internal bool TryApply(', '// 70707 research candidate')
        for text in ('SameQuietPlayer(', 'reader.ReadTargetGuid() != selected', 'SameSlot(',
                     'TotemManaEnough(', 'TotemDue(', '_schedule.RecordAttempt'):
            self.assertIn(text, quiet)
        self.assertLess(quiet.index('WowBTotemAuraCandidate.Capture(memory, reader, player)', quiet.index('foreach (ClickAimAction action in due)')), quiet.index('ClickAimInput.TapKey'))

    def test_walking_yields_to_outer_loop_without_cast_in_callback(self):
        need = section(B, 'internal bool TotemNeedsAttention(', '    private void ReportTotem(')
        self.assertNotIn('TapKey', need)
        for text in ('TryQuietPlayer(', 'SameQuietPlayer(', 'TotemManaEnough(', 'TotemDue(', 'ScanIntervalMs'):
            self.assertIn(text, need)
        self.assertIn('Buffs.TotemNeedsAttention(hwnd, () => _stop)', G)
        self.assertIn('GrindRunner.TotemNeedsAttention(ObjectManager.Wow?.WindowHandle', H)
        walk = section(G, 'RouteRunner.ShouldInterrupt = () =>', 'RouteRunner.PollInterruptDuringWalk = true;')
        self.assertLess(walk.index('VendorDue()'), walk.index('Buffs.TotemNeedsAttention'))
        self.assertLess(walk.index('FightNeeded('), walk.index('Buffs.TotemNeedsAttention'))

    def test_ui_and_docs_are_honest(self):
        ET.fromstring(X)
        self.assertIn('Walking out of range can cause a new totem', X)
        self.assertIn('at least 10 seconds between attempts', X)
        self.assertIn('Other ranks and spells are **not** mapped', read('TOTEM_BUFFS_70862.md'))

if __name__ == '__main__': unittest.main()

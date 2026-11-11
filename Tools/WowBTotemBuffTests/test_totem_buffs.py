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
        self.assertIn('castId == 8160 ? 8162 : castId == 5394 ? 5672 : castId == 5675 ? 5677 : 0', A)
        self.assertIn('{ 8160, "Strength of Earth Totem" }', C)
        self.assertIn('{ 5394, "Healing Stream Totem" }', C)
        self.assertIn('{ 5675, "Mana Spring Totem" }', C)
        self.assertIn('{ "Strength of Earth Totem", ClickAimSpellRole.Buff }', C)
        self.assertIn('{ "Healing Stream Totem", ClickAimSpellRole.Buff }', C)
        self.assertIn('{ "Mana Spring Totem", ClickAimSpellRole.Buff }', C)
        self.assertIn('reader.Uses70862Features && WowBTotemAuraCandidate.EffectForCast', B)

    def test_unknown_absent_and_active_are_distinct(self):
        for item in ('!reader.Uses70862Features', 'ReadExternalCandidate(memory, player.UnitAddress',
                     'count != BitConverter.ToUInt32(after, 0)', 'ids.Count == count', '!ids.Add(id)',
                     'count == 0', 'ExternalMaxSlots', 'IdWithinSlot = 0xE8'):
            self.assertIn(item, A)
        self.assertIn('aura?.Active(action.Id) == false', B)
        self.assertNotIn('duration', A.lower())
        self.assertIn('!active.HasValue ? "unavailable; cast withheld"', B)

    def test_combat_only_and_ten_second_backoff(self):
        self.assertIn('TotemRetryMs = 10_000', B)
        self.assertIn('_schedule.PastAttemptBackoff(action.Id, now, TotemRetryMs)', B)
        self.assertIn('fields.PlayerPowerTypeCandidate == 0', B)
        self.assertIn('spellId == 8160 ? 65U : spellId == 5394 ? 40U : spellId == 5675 ? 40U', B)
        combat = section(B, 'internal bool TryApplyInCombat(', '    // Returns true only if at least one buff key')
        self.assertLess(combat.index('WowBTotemAuraCandidate.Capture'), combat.index('ClickAimInput.TapKey'))
        for text in ('CombatPlayerSafe(', 'targetSafe()', 'SameSlot(', 'TotemManaEnough(',
                     'TotemDue(', 'ClickAimSettings.UseBuffsInCombat', '_schedule.RecordAttempt'):
            self.assertIn(text, combat)
        safe = section(B, 'private static bool CombatPlayerSafe(', '    internal bool TryApplyInCombat(')
        self.assertIn('!GrindRunner.TryReadGatherCombat(out bool fighting) || !fighting', safe)
        self.assertGreaterEqual(combat.count('CombatPlayerSafe('), 3)
        quiet = section(B, 'internal bool TryApply(', '// 70707 research candidate')
        self.assertEqual(quiet.count('if (WowBTotemAuraCandidate.EffectForCast(action.Id) != 0) continue;'), 2)
        self.assertNotIn('WowBTotemAuraCandidate.Capture(', quiet)
        self.assertNotIn('TotemDue(', quiet)
        self.assertIn('_schedule.Due(action.Id, now, remaining)', quiet)
        self.assertIn('SameQuietPlayer(', quiet)

    def test_no_walk_interrupts_or_out_of_combat_retries(self):
        for source in (B, G, H):
            self.assertNotIn('TotemNeedsAttention', source)
        self.assertNotIn('buffInterrupt', G)

    def test_ui_and_docs_are_honest(self):
        ET.fromstring(X)
        self.assertIn('The supported 70862/70888 totems are combat-only', X)
        self.assertIn('at least 10 seconds between attempts', X)
        doc = read('TOTEM_BUFFS_70862.md')
        self.assertIn('Other ranks and spells are **not** mapped', doc)
        self.assertIn('no attempts out of combat', doc)

if __name__ == '__main__': unittest.main()

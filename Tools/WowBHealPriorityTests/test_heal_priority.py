"""Static wiring checks for the heal's priority, not an in-game heal test.

The question these answer is the one asked on 2026-11-17: "when healing is there a
delay of 1.8 seconds before retrying ? if character is under 50% and it has enough
mana healing should have priority".

The 1.8 s was ClickAimCombat.CastSettleMs, the client's global cooldown, and it used
to be charged to the heal by every combat key - including auto-attack and Start
Attack, which are not spells and never occupy the bar. Two clocks now: spells pay the
cooldown, plain inputs pay only ClickAimCombat.KeySettleMs. Under
ClickAimCombat.HealUrgentPercent with power to pay for it, the heal also outranks the
gap between heals, and no damage key is sent while it waits.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
G = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
C = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs').read_text(encoding='utf-8-sig')
P = (ROOT / 'Styx/Logic/Pathing/ClickAim/ClickAimHealPolicy.cs').read_text(encoding='utf-8-sig')


def section(source, start, end):
    a = source.index(start)
    return source[a:source.index(end, a)]


def constant(source, name, cast):
    m = re.search(r'\b' + name + r'\s*=\s*([0-9.]+)\s*;', source)
    return cast(m.group(1))


def site_contexts(source, call, before):
    """Every call site of `call` with the `before` lines of code above it."""
    lines = source.splitlines()
    out = []
    for i, line in enumerate(lines):
        if call in line:
            out.append('\n'.join(lines[max(0, i - before):i]))
    return out


class HealPriority(unittest.TestCase):
    def test_two_clocks(self):
        self.assertIn('public const int KeySettleMs = 250;', C)
        self.assertIn('public const double HealUrgentPercent = 50.0;', C)
        cast_settle = constant(C, 'CastSettleMs', int)
        key_settle = constant(C, 'KeySettleMs', int)
        heal_retry = constant(C, 'HealRetryMs', int)
        urgent = constant(C, 'HealUrgentPercent', float)
        heal_line = constant(C, 'HealBelowPercent', float)
        # A plain keypress must not cost the heal the whole cooldown ...
        self.assertLess(key_settle, cast_settle)
        self.assertLessEqual(key_settle, 250)
        # ... and the urgent line sits at or under the configured heal line.
        self.assertLessEqual(urgent, heal_line)
        self.assertEqual(50.0, urgent)
        self.assertLess(cast_settle, heal_retry)

    def test_spell_and_input_keys_are_recorded_separately(self):
        helpers = section(G, '        void NoteSpellKey()', '        /* Melee bookkeeping')
        self.assertIn('void NoteSpellKey() { lastCombatKeyMs = Environment.TickCount; }', helpers)
        self.assertIn('void NoteCombatInput() { lastInputKeyMs = Environment.TickCount; }', helpers)
        self.assertIn('int lastCombatKeyMs = 0;', G)
        self.assertIn('int lastInputKeyMs = 0;', G)
        # the old single clock is gone, so no site can charge the cooldown by accident
        self.assertNotIn('NoteCombatKey', G)
        cooldown = section(G, '        bool GlobalCooldownClear()', '        /* Melee bookkeeping')
        self.assertIn('ClickAimCombat.CastSettleMs', cooldown)
        self.assertIn('ClickAimCombat.KeySettleMs', cooldown)
        self.assertIn('lastInputKeyMs', cooldown)

    def test_only_spells_start_the_global_cooldown(self):
        input_markers = ('TryFightKeyOneMacro(', 'TapKey(hwnd, auto.KeyVk', 'TapKey(hwnd, postHealStartKey')
        inputs = site_contexts(G, 'NoteCombatInput();', 8)
        self.assertGreaterEqual(len(inputs), 8)
        for context in inputs:
            self.assertTrue(any(m in context for m in input_markers), context)

        spell_markers = ('TapKey(hwnd, heal.KeyVk', 'TapKey(hwnd, dot.KeyVk', 'TapKey(hwnd, castDamage',
                         'TapKey(hwnd, meleeHit.KeyVk', 'lastCastMs = Environment.TickCount;')
        spells = site_contexts(G, 'NoteSpellKey();', 8)
        self.assertGreaterEqual(len(spells), 7)
        for context in spells:
            self.assertTrue(any(m in context for m in spell_markers), context)

        # auto-attack and Start Attack are the keys that used to cost the heal 1.8 s
        self.assertIn('TapKey(hwnd, auto.KeyVk, 40);\n                NoteCombatInput();', G)
        self.assertIn('TapKey(hwnd, postHealStartKey, 40);\n                NoteCombatInput();', G)
        self.assertIn('TapKey(hwnd, heal.KeyVk, 40);\n                    NoteSpellKey();', G)

    def test_urgent_heal_outranks_the_gap_between_heals(self):
        gate = section(G, '            bool healWanted =', '            if (ClickAimHealPolicy.ShouldAttemptHeal(')
        self.assertIn('bool healWanted = heal != null && heal.KeyVk != 0 && heal.IsHeal &&', gate)
        self.assertIn('bool healUrgent = healWanted && healPowerOk &&', gate)
        self.assertIn('ClickAimCombat.HealUrgentPercent', gate)
        self.assertIn('healUrgent ? ClickAimCombat.CastSettleMs : ClickAimCombat.HealRetryMs', gate)
        self.assertIn('bool healCooldownReady = healRetryReady && GlobalCooldownClear();', gate)
        # waiting for the heal must not send a damage key, and must keep checking STOP
        self.assertIn('heal != null && healWanted && healPowerOk && !healCooldownReady && (healUrgent || healRetryReady)', gate)
        self.assertIn('ClickAimAbort.Wait(120);', gate)
        self.assertNotIn('TapKey', gate)

    def test_urgency_never_holds_the_heal_back(self):
        # the heal is still decided only by the health line, power, spacing and cooldown
        policy = section(P, '    internal static bool ShouldAttemptHeal(', '    // A net health gain')
        for gate in ('cooldownReady', 'powerAllowed', 'health * 100.0 / maximumHealth <= healPercent'):
            self.assertIn(gate, policy)
        # and the fight passes the readiness it computed, urgency included
        self.assertIn('ClickAimSettings.HealBelowPercent, healCooldownReady, healPowerOk)', G)
        for forbidden in ('healHeld', 'healDisabled', 'healBlocked', 'healNoEffect'):
            self.assertNotIn(forbidden, G)


if __name__ == '__main__':
    unittest.main()

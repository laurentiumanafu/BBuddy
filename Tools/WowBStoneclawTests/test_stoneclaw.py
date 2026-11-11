"""Static safety checks. Synthetic fixture requires .NET; neither validates gameplay."""
from pathlib import Path
import unittest
R = Path(__file__).resolve().parents[2]
def read(p): return (R / p).read_text(encoding='utf-8-sig')
G=read('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
P=read('Styx/Logic/Pathing/ClickAim/WowBStoneclawPolicy.cs')
B=read('Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs')
C=read('Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs')
def section(src,start,end):
 i=src.index(start);return src[i:src.index(end,i)]
class Stoneclaw(unittest.TestCase):
 def test_supported_ranks_no_implicit_cast_as_buff(self):
  for id in (5730,6390,6391,6392,10427,10428):
   self.assertIn(f'{id} =>',P);self.assertIn(f'{{ {id}, "Stoneclaw Totem" }}',C)
  self.assertIn('{ "Stoneclaw Totem", ClickAimSpellRole.Utility }',C)
  self.assertIn('WowBStoneclawPolicy.ManaEstimate(action.Id) != 0',B)
  self.assertIn('role == ClickAimSpellRole.Unknown || role == ClickAimSpellRole.Utility || role == ClickAimSpellRole.Buff',P)
 def test_pair_is_bounded_not_claimed_as_attackers(self):
  for t in ('RadiusYd = 10.0', 'd >= RadiusYd', 'Math.Sqrt(dx * dx + dy * dy + dz * dz)', 'unit.HasPlayerVtable',
            'fields.Health > 0', 'fields.MaxHealth > 0', 'WowBGrindFactions.TryReadBoundKey', 'factions.Allows(true, key)', 'factions.Mode != GrindFactionMode.Allow', '!seen.Add(unit.Guid)', 'SamePairStillNear',
            'SameIdentity(first, a)', 'SameIdentity(second, b)', 'attacks the player'):
   self.assertIn(t,P)
 def test_guarded_combat_cast_and_backoff(self):
  cast=section(G,'private static bool TryStoneclaw(', '    // Key 1 is an explicit')
  safe=section(G,'private static bool StoneclawSnapshot(', '    // Two nearby live NPC candidates')
  for t in ('TryReadGatherCombat(out bool fighting) || !fighting','!targetSafe()',
            'CheckDeathCheckpoint(trackerOnly: true)','!WowBGrindFactions.Experimental70707BarSafe(reader)',
            'me.PlayerPowerTypeCandidate != 0','me.Power < mana','SameIdentity(player, expectedPlayer)',
            'SameIdentity(target, expectedTarget)','currentMap != map'):
   self.assertIn(t,safe)
  for t in ('!reader.Uses70862Features', 'area == null', 'GrindFactionRules.TryCreate(GrindFactionMode.Allow, area.Factions', 'map != _profileSessionMap', '_activeProfileArea != area', 'area.Factions != factionText', 'StoneclawSnapshot(', 'WowBStoneclawPolicy.FindPair(',
            'WowBStoneclawPolicy.SamePairStillNear(', 'StoneclawSlotCurrent(', '!targetSafe()',
            'WowBStoneclawPolicy.RetryMs', 'Buffs.ReserveEarthAfterStoneclaw()',
            'ClickAimInput.TapKey(hwnd, action.KeyVk, 40)'):
   self.assertIn(t,cast)
  self.assertLess(cast.index('WowBStoneclawPolicy.RetryMs'),cast.index('ClickAimInput.TapKey'))
  self.assertLess(cast.index('Buffs.ReserveEarthAfterStoneclaw()'),cast.index('ClickAimInput.TapKey'))
  self.assertIn('RetryMs = 30_000',P)
 def test_earth_conflict_and_order(self):
  self.assertIn('_earthReservedUntil = _clock() + 20_000',B)
  self.assertIn('action.Id == 8160 && _clock() < _earthReservedUntil',B)
  self.assertIn('!Eligible(action) || EarthReserved(action)',B)
  self.assertIn('!stillDue || EarthReserved(buff)',B)
  fight=section(G,'private static string Fight(',"    /// <summary>\n    /// A unit's health straight out of its data block")
  self.assertLess(fight.index('ClickAimInput.TapKey(hwnd, heal.KeyVk'),fight.index('TryStoneclaw(hwnd'))
  self.assertLess(fight.index('TryStoneclaw(hwnd'),fight.index('Buffs.TryApplyInCombat('))
  self.assertIn('defenceTarget, expectedPull,\n                        !defenceOnly || defenceFactionFilter, true, !defenceOnly',fight)
 def test_clear_disclosure(self):
  text=read('STONECLAW_70862.md')
  for t in ('not attacker detection','friendly, neutral, idle or summoned', 'less than 10 yards', 'no active Grind profile','once per 30 seconds',
            'for 20 seconds','Ignore','normal first-action-bar'):
   self.assertIn(t,text)
if __name__=='__main__':unittest.main()

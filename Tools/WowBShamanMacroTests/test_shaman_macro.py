"""Static tests only; synthetic C# fixture requires .NET, no live client."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[2]
def get(p):return (R/p).read_text(encoding='utf-8-sig')
B=get('Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs')
S=get('Styx/Logic/Pathing/ClickAim/ClickAimSettings.cs')
C=get('Styx/Logic/Pathing/ClickAim/ClickAimCombat.cs')
U=get('UI/ClickAimCombatSettingsWindow.xaml')
D=get('UI/ShamanTotemMacroWindow.xaml')
DC=get('UI/ShamanTotemMacroWindow.xaml.cs')
P=get('UI/ClickAimCombatSettingsWindow.xaml.cs')
class Macro(unittest.TestCase):
 def test_dialog_and_validation(self):
  ET.fromstring(U); ET.fromstring(D)
  for t in ('btnShamanTotemMacro_Click','Shaman totem macro…'):self.assertIn(t,U)
  for t in ('chkEnabled','cmbKey','cmbPresses','chkEarth','chkHealing','chkMana','Click="Ok_Click"'):self.assertIn(t,D)
  self.assertIn('healing && mana',DC);self.assertIn('key == 1 && _keyOneAttack',DC)
  self.assertIn('ClickAimUi.Running || ClickAimUi.Paused',P);self.assertIn('_shamanTotemOptions.Apply()',P)
  for key in ('UseShamanTotemMacro','ShamanTotemMacroKey','ShamanTotemMacroPresses','ShamanTotemEarth','ShamanTotemHealing','ShamanTotemMana'):
   self.assertGreaterEqual(S.count('"'+key+'"'),1);self.assertIn('"'+key+' = "',S)
  self.assertIn('!(ShamanTotemHealing && ShamanTotemMana)',S);self.assertIn('UseKeyOneStartAttackMacro && ShamanTotemMacroKey == 1',S)
 def test_opaque_macro_reserved_and_safety(self):
  self.assertIn('decoded.RemoveAll(action => action.Slot == ClickAimSettings.ShamanTotemMacroKey)',C)
  i=B.index('private bool TryTotemMacro('); j=B.index('    internal bool TryApplyInCombat(',i); body=B[i:j]
  for t in ('reader.Uses70862Features','MacroMask()', 'now < _earthReservedUntil','_macroRetryAt','_macroNextPressAt',
            'CombatPlayerSafe(', 'targetSafe()', 'PlayerPowerTypeCandidate != 0','fields.Power < minimumMana',
            'TryReadExperimentalActionSnapshot(', 'bar[key - 1] == 0', 'afterBar[key - 1] != bar[key - 1]',
            'WowBTotemAuraCandidate.Capture(', 'aura == null', 'starting && !MacroMissing',
            'mask != MacroMask()', 'ClickAimInput.TapKey(hwnd, 0x30 + key, 40)', 'TotemRetryMs', 'CastSettleMs'):
   self.assertIn(t,body)
  self.assertLess(body.index('_macroRetryAt = now + TotemRetryMs'),body.index('ClickAimInput.TapKey'))
  self.assertIn('MacroHandles(macroMask, action.Id)',B);self.assertIn('ConflictingWater(bar)',B)
 def test_inline_and_diagnostics_wiring(self):
  T=get('Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs')
  G=get('Styx/Logic/Pathing/ClickAim/GrindRunner.cs')
  self.assertIn('WowBInlineAuraCandidate.Capture(memory, player)',T)
  self.assertIn('inline.CopyIds()',T); self.assertIn('ids.Count != 0',T)
  self.assertIn('ReadExternalCandidate(memory, player.UnitAddress',T)
  self.assertIn('Word(first, ActiveCount) != (uint)occupied',B)
  self.assertIn('!seen.Add(id[i])',B)
  self.assertEqual(G.count('ClickAimBuffController.ReportMacroSettings();'),2)
  for msg in ('aura unknown:', 'combat target safety not confirmed', 'action bar unreadable or unstable',
              'Stoneclaw Earth reservation', 'failed final revalidation', '15000 : 5000'):
   self.assertIn(msg,B)
  self.assertIn('optional buffs deferred while closing',G)
 def test_only_selected_effects(self):
  for t in ('(mask & 1) != 0 && aura.Active(8160) == false','(mask & 2) != 0 && aura.Active(5394) == false',
            '(mask & 4) != 0 && aura.Active(5675) == false'):
   self.assertIn(t,B)
  doc=get('TOTEM_BUFFS_70862.md')
  for t in ('both Water totems','at least 10 seconds after the last press','not** establish','cannot skip','only during combat'):
   self.assertIn(t,doc)
if __name__=='__main__':unittest.main()

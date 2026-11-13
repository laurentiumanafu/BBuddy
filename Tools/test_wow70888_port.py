"""Structural wiring regression checks; not runtime client validation."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

def text(path): return (ROOT / path).read_text()

class Port70888(unittest.TestCase):
    def test_registration_and_shared_features(self):
        policy = text('Styx/WoWInternals/ClientAttachPolicy.cs')
        reader = text('Styx/WoWInternals/WowBReader.cs')
        self.assertIn('ExperimentalBuild70888 = 70888', policy)
        self.assertIn('ExperimentalBuild70862, ExperimentalBuild70888, ExperimentalBuild70938 }', policy)
        self.assertIn('Uses70862Features => IsExperimental70862 || IsExperimental70888', reader)
        self.assertIn('experimental70862 ? 1 : 0) + (experimental70888 ? 1 : 0)', reader)
        for field in ('PlayerGuid', 'PlayerNamePrimary', 'PlayerTargetGuid', 'MouseoverGuidCandidate',
                      'ObjectManager', 'HandlePools', 'MapRoot', 'CameraWorldFrameGlobal',
                      'PlayerVtable', 'UnitVtable', 'GameObjectVtableCandidate', 'StoredActionArrayCandidate'):
            self.assertIn(f'IsExperimental70888 ? Wow70888Offsets.{field} : IsExperimental70862 ? Wow70862Offsets.{field}', reader)
        for file in ('GrindRunner.cs', 'GatherRunner.cs', 'ClickAimBuffController.cs', 'WowBTotemAuraCandidate.cs'):
            self.assertIn('Uses70862Features', text('Styx/Logic/Pathing/ClickAim/' + file))

    def test_specific_markers_and_attach(self):
        faction = text('Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs')
        self.assertIn('0x1F9299C, 0x1FBCA57, 0x2799845, 0xA2BA0D', faction)
        self.assertIn('matches >= 2 && mismatches == 0', faction)
        inventory = text('Styx/Logic/Pathing/ClickAim/WowBInventoryProbe.cs')
        for address in ('0x2475926UL', '0x2475939UL', '0x2475944UL'):
            self.assertEqual(inventory.count('reader.IsExperimental70888 ? ' + address), 2)
        containers = text('Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs')
        self.assertIn('build == 70888 ? Markers70888 : build == 70862 ? Markers70862', containers)
        self.assertIn('reader.IsExperimental70888 ? Wow70888Offsets.ContainerIdentityTableCandidate', containers)
        self.assertIn('initialBuild == ClientAttachPolicy.ExperimentalBuild70888', text('Styx/WoWInternals/ObjectManager.cs'))
        self.assertIn('build == ClientAttachPolicy.ExperimentalBuild70888 ? Wow70888Offsets.PlayerNamePrimary', text('UI/ProcessSelectorWindow.xaml.cs'))

if __name__ == '__main__': unittest.main()

"""70938 source wiring checks; not C# execution or live-client validation."""
import unittest
import re
import check_wow70938_image as image
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

def text(path): return (ROOT / path).read_text()

class Port70938(unittest.TestCase):
    def test_registration_and_shared_70888_paths(self):
        policy = text('Styx/WoWInternals/ClientAttachPolicy.cs')
        reader = text('Styx/WoWInternals/WowBReader.cs')
        self.assertIn('ExperimentalBuild70938 = 70938', policy)
        self.assertIn('ExperimentalBuild70888, ExperimentalBuild70938 }', policy)
        self.assertIn('Uses70888Features => IsExperimental70888 || IsExperimental70938', reader)
        self.assertIn('Uses70862Features => IsExperimental70862 || IsExperimental70888 || IsExperimental70938', reader)
        self.assertIn('(experimental70938 ? 1 : 0) > 1', reader)
        for field in ('PlayerGuid', 'PlayerNamePrimary', 'PlayerTargetGuid', 'MouseoverGuidCandidate',
                      'ObjectManager', 'HandlePools', 'MapRoot', 'CameraWorldFrameGlobal',
                      'PlayerVtable', 'UnitVtable', 'GameObjectVtableCandidate', 'StoredActionArrayCandidate'):
            self.assertIn(f'IsExperimental70938 ? Wow70938Offsets.{field} : IsExperimental70888 ?', reader)
        for file in ('GrindRunner.cs', 'WowBDeathCheckpoint.cs', 'WowBTotemAuraCandidate.cs', 'WowBCombatResearch.cs'):
            self.assertIn('Uses70888Features', text('Styx/Logic/Pathing/ClickAim/' + file))
        self.assertIn('initialBuild == ClientAttachPolicy.ExperimentalBuild70938', text('Styx/WoWInternals/ObjectManager.cs'))
        self.assertIn('ClientAttachPolicy.ExperimentalBuild70938 ? Wow70938Offsets.PlayerNamePrimary', text('UI/ProcessSelectorWindow.xaml.cs'))

    def test_recorded_candidates_and_guard_bytes_match_image_checker(self):
        offsets = text('Styx/Offsets/Wow70938Offsets.cs')
        for field, value in {
            'PlayerGuid': 0x4D2EAE0, 'PlayerNamePrimary': 0x4D2EAF0,
            'PlayerTargetGuid': 0x6B467F8, 'MouseoverGuidCandidate': 0x4DEA308,
            'ObjectManager': 0x6D48D78, 'HandlePools': 0x483CBE0, 'MapRoot': 0x6D48CF8,
            'CameraWorldFrameGlobal': 0x4B661F0, 'StoredActionArrayCandidate': 0x6D674C0,
            'ContainerIdentityTableCandidate': 0x6D6A500, 'InventoryLookup': 0x21796E0,
            'InventoryHandler': 0x26E1230, 'PlayerVtable': 0x41F8070,
            'UnitVtable': 0x420C0D8, 'GameObjectVtableCandidate': 0x41FFF48,
        }.items():
            match = re.search(r'\b' + field + r' = 0x([0-9A-F]+)', offsets)
            self.assertIsNotNone(match, field)
            self.assertEqual(int(match.group(1), 16), value, field)
        faction = text('Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs')
        for rva in (0x209774C, 0x20C1C37, 0x28A8105, 0xA6017D):
            self.assertIn(image.MARKERS[rva], faction)
        containers = text('Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs')
        for rva in (0xD6C5BC, 0xD6C5E0, 0x257FB96, 0x257FBA4, 0x257FBB0):
            self.assertIn(f'(0x{rva:X}, "{image.MARKERS[rva]}")', containers)

    def test_markers_inventory_and_fixtures(self):
        faction = text('Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs')
        self.assertIn('0x209774C, 0x20C1C37, 0x28A8105, 0xA6017D', faction)
        self.assertIn('matches >= 2 && mismatches == 0', faction)
        inventory = text('Styx/Logic/Pathing/ClickAim/WowBInventoryProbe.cs')
        for rva in ('0x257FBE6UL', '0x257FBF9UL', '0x257FC04UL'):
            self.assertEqual(inventory.count('reader.IsExperimental70938 ? ' + rva), 2)
        containers = text('Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs')
        self.assertIn('build == 70938 ? Markers70938', containers)
        self.assertIn('reader.IsExperimental70938 ? Wow70938Offsets.ContainerIdentityTableCandidate', containers)
        self.assertIn('Experimental70938Tests.Run()', text('Tools/WowBReaderTests/Program.cs'))
        self.assertIn('experimental70938: true', text('Tools/WowBReaderTests/Experimental70938Tests.cs'))

if __name__ == '__main__': unittest.main()

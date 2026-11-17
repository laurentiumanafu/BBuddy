"""70959 source wiring checks; not C# execution or live-client validation.

Every candidate RVA asserted here was carried across from 1.60.1.70938 by
matching the referencing code in both images (Tools/WowBBuildPort/
derive_build_anchors.py) and is re-checked against the real image by
Tools/check_wow70959_image.py. None of it is live validation.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def text(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')


class Port70959(unittest.TestCase):
    def test_70959_is_registered_and_is_the_default(self):
        policy = text('Styx/WoWInternals/ClientAttachPolicy.cs')
        self.assertIn('ExperimentalBuild70959 = 70959', policy)
        self.assertIn('build == ExperimentalBuild70938 || build == ExperimentalBuild70959', policy)
        builds = re.search(r'RegisteredBuilds = \{([^}]*)\}', policy).group(1)
        names = [b.strip() for b in builds.split(',')]
        # DefaultBuild is RegisteredBuilds.Max(); 70959 must be the last, highest entry.
        self.assertEqual(names[-1], 'ExperimentalBuild70959')
        self.assertIn('SupportedModernBuild', builds)          # 70291 preserved
        self.assertIn('SupportedLegacyBuild', builds)          # 12340 preserved
        self.assertIn('ExperimentalBuild70938', builds)        # 70938 stays registered

    def test_reader_families_cover_70959(self):
        reader = text('Styx/WoWInternals/WowBReader.cs')
        self.assertIn('public bool IsExperimental70959 { get; }', reader)
        self.assertIn('public bool Uses70938Features => IsExperimental70938 || IsExperimental70959', reader)
        self.assertIn('Uses70888Features => IsExperimental70888 || IsExperimental70938 || IsExperimental70959', reader)
        self.assertIn('Uses70862Features => IsExperimental70862 || IsExperimental70888 || IsExperimental70938 || IsExperimental70959', reader)
        self.assertIn('ExperimentalBuildNumber => IsExperimental70959 ? 70959 : IsExperimental70938 ? 70938', reader)
        self.assertIn('bool experimental70959 = false', reader)
        self.assertIn('(experimental70938 ? 1 : 0) + (experimental70959 ? 1 : 0) > 1', reader)
        self.assertIn('|| experimental70938 || experimental70959;', reader)
        # Offsets stay build-specific: 70959 gets its own branch ahead of 70938.
        for field in ('PlayerGuid', 'PlayerNamePrimary', 'PlayerTargetGuid', 'MouseoverGuidCandidate',
                      'ObjectManager', 'HandlePools', 'MapRoot', 'CameraWorldFrameGlobal',
                      'PlayerVtable', 'UnitVtable', 'GameObjectVtableCandidate',
                      'StoredActionArrayCandidate'):
            self.assertIn(f'IsExperimental70959 ? Wow70959Offsets.{field} : IsExperimental70938 ? Wow70938Offsets.{field} :', reader)

    def test_object_manager_and_selector_wire_the_build(self):
        self.assertIn('initialBuild == ClientAttachPolicy.ExperimentalBuild70959',
                      text('Styx/WoWInternals/ObjectManager.cs'))
        selector = text('UI/ProcessSelectorWindow.xaml.cs')
        self.assertIn('ClientAttachPolicy.ExperimentalBuild70959 ? Wow70959Offsets.PlayerNamePrimary', selector)
        self.assertIn('ClientAttachPolicy.ExperimentalBuild70938 ? Wow70938Offsets.PlayerNamePrimary', selector)

    def test_recorded_candidates_match_the_derived_values(self):
        offsets = text('Styx/Offsets/Wow70959Offsets.cs')
        expected = {
            'PlayerGuid': 0x4C28A30, 'PlayerNamePrimary': 0x4C28A40,
            'PlayerTargetGuid': 0x6A407B8, 'MouseoverGuidCandidate': 0x4CE4258,
            'ObjectManager': 0x6C42D38, 'HandlePools': 0x4736BE0, 'MapRoot': 0x6C42CB8,
            'CameraWorldFrameGlobal': 0x4A60170, 'StoredActionArrayCandidate': 0x6C61480,
            'ContainerIdentityTableCandidate': 0x6C644C0, 'InventoryLookup': 0x207EF20,
            'InventoryHandler': 0x25DDD60, 'PlayerVtable': 0x40F23A8,
            'UnitVtable': 0x4106190, 'GameObjectVtableCandidate': 0x40FA278,
            'PlayerGetterCandidateRva': 0x1E74780,
        }
        for field, value in expected.items():
            match = re.search(r'\b' + field + r' = 0x([0-9A-F]+)', offsets)
            self.assertIsNotNone(match, field)
            self.assertEqual(int(match.group(1), 16), value, field)
        # Member offsets are inherited, and re-read inside the carried functions.
        for field, value in {'AutoAttackCandidate': 0x2C9, 'CurrentTargetCandidate': 0x2C0,
                             'ShapeshiftFlagsCandidate': 0x2D0, 'ShapeshiftFlagCandidate': 0x10000000,
                             'AttackTargetCandidate': 0x1D40, 'ComboPointsCandidate': 0x1E1D,
                             'ShapeshiftCandidate': 0x1EEF, 'AttackStateFlagsCandidate': 0x250C,
                             'AttackEffectCandidate': 0x25C8}.items():
            match = re.search(r'\b' + field + r' = 0x([0-9A-F]+)', offsets)
            self.assertIsNotNone(match, field)
            self.assertEqual(int(match.group(1), 16), value, field)

    def test_build_specific_markers_exist_for_70959(self):
        factions = text('Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs')
        self.assertIn('build70959 = ObjectManager.Wow == memory && ObjectManager.WowB?.IsExperimental70959 == true', factions)
        self.assertIn('build70959 ? new ulong[] { 0x1F9D48C, 0x1FC7547, 0x27A2B65, 0xA3695D }', factions)
        self.assertIn('"488D0D6D61A004E8F88852FE8B93541E0000"', factions)
        # 70938 keeps its own markers; the two must not share one array.
        self.assertIn('build70938 ? new ulong[] { 0x209774C, 0x20C1C37, 0x28A8105, 0xA6017D }', factions)
        probe = text('Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs')
        self.assertIn('(ulong Rva, string Bytes)[] Markers70959', probe)
        self.assertIn('build == 70959 ? Markers70959 :', probe)
        for marker in ('0xD1ACBC, "E8AF207601"', '0xD1ACE0, "8B80C00A0000"', '0x247E886, "486390C00A0000"',
                       '0x247E894, "4C8D8090040000"', '0x247E8A0, "498B00"'):
            self.assertIn(marker, probe)
        inventory = text('Styx/Logic/Pathing/ClickAim/WowBInventoryProbe.cs')
        for rva in ('0x247E8D6UL', '0x247E8E9UL', '0x247E8F4UL'):
            self.assertIn('reader.IsExperimental70959 ? ' + rva, inventory)
        for mail in ('Styx/Logic/Pathing/ClickAim/WowBMailAttachmentResearch.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBMailCandidateReader.cs'):
            body = text(mail)
            self.assertIn('TableRva70959 = 0x4D66A60, MarkerRva70959 = 0x25B1AA8', body)
            self.assertIn('Marker70959 = "FFC883F8107338488D0C80488D05A64F7B02488D04C84885C0"', body)
            self.assertIn('reader.IsExperimental70959 ? TableRva70959 : TableRva70938', body)

    def test_70938_behaviour_routes_through_the_family(self):
        """Every 70938 behaviour gate must reach 70959 too, not only 70938."""
        for path in ('Styx/Logic/Pathing/ClickAim/GrindRunner.cs',
                     'Styx/Logic/Pathing/ClickAim/GatherRunner.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBCombatResearch.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBInventoryProbe.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBCorpseRecovery.cs',
                     'Styx/Logic/Pathing/ClickAim/ClickAimBuffController.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBContainerResearchProbe.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBGhostWolfTravel.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBTotemAuraCandidate.cs',
                     'Styx/Logic/Pathing/ClickAim/WowBMailCandidateReader.cs'):
            body = text(path)
            if 'IsExperimental70938' in body:
                # Only exact-offset or exact-build decisions may name 70938.
                for line in body.splitlines():
                    if 'IsExperimental70938' in line and 'Wow70938Offsets' not in line \
                            and '0x' not in line and 'Markers70938' not in line:
                        self.assertNotIn('IsExperimental70938', line,
                                         f'{path}: behaviour gate still exact-build: {line.strip()[:90]}')
            self.assertIn('Uses70938Features', body, path)

    def test_image_checker_records_the_new_image(self):
        checker = text('Tools/check_wow70959_image.py')
        self.assertIn('dd2f820f68a825cd1b3ab62c2efe7e1993bbe974be6537f3ad2312de9d3012df', checker)
        for rva in (0x1F9D48C, 0x1FC7547, 0x27A2B65, 0xA3695D, 0x25B1AA8, 0xD1ACBC):
            self.assertIn(hex(rva).upper().replace('0X', '0x'), checker)


if __name__ == '__main__':
    unittest.main()

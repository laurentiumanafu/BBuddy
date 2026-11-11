"""Structural checks only, not a live client or movement test."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[2]
r = (root/'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text()
p = (root/'Styx/Logic/Pathing/ClickAim/ProfileRoute.cs').read_text()
s = (root/'Styx/Logic/Pathing/ClickAim/GrindProfilePolicy.cs').read_text()
f = (root/'Styx/Logic/Pathing/ClickAim/WowBGrindFactions.cs').read_text()

def block(start,end):
    i=r.index(start); return r[i:r.index(end,i)]

class ProfileIntegration(unittest.TestCase):
    def test_profile_validation_precedes_arming(self):
        start=block('    private static bool StartCore(', '    internal static bool StartRoaming(')
        self.assertLess(start.index('ProfileRoute.LoadGrindAreas'), start.index('PrepareForGrind'))
        self.assertLess(start.index('GrindProfilePolicy.ValidLevels'), start.index('PrepareForGrind'))
        self.assertIn('GrindFactionMode.Allow, profileArea.Factions', start)
        self.assertIn('PrepareForGrind(out string factionError, initialRules)',start)
        self.assertIn('if (profileRules != null) rules = profileRules;', f)
        self.assertIn('GrindFactionSettings.TryLoad',f)
    def test_player_and_target_bands_separate(self):
        self.assertIn('sub.Element("MinLevel")',p)
        self.assertIn('sub.Element("MaxLevel")',p)
        self.assertIn('TryReadBand(grind, sub, "TargetMinLevel"',p)
        self.assertIn('TryReadBand(grind, sub, "TargetMaxLevel"',p)
        self.assertIn('GrindProfilePolicy.PlayerInBand(area, playerLevel ?? PlayerLevel())',r)
        self.assertIn('level < (uint)area.TargetMinLevel',s)
        self.assertIn('level > (uint)area.TargetMaxLevel',s)
        self.assertIn('if (level <= 0 || !InBand(areas[chosen], level))',r)
        self.assertIn('if (!InBand(areas[nextArea], now))',r)
        self.assertIn('ActivateProfileArea(area);',r)
        self.assertIn('_activeProfileArea = null;',r)
    def test_radius_and_fresh_validation(self):
        self.assertIn('HotspotRadiusYd = 100.0',s)
        self.assertIn('foreach (RoutePoint point in area.Hotspots)',s)
        self.assertIn('dx * dx + dy * dy <= HotspotRadiusYd * HotspotRadiusYd',s)
        self.assertIn('if (applyFactionFilter && !ProfileTargetAllowed(reader, candidate, myLevel))',r)
        self.assertIn('!ProfileTargetAllowed(reader, target)',r)
        self.assertIn('!ProfileTargetAllowed(reader, candidate, approachLevel)',r)
        self.assertIn('!defenceOnly && !ProfileTargetAllowed(reader, target)',r)
        guard=block('    private static bool FightAttackInputAllowed(', '    // End the old rotation')
        for text in ('enforceProfile && _activeProfileArea != null', 'freshProfile.TryGetSelectedTarget', 'freshTarget.Guid != expected.Guid', '!ProfileTargetAllowed(reader, freshTarget)', 'FactionAttackInputAllowed'):
            self.assertIn(text,guard)
        self.assertIn('defenceOnly, !defenceOnly)',r)
    def test_defence_and_unfiltered_safety_unrestricted(self):
        self.assertIn('applyFactionFilter && !ProfileTargetAllowed',r)
        self.assertIn('out near, out nearYd, out live2, out unread2, null, false)',r)
        self.assertIn('=> !grindMode || reader.Uses70862Features || WowBGrindFactions.AllowsPull',r)
        self.assertIn('if (area == null) return true;',r)
        self.assertIn('if (roam == null && !ProfileMapCurrent())',r)
        self.assertIn('map != _profileSessionMap',r)
    def test_avoid_mobs_and_malformed_data(self):
        self.assertIn('ReadAvoidEntries(doc.Root, area)',p)
        self.assertIn('ReadAvoidEntries(sub, area)',p)
        self.assertIn('InvalidLevelData(sub',p)
        self.assertIn('!area.AvoidEntries.Contains(entry)',r)
        self.assertIn('area.InvalidProfileData',s)

if __name__=='__main__': unittest.main()

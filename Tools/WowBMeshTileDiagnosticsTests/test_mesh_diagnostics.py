from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[2]
d=(root/'Styx/Logic/Pathing/ClickAim/ClickAimDiagnostics.cs').read_text(encoding='utf-8-sig')
r=(root/'Styx/Logic/Pathing/ClickAim/MeshRoam.cs').read_text(encoding='utf-8-sig')
class MeshDiagnostics(unittest.TestCase):
 def test_logged_map_is_not_misrepresented(self):
  self.assertIn('configured NavGrid map',d); self.assertIn('not a live client map read',d)
 def test_both_naming_schemes_neighbors_and_load_status(self):
  s=d[d.index('internal static void ReportMeshTileDiagnostics'):d.index('/// The map id to route on.')]
  for item in ('GetByPosition(at.X, at.Y)', 'map.ToString("000"', 'map.ToString("0000"', '{0:000}{1:00}{2:00}.mmtile', '{0:0000}_{1:00}_{2:00}.mmtile', 'GetLoadedTilesCount(map)', 'nav.IsTileLoaded(map, tx, ty)', 'dx = -1; dx <= 1', 'dy = -1; dy <= 1', 'does not prove'):
   self.assertIn(item,s)
 def test_refusal_does_not_enable_movement(self):
  s=r[r.index('internal static bool TryCreate('):r.index('internal bool MapCurrent()')]
  self.assertIn('if (sameMap) ClickAimDiagnostics.ReportMeshTileDiagnostics',s)
  self.assertIn('return false;',s[s.index('if (sameMap) ClickAimDiagnostics.ReportMeshTileDiagnostics'):s.index('int radius')])
  self.assertIn('FindNearestPolyRef(map, start, 2.5f, 5f',s)
 def test_log_position_near_tile_boundary(self):
  def tile(p): return int((32*533.3333-p)/533.3333)
  self.assertEqual((tile(-9196.181),tile(-4799.7695)),(49,40))
  self.assertEqual(tile(-4800.3),41)
if __name__=='__main__':unittest.main()

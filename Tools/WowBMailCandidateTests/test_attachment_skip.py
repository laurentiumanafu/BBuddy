"""Static source checks; not a simulated game or proof of delivery."""
from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[2]/'Styx/Logic/Pathing/ClickAim'
m=(R/'WowBExperimentalMail.cs').read_text()
g=(R/'WowBMailGridLookup.cs').read_text()
class SkipWiring(unittest.TestCase):
 def test_skip_failed_input_and_never_reclick(self):
  self.assertIn('ISet<WowBGuid>? excluded',g)
  self.assertIn('excluded.Contains(item)',g)
  for text in ('attempted.Add(slot.Item)', 'attempted.Contains(slot.Item)',
               'skipped.Add(slot.Item)', 'if (!safe()) return false;',
               'skipping this item for the trip',
               'TryPreviewExcluding(grid, player.ToString(), w, h, bags, attempted'):
   self.assertIn(text,m)
  self.assertIn('expected.Count >= 12',m)
  self.assertIn('no item click was posted',m)
 def test_only_clicks_used_for_post_send_observation(self):
  self.assertEqual(m.count('Left(hwnd, ui.SendX'),1)
  self.assertIn('expected.Add(new WowBMailCandidateTable.Expected(slot.Item, bag.Identity))',m)
  self.assertIn('WowBMailSendObservation.CountMissing(expected, after)',m)
  self.assertIn('WowBMailGridLookup.Validate(grid, player.ToString(), w, h, after)',m)
  self.assertIn('WowBMailGridLookup.Same(after, afterAgain)',m)
  self.assertNotIn('WowBMailCandidateReader.TryRead',m)
if __name__=='__main__': unittest.main()

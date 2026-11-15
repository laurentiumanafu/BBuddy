"""Static wiring checks; not a simulated game, C# execution or proof of delivery."""
from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[2]/'Styx/Logic/Pathing/ClickAim'
m=(R/'WowBExperimentalMail.cs').read_text()
p=(R/'WowBMailAttachmentPolicy.cs').read_text()
g=(R/'WowBMailGridLookup.cs').read_text()
class SkipWiring(unittest.TestCase):
 def test_skip_only_after_full_unchanged_observation_and_recheck(self):
  for text in ('_samples >= 3','!_uncertain','completedWindow','bagStable','finalTableMatchesBefore', 'Decision.Hold'):
   self.assertIn(text,p)
  for text in ('attempt < 20','ClickAimAbort.Wait(200)','decision.Observe',
               'decision.Finish(complete, stable, stable)', 'afterClick1', 'afterClick2',
               'WowBMailGridLookup.Same(bags, afterClick1)', 'WowBMailGridLookup.Same(bags, afterClick2)',
               'attachment outcome uncertain; no retry or send'):
   self.assertIn(text,m)
  self.assertLess(m.index('decision.Finish(complete, stable, stable)'),m.index('skipped.Add(slot.Item)'))
 def test_skip_does_not_end_scan_or_retry_item(self):
  self.assertIn('ISet<WowBGuid>? excluded',g)
  self.assertIn('excluded.Contains(item)',g)
  self.assertIn('new HashSet<WowBGuid>()',m)
  self.assertIn('selected.Count > 12',m)
  self.assertIn('TryPreviewExcluding(grid, player.ToString(), w, h, bags, skipped',m)
  self.assertIn('skipped.Contains(slot.Item)',m)
  self.assertIn('if (expected.Count > 0) break;',m)
  self.assertIn('No send input for the empty composition',m)
 def test_only_actual_attached_items_checked_after_one_send(self):
  self.assertEqual(m.count('Left(hwnd, ui.SendX'),1)
  self.assertIn('foreach (var sent in expected) if (id == sent.Item)',m)
  self.assertNotIn('foreach (var sent in selected)',m)
  self.assertIn('WowBMailGridLookup.Validate(grid, player.ToString(), w, h, after)',m)
  self.assertIn('send outcome uncertain; no retry',m)
if __name__=='__main__': unittest.main()

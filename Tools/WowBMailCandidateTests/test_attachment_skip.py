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
               'skipped.Add(slot.Item)',
               # an unsafe state still ends the batch; it now says why first
               'if (!safe())\n                            {\n                                Logging.Write("[Mail] item move stopped after ',
               'skipping this item for the trip',
               'TryPreviewExcluding(grid, player.ToString(), w, h, bags, attempted'):
   self.assertIn(text,m)
  self.assertIn('expected.Count >= 12',m)
  self.assertIn('no item click was posted',m)
 def test_every_stop_before_send_says_why_and_what_was_clicked(self):
  # 2026-11-17 11:40: the trip died 1.6 s after the recipient was typed with no
  # line at all between "recipient characters posted" and the caller's abort.
  body = m[m.index('foreach (var slot in selected)'):m.index('if (expected.Count >= 12) break;\n                    if (!WowBMailGridLookup.TryPreviewExcluding')]
  self.assertEqual(body.count('lastSafetyFailure +'), 2)   # move and click aborts
  for text in ('item move stopped after ', 'item click stopped after ',
               'item button release or state uncertain ('):
   self.assertIn(text, body)
  self.assertIn('safety changed before Send after ', m)
  self.assertIn('final bag observations unstable', m)
  # a posted click is logged, so "attached some, failed to attach others" is readable
  self.assertIn('item click posted for candidate ', body)
  self.assertIn('Attachment, mailability and identity are unverified.', body)
  self.assertIn('A nonzero candidate is an observation, not proof that the item is mailable.', m)
 def test_an_attached_item_is_not_a_changed_bag(self):
  # attaching empties the slot it came from; that must not end the batch
  self.assertIn('!WowBMailGridLookup.Same(bags, current) &&\n                            !WowBMailGridLookup.Validate(grid, player.ToString(), w, h, current)', m)
  self.assertIn('bag identity or slot count changed before an item click', m)
  self.assertIn('!WowBMailGridLookup.Same(beforeSend, beforeSendAgain)', m)
  self.assertIn('!WowBMailGridLookup.Validate(grid, player.ToString(), w, h, beforeSend)', m)
  self.assertEqual(m.count('Left(hwnd, ui.SendX'), 1)
 def test_only_clicks_used_for_post_send_observation(self):
  self.assertEqual(m.count('Left(hwnd, ui.SendX'),1)
  self.assertIn('expected.Add(new WowBMailCandidateTable.Expected(slot.Item, bag.Identity))',m)
  self.assertIn('WowBMailSendObservation.CountMissing(expected, after)',m)
  self.assertIn('WowBMailGridLookup.Validate(grid, player.ToString(), w, h, after)',m)
  self.assertIn('WowBMailGridLookup.Same(after, afterAgain)',m)
  self.assertNotIn('WowBMailCandidateReader.TryRead',m)
if __name__=='__main__': unittest.main()

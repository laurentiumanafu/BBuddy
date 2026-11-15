import unittest
from analyze_mail_bag_snapshots import parse, compare

def snapshot(guid='0xAAAA', bag='0x1111', values=('00'*16,'11'*16)):
 return f'''[Bag research] 70938 read-only inventory research snapshot; candidate only
[Bag research] UTC today; player GUID {guid}; player object 0x1234
[Bag research] container-candidate[1] identity={bag} object=0x1234 raw+0xAC0=2; previous=comparable.
[Bag research] raw+0x488 candidate 16-byte entries={''.join(values)}
'''
class TestSnapshots(unittest.TestCase):
 def test_changed_entry(self):
  s=parse(snapshot()+snapshot(values=('00'*16,'22'*16)))
  self.assertEqual(len(s),2)
  self.assertIn('indexes [1]',compare(*s)[1])
 def test_identity_and_incomplete(self):
  s=parse(snapshot()+snapshot(bag='0x2222')); self.assertIn('unknown or changed',compare(*s)[1])
  s=parse(snapshot()+snapshot(guid='0xBBBB')); self.assertIn('not comparable',compare(*s)[0])
  s=parse(snapshot()+snapshot().replace('raw+0x488 candidate 16-byte entries=','not captured=')); self.assertIn('unknown',compare(*s)[1])
 def test_unresolved_is_not_reused(self):
  s=parse(snapshot()+snapshot()+'[Bag research] container-candidate[1] no unique stable identity-matching header; Unknown.\n')
  self.assertIn('unknown',compare(*s)[1])
if __name__=='__main__': unittest.main()

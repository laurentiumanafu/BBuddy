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
 def test_classification_does_not_discard_valid_observation(self):
  classification='[Bag research] container-candidate[1] user category=Ordinary; classification is per bag instance.\n'
  s=parse(snapshot()+classification+snapshot(values=('00'*16,'22'*16))+classification)
  self.assertIn('indexes [1]',compare(*s)[1])
 def test_player_array_baseline_deltas_and_inconsistent_old_value(self):
  head='[Bag research] 70938 read-only inventory research snapshot; candidate only\n[Bag research] UTC today; player GUID 0xAAAA; player object 0x1\n'
  zero='00'*16; item='11'*16
  baseline=head+'candidate count=2; array=0xBEEF; stride=16; previous=none or different context\n'+f'entry[0] {item}\nentry[1] {zero}\n70938 special-index research:\n'
  change=head+'candidate count=2; array=0xBEEF; stride=16; previous=comparable\n'+f'entry[0] {item} -> {zero}\nentry[1] {zero} -> {item}\n70938 special-index research:\n'
  s=parse(baseline+change); self.assertIn('indexes [0, 1]',compare(*s)[-1])
  invalid=change.replace(f'entry[0] {item}', f'entry[0] {zero}')
  s=parse(baseline+invalid); self.assertIn('unknown',compare(*s)[-1])
 def test_supplied_sequence_semantics_anonymized(self):
  values=[[['00'*16]*12 for _ in range(4)] for _ in range(11)]
  objects=['%02X'%n*16 for n in (17,34,51,68)]
  for k in range(11):
   for b in range(4):
    before=(0,0,2,0)[b]; after=(1,2,0,11)[b]; transition=(3,4,5,6)[b]
    if k==10 and b in (0,3): continue
    values[k][b][after if k>=transition else before]=objects[b]
  def build(bags):
   text='[Bag research] 70938 read-only inventory research snapshot; candidate only\n[Bag research] player GUID 0xAAAA; player object 0x123\n'
   for b,v in enumerate(bags):
    text+=f'[Bag research] container-candidate[{b}] identity=0x{b+1:04X} object=0x1234 raw+0xAC0=12; previous=comparable.\n'
    text+='[Bag research] raw+0x488 candidate 16-byte entries='+''.join(v)+'\n'
   for b in range(4): text+=f'[Bag research] container-candidate[{b}] user category=Ordinary; classification is per bag instance.\n'
   return text
  s=parse(''.join(map(build,values))); self.assertEqual(len(s),11)
  for old,b,expected in ((2,0,'[0, 1]'),(3,1,'[0, 2]'),(4,2,'[0, 2]'),(5,3,'[0, 11]'),(9,0,'[1]'),(9,3,'[11]')):
   self.assertIn(expected,compare(s[old],s[old+1])[b])
  for old in (6,7,8):
   for line in compare(s[old],s[old+1])[:4]: self.assertIn('indexes []',line)
if __name__=='__main__': unittest.main()

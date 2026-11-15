import unittest
from unittest.mock import patch
import analyze_town_cast_trace as a

class TraceTest(unittest.TestCase):
 def test_interrupted_and_split_candidate(self):
  changes={('player',0x3C0):[(5,0,556),(14.5,556,0)],('player',0x3EC):[(5.25,0,556)],
           ('player',0x418):[(5.25,0,10000)],('player',0x41C):[(5.25,0,20000)],('player',0x1C10):[(14.5,19000,19500)]}
  with patch.object(a,'read_trace',return_value=(changes,[],100,'completed')):
   self.assertEqual(a.summary('x')[2],[(556,5,10000,14.5,500)])
 def test_no_mirrored_id_is_not_reported(self):
  with patch.object(a,'read_trace',return_value=({('player',0x3C0):[(1,0,556)]},[],1,'completed')):
   self.assertEqual(a.summary('x')[2],[])
if __name__=='__main__': unittest.main()

import unittest
from compare_templates import template_relation


def row(faction=1, flags=0, group=0, friendly=0, hostile=0, enemies=None, friends=None):
    arr = lambda x: {'index': 0, 'values': (x or []) + [0] * (8 - len(x or []))}
    return [faction, flags, group, friendly, hostile, arr(enemies), arr(friends)]


class CompareTemplateTests(unittest.TestCase):
    def test_hostile_overrides_friendly(self):
        a = row(group=2, friendly=2, hostile=2)
        b = row(group=2, friendly=2)
        self.assertEqual(template_relation(a, b), 1)

    def test_friendly_and_reverse_friendly(self):
        self.assertEqual(template_relation(row(friendly=2), row(group=2)), 4)
        self.assertEqual(template_relation(row(group=2), row(friendly=2)), 4)
        self.assertEqual(template_relation(row(faction=7, friends=[11]), row(faction=11)), 4)
        self.assertEqual(template_relation(row(faction=11), row(faction=7, friends=[11])), 4)

    def test_enemy_and_zero_termination(self):
        self.assertEqual(template_relation(row(enemies=[11]), row(faction=11)), 1)
        self.assertEqual(template_relation(row(enemies=[0, 11]), row(faction=11)), 3)

    def test_fallback(self):
        self.assertEqual(template_relation(row(flags=0), row()), 3)
        self.assertEqual(template_relation(row(flags=0x2000), row()), 1)
        self.assertEqual(template_relation(row(flags=-1), row()), 1)

    def test_example_table_only_pairs(self):
        player = row(faction=3, flags=72, group=3, friendly=2, hostile=12)
        friendly = row(faction=47, flags=0, group=2, friendly=2, hostile=4, friends=[47])
        neutral = row(faction=7, flags=1024)
        aggressive = row(faction=37, flags=1, group=8, hostile=1, friends=[37])
        self.assertEqual((template_relation(friendly, player), template_relation(player, friendly)), (4, 4))
        self.assertEqual((template_relation(neutral, player), template_relation(player, neutral)), (3, 3))
        self.assertEqual((template_relation(aggressive, player), template_relation(player, aggressive)), (1, 1))

    def test_missing_arrays_rejected(self):
        bad = row()
        bad[5] = {'index': 0, 'values': None}
        with self.assertRaisesRegex(ValueError, 'array palette'):
            template_relation(bad, row())


if __name__ == '__main__':
    unittest.main()

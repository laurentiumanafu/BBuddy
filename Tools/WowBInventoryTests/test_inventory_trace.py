"""Static checks for research trace seeds; this does not execute Ghidra."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class InventoryTraceTests(unittest.TestCase):
    def test_handler_seeds_match_recorded_registry(self):
        script = (ROOT / "hunt/WowBInventoryTrace70291.java").read_text()
        registry = (ROOT / "hunt/api_map.txt").read_text()
        seeds = re.findall(r'new Seed\("(\w+)[^"]*", 0x([0-9A-F]+)L\)', script)
        self.assertEqual(len(seeds), 4)
        for name, rva in seeds:
            self.assertRegex(registry, r"(?m)^\s*\+0x[0-9A-F]+\s+" + name + r"\s+0x" + rva + r"\s*$")

    def test_name_addresses_match_recorded_dump_and_bounds_exist(self):
        script = (ROOT / "hunt/WowBInventoryTrace70291.java").read_text()
        names = (ROOT / "hunt/api_names_runtime.txt").read_text()
        for rva, name in (("4F58EF8", "GetItemGUID"), ("4F59B08", "GetItemIDByGUID")):
            self.assertIn(f"0x{rva}  {name}", names)
            self.assertIn(f"emitEntryXrefs(0x{rva}L)", script)
        for limit in ("MAX_INSTRUCTIONS", "MAX_BLOCKS", "MAX_BYTES_FROM_ENTRY", "MAX_CALL_TARGETS", "MAX_CALLEE_INSTRUCTIONS"):
            self.assertRegex(script, r"private static final int " + limit + r" = (?:0x[0-9A-F]+|[0-9]+);")
        self.assertIn("new PseudoDisassembler(currentProgram)", script)
        self.assertNotIn("executeInTarget", script)


if __name__ == "__main__":
    unittest.main()

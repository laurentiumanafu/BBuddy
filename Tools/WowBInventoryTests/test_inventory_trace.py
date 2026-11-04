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

    def test_followup_targets_observed_static_leads(self):
        script = (ROOT / "hunt/WowBInventoryLookup70291.java").read_text()
        self.assertIn("class WowBInventoryLookup70291 extends GhidraScript", script)
        self.assertIn('dir.resolve("WowBInventoryLookup70291-"', script)
        for rva in ("290CFC0", "290BDD0", "290ABC0", "316C660", "2F5D710"):
            self.assertIn("0x" + rva + "L", script)
        self.assertIn("private static final int MAX_CALL_TARGETS = 0;", script)
        self.assertIn("new PseudoDisassembler(currentProgram)", script)

    def test_70707_lookup_trace_uses_observed_call_targets(self):
        script = (ROOT / "hunt/WowBInventoryLookup70707.java").read_text()
        self.assertIn("class WowBInventoryLookup70707 extends GhidraScript", script)
        self.assertIn('dir.resolve("WowBInventoryLookup70707-"', script)
        for rva in ("2162D00", "286B290", "26C8DF0", "2080D2C", "20AAF97", "2890305", "A538FD"):
            self.assertIn("0x" + rva + "L", script)
        self.assertIn("private static final int MAX_CALL_TARGETS = 0;", script)
        self.assertIn("new PseudoDisassembler(currentProgram)", script)
        self.assertIn("if (!markersMatch)", script)
        self.assertNotIn("executeInTarget", script)

    def test_70707_container_search_is_bounded_and_read_only(self):
        script = (ROOT / "hunt/WowBContainerSearch70707.java").read_text()
        self.assertIn("class WowBContainerSearch70707 extends GhidraScript", script)
        for name in ("GetInventoryItemID", "GetContainerNumSlots", "GetContainerNumFreeSlots", "GetContainerItemInfo"):
            self.assertIn('"' + name + '"', script)
        for guard in ("MAX_NAMES_PER_STRING", "MAX_POINTER_REFS_PER_NAME", "MAX_TRACES_PER_NAME", "MAX_INSTRUCTIONS", "MAX_BLOCKS"):
            self.assertRegex(script, r"private static final int " + guard + r" = [0-9]+;")
        for marker in ("2080D2C", "20AAF97", "2890305", "A538FD"):
            self.assertIn("0x" + marker + "L", script)
        self.assertIn("new PseudoDisassembler(currentProgram)", script)
        self.assertIn("findBytes", script)
        self.assertNotIn("executeInTarget", script)

    def test_70707_container_reference_followup_uses_observed_name_locations(self):
        script = (ROOT / "hunt/WowBContainerRefs70707.java").read_text()
        self.assertIn("class WowBContainerRefs70707 extends GhidraScript", script)
        for rva in ("4245E80", "40FDAD0", "40FDA90", "40FDF88", "2080D2C", "20AAF97", "2890305", "A538FD"):
            self.assertIn("0x" + rva + "L", script)
        for limit in ("MAX_RAW_MATCHES", "MAX_RIP_EXACT", "MAX_RIP_NEARBY"):
            self.assertRegex(script, r"private static final int " + limit + r" = [0-9]+;")
        self.assertIn("if (!markersMatch)", script)
        self.assertIn("if (!namesMatch)", script)
        self.assertIn("new PseudoDisassembler(currentProgram)", script)
        self.assertIn("findBytes", script)
        self.assertNotIn("executeInTarget", script)

    def test_70707_container_code_followup_uses_reported_sites(self):
        script = (ROOT / "hunt/WowBContainerCode70707.java").read_text()
        self.assertIn("class WowBContainerCode70707 extends GhidraScript", script)
        for rva in ("D5C2E2", "D5C82B", "D65BE5", "D65C04", "2080D2C", "20AAF97", "2890305", "A538FD"):
            self.assertIn("0x" + rva + "L", script)
        for sig in ("4C8D05A7173A03", "4C8D059E123A03", "4C8D05A47E3903", "4C8D05C57E3903"):
            self.assertIn(sig, script)
        self.assertIn("if (!markersMatch)", script)
        self.assertIn("if (!bytes.equals(SITE_BYTES[i]))", script)
        self.assertIn("matches == 1 && failed == 0", script)
        self.assertIn("new PseudoDisassembler(currentProgram)", script)
        self.assertIn("MAX_PDATA_MATCHES", script)
        self.assertNotIn("executeInTarget", script)

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

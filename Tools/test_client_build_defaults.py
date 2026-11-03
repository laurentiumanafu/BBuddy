"""Structural regression checks; these do not replace a Windows/MSBuild build."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class ClientBuildDefaultsTests(unittest.TestCase):
    def test_project_targets_and_navigation_match(self):
        root = ET.parse(ROOT / "CopilotBuddy.csproj").getroot()
        targets = {node.get("Condition"): node.text for node in root.findall("./PropertyGroup/PlatformTarget")}
        self.assertEqual(targets["'$(Platform)' == 'x86'"], "x86")
        self.assertEqual(targets["'$(Platform)' != 'x86'"], "x64")
        native = {}
        for group in root.findall("./ItemGroup"):
            for node in group.findall("None"):
                if node.get("Link") == "Navigation.dll":
                    native[group.get("Condition")] = node.get("Include")
        self.assertEqual(native["'$(PlatformTarget)' == 'x64'"], r"Lib\x64\Navigation.dll")
        self.assertEqual(native["'$(PlatformTarget)' == 'x86'"], r"Lib\Navigation.dll")

    def test_solution_defaults_to_x64_but_keeps_x86(self):
        root = ET.parse(ROOT / "CopilotBuddy.slnx").getroot()
        platforms = {node.get("Solution"): node.get("Project") for node in root.findall("./Project/Platform")}
        self.assertEqual(platforms["*|Any CPU"], "x64")
        self.assertEqual(platforms["*|x64"], "x64")
        self.assertEqual(platforms["*|x86"], "x86")


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Exercise the production node scanner with synthetic loaded objects.

The source assertions cover GatherBuddy wiring, not live streaming or navigation.
"""
from pathlib import Path
import re
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
runner = (root / 'Styx/Logic/Pathing/ClickAim/GatherRunner.cs').read_text(encoding='utf-8-sig')
scanner = (root / 'Styx/Logic/Pathing/ClickAim/GatherNodeScanner.cs').read_text(encoding='utf-8-sig')
gather_interaction = (root / 'Styx/Logic/Pathing/ClickAim/GatherInteraction.cs').read_text(encoding='utf-8-sig')
match = re.search(r'private const double SearchYd\s*=\s*([\d.]+)\s*;', runner)
assert match and float(match.group(1)) == 1000
grind_match = re.search(r'private const double GrindSearchYd\s*=\s*([\d.]+)\s*;', runner)
assert grind_match and float(grind_match.group(1)) == 150
assert re.search(r'private const double InteractYd\s*=\s*5\.0\s*;', runner)
grind_runner = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
assert re.search(r'private const double SearchYd\s*=\s*100\.0\s*;', grind_runner)
assert 'FindNode(out GatherNodeScanner.Node node, double radius = SearchYd)' in runner
assert 'GatherNodeScanner.TryCapture(radius, out List<GatherNodeScanner.Node> nodes' in runner
assert 'FindNode(out _, GrindSearchYd)' in runner and 'FindNode(out GatherNodeScanner.Node node, GrindSearchYd)' in runner
assert 'GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> current)' in runner
assert 'GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> nearby)' in runner
assert 'if (!RouteRunner.MeshReady())' in runner and 'RouteRunner.RequireMeshDuringSession = true' in runner
assert 'if (!GrindRunner.TryReadGatherCombat(out bool combatBeforeFace))' in runner
assert 'if (combatBeforeFace)' in runner
assert 'GatherNodeScanner.TryCapture(5.0' in gather_interaction
assert 'reader.EnumeratePoolObjects()' in scanner and 'nodes.Sort((a, b) => a.Distance.CompareTo(b.Distance))' in scanner
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text(encoding='utf-8')
fixture = fixture.replace('__RADIUS__', match.group(1)).replace('__GRIND_RADIUS__', grind_match.group(1))
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-gather-radius-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Scanner.cs').write_text(scanner)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)
print('GatherBuddy and Grind resource radius integration assertions passed (static only).')

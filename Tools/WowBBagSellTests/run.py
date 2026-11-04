#!/usr/bin/env python3
"""Compile the production VendorDue method with isolated deterministic stubs (.NET 10 SDK)."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
method = source[source.index('    private static bool VendorDue('):source.index('    /// <summary>\n    /// The original CopilotBuddy selling procedure')]
route = source[source.index('                bool resourceInterrupt = false, vendorInterrupt = false;'):source.index('                if (ok)', source.index('                bool resourceInterrupt = false, vendorInterrupt = false;'))]
assert 'if (VendorDue()) { vendorInterrupt = true; return true; }' in route
assert 'if (vendorInterrupt)' in route
assert 'if (VendorDue()) break;' in source[source.index('    private static string DwellAtHotspot('):source.index('    private static', source.index('    private static string DwellAtHotspot(') + 25)]
fixture = Path(__file__).with_name('Fixture.cs.txt').read_text(encoding='utf-8')
assert fixture.count('// __PRODUCTION_VENDOR_DUE__') == 1
fixture = fixture.replace('// __PRODUCTION_VENDOR_DUE__', method)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-bag-sell-tests-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project, encoding='utf-8')
    (directory / 'Program.cs').write_text(fixture, encoding='utf-8')
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

#!/usr/bin/env python3
"""Synthetic policy fixture; requires .NET 10. Does not test live UI."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='mail-attachment-policy-') as d:
    p = Path(d)
    (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    (p/'Fixture.cs').write_text(Path(__file__).with_name('AttachmentPolicyFixture.cs.txt').read_text())
    (p/'Policy.cs').write_text((root/'Styx/Logic/Pathing/ClickAim/WowBMailAttachmentPolicy.cs').read_text())
    subprocess.run(['dotnet','run','--project',str(p/'Test.csproj')],check=True)

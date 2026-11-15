#!/usr/bin/env python3
"""Compile the production mailbox-stage method against deterministic stubs (.NET 10)."""
from pathlib import Path
import subprocess, tempfile
root = Path(__file__).resolve().parents[2]
s = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
method = s[s.index('    private static bool WalkToRecordedMailbox('):s.index('    /// <summary>\n    /// Replays the recorded plan:', s.index('    private static bool WalkToRecordedMailbox('))]
fixture = Path(__file__).with_name('Fixture.cs.txt').read_text().replace('        // __PRODUCTION__', method)
with tempfile.TemporaryDirectory(prefix='mailbox-route-') as temp:
    p = Path(temp)
    (p / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (p / 'Program.cs').write_text(fixture)
    subprocess.run(['dotnet', 'run', '--project', str(p / 'Fixture.csproj')], check=True)

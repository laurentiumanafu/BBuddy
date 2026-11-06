#!/usr/bin/env python3
"""Test the production free-region guard with a mocked VirtualQueryEx."""
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[2]
source = (root / 'GreenMagic/Memory.cs').read_text(encoding='utf-8-sig')
start = source.index('        public bool IsMemoryRangeFree64(')
opening = source.index('{', start)
depth = 1
end = opening + 1
while depth:
    depth += (source[end] == '{') - (source[end] == '}')
    end += 1
method = source[start:end]
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text(encoding='utf-8').replace('        // __METHOD__', method)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-memory-region-') as directory:
    directory = Path(directory)
    (directory / 'Fixture.csproj').write_text(project)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Info.cs').write_text((root / 'GreenMagic/Native/MEMORY_BASIC_INFORMATION64.cs').read_text(encoding='utf-8-sig'))
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

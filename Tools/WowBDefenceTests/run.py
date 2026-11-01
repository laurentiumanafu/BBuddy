#!/usr/bin/env python3
"""Exercise the actual Grind defence method against deterministic game/input stubs.

Requires a .NET 10 SDK. No game, native input, or production project build is used.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "Styx/Logic/Pathing/ClickAim/GrindRunner.cs").read_text(encoding="utf-8-sig")
fixture = (Path(__file__).parent / "Fixture.cs.txt").read_text(encoding="utf-8")
# Include the exact production methods, not a second implementation of the policy.
level = source[source.index("    private static bool LevelViable("):source.index("    /// <summary>\n    /// Whether the walk should stop here.")]
defence = source[source.index("    private static bool DefenceFieldsValid("):source.index("    private static bool InRealCombat()")]
assert fixture.count("// __PRODUCTION_METHODS__") == 1
fixture = fixture.replace("// __PRODUCTION_METHODS__", level + "\n" + defence)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix="wowb-defence-tests-") as directory:
    directory = Path(directory)
    (directory / "Fixture.csproj").write_text(project, encoding="utf-8")
    (directory / "Program.cs").write_text(fixture, encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project", str(directory / "Fixture.csproj")], check=True)

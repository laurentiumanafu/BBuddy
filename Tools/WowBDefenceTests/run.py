#!/usr/bin/env python3
"""Exercise the actual Grind defence method against deterministic game/input stubs.

Requires a .NET 10 SDK. No game, native input, or production project build is used.
"""
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "Styx/Logic/Pathing/ClickAim/GrindRunner.cs").read_text(encoding="utf-8-sig")
fixture = (Path(__file__).parent / "Fixture.cs.txt").read_text(encoding="utf-8")
# Include the exact production methods, not a second implementation of the policy.
level = source[source.index("    private static bool LevelViable("):source.index("    /// <summary>\n    /// Whether the walk should stop here.")]
defence = source[source.index("    private static bool DefenceFieldsValid("):source.index("    private static bool InRealCombat()")]
selection = source[source.index("    private static bool SelectionFieldsValid("):source.index("    // Do not treat a failed read or a corpse")]
assert fixture.count("// __PRODUCTION_METHODS__") == 1
fixture = fixture.replace("// __PRODUCTION_METHODS__", level + "\n" + selection + "\n" + defence)
# Read constants from the same production file so these boundary tests cannot
# accidentally pass against a stale copy of the level limits.
constants = []
for name in ("LevelAbovePlayer", "LevelBelowPlayer"):
    matches = re.findall(r"^\s*private const int " + name + r"\s*=\s*\d+;", source, re.MULTILINE)
    if len(matches) != 1:
        raise ValueError("Expected exactly one production constant " + name)
    constants.append(matches[0].strip())
assert fixture.count("// __PRODUCTION_LEVEL_CONSTANTS__") == 1
fixture = fixture.replace("// __PRODUCTION_LEVEL_CONSTANTS__", "\n        ".join(constants))
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix="wowb-defence-tests-") as directory:
    directory = Path(directory)
    (directory / "Fixture.csproj").write_text(project, encoding="utf-8")
    (directory / "Program.cs").write_text(fixture, encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project", str(directory / "Fixture.csproj")], check=True)

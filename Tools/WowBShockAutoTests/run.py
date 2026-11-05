#!/usr/bin/env python3
"""Check the production Fight wiring; RotationTests executes the policy itself.

These source assertions are not a live client or full Fight simulation.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
fight = g[g.index('    private static string Fight('):g.index('    // TAB is used only to restore the original target')]
guard = g[g.index('    private static bool FightAttackInputAllowed('):g.index('    // End the old rotation', g.index('    private static bool FightAttackInputAllowed('))]

# Preserve the outer STOP, selected-target and faction gates, and recheck them
# immediately before either combat key. No helper may bypass these gates.
assert 'while (!_stop && !ClickAimAbort.Requested' in fight
assert 'if (target.Guid != defenceTarget)' in fight
assert 'FactionPullStillAllowed(reader, expectedPull, target)' in fight
for condition in ('_stop', 'ClickAimAbort.Requested', 'reader.ReadTargetGuid() != target', 'FactionAttackInputAllowed'):
    assert condition in guard
attack = fight[fight.index('            if (auto != null)'):fight.index('            else if (!swinging && !pressPending')]
assert attack.index('FightAttackInputAllowed(') < attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
assert attack.index('if (!auto.IsAttack)') < attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
assert 'MayPressShockAttack(true, attackPressed, swinging, pressPending, d, AttackRangeYd)' in attack
assert attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk') < attack.index('attackPressed = true;')
spell = fight[fight.index('            if (canCast)'):fight.index('            /* ---- 4. melee:')]
assert spell.index('FightAttackInputAllowed(') < spell.index('ClickAimInput.TapKey(hwnd, castDamage!')
assert 'if (!ResourceInputStillAllowed(reader)) continue;' in spell
assert 'ClickAimClassRotation.Defer(_rotationCooldowns' in spell

# The temporary path must flow through the existing chase, without setting the
# permanent melee latch; ready Shock must still reach the cast branch.
assert 'ClickAimClassRotation.PreferShockMelee(shockAutoPlan, shockReady)' in fight
assert '!melee && !shockMelee && castDamage != null' in fight
assert 'bool temporaryShockMelee = shockAutoPlan && !melee;' in fight
assert 'if (!temporaryShockMelee && !meleeOnce)' in fight
assert fight.index('bool temporaryShockMelee =') < fight.index('if (d > MeleeEngageYd)')
assert 'if (!ChaseStep(hwnd, watch, target, out blocked))' in fight
assert 'damage source and auto-attack state unverified' in fight
assert 'lastShockDamageMs' in fight[fight.index('            /* Melee and ranged both reported nothing:'):]
print('Shock/Attack production integration assertions passed (static, not a live fight).')
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __GUARD__', guard)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-shock-tests-') as directory:
    directory = Path(directory)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Fixture.csproj').write_text(project)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

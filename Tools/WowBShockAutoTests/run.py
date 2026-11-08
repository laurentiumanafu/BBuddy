#!/usr/bin/env python3
"""Check the production Fight wiring; RotationTests executes the policy itself.

These source assertions are not a live client or full Fight simulation.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
g = (root / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
fight = g[g.index('    private static string Fight('):g.index('    /// <summary>\n    /// A unit\'s health straight out of its data block')]
guard = g[g.index('    private static bool FightAttackInputAllowed('):g.index('    // End the old rotation', g.index('    private static bool FightAttackInputAllowed('))]

# Preserve the outer STOP, selected-target and faction gates, and recheck them
# immediately before either combat key. No helper may bypass these gates.
assert 'while (!_stop && !ClickAimAbort.Requested' in fight
assert 'if (target.Guid != defenceTarget)' in fight
assert 'FactionPullStillAllowed(reader, expectedPull, target)' in fight
for condition in ('_stop', 'ClickAimAbort.Requested', 'reader.ReadTargetGuid() != target', 'FactionAttackInputAllowed'):
    assert condition in guard
attack = fight[fight.index('            if (!keyOneMacro && auto != null)'):fight.index('            else if (!keyOneMacro && !swinging && !pressPending')]
assert attack.index('FightAttackInputAllowed(') < attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
assert attack.index('if (!auto.IsAttack)') < attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
assert 'MayPressConservativeAttack(true, attackPressed, swinging, pressPending, d, AttackRangeYd)' in attack
assert attack.index('ClickAimInput.TapKey(hwnd, auto.KeyVk') < attack.index('attackPressed = true;')
early = fight[fight.index('            // For a Shock/Attack Shaman'):fight.index('            /* ---- 1b. the damage-over-time spell')]
assert early.index('MayPressConservativeAttack(shockAutoPlan, attackPressed, false, false, d, AttackRangeYd)') < early.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
for required in ('FightAttackInputAllowed(', 'TryGetSelectedTarget(out WowBObservedUnit attackTarget)',
                 'attackTarget.Guid != defenceTarget', 'attackTarget.UnitAddress != expectedPull.UnitAddress',
                 'attackTarget.RecordAddress != expectedPull.RecordAddress', 'DefenceFieldsValid(',
                 'MayPressConservativeAttack(shockAutoPlan, attackPressed, false, false, attackDistance, AttackRangeYd)',
                 'auto == null || !auto.IsAutoAttack || !auto.IsAttack'):
    assert early.index(required) < early.index('ClickAimInput.TapKey(hwnd, auto.KeyVk')
assert early.count('FightAttackInputAllowed(') == 2
assert early.index('ClickAimInput.TapKey(hwnd, auto.KeyVk') < early.index('attackPressed = true;')
assert fight.index('if (ClickAimHealPolicy.ShouldAttemptHeal(') < fight.index('            // For a Shock/Attack Shaman')
assert fight.index('            // For a Shock/Attack Shaman') < fight.index('            if (canCast)')

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
assert 'if (!ChaseStep(hwnd, watch, target, out blocked, combatMoveAllowed: defensiveMovement))' in fight
assert 'damage source and auto-attack state unverified' in fight
assert 'lastShockDamageMs' in fight[fight.index('            /* Melee and ranged both reported nothing:'):]
heal = fight[fight.index('if (ClickAimHealPolicy.ShouldAttemptHeal('):fight.index('// Optional observed-expired self-only buffs')]
heal_key = heal.index('ClickAimInput.TapKey(hwnd, heal.KeyVk')
for required in ('StillOnBar(heal, ClickAimCombat.ReadBar(reader))', 'HealPowerOk(freshHeal.Power, freshHeal.MaxPower)',
                 'FightAttackInputAllowed(', 'reader.ReadTargetGuid() != defenceTarget', '_stop || ClickAimAbort.Requested'):
    assert heal.index(required) < heal_key
assert 'ClickAimInput.TapKey(hwnd, 0x70' not in heal and 'ClickAimInput.TapKey(hwnd, TabVk' not in heal
assert 'TryRestoreHealTarget' not in fight and 'meleeSwinging = false' not in heal
assert 'if (attackPressed || lastMeleeClickMs != 0) preserveAttackAfterHeal = true;' in heal
assert 'conservativeShockAttack || preserveAttackAfterHeal' in attack
assert '!preserveAttackAfterHeal' in fight[fight.index('            else if (!keyOneMacro && !swinging && !pressPending'):]
print('Shock/Attack and no-retarget heal integration assertions passed (static, not a live fight).')
fixture = (Path(__file__).parent / 'Fixture.cs.txt').read_text().replace('        // __GUARD__', guard)
project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>'
with tempfile.TemporaryDirectory(prefix='wowb-shock-tests-') as directory:
    directory = Path(directory)
    (directory / 'Program.cs').write_text(fixture)
    (directory / 'Fixture.csproj').write_text(project)
    subprocess.run(['dotnet', 'run', '--project', str(directory / 'Fixture.csproj')], check=True)

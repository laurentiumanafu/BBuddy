using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Stage-one hotbar priorities, not a spellbook, specialization or aura reader.
// The explicit selector avoids relying on unverified class offsets; Auto requires
// unambiguous class-specific action-bar evidence and otherwise keeps Generic.
internal enum ClickAimRotationClass
{
    Auto, Generic, Druid, Hunter, Mage, Paladin, Priest, Rogue, Shaman, Warlock, Warrior, DeathKnight
}

internal sealed class ClickAimRotationPlan
{
    public ClickAimRotationClass Class;
    public ClickAimAction? Damage, Melee, Dot, Heal, Supplemental;
    public int SupplementalIntervalMs, DamageIntervalMs, MeleeIntervalMs;
    public string SupplementalGroup = "", DamageGroup = "", MeleeGroup = "";
}

internal static class ClickAimClassRotation
{
    private sealed class Definition
    {
        public readonly string[] Evidence, Damage, Melee, Dot, Heal;
        public readonly string Supplemental, SupplementalGroup;
        public readonly int SupplementalMs, DamageMs;
        public Definition(string[] evidence, string[] damage, string[] melee, string[] dot, string[] heal,
                          string supplemental = "", int supplementalMs = 0, string supplementalGroup = "", int damageMs = 0)
        { Evidence = evidence; Damage = damage; Melee = melee; Dot = dot; Heal = heal;
          Supplemental = supplemental; SupplementalMs = supplementalMs; SupplementalGroup = supplementalGroup; DamageMs = damageMs; }
    }

    // These are simple solo-leveling hotbar priorities inspired by Singular's
    // WotLK lowbie lists, not a promise that this client's spell data matches.
    // Position-, form-, pet-, proc-, combo- or target-aura-dependent skills are
    // deliberately omitted until those states can be read reliably.
    private static readonly Dictionary<ClickAimRotationClass, Definition> Definitions = new()
    {
        [ClickAimRotationClass.Druid] = new(new[] { "Wrath", "Moonfire", "Healing Touch" }, new[] { "Wrath", "Starfire" }, Array.Empty<string>(), new[] { "Moonfire", "Insect Swarm" }, new[] { "Healing Touch", "Regrowth", "Rejuvenation" }),
        [ClickAimRotationClass.Hunter] = new(new[] { "Arcane Shot", "Serpent Sting", "Auto Shot", "Raptor Strike" }, new[] { "Arcane Shot" }, new[] { "Raptor Strike" }, new[] { "Serpent Sting" }, Array.Empty<string>(), damageMs: 8000),
        [ClickAimRotationClass.Mage] = new(new[] { "Fireball", "Frostbolt", "Fire Blast", "Arcane Missiles" }, new[] { "Frostbolt", "Fireball" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "Fire Blast", 10000, "Fire Blast"),
        [ClickAimRotationClass.Paladin] = new(new[] { "Holy Light", "Flash of Light", "Judgement", "Seal of Righteousness", "Crusader Strike" }, Array.Empty<string>(), new[] { "Crusader Strike" }, Array.Empty<string>(), new[] { "Holy Light", "Flash of Light" }),
        [ClickAimRotationClass.Priest] = new(new[] { "Smite", "Shadow Word: Pain", "Lesser Heal", "Power Word: Fortitude", "Mind Blast" }, new[] { "Smite" }, Array.Empty<string>(), new[] { "Shadow Word: Pain" }, new[] { "Lesser Heal", "Heal", "Flash Heal", "Renew" }, "Mind Blast", 10000, "Mind Blast"),
        [ClickAimRotationClass.Rogue] = new(new[] { "Sinister Strike", "Eviscerate", "Backstab", "Stealth" }, Array.Empty<string>(), new[] { "Sinister Strike" }, Array.Empty<string>(), Array.Empty<string>()),
        [ClickAimRotationClass.Shaman] = new(new[] { "Lightning Bolt", "Earth Shock", "Frost Shock", "Flame Shock", "Healing Wave", "Lesser Healing Wave", "Lightning Shield" }, new[] { "Lightning Bolt" }, Array.Empty<string>(), new[] { "Flame Shock" }, new[] { "Healing Wave", "Lesser Healing Wave" }, "Earth Shock", 8000, "Shocks"),
        [ClickAimRotationClass.Warlock] = new(new[] { "Shadow Bolt", "Corruption", "Immolate", "Demon Skin" }, new[] { "Shadow Bolt" }, Array.Empty<string>(), new[] { "Corruption", "Immolate", "Curse of Agony" }, Array.Empty<string>()),
        [ClickAimRotationClass.Warrior] = new(new[] { "Heroic Strike", "Rend", "Battle Shout", "Charge" }, Array.Empty<string>(), new[] { "Heroic Strike" }, new[] { "Rend" }, Array.Empty<string>()),
        [ClickAimRotationClass.DeathKnight] = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
    };

    private static ClickAimAction? Find(List<ClickAimAction> bar, string[] names, ClickAimSpellRole role)
    {
        foreach (string name in names)
            foreach (ClickAimAction action in bar)
                if (action.Kind == ClickAimActionKind.Spell && action.KeyVk != 0 &&
                    ClickAimCombat.RoleOf(action.Id) == role &&
                    string.Equals(ClickAimCombat.NameOf(action.Id), name, StringComparison.OrdinalIgnoreCase))
                    return action;
        return null;
    }

    internal static ClickAimRotationClass Resolve(ClickAimRotationClass requested, List<ClickAimAction> bar)
    {
        if (requested != ClickAimRotationClass.Auto)
            return Definitions.ContainsKey(requested) ? requested : ClickAimRotationClass.Generic;
        ClickAimRotationClass found = ClickAimRotationClass.Generic;
        foreach (var pair in Definitions)
        {
            bool evidence = false;
            foreach (ClickAimAction a in bar)
            {
                if (a.Kind != ClickAimActionKind.Spell || a.KeyVk == 0 || ClickAimCombat.RoleOf(a.Id) == ClickAimSpellRole.Ignore) continue;
                string name = ClickAimCombat.NameOf(a.Id);
                foreach (string signature in pair.Value.Evidence)
                    if (string.Equals(name, signature, StringComparison.OrdinalIgnoreCase)) { evidence = true; break; }
                if (evidence) break;
            }
            if (!evidence) continue;
            if (found != ClickAimRotationClass.Generic) return ClickAimRotationClass.Generic;
            found = pair.Key;
        }
        return found;
    }

    // 70938 timing is an estimate from published data and one user trace, not
    // a readable per-spell cooldown; failed attempts can also start this timer.
    internal const int StormstrikeId = 17364, StormstrikeInterval70938Ms = 8000;
    internal const uint StormstrikePowerFloor70938 = 125;
    internal const string StormstrikeCooldownGroup70938 = "Stormstrike-70938";

    internal static ClickAimRotationPlan Build(ClickAimRotationClass requested, List<ClickAimAction> bar, bool experimental70938 = false)
    {
        var plan = new ClickAimRotationPlan { Class = Resolve(requested, bar) };
        if (!Definitions.TryGetValue(plan.Class, out Definition? def))
        {
            plan.Damage = ClickAimCombat.FirstDamageOrDot(bar);
            plan.Melee = ClickAimCombat.FirstMeleeDamage(bar);
            plan.Dot = ClickAimCombat.FirstDot(bar);
            plan.Heal = ClickAimCombat.FirstHeal(bar);
            ConfigureStormstrike(plan, experimental70938);
            return plan;
        }
        plan.Damage = Find(bar, def.Damage, ClickAimSpellRole.Damage);
        plan.Melee = Find(bar, def.Melee, ClickAimSpellRole.Damage);
        if (experimental70938 && plan.Class == ClickAimRotationClass.Shaman && plan.Melee == null)
            plan.Melee = Find(bar, new[] { "Stormstrike" }, ClickAimSpellRole.Damage);
        if (plan.Melee != null && (plan.Class == ClickAimRotationClass.Hunter || plan.Class == ClickAimRotationClass.Paladin))
        {
            plan.MeleeGroup = ClickAimCombat.NameOf(plan.Melee.Id);
            plan.MeleeIntervalMs = plan.Class == ClickAimRotationClass.Hunter ? 9000 : 6000;
        }
        plan.Dot = Find(bar, def.Dot, ClickAimSpellRole.Dot);
        plan.Heal = Find(bar, def.Heal, ClickAimSpellRole.Heal);
        plan.Supplemental = Find(bar, new[] { def.Supplemental }, ClickAimSpellRole.Damage);
        plan.SupplementalIntervalMs = def.SupplementalMs;
        plan.SupplementalGroup = def.SupplementalGroup;
        plan.DamageIntervalMs = def.DamageMs;
        plan.DamageGroup = plan.Damage == null ? "" : ClickAimCombat.NameOf(plan.Damage.Id);
        // A short-range instant can be the only direct damage spell available.
        // It retains its estimated cooldown even when used as the fallback.
        if (plan.Damage == null && plan.Supplemental != null)
        {
            plan.Damage = plan.Supplemental;
            plan.DamageIntervalMs = plan.SupplementalIntervalMs;
            plan.DamageGroup = plan.SupplementalGroup;
            plan.Supplemental = null;
        }
        // If Bolt and Earth Shock are unavailable, Frost Shock can be the
        // Shaman's direct opener. Keep Earth Shock's existing priority.
        if (plan.Class == ClickAimRotationClass.Shaman && plan.Damage == null)
            plan.Damage = Find(bar, new[] { "Frost Shock" }, ClickAimSpellRole.Damage);

        // A user-specified damage or heal override is still respected even when
        // its spell name is not in the built-in priority list.
        foreach (ClickAimAction action in bar)
        {
            if (action.Kind != ClickAimActionKind.Spell || action.KeyVk == 0) continue;
            ClickAimSpellRole role = ClickAimCombat.RoleOverrideOf(action.Id);
            if (role == ClickAimSpellRole.Damage)
            {
                if (action.IsMelee && plan.Melee == null) plan.Melee = action;
                else if (!action.IsMelee && plan.Damage == null) { plan.Damage = action; plan.DamageIntervalMs = 0; plan.DamageGroup = ""; }
            }
            else if (role == ClickAimSpellRole.Dot && plan.Dot == null) plan.Dot = action;
            else if (role == ClickAimSpellRole.Heal && plan.Heal == null) plan.Heal = action;
        }
        // Manual damage overrides previously erased the Shock cooldown metadata.
        // Only known Shaman Shock direct spells use this shared estimate.
        if (plan.Class == ClickAimRotationClass.Shaman && plan.Damage != null && IsDirectShock(plan.Damage))
        {
            plan.DamageGroup = "Shocks";
            plan.DamageIntervalMs = 8000;
        }
        ConfigureStormstrike(plan, experimental70938);
        return plan;
    }

    private static void ConfigureStormstrike(ClickAimRotationPlan plan, bool experimental70938)
    {
        if (experimental70938 && plan.Melee != null && plan.Melee.Id == StormstrikeId &&
            plan.Melee.IsDamage && plan.Melee.IsMelee && plan.Melee.KeyVk != 0)
        {
            plan.MeleeGroup = StormstrikeCooldownGroup70938;
            plan.MeleeIntervalMs = StormstrikeInterval70938Ms;
        }
    }

    internal static bool IsDirectShock(ClickAimAction action)
        => string.Equals(ClickAimCombat.NameOf(action.Id), "Earth Shock", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(ClickAimCombat.NameOf(action.Id), "Frost Shock", StringComparison.OrdinalIgnoreCase);

    // Earth or Frost Shock as the Shaman's only direct spell is an instant with an
    // estimated shared cooldown, not a ranged filler. With Attack available,
    // close in after the opener and use melee between shocks. Do not apply this
    // policy to a Lightning Bolt filler, Hunter shots, or an ignored Attack.
    internal static bool ShockAutoPlan(ClickAimRotationPlan plan, ClickAimAction? auto, bool keyOneMacro = false)
        => plan.Class == ClickAimRotationClass.Shaman && plan.Damage != null && plan.Damage.IsDamage &&
           IsDirectShock(plan.Damage) &&
           plan.DamageGroup == "Shocks" && plan.DamageIntervalMs > 0 &&
           (plan.Supplemental == null || (keyOneMacro && IsDirectShock(plan.Supplemental))) &&
           (keyOneMacro || (auto != null && auto.IsAutoAttack && auto.IsAttack && auto.KeyVk != 0));

    internal static bool IsAnyShock(ClickAimAction action)
        => IsDirectShock(action) || string.Equals(ClickAimCombat.NameOf(action.Id), "Flame Shock", StringComparison.OrdinalIgnoreCase);

    internal static bool PreferShockMelee(bool shockAutoPlan, bool shockReady)
        => shockAutoPlan && !shockReady;

    // Attack is an unverified toggle: use this conservative policy for the
    // Shock rotation or after a heal, rather than toggling it on silence.
    internal static bool MayPressConservativeAttack(bool conservative, bool attackPressed, bool swinging, bool pressPending, double distance, double meleeRange)
        => conservative && !attackPressed && !swinging && !pressPending &&
           double.IsFinite(distance) && distance >= 0 && distance <= meleeRange;

    internal static bool ShouldApproachDotOnly(ClickAimAction? damage, ClickAimAction? dot, bool melee,
        bool enabled, bool due, bool ready, uint power, uint estimatedCost, double distance)
        => damage == null && dot != null && dot.IsDot && !dot.IsMelee && !melee && enabled && due && ready &&
           power >= estimatedCost && dot.RangeYd > 0 && distance > dot.RangeYd;

    internal static ClickAimAction? SelectSupplemental(ClickAimRotationPlan plan, bool melee, bool targetDamaged,
        double distance, uint power, uint estimatedCost, int now, Dictionary<string, int> until)
    {
        ClickAimAction? action = plan.Supplemental;
        return !melee && targetDamaged && action != null && action.IsDamage && action.RangeYd > 0 &&
               distance <= action.RangeYd && power >= estimatedCost && Ready(until, plan.SupplementalGroup, now)
            ? action : null;
    }

    internal static bool Ready(Dictionary<string, int> until, string group, int now)
        => group.Length == 0 || !until.TryGetValue(group, out int deadline) || unchecked(now - deadline) >= 0;

    internal static void Defer(Dictionary<string, int> until, string group, int milliseconds, int now)
    {
        if (group.Length > 0 && milliseconds > 0) until[group] = unchecked(now + milliseconds);
    }

    internal static string DotCooldownGroup(ClickAimAction action)
        => string.Equals(ClickAimCombat.NameOf(action.Id), "Flame Shock", StringComparison.OrdinalIgnoreCase) ? "Shocks" : "";
}

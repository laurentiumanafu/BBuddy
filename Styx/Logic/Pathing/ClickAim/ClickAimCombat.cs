using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// The action bar, read from the client's memory, and the priority order to press it in.
///
/// Why this exists: the Lua bridge is retired (it killed wowb.exe twice on 2026-10-30), so
/// <c>UseAction(slot)</c> and <c>IsActionInRange(slot)</c> are gone. What replaced them is
/// the array the client itself keeps - <c>WowBOffsets.StoredActionArray</c>, 51 eight-byte
/// slots - which the Offsets button dumped on 2026-10-30 15:57 for a level-2 Tauren druid:
///
///   slot 1  0x00000000000019CB  6603   Attack          (auto-attack)
///   slot 2  0x0000000000001438  5176   Wrath
///   slot 3  0x0000000000001441  5185   Healing Touch
///   slot 4  0x0000000000001438  5176   Wrath
///   slot 9  0x0000000000005045 20549   War Stomp       (Tauren racial)
///   slot 11 0x800000000000009F   159   Refreshing Spring Water   (an ITEM)
///
/// Every id is the low 32 bits and every item carries bit 31 of the high dword, so the
/// packing is settled: <c>id = low32</c>, <c>type = high32</c> (0 spell, 0x80000000 item).
/// The ids are real Classic spell ids, which is what makes a rotation possible at all.
///
/// The order below is Singular's, taken from its own lowbie druid routine
/// (<c>Singular wotlk/ClassSpecific/Druid/Lowbie.cs</c>):
///
///   Pull    : Entangling Roots (before Cat Form), Moonfire (after), then Wrath,
///             and keep the target within 30 yd.
///   Combat  : if not on the global cooldown - Cat Form (a buff), interrupt,
///             Rejuvenation when HealthPercent &lt;= 60, auto-attack,
///             then Moonfire and Wrath.
///
/// This character has neither Cat Form nor Moonfire yet, so what is left of that list is
/// exactly: heal self at 60%, auto-attack, Wrath. The thresholds and the ordering are
/// Singular's; the spell ids are the ones the client says are on the bar.
/// </summary>
internal enum ClickAimActionKind
{
    /// <summary>A spell id on the bar (high dword 0).</summary>
    Spell = 0,

    /// <summary>An item id on the bar (high dword 0x80000000) - a potion, food, a weapon.</summary>
    Item = 1
}

/// <summary>What a spell on the bar is for. See <see cref="ClickAimCombat.RoleOf"/>.</summary>
internal enum ClickAimSpellRole
{
    Unknown = 0,
    Attack,      // auto-attack / auto shot: not a cast
    Damage,      // a direct-damage spell, pressed over and over
    Dot,         // damage over time: applied once per target, refreshed on a timer
    Heal,
    Buff,
    Utility,     // roots, fear, stealth: known, never pressed by the rotation
    Ignore       // the user's veto: never press this one, whatever the name table thinks
}

/// <summary>What an item on the bar is for.</summary>
internal enum ClickAimItemUse
{
    Other = 0,
    Food,
    Drink
}

/// <summary>One slot of the client's stored action bar.</summary>
internal sealed class ClickAimAction
{
    public int Slot;                 // 1-based, the number UseAction took
    public ulong Raw;
    public int Id;                   // spell id or item id
    public ClickAimActionKind Kind;
    public string Name = "";         // from KnownSpells, or "spell <id>"
    public int KeyVk;                // the default binding for this slot

    public bool IsAutoAttack { get { return Kind == ClickAimActionKind.Spell && Id == ClickAimCombat.AttackSpellId; } }
    public bool IsDamage { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsDamageSpell(Id); } }
    public bool IsDot { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsDotSpell(Id); } }
    public bool IsHeal { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsHealSpell(Id); } }
    public bool IsBuff { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsBuffSpell(Id); } }
    public bool IsAttack { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsAttackSpell(Id); } }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "slot {0} key '{1}': {2} {3} ({4})", Slot, ClickAimCombat.KeyLabel(KeyVk), Kind, Id, Name);
    }
}

internal static class ClickAimCombat
{
    /// <summary>The action the client calls "Attack" - auto-attack, not a cast.</summary>
    public const int AttackSpellId = 6603;

    /// <summary>
    /// How long one cast occupies the action bar: the 1.5 s global cooldown plus the
    /// room a 1.5-2 s cast needs to finish. Singular gates on <c>!Spell.IsGlobalCooldown()</c>
    /// and waits on <c>Spell.WaitForCast</c>; without either API, pressing again too
    /// early is simply swallowed, so the rotation waits this long instead.
    /// </summary>
    public const int CastSettleMs = 1800;

    /// <summary>Singular heals at <c>HealthPercent &lt;= 60</c>. The number is theirs.</summary>
    public const double HealBelowPercent = 60.0;

    /// <summary>Do not self-heal again this soon: Healing Touch has a 1.5 s cast and mana cost.</summary>
    public const int HealRetryMs = 4000;

    /// <summary>
    /// How long a cast may take before it counts as "did not cast". Wrath is a 1.5 s cast
    /// plus the 1.5 s global cooldown, and the 16:09 run proved the point the hard way: a
    /// single read 1.8 s after the keypress said "changed nothing" for a Wrath that was
    /// still in flight, and two of those switched the rotation to melee for the fight.
    /// </summary>
    public const int CastVerifyMs = 3200;

    /// <summary>The same for a heal, which has to land and then show up in the health word.</summary>
    public const int HealVerifyMs = 3600;

    /// <summary>Range for the opening cast, which is Singular's <c>MoveToTarget(true, 30f)</c>.</summary>
    public const double CasterRangeYd = 28.0;

    /// <summary>
    /// The spells this rotation knows by name, from Classic (1.12) ids. Everything not
    /// listed is still fired if it is the only thing on the bar - it just prints as
    /// <c>spell &lt;id&gt;</c>, which is how the next id gets added here.
    /// </summary>
    private static readonly Dictionary<int, string> KnownSpells = new Dictionary<int, string>
    {
        { 6603, "Attack" },
        { 5176, "Wrath" }, { 5177, "Wrath" }, { 5178, "Wrath" }, { 5179, "Wrath" }, { 5180, "Wrath" },
        { 5185, "Healing Touch" }, { 5186, "Healing Touch" }, { 5187, "Healing Touch" },
        { 5188, "Healing Touch" }, { 5189, "Healing Touch" },
        { 774, "Rejuvenation" }, { 1058, "Rejuvenation" }, { 1430, "Rejuvenation" },
        { 2090, "Rejuvenation" }, { 2091, "Rejuvenation" },
        { 8925, "Moonfire" }, { 8926, "Moonfire" }, { 8927, "Moonfire" },
        { 8928, "Moonfire" }, { 8929, "Moonfire" },
        { 2912, "Starfire" }, { 8949, "Starfire" },
        { 339, "Entangling Roots" }, { 1062, "Entangling Roots" },
        { 5195, "Entangling Roots" }, { 5196, "Entangling Roots" },
        { 467, "Thorns" }, { 1126, "Mark of the Wild" },
        { 20549, "War Stomp" }, { 20550, "Endurance" }, { 20583, "Nature Resistance" },

        /* Moonfire rank 1 is 8921 and it was the one spell the level-4 druid's own bar had
         * that this table did not know (the 19:07 run prints "spell 8921 (not in the table
         * yet)" on every single engagement). The ranks below are the whole family, because a
         * character that just trained rank 2 has a different id and would go quiet again. */
        { 8921, "Moonfire" }, { 8922, "Moonfire" }, { 8923, "Moonfire" }, { 8924, "Moonfire" },
        { 9833, "Moonfire" }, { 9834, "Moonfire" }, { 9835, "Moonfire" },
        { 770, "Faerie Fire" }, { 778, "Faerie Fire" }, { 9749, "Faerie Fire" },
        { 5570, "Insect Swarm" }, { 24974, "Insect Swarm" }, { 24975, "Insect Swarm" },
        { 8936, "Regrowth" }, { 8938, "Regrowth" }, { 8939, "Regrowth" },
        { 8940, "Regrowth" }, { 8941, "Regrowth" }, { 9750, "Regrowth" },

        /* --- Mage --- */
        { 133, "Fireball" }, { 143, "Fireball" }, { 145, "Fireball" }, { 3140, "Fireball" },
        { 8400, "Fireball" }, { 8401, "Fireball" }, { 8402, "Fireball" }, { 10148, "Fireball" },
        { 10149, "Fireball" }, { 10150, "Fireball" }, { 10151, "Fireball" },
        { 116, "Frostbolt" }, { 205, "Frostbolt" }, { 837, "Frostbolt" }, { 7322, "Frostbolt" },
        { 8406, "Frostbolt" }, { 8407, "Frostbolt" }, { 8408, "Frostbolt" },
        { 10179, "Frostbolt" }, { 10180, "Frostbolt" }, { 10181, "Frostbolt" },
        { 5143, "Arcane Missiles" }, { 5144, "Arcane Missiles" }, { 5145, "Arcane Missiles" },
        { 8416, "Arcane Missiles" }, { 8417, "Arcane Missiles" },
        { 2136, "Fire Blast" }, { 2137, "Fire Blast" }, { 2138, "Fire Blast" },
        { 8412, "Fire Blast" }, { 8413, "Fire Blast" }, { 10197, "Fire Blast" },
        { 1459, "Arcane Intellect" }, { 1460, "Arcane Intellect" }, { 1461, "Arcane Intellect" },
        { 168, "Frost Armor" }, { 7300, "Frost Armor" }, { 7301, "Frost Armor" },
        { 122, "Frost Nova" }, { 865, "Frost Nova" }, { 6131, "Frost Nova" },
        { 118, "Polymorph" },

        /* --- Priest --- */
        { 585, "Smite" }, { 591, "Smite" }, { 598, "Smite" }, { 984, "Smite" },
        { 1004, "Smite" }, { 6060, "Smite" }, { 10933, "Smite" }, { 10934, "Smite" },
        { 589, "Shadow Word: Pain" }, { 594, "Shadow Word: Pain" }, { 970, "Shadow Word: Pain" },
        { 992, "Shadow Word: Pain" }, { 2767, "Shadow Word: Pain" },
        { 10892, "Shadow Word: Pain" }, { 10893, "Shadow Word: Pain" }, { 10894, "Shadow Word: Pain" },
        { 2050, "Lesser Heal" }, { 2052, "Lesser Heal" }, { 2053, "Lesser Heal" },
        { 2060, "Heal" }, { 2054, "Heal" }, { 2055, "Heal" }, { 6063, "Heal" }, { 6064, "Heal" },
        { 139, "Renew" }, { 6074, "Renew" }, { 6075, "Renew" }, { 6076, "Renew" },
        { 6077, "Renew" }, { 6078, "Renew" }, { 10927, "Renew" }, { 10928, "Renew" }, { 10929, "Renew" },
        { 2061, "Flash Heal" }, { 9472, "Flash Heal" }, { 9473, "Flash Heal" },
        { 9474, "Flash Heal" }, { 10915, "Flash Heal" }, { 10916, "Flash Heal" }, { 10917, "Flash Heal" },
        { 8092, "Mind Blast" }, { 8102, "Mind Blast" }, { 8103, "Mind Blast" },
        { 8104, "Mind Blast" }, { 8105, "Mind Blast" }, { 8106, "Mind Blast" },
        { 1243, "Power Word: Fortitude" }, { 1244, "Power Word: Fortitude" },
        { 1245, "Power Word: Fortitude" }, { 2791, "Power Word: Fortitude" },
        { 17, "Power Word: Shield" }, { 592, "Power Word: Shield" }, { 600, "Power Word: Shield" },
        { 14914, "Holy Fire" },

        /* --- Warlock --- */
        { 686, "Shadow Bolt" }, { 695, "Shadow Bolt" }, { 705, "Shadow Bolt" },
        { 1088, "Shadow Bolt" }, { 1106, "Shadow Bolt" }, { 7641, "Shadow Bolt" },
        { 11659, "Shadow Bolt" }, { 11660, "Shadow Bolt" }, { 11661, "Shadow Bolt" },
        { 172, "Corruption" }, { 6222, "Corruption" }, { 6223, "Corruption" },
        { 7648, "Corruption" }, { 11671, "Corruption" }, { 11672, "Corruption" },
        { 348, "Immolate" }, { 707, "Immolate" }, { 1094, "Immolate" },
        { 2941, "Immolate" }, { 11665, "Immolate" }, { 11667, "Immolate" }, { 11668, "Immolate" },
        { 980, "Curse of Agony" }, { 1014, "Curse of Agony" }, { 6217, "Curse of Agony" },
        { 11711, "Curse of Agony" }, { 11712, "Curse of Agony" }, { 11713, "Curse of Agony" },
        { 689, "Drain Life" }, { 699, "Drain Life" }, { 709, "Drain Life" },
        { 7651, "Drain Life" }, { 11699, "Drain Life" }, { 11700, "Drain Life" },
        { 687, "Demon Skin" }, { 696, "Demon Skin" },
        { 1454, "Life Tap" }, { 1455, "Life Tap" }, { 1456, "Life Tap" },

        /* --- Shaman --- */
        { 403, "Lightning Bolt" }, { 529, "Lightning Bolt" }, { 548, "Lightning Bolt" },
        { 915, "Lightning Bolt" }, { 943, "Lightning Bolt" }, { 6041, "Lightning Bolt" },
        { 10391, "Lightning Bolt" }, { 10392, "Lightning Bolt" },
        { 15207, "Lightning Bolt" }, { 15208, "Lightning Bolt" },
        { 331, "Healing Wave" }, { 332, "Healing Wave" }, { 547, "Healing Wave" },
        { 913, "Healing Wave" }, { 939, "Healing Wave" }, { 959, "Healing Wave" },
        { 8005, "Healing Wave" }, { 10395, "Healing Wave" }, { 10396, "Healing Wave" },
        { 8004, "Lesser Healing Wave" }, { 8008, "Lesser Healing Wave" },
        { 8010, "Lesser Healing Wave" }, { 10405, "Lesser Healing Wave" },
        { 10406, "Lesser Healing Wave" }, { 10407, "Lesser Healing Wave" },
        { 8042, "Earth Shock" }, { 8044, "Earth Shock" }, { 8045, "Earth Shock" },
        { 8046, "Earth Shock" }, { 10412, "Earth Shock" }, { 10413, "Earth Shock" },
        { 8050, "Flame Shock" }, { 8052, "Flame Shock" }, { 8053, "Flame Shock" },
        { 10447, "Flame Shock" }, { 10448, "Flame Shock" }, { 10449, "Flame Shock" },
        { 8056, "Frost Shock" }, { 8058, "Frost Shock" }, { 10472, "Frost Shock" },
        { 10473, "Frost Shock" },
        { 324, "Lightning Shield" }, { 325, "Lightning Shield" }, { 905, "Lightning Shield" },
        { 945, "Lightning Shield" }, { 8134, "Lightning Shield" },
        { 8017, "Rockbiter Weapon" }, { 8018, "Rockbiter Weapon" }, { 8019, "Rockbiter Weapon" },

        /* --- Paladin --- */
        { 635, "Holy Light" }, { 639, "Holy Light" }, { 647, "Holy Light" },
        { 1026, "Holy Light" }, { 1042, "Holy Light" }, { 3472, "Holy Light" },
        { 10328, "Holy Light" }, { 10329, "Holy Light" },
        { 19750, "Flash of Light" }, { 19939, "Flash of Light" }, { 19940, "Flash of Light" },
        { 19941, "Flash of Light" }, { 19942, "Flash of Light" }, { 19943, "Flash of Light" },
        { 20165, "Seal of Righteousness" }, { 20166, "Seal of Righteousness" },
        { 20271, "Judgement" }, { 853, "Hammer of Justice" }, { 465, "Devotion Aura" },

        /* --- Warrior --- */
        { 78, "Heroic Strike" }, { 284, "Heroic Strike" }, { 285, "Heroic Strike" },
        { 1608, "Heroic Strike" }, { 11564, "Heroic Strike" }, { 11565, "Heroic Strike" },
        { 11566, "Heroic Strike" }, { 11567, "Heroic Strike" },
        { 772, "Rend" }, { 6546, "Rend" }, { 6547, "Rend" }, { 6548, "Rend" },
        { 11574, "Rend" }, { 11575, "Rend" }, { 11576, "Rend" },
        { 634, "Thunder Clap" }, { 8198, "Thunder Clap" }, { 8204, "Thunder Clap" },
        { 8205, "Thunder Clap" }, { 11580, "Thunder Clap" }, { 11581, "Thunder Clap" },
        { 5308, "Execute" }, { 100, "Charge" },
        { 6673, "Battle Shout" }, { 5242, "Battle Shout" }, { 6192, "Battle Shout" },
        { 11549, "Battle Shout" }, { 11550, "Battle Shout" }, { 11551, "Battle Shout" },

        /* --- Rogue --- */
        { 1752, "Sinister Strike" }, { 1757, "Sinister Strike" }, { 1758, "Sinister Strike" },
        { 1759, "Sinister Strike" }, { 1760, "Sinister Strike" }, { 8621, "Sinister Strike" },
        { 11293, "Sinister Strike" }, { 11294, "Sinister Strike" },
        { 2098, "Eviscerate" }, { 6760, "Eviscerate" }, { 6761, "Eviscerate" },
        { 6762, "Eviscerate" }, { 8623, "Eviscerate" }, { 8624, "Eviscerate" },
        { 53, "Backstab" }, { 2589, "Backstab" }, { 2590, "Backstab" }, { 2591, "Backstab" },
        { 8721, "Backstab" }, { 11279, "Backstab" }, { 11280, "Backstab" }, { 11281, "Backstab" },
        { 1784, "Stealth" }, { 1776, "Gouge" },

        /* --- Hunter: the auto shot that IS the hunter's attack, and the shots around it --- */
        { 75, "Auto Shot" }, { 2973, "Raptor Strike" }, { 1495, "Mongoose Bite" },
        { 1978, "Serpent Sting" }, { 13549, "Serpent Sting" }, { 13550, "Serpent Sting" },
        { 13551, "Serpent Sting" }, { 13552, "Serpent Sting" }, { 13553, "Serpent Sting" },
        { 13554, "Serpent Sting" }, { 13555, "Serpent Sting" },
        { 3044, "Arcane Shot" }, { 14281, "Arcane Shot" }, { 14282, "Arcane Shot" },
        { 14283, "Arcane Shot" }, { 14284, "Arcane Shot" }, { 14285, "Arcane Shot" },
        { 14286, "Arcane Shot" }, { 14287, "Arcane Shot" },
        { 1130, "Hunter's Mark" }, { 13165, "Aspect of the Hawk" }
    };

    /// <summary>
    /// What a slot is FOR. This used to be three hand-written sets of ids - damage, heal, buff -
    /// and anything not in them printed as "spell &lt;id&gt; (not in the table yet)" and was never
    /// pressed. That is exactly why the level-4 druid carried Moonfire (8921) on slot 5 for a
    /// whole evening and never cast it: the name table skipped rank 1 and no set had it.
    ///
    /// The role is now derived from the spell's NAME, so adding an id to the name table is
    /// enough to make it usable, and a rank nobody has listed yet still behaves like its family
    /// as soon as its id is added once.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, ClickAimSpellRole> RolesByName =
        new System.Collections.Generic.Dictionary<string, ClickAimSpellRole>(StringComparer.OrdinalIgnoreCase)
    {
        { "Attack", ClickAimSpellRole.Attack },
        { "Auto Shot", ClickAimSpellRole.Attack },

        /* Direct damage: what the rotation presses when there is nothing better to do. */
        { "Wrath", ClickAimSpellRole.Damage }, { "Starfire", ClickAimSpellRole.Damage },
        { "Fireball", ClickAimSpellRole.Damage }, { "Frostbolt", ClickAimSpellRole.Damage },
        { "Arcane Missiles", ClickAimSpellRole.Damage }, { "Fire Blast", ClickAimSpellRole.Damage },
        { "Smite", ClickAimSpellRole.Damage }, { "Holy Fire", ClickAimSpellRole.Damage },
        { "Mind Blast", ClickAimSpellRole.Damage }, { "Shadow Bolt", ClickAimSpellRole.Damage },
        { "Drain Life", ClickAimSpellRole.Damage }, { "Lightning Bolt", ClickAimSpellRole.Damage },
        { "Earth Shock", ClickAimSpellRole.Damage }, { "Frost Shock", ClickAimSpellRole.Damage },
        { "Judgement", ClickAimSpellRole.Damage }, { "Holy Shock", ClickAimSpellRole.Damage },
        { "Heroic Strike", ClickAimSpellRole.Damage }, { "Execute", ClickAimSpellRole.Damage },
        { "Thunder Clap", ClickAimSpellRole.Damage }, { "Sinister Strike", ClickAimSpellRole.Damage },
        { "Eviscerate", ClickAimSpellRole.Damage }, { "Backstab", ClickAimSpellRole.Damage },
        { "Arcane Shot", ClickAimSpellRole.Damage }, { "Raptor Strike", ClickAimSpellRole.Damage },
        { "Mongoose Bite", ClickAimSpellRole.Damage }, { "War Stomp", ClickAimSpellRole.Damage },

        /* Damage over time: applied once per target and refreshed on a timer, not spammed. */
        { "Moonfire", ClickAimSpellRole.Dot }, { "Insect Swarm", ClickAimSpellRole.Dot },
        { "Shadow Word: Pain", ClickAimSpellRole.Dot }, { "Corruption", ClickAimSpellRole.Dot },
        { "Immolate", ClickAimSpellRole.Dot }, { "Curse of Agony", ClickAimSpellRole.Dot },
        { "Flame Shock", ClickAimSpellRole.Dot }, { "Serpent Sting", ClickAimSpellRole.Dot },
        { "Rend", ClickAimSpellRole.Dot },

        /* Healing. */
        { "Healing Touch", ClickAimSpellRole.Heal }, { "Rejuvenation", ClickAimSpellRole.Heal },
        { "Regrowth", ClickAimSpellRole.Heal }, { "Lesser Heal", ClickAimSpellRole.Heal },
        { "Heal", ClickAimSpellRole.Heal }, { "Flash Heal", ClickAimSpellRole.Heal },
        { "Renew", ClickAimSpellRole.Heal }, { "Healing Wave", ClickAimSpellRole.Heal },
        { "Lesser Healing Wave", ClickAimSpellRole.Heal }, { "Holy Light", ClickAimSpellRole.Heal },
        { "Flash of Light", ClickAimSpellRole.Heal },

        /* Buffs: pressed out of combat, once. */
        { "Thorns", ClickAimSpellRole.Buff }, { "Mark of the Wild", ClickAimSpellRole.Buff },
        { "Arcane Intellect", ClickAimSpellRole.Buff }, { "Frost Armor", ClickAimSpellRole.Buff },
        { "Power Word: Fortitude", ClickAimSpellRole.Buff },
        { "Power Word: Shield", ClickAimSpellRole.Buff },
        { "Demon Skin", ClickAimSpellRole.Buff }, { "Lightning Shield", ClickAimSpellRole.Buff },
        { "Rockbiter Weapon", ClickAimSpellRole.Buff }, { "Battle Shout", ClickAimSpellRole.Buff },
        { "Devotion Aura", ClickAimSpellRole.Buff }, { "Seal of Righteousness", ClickAimSpellRole.Buff },
        { "Aspect of the Hawk", ClickAimSpellRole.Buff }, { "Endurance", ClickAimSpellRole.Buff },
        { "Nature Resistance", ClickAimSpellRole.Buff },

        /* Known, but not something to press on its own. */
        { "Entangling Roots", ClickAimSpellRole.Utility }, { "Faerie Fire", ClickAimSpellRole.Utility },
        { "Frost Nova", ClickAimSpellRole.Utility }, { "Polymorph", ClickAimSpellRole.Utility },
        { "Hammer of Justice", ClickAimSpellRole.Utility }, { "Charge", ClickAimSpellRole.Utility },
        { "Stealth", ClickAimSpellRole.Utility }, { "Gouge", ClickAimSpellRole.Utility },
        { "Life Tap", ClickAimSpellRole.Utility }, { "Hunter's Mark", ClickAimSpellRole.Utility }
    };

    /// <summary>
    /// The role of a spell id: the user's own word first, then the name table, then
    /// <see cref="ClickAimSpellRole.Unknown"/>.
    /// </summary>
    public static ClickAimSpellRole RoleOf(int id)
    {
        ClickAimSpellRole role;
        if (RoleOverrides.TryGetValue(id, out role)) return role;

        string name;
        if (!KnownSpells.TryGetValue(id, out name)) return ClickAimSpellRole.Unknown;
        return RolesByName.TryGetValue(name, out role) ? role : ClickAimSpellRole.Unknown;
    }

    public static bool IsDamageSpell(int id) { return RoleOf(id) == ClickAimSpellRole.Damage; }
    public static bool IsDotSpell(int id) { return RoleOf(id) == ClickAimSpellRole.Dot; }
    public static bool IsHealSpell(int id) { return RoleOf(id) == ClickAimSpellRole.Heal; }
    public static bool IsBuffSpell(int id) { return RoleOf(id) == ClickAimSpellRole.Buff; }
    public static bool IsAttackSpell(int id) { return RoleOf(id) == ClickAimSpellRole.Attack; }

    // ------------------------------------------------------------------ role overrides

    /// <summary>
    /// Spell id to role, exactly as the user set it in the Combat settings window.
    ///
    /// The name table above covers the 3.3.5a spellbook; this covers what it cannot: a rank a
    /// private server added, a spell whose name never got into the table, or a spell the user
    /// would rather the bot never pressed at all. It is consulted first, so an override always
    /// wins, and it lives in its own file so a hand edit and the window write the same thing.
    /// </summary>
    private static readonly Dictionary<int, ClickAimSpellRole> RoleOverrides =
        new Dictionary<int, ClickAimSpellRole>();

    /// <summary>How many spells the user has classified by hand.</summary>
    public static int OverrideCount { get { return RoleOverrides.Count; } }

    /// <summary>Forgets every override - used by the window's Reset and by the self-test.</summary>
    public static void ClearRoleOverrides() { RoleOverrides.Clear(); }

    /// <summary>Sets one override; <see cref="ClickAimSpellRole.Unknown"/> removes it again.</summary>
    public static void SetRoleOverride(int id, ClickAimSpellRole role)
    {
        if (id <= 0) return;
        if (role == ClickAimSpellRole.Unknown) RoleOverrides.Remove(id);
        else RoleOverrides[id] = role;
    }

    /// <summary>The user's word for this spell, or Unknown when they have not given one.</summary>
    public static ClickAimSpellRole RoleOverrideOf(int id)
    {
        ClickAimSpellRole role;
        return RoleOverrides.TryGetValue(id, out role) ? role : ClickAimSpellRole.Unknown;
    }

    /// <summary>Every overridden id, ascending, so a saved file has a stable order.</summary>
    public static List<int> OverrideIds()
    {
        var ids = new List<int>(RoleOverrides.Keys);
        ids.Sort();
        return ids;
    }

    /// <summary>Writes <c>Settings\clickaim-skills.txt</c>; false when the file could not be written.</summary>
    public static bool SaveRoleOverrides()
    {
        string path;
        try { path = ClickAimSettings.SkillsPath; }
        catch (Exception ex)
        {
            Styx.Helpers.Logging.Write("[Combat] no place to write the spell roles: " + ex.Message);
            return false;
        }

        var text = new StringBuilder();
        text.AppendLine("# CopilotBuddy spell roles: what each spell on the bar is for.");
        text.AppendLine("# Written by the Combat settings window. One line per spell:");
        text.AppendLine("#   <spell id> = Attack | Damage | Dot | Heal | Buff | Utility | Ignore");
        text.AppendLine("# Delete a line to go back to what the built-in name table says.");
        List<int> ids = OverrideIds();
        for (int i = 0; i < ids.Count; i++)
        {
            text.AppendLine(ids[i].ToString(CultureInfo.InvariantCulture) + " = "
                            + RoleOverrides[ids[i]].ToString());
        }

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text.ToString());
            return true;
        }
        catch (Exception ex)
        {
            Styx.Helpers.Logging.Write("[Combat] could not write " + path + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Reads the override file. A missing file is a first run; an unreadable line is skipped
    /// rather than fatal, because a typo in an id must not stop the grind from starting.
    /// </summary>
    public static void LoadRoleOverrides()
    {
        RoleOverrides.Clear();

        string path;
        try { path = ClickAimSettings.SkillsPath; }
        catch { return; }

        string[] lines;
        try
        {
            if (!File.Exists(path)) return;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            Styx.Helpers.Logging.Write("[Combat] could not read " + path + ": " + ex.Message);
            return;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            int id;
            if (!int.TryParse(line.Substring(0, eq).Trim(), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out id) || id <= 0)
                continue;

            ClickAimSpellRole role;
            if (!Enum.TryParse(line.Substring(eq + 1).Trim(), true, out role)) continue;
            if (role == ClickAimSpellRole.Unknown) continue;
            RoleOverrides[id] = role;
        }
    }

    // ------------------------------------------------------------------ consumables

    /// <summary>
    /// Food and drink, by item id. Both are items on the action bar - slot 11 carried 159
    /// (Refreshing Spring Water) and slot 12 carried 4536 (Shiny Red Apple) on the character this
    /// was written for - so eating and drinking is a key press, like everything else here.
    /// </summary>
    private static readonly HashSet<int> FoodItems = new HashSet<int>
    {
        117, 228, 414, 422, 724, 733,                       // Tough Jerky, Haunch of Meat, ...
        2679, 2680, 2681, 2685, 2686, 2687,                 // cooked meats
        4536, 4537, 4538, 4539,                             // Shiny Red Apple, Tel'Abim Banana, ...
        4540, 4541, 4542, 4544, 4604, 4605, 4606, 4607, 4608,
        5349, 11100                                         // Conjured Bread, Conjured Pumpernickel
    };

    private static readonly HashSet<int> DrinkItems = new HashSet<int>
    {
        159, 1179, 1205, 1645, 1708, 8766,                  // Spring Water, Ice Cold Milk, ...
        28399, 27860, 18300,                                // Filtered Draenic Water, ...
        5350, 8077                                          // Conjured Water, Conjured Purified Water
    };

    /// <summary>What an item on the bar is for, as far as the rest loop cares.</summary>
    public static ClickAimItemUse ItemUseOf(int itemId)
    {
        if (FoodItems.Contains(itemId)) return ClickAimItemUse.Food;
        if (DrinkItems.Contains(itemId)) return ClickAimItemUse.Drink;
        return ClickAimItemUse.Other;
    }

    /// <summary>The food on the bar: the configured slot first, then the first item that is food.</summary>
    public static ClickAimAction? FindFood(List<ClickAimAction> bar, int slot)
    {
        return FindItem(bar, slot, ClickAimItemUse.Food);
    }

    /// <summary>The drink on the bar: the configured slot first, then the first item that is drink.</summary>
    public static ClickAimAction? FindDrink(List<ClickAimAction> bar, int slot)
    {
        return FindItem(bar, slot, ClickAimItemUse.Drink);
    }

    private static ClickAimAction? FindItem(List<ClickAimAction> bar, int slot, ClickAimItemUse wanted)
    {
        if (slot > 0)
        {
            for (int i = 0; i < bar.Count; i++)
            {
                if (bar[i].Slot != slot) continue;
                if (bar[i].Kind != ClickAimActionKind.Item) continue;
                return bar[i];
            }
        }
        for (int i = 0; i < bar.Count; i++)
        {
            if (bar[i].Kind != ClickAimActionKind.Item) continue;
            if (ItemUseOf(bar[i].Id) == wanted) return bar[i];
        }
        return null;
    }

    public static string NameOf(int id)
    {
        string name;
        return KnownSpells.TryGetValue(id, out name)
            ? name
            : "spell " + id.ToString(CultureInfo.InvariantCulture) + " (not in the table yet)";
    }

    /// <summary>
    /// The default binding of action-bar slot 1-12: 1..9, then 0, then '-' and '='.
    /// <paramref name="slot"/> is 1-based; 0 means "no key", which is what a slot past 12
    /// answers - those buttons exist on other bars and have no default binding.
    /// </summary>
    public static int KeyForSlot(int slot)
    {
        if (slot >= 1 && slot <= 9) return 0x31 + (slot - 1);   // '1'..'9'
        if (slot == 10) return 0x30;                            // '0'
        if (slot == 11) return 0xBD;                            // VK_OEM_MINUS '-'
        if (slot == 12) return 0xBB;                            // VK_OEM_PLUS  '='
        return 0;
    }

    /// <summary>
    /// The bar as the client stores it. Slots with no binding key (13 and up) are skipped
    /// because this bot can only press keys, and an empty slot is all zeros.
    /// </summary>
    public static List<ClickAimAction> ReadBar(WowBReader? reader)
    {
        var result = new List<ClickAimAction>();
        if (reader == null) return result;

        List<WowBReader.WowBActionSlot> raw = reader.ReadActionBar();
        for (int i = 0; i < raw.Count; i++)
        {
            WowBReader.WowBActionSlot slot = raw[i];
            int key = KeyForSlot(slot.Index);
            if (key == 0) continue;

            /* The dump settled the packing: id in the low dword, type in the high one, and
             * 0x80000000 there means "this is an item". */
            var action = new ClickAimAction();
            action.Slot = slot.Index;
            action.Raw = slot.Raw;
            action.Kind = slot.TypeHigh32 == unchecked((int)0x80000000)
                ? ClickAimActionKind.Item
                : ClickAimActionKind.Spell;
            action.Id = slot.IdLow32;          // spell id, or item id on an item slot
            action.KeyVk = key;
            action.Name = action.Kind == ClickAimActionKind.Item
                ? "item " + action.Id.ToString(CultureInfo.InvariantCulture)
                : NameOf(action.Id);
            result.Add(action);
        }
        return result;
    }

    /// <summary>
    /// What to call a binding in the log. <c>(char)0xBD</c> prints as '½' and 0xBB as '»',
    /// which is how the 16:08 run described its own action bar - the keys are '-' and '=',
    /// and a log that says otherwise is a log nobody can check against the client.
    /// </summary>
    public static string KeyLabel(int vk)
    {
        if (vk == 0xBD) return "-";
        if (vk == 0xBB) return "=";
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        return "VK 0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>The first damage spell on the bar, or null.</summary>
    public static ClickAimAction? FirstDamage(List<ClickAimAction> bar)
    {
        for (int i = 0; i < bar.Count; i++) if (bar[i].IsDamage) return bar[i];
        return null;
    }

    /// <summary>The first damage-over-time spell on the bar, or null.</summary>
    public static ClickAimAction? FirstDot(List<ClickAimAction> bar)
    {
        for (int i = 0; i < bar.Count; i++) if (bar[i].IsDot) return bar[i];
        return null;
    }

    /// <summary>The first direct-damage spell on the bar, or a DoT when that is all there is.</summary>
    public static ClickAimAction? FirstDamageOrDot(List<ClickAimAction> bar)
    {
        ClickAimAction? direct = FirstDamage(bar);
        return direct ?? FirstDot(bar);
    }

    /// <summary>The first self-buff on the bar, or null.</summary>
    public static ClickAimAction? FirstBuff(List<ClickAimAction> bar)
    {
        for (int i = 0; i < bar.Count; i++) if (bar[i].IsBuff) return bar[i];
        return null;
    }

    /// <summary>The first heal on the bar, or null.</summary>
    public static ClickAimAction? FirstHeal(List<ClickAimAction> bar)
    {
        for (int i = 0; i < bar.Count; i++) if (bar[i].IsHeal) return bar[i];
        return null;
    }

    /// <summary>The auto-attack slot (spell 6603), or null.</summary>
    public static ClickAimAction? AutoAttack(List<ClickAimAction> bar)
    {
        for (int i = 0; i < bar.Count; i++) if (bar[i].IsAutoAttack) return bar[i];
        return null;
    }

    /// <summary>One line per filled slot, for the log.</summary>
    public static string Describe(List<ClickAimAction> bar)
    {
        if (bar.Count == 0) return "the action bar reads empty";
        var parts = new List<string>();
        for (int i = 0; i < bar.Count; i++)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "slot {0} '{1}' {2} {3} ({4})",
                bar[i].Slot, KeyLabel(bar[i].KeyVk), bar[i].Kind, bar[i].Id, bar[i].Name));
        }
        return string.Join("; ", parts.ToArray());
    }

    /// <summary>
    /// The parts of this that need no game: slot decoding, the key map, and the spell
    /// table agreeing with itself. A wrong key map presses the wrong button, which looks
    /// exactly like "the keyboard does not work", so it is worth a check.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0, total = 0;
        var fails = new System.Text.StringBuilder();
        Action<bool, string> check = (ok, name) =>
        {
            total++;
            if (ok) passed++;
            else fails.Append(' ').Append(name);
        };

        check(KeyForSlot(1) == 0x31, "key1");
        check(KeyForSlot(9) == 0x39, "key9");
        check(KeyForSlot(10) == 0x30, "key10");
        check(KeyForSlot(11) == 0xBD, "key11");
        check(KeyForSlot(12) == 0xBB, "key12");
        check(KeyForSlot(13) == 0, "key13");

        /* The exact words the 15:57 dump produced. */
        var wrath = new WowBReader.WowBActionSlot(2, 0x0000000000001438UL);
        check(wrath.IdLow32 == 5176, "id5176");
        check(NameOf(5176) == "Wrath", "nameWrath");
        check(IsDamageSpell(5176) && !IsHealSpell(5176), "classWrath");

        var water = new WowBReader.WowBActionSlot(11, 0x800000000000009FUL);
        check(water.IdLow32 == 159, "id159");
        check(water.TypeHigh32 == unchecked((int)0x80000000), "itemFlag");

        check(NameOf(5185) == "Healing Touch" && IsHealSpell(5185), "heal");
        check(AttackSpellId == 6603 && NameOf(6603) == "Attack", "attack");
        check(!IsDamageSpell(6603), "attackIsNotACast");
        check(HealBelowPercent == 60.0, "healAt60");

        /* The spell the 19:07 run carried on slot 5 and never cast. */
        check(NameOf(8921) == "Moonfire", "name8921");
        check(IsDotSpell(8921) && !IsDamageSpell(8921), "moonfireIsADot");
        check(RoleOf(8925) == ClickAimSpellRole.Dot, "moonfireRank2");

        /* The user's word beats the table both ways: a spell they classified themselves, and a
         * spell they vetoed outright. Wrath is Damage in the table, so both directions show. */
        ClearRoleOverrides();
        SetRoleOverride(5176, ClickAimSpellRole.Ignore);
        check(RoleOf(5176) == ClickAimSpellRole.Ignore, "overrideWins");
        check(!IsDamageSpell(5176), "overrideVetoes");
        SetRoleOverride(8921, ClickAimSpellRole.Damage);
        check(IsDamageSpell(8921) && !IsDotSpell(8921), "overridePromotes");
        SetRoleOverride(8921, ClickAimSpellRole.Unknown);
        check(IsDotSpell(8921), "overrideCleared");
        ClearRoleOverrides();
        check(RoleOf(5176) == ClickAimSpellRole.Damage && OverrideCount == 0, "overrideReset");
        check(RoleOf(5176) == ClickAimSpellRole.Damage, "wrathIsDamage");
        check(RoleOf(339) == ClickAimSpellRole.Utility, "rootsIsUtility");
        check(NameOf(5176) != NameOf(8921), "noMixedNames");

        /* One spell per class, so a character that is not a druid is not a blank bar. */
        check(IsDamageSpell(133), "mageFireball");
        check(IsHealSpell(2050), "priestLesserHeal");
        check(IsDotSpell(589), "priestSwPain");
        check(IsDamageSpell(686), "warlockShadowBolt");
        check(IsDotSpell(172), "warlockCorruption");
        check(IsDamageSpell(403), "shamanLightningBolt");
        check(IsHealSpell(331), "shamanHealingWave");
        check(IsHealSpell(635), "paladinHolyLight");
        check(IsDamageSpell(78), "warriorHeroicStrike");
        check(IsDamageSpell(1752), "rogueSinisterStrike");
        check(IsDamageSpell(3044), "hunterArcaneShot");
        check(IsAttackSpell(75), "hunterAutoShot");

        /* Food and drink, by item id, from the two slots this character actually carries. */
        check(ItemUseOf(159) == ClickAimItemUse.Drink, "water159");
        check(ItemUseOf(4536) == ClickAimItemUse.Food, "apple4536");
        check(ItemUseOf(118) == ClickAimItemUse.Other, "potionIsNotFood");

        if (fails.Length == 0) return string.Format("Combat self-test {0}/{1} passed", passed, total);
        return string.Format("Combat self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }
}

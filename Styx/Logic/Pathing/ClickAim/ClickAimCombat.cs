using System;
using System.Collections.Generic;
using System.Globalization;
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
    public bool IsHeal { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsHealSpell(Id); } }
    public bool IsBuff { get { return Kind == ClickAimActionKind.Spell && ClickAimCombat.IsBuffSpell(Id); } }

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
        { 467, "Thorns" }, { 1126, "Mark of the Wild" },
        { 20549, "War Stomp" }, { 20550, "Endurance" }, { 20583, "Nature Resistance" }
    };

    private static readonly HashSet<int> DamageSpells = new HashSet<int>
    {
        5176, 5177, 5178, 5179, 5180,          // Wrath
        8925, 8926, 8927, 8928, 8929,          // Moonfire
        2912, 8949,                            // Starfire
        339, 1062,                             // Entangling Roots (damage over time, Singular's pull)
        20549                                  // War Stomp
    };

    private static readonly HashSet<int> HealSpells = new HashSet<int>
    {
        5185, 5186, 5187, 5188, 5189,          // Healing Touch
        774, 1058, 1430, 2090, 2091,           // Rejuvenation
        8936, 8938, 8939                       // Regrowth
    };

    private static readonly HashSet<int> BuffSpells = new HashSet<int>
    {
        467, 1126, 20550, 20583                // Thorns, Mark of the Wild, racial passives
    };

    public static bool IsDamageSpell(int id) { return DamageSpells.Contains(id); }
    public static bool IsHealSpell(int id) { return HealSpells.Contains(id); }
    public static bool IsBuffSpell(int id) { return BuffSpells.Contains(id); }

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

        if (fails.Length == 0) return string.Format("Combat self-test {0}/{1} passed", passed, total);
        return string.Format("Combat self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }
}

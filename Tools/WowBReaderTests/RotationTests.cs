#if WOWB_READER_TESTS
using System;
using System.Collections.Generic;
using Styx.Logic.Pathing;

internal static class RotationTests
{
    private static ClickAimAction A(int id, int slot = 1) => new() {
        Id = id, Slot = slot, KeyVk = ClickAimCombat.KeyForSlot(slot), Kind = ClickAimActionKind.Spell,
        Name = ClickAimCombat.NameOf(id)
    };
    private static List<ClickAimAction> Bar(params int[] ids)
    {
        var bar = new List<ClickAimAction>();
        for (int i = 0; i < ids.Length; i++) bar.Add(A(ids[i], i + 1));
        return bar;
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("Rotation: " + name); }

    private static void ShockAutoFlowTests()
    {
        var bar = Bar(6603, 403, 331, 8042);
        var attack = bar[0];
        var normal = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, bar);
        Check(!ClickAimClassRotation.ShockAutoPlan(normal, attack), "Lightning Bolt rotation does not chase to melee");
        ClickAimCombat.SetRoleOverride(403, ClickAimSpellRole.Ignore);
        var plan = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, bar);
        bool hybrid = ClickAimClassRotation.ShockAutoPlan(plan, attack);
        Check(hybrid && plan.Damage?.Id == 8042, "ignored Lightning Bolt enables Earth Shock and Attack");
        Check(!ClickAimClassRotation.ShockAutoPlan(plan, null), "no Attack key keeps ranged fallback");
        Check(ClickAimClassRotation.ShockAutoPlan(plan, null, true), "opted-in macro supports Earth Shock without decoded Attack");
        Check(!ClickAimClassRotation.ShockAutoPlan(normal, null, true), "macro does not change Lightning Bolt filler");
        Check(!ClickAimClassRotation.ShockAutoPlan(ClickAimClassRotation.Build(ClickAimRotationClass.Hunter, Bar(3044)), attack), "Hunter does not chase on cooldown");
        Check(!ClickAimClassRotation.ShockAutoPlan(ClickAimClassRotation.Build(ClickAimRotationClass.Mage, Bar(2136)), attack), "Mage does not chase on cooldown");
        Check(!ClickAimClassRotation.ShockAutoPlan(ClickAimClassRotation.Build(ClickAimRotationClass.Generic, Bar(8042)), attack), "generic rotation unchanged");

        // Drive the same production policy as Fight: opener, chase while the
        // shared timer is pending, a single melee key, then Shock again when due.
        var until = new Dictionary<string, int>();
        bool ready = ClickAimClassRotation.Ready(until, plan.DamageGroup, 1000);
        Check(ready && !ClickAimClassRotation.PreferShockMelee(hybrid, ready), "ready opener casts at ranged distance");
        ClickAimClassRotation.Defer(until, plan.DamageGroup, plan.DamageIntervalMs, 1000);
        ready = ClickAimClassRotation.Ready(until, plan.DamageGroup, 2000);
        Check(!ready && ClickAimClassRotation.PreferShockMelee(hybrid, ready), "pending Shock chooses melee chase");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(hybrid, false, false, false, 12, 4), "no Attack at ranged distance");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(hybrid, false, false, false, 4.1, 4), "no Attack outside melee range");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(hybrid, false, false, false, double.NaN, 4), "unknown distance fails closed");
        Check(ClickAimClassRotation.MayPressConservativeAttack(hybrid, false, false, false, 4, 4), "one Attack at melee range");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(hybrid, true, false, false, 3, 4), "never retry the toggle after silence or a heal");
        ready = ClickAimClassRotation.Ready(until, plan.DamageGroup, 9000);
        Check(ready && !ClickAimClassRotation.PreferShockMelee(hybrid, ready), "Shock resumes when ready even while approaching or swinging");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(hybrid, true, false, false, 3, 4), "no second Attack after Shock");
        Check(!ClickAimClassRotation.PreferShockMelee(false, false), "ordinary casters retain cooldown waiting");
        ClickAimCombat.SetRoleOverride(6603, ClickAimSpellRole.Ignore);
        Check(!ClickAimClassRotation.ShockAutoPlan(plan, attack), "ignored Attack disables hybrid plan");
        ClickAimCombat.ClearRoleOverrides();

        // Model the 19:16 bar with overrides selecting Frost Shock as damage,
        // Lightning Bolt and Earth Shock ignored, and Attack in slot 1.
        var frostBar = Bar(6603, 915, 8004, 8056, 8045, 325, 8019);
        ClickAimCombat.SetRoleOverride(915, ClickAimSpellRole.Ignore);
        ClickAimCombat.SetRoleOverride(8045, ClickAimSpellRole.Ignore);
        ClickAimCombat.SetRoleOverride(8056, ClickAimSpellRole.Damage);
        var frost = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, frostBar);
        bool frostHybrid = ClickAimClassRotation.ShockAutoPlan(frost, frostBar[0]);
        Check(frost.Class == ClickAimRotationClass.Shaman && frost.Damage?.Id == 8056 && frostHybrid,
            "Frost Shock override selects the Shaman Attack hybrid");
        Check(frost.DamageGroup == "Shocks" && frost.DamageIntervalMs == 8000, "Frost Shock override preserves shared cooldown");
        var frostUntil = new Dictionary<string, int>();
        ClickAimClassRotation.Defer(frostUntil, frost.DamageGroup, frost.DamageIntervalMs, 1000);
        Check(ClickAimClassRotation.PreferShockMelee(frostHybrid, ClickAimClassRotation.Ready(frostUntil, frost.DamageGroup, 2000)),
            "Frost Shock cooldown closes to melee rather than waiting");
        Check(ClickAimClassRotation.MayPressConservativeAttack(frostHybrid, false, false, false, 3, 4), "Frost Attack starts in melee");
        Check(!ClickAimClassRotation.MayPressConservativeAttack(frostHybrid, true, false, false, 3, 4), "Frost never toggles Attack twice in one fight");
        Check(ClickAimClassRotation.Ready(frostUntil, frost.DamageGroup, 9000), "Frost Shock resumes after estimated cooldown");
        ClickAimCombat.ClearRoleOverrides();
        var frostDefault = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(6603, 8056));
        Check(frostDefault.Class == ClickAimRotationClass.Shaman && frostDefault.Damage?.Id == 8056 &&
              frostDefault.DamageGroup == "Shocks" && frostDefault.DamageIntervalMs == 8000, "Frost alone resolves Shaman and cooldown");
        var macroFrost = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(8056, 331));
        Check(ClickAimClassRotation.ShockAutoPlan(macroFrost, null, true), "macro plus Frost closes during cooldown without Attack slot");
        Check(ClickAimClassRotation.PreferShockMelee(ClickAimClassRotation.ShockAutoPlan(macroFrost, null, true), false), "macro Frost does not wait idle");
        Check(ClickAimClassRotation.IsAnyShock(A(8042)) && ClickAimClassRotation.IsAnyShock(A(8056)) &&
              ClickAimClassRotation.IsAnyShock(A(8050)) && !ClickAimClassRotation.IsAnyShock(A(403)), "all known Shock types only");
        var both = ClickAimClassRotation.Build(ClickAimRotationClass.Shaman, Bar(6603, 8056, 8045));
        Check(both.Damage?.Id == 8045, "Earth Shock remains preferred over Frost when both available");
        var boltAndFrost = ClickAimClassRotation.Build(ClickAimRotationClass.Shaman, Bar(6603, 915, 8056));
        Check(boltAndFrost.Damage?.Id == 915 && !ClickAimClassRotation.ShockAutoPlan(boltAndFrost, A(6603)),
            "Lightning Bolt filler retains ranged rotation");
    }

    internal static void Run()
    {
        ClickAimCombat.ClearRoleOverrides();
        try
        {
            var shaman = Bar(8042, 403, 331, 8050); // shock before bolt on the bar
            var plan = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, shaman);
            Check(plan.Class == ClickAimRotationClass.Shaman && plan.Damage?.Id == 403 &&
                  plan.Supplemental?.Id == 8042 && plan.Heal?.Id == 331 && plan.Dot?.Id == 8050,
                  "shaman priorities ignore bar order");
            Check(plan.SupplementalIntervalMs >= 6000 && plan.SupplementalGroup == "Shocks" &&
                  ClickAimClassRotation.DotCooldownGroup(plan.Dot!) == "Shocks", "shared shock estimate");
            var cooldowns = new Dictionary<string, int>();
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, true, 15, 50, 20, 1000, cooldowns)?.Id == 8042, "optional shock in range with sufficient power");
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, false, 15, 50, 20, 1000, cooldowns) == null, "pull uses filler first");
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, true, 21, 50, 20, 1000, cooldowns) == null, "optional range");
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, true, 15, 19, 20, 1000, cooldowns) == null, "optional resource gate");
            Check(ClickAimClassRotation.SelectSupplemental(plan, true, true, 15, 50, 20, 1000, cooldowns) == null, "melee latch");
            ClickAimClassRotation.Defer(cooldowns, "Shocks", 8000, 1000);
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, true, 15, 50, 20, 2000, cooldowns) == null &&
                  ClickAimClassRotation.SelectSupplemental(plan, false, true, 15, 50, 20, 9000, cooldowns)?.Id == 8042, "cooldown fallback and expiry");
            ClickAimCombat.SetRoleOverride(8042, ClickAimSpellRole.Ignore);
            Check(ClickAimClassRotation.SelectSupplemental(plan, false, true, 15, 50, 20, 9000, cooldowns) == null, "live override veto");
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Auto, shaman).Supplemental == null, "ignore supplemental");
            ClickAimCombat.SetRoleOverride(403, ClickAimSpellRole.Ignore);
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Auto, shaman).Damage == null, "ignore damage");
            ClickAimCombat.ClearRoleOverrides();
            var shockOnly = ClickAimClassRotation.Build(ClickAimRotationClass.Shaman, Bar(8042));
            Check(shockOnly.Damage?.Id == 8042 && shockOnly.Supplemental == null && shockOnly.DamageIntervalMs >= 6000,
                "short-range only fallback is timed");
            ShockAutoFlowTests();
            Check(ClickAimClassRotation.Resolve(ClickAimRotationClass.Auto, Bar(403, 133)) == ClickAimRotationClass.Generic, "ambiguous class -> generic");
            Check(ClickAimClassRotation.Resolve(ClickAimRotationClass.Auto, Bar(6603)) == ClickAimRotationClass.Generic, "no signature -> generic");
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Generic, Bar(8042, 403)).Damage?.Id == 8042, "generic preserves bar order");
            var dotOnly = ClickAimClassRotation.Build(ClickAimRotationClass.Warlock, Bar(172));
            Check(ClickAimClassRotation.ShouldApproachDotOnly(dotOnly.Damage, dotOnly.Dot, false, true, true, true, 50, 10, 35), "DoT-only approaches to cast range");
            Check(!ClickAimClassRotation.ShouldApproachDotOnly(dotOnly.Damage, dotOnly.Dot, false, false, true, true, 50, 10, 35) &&
                  !ClickAimClassRotation.ShouldApproachDotOnly(dotOnly.Damage, dotOnly.Dot, false, true, false, true, 50, 10, 35) &&
                  !ClickAimClassRotation.ShouldApproachDotOnly(dotOnly.Damage, dotOnly.Dot, false, true, true, true, 5, 10, 35), "DoT-only respects setting, timer and power");
            var druid = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(8921, 5176, 5185));
            Check(druid.Class == ClickAimRotationClass.Druid && druid.Damage?.Id == 5176 && druid.Dot?.Id == 8921 && druid.Heal?.Id == 5185, "druid");
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(1752, 2098)).Melee?.Id == 1752, "rogue builder, no finisher");
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Rogue, Bar(2098)).Melee == null, "no unguarded combo finisher");
            var warrior = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(772, 78));
            Check(warrior.Class == ClickAimRotationClass.Warrior && warrior.Dot?.Id == 772 && warrior.Melee?.Id == 78, "warrior");
            var hunter = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(1978, 3044, 2973));
            Check(hunter.Class == ClickAimRotationClass.Hunter && hunter.Dot?.Id == 1978 && hunter.Damage?.Id == 3044 && hunter.DamageIntervalMs >= 6000 && hunter.MeleeIntervalMs > 0, "hunter");
            var mage = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(2136, 133));
            Check(mage.Class == ClickAimRotationClass.Mage && mage.Damage?.Id == 133 && mage.Supplemental?.Id == 2136, "mage");
            var priest = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(8092, 585, 2050, 589));
            Check(priest.Class == ClickAimRotationClass.Priest && priest.Damage?.Id == 585 && priest.Supplemental?.Id == 8092 && priest.Heal?.Id == 2050 && priest.Dot?.Id == 589, "priest");
            var warlock = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(172, 686));
            Check(warlock.Class == ClickAimRotationClass.Warlock && warlock.Damage?.Id == 686 && warlock.Dot?.Id == 172, "warlock");
            var paladin = ClickAimClassRotation.Build(ClickAimRotationClass.Auto, Bar(635, 35395));
            Check(paladin.Class == ClickAimRotationClass.Paladin && paladin.Heal?.Id == 635 && paladin.Melee?.Id == 35395 && paladin.MeleeIntervalMs > 0, "paladin");
            var dk = ClickAimClassRotation.Build(ClickAimRotationClass.DeathKnight, Bar(6603));
            Check(dk.Class == ClickAimRotationClass.DeathKnight && dk.Damage == null && dk.Melee == null, "runes withheld");
            var unknown = Bar(999999);
            ClickAimCombat.SetRoleOverride(999999, ClickAimSpellRole.Damage);
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Shaman, unknown).Damage?.Id == 999999, "explicit custom override");
            ClickAimCombat.SetRoleOverride(6603, ClickAimSpellRole.Ignore);
            Check(ClickAimCombat.AutoAttack(Bar(6603)) == null, "ignore Attack override");
            ClickAimCombat.ClearRoleOverrides();
            unknown[0].KeyVk = 0;
            Check(ClickAimClassRotation.Build(ClickAimRotationClass.Shaman, unknown).Damage == null, "no unbound slot");
            var times = new Dictionary<string, int>();
            ClickAimClassRotation.Defer(times, "Shocks", 8000, int.MaxValue - 100);
            Check(!ClickAimClassRotation.Ready(times, "Shocks", int.MaxValue - 99) &&
                  !ClickAimClassRotation.Ready(times, "Shocks", unchecked(int.MinValue + 200)) &&
                  ClickAimClassRotation.Ready(times, "Shocks", unchecked(int.MinValue + 8000)), "timer wrap and cross-fight state");
            ClickAimSettings.ResetDefaults();
            Check(ClickAimSettings.RotationClass == ClickAimRotationClass.Auto, "default");
            ClickAimSettings.Apply("RotationClass", "shaman");
            Check(ClickAimSettings.RotationClass == ClickAimRotationClass.Shaman, "setting parse");
            ClickAimSettings.Apply("RotationClass", "999");
            Check(ClickAimSettings.RotationClass == ClickAimRotationClass.Shaman, "invalid setting ignored");
            string originalPath = Styx.Helpers.Logging.ApplicationPath;
            string temporaryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bbuddy-rotation-" + Guid.NewGuid().ToString("N"));
            try
            {
                Styx.Helpers.Logging.ApplicationPath = temporaryPath;
                Check(ClickAimSettings.Save(), "save setting");
                ClickAimSettings.ResetDefaults();
                ClickAimSettings.Load();
                Check(ClickAimSettings.RotationClass == ClickAimRotationClass.Shaman, "persisted setting round-trip");
            }
            finally
            {
                Styx.Helpers.Logging.ApplicationPath = originalPath;
                if (System.IO.Directory.Exists(temporaryPath)) System.IO.Directory.Delete(temporaryPath, true);
            }
            ClickAimSettings.ResetDefaults();
        }
        finally { ClickAimCombat.ClearRoleOverrides(); }
        Console.WriteLine("Rotation fixtures passed (synthetic only).");
    }
}
#endif

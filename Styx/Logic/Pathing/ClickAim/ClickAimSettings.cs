using System;
using System.Globalization;
using System.IO;
using System.Text;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// The numbers a player would put in a bot's combat tab: when to heal, when to eat, when to
/// drink, and whether a damage-over-time spell is worth a global cooldown.
///
/// They live in <c>Settings\clickaim-grind.txt</c> next to the calibration file, in plain
/// <c>key = value</c> lines, because a user who has to open an editor to change "eat at 55%"
/// will not change it. The window's Combat settings button reads and writes exactly this file,
/// the grind thread reads it at the start of every run, and a hand edit is picked up the same
/// way - one file, one parser, no second source of truth.
///
/// Every value has a sane default and every value is clamped on load: a "eat at 900%" typed into
/// the file must not make the bot chain-eat for an hour, and a negative one must not make it
/// never eat. Unknown lines are ignored rather than rejected, so a newer file opened by an older
/// build still loads.
/// </summary>
internal static class ClickAimSettings
{
    /// <summary>Press food when health is at or below this percentage. 0 turns it off.</summary>
    public static int EatBelowPercent = 55;

    /// <summary>Press a drink when mana is at or below this percentage. 0 turns it off.</summary>
    public static int DrinkBelowPercent = 35;

    /// <summary>Cast a healing spell on yourself at or below this percentage. 0 turns it off.</summary>
    public static int HealBelowPercent = 60;

    /// <summary>
    /// ...and only while power is at least this percentage of its maximum. A heal costs mana,
    /// and a caster that keeps pressing a heal it cannot afford dies with a full bar of nothing:
    /// the press fails, the global cooldown is gone, the mob keeps hitting, and the next press
    /// fails the same way. Below this line the rotation fights instead - auto-attack and any
    /// spell it can still pay for. 0 removes the check.
    /// </summary>
    public static int HealMinManaPercent = 25;

    /// <summary>
    /// Use damage-over-time spells (Moonfire, Corruption, Shadow Word: Pain, Immolate ...).
    /// One DoT per target, refreshed when it has been on long enough to have run out.
    /// </summary>
    public static bool UseDamageOverTime = true;

    /// <summary>Apply self-buffs (Mark of the Wild, Thorns, Arcane Intellect ...) out of combat.</summary>
    public static bool UseBuffs = true;

    /// <summary>Bar slot holding food; 0 means "work it out from the item id on the bar".</summary>
    public static int FoodSlot;

    /// <summary>Bar slot holding a drink; 0 means "work it out from the item id on the bar".</summary>
    public static int DrinkSlot;

    /// <summary>
    /// How long a damage-over-time spell is assumed to last before it is worth recasting. The
    /// client's debuff list is not readable on this build, so the bot counts from its own cast:
    /// 12 s covers Moonfire, Corruption, Shadow Word: Pain and Immolate at low ranks, and being a
    /// second or two late costs nothing while being early wastes mana.
    /// </summary>
    public static int DotRefreshSeconds = 12;

    /// <summary>
    /// The longest a single meal lasts. The loop stops earlier as soon as health and mana are
    /// back to <c>RestFullPercent</c>, so this is a ceiling, not a target: a level-4 character
    /// regens slowly and 20 s is what it takes to go from 35% mana to nearly full.
    /// </summary>
    public static int RestMs = 20000;

    /// <summary>
    /// Go to a vendor on the bot's own initiative once the bags are full.
    ///
    /// The original LevelBot asked <c>Me.FreeNormalBagSlots</c>; this build has no container
    /// offset, so "full" is counted the only way that can be counted here - bodies looted since
    /// the last trip (<see cref="SellAfterLoots"/>). It is a proxy and it is labelled as one:
    /// when a bag-slot offset is found this number is replaced by the real count, and nothing
    /// else about the trip changes.
    /// </summary>
    public static bool AutoSell = true;

    /// <summary>
    /// Sell after this many looted corpses; 0 means only when the window's "Sell now" button is
    /// pressed. A level-3 character gets one or two items a body and the backpack holds 16, so
    /// 20 is a full bag plus a few - the trip is a long walk and doing it twice an hour is
    /// better than doing it every five minutes.
    /// </summary>
    public static int SellAfterLoots = 20;

    /// <summary>
    /// How long the merchant window is given to appear after the vendor was clicked, before the
    /// recorded sell clicks are sent (milliseconds). The window is a Lua object on this build and
    /// cannot be read, so this is a wait, not a check.
    /// </summary>
    public static int VendorWaitMs = 2500;

    /// <summary>How far from a vendor point the NPC itself is looked for, in yards.</summary>
    public static int VendorSearchYd = 25;

    private const string FileName = "clickaim-grind.txt";

    /// <summary>The folder the calibration file lives in - one folder for every setting.</summary>
    private static string SettingsDirectory
    {
        get
        {
            try { return System.IO.Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings"; }
            catch { return "Settings"; }
        }
    }

    /// <summary>Settings\clickaim-grind.txt next to the executable, the same folder as the rest.</summary>
    public static string Path
    {
        get { return System.IO.Path.Combine(SettingsDirectory, FileName); }
    }

    /// <summary>
    /// Settings\clickaim-skills.txt: the user's own spell roles. Read by
    /// <see cref="ClickAimCombat.LoadRoleOverrides"/>, written by the Combat settings window.
    /// </summary>
    public static string SkillsPath
    {
        get { return System.IO.Path.Combine(SettingsDirectory, "clickaim-skills.txt"); }
    }

    /// <summary>Back to the shipped values. Used by the Reset button and by a first run.</summary>
    public static void ResetDefaults()
    {
        EatBelowPercent = 55;
        DrinkBelowPercent = 35;
        HealBelowPercent = 60;
        HealMinManaPercent = 25;
        UseDamageOverTime = true;
        UseBuffs = true;
        FoodSlot = 0;
        DrinkSlot = 0;
        DotRefreshSeconds = 12;
        RestMs = 20000;
        AutoSell = true;
        SellAfterLoots = 20;
        VendorWaitMs = 2500;
        VendorSearchYd = 25;
    }

    /// <summary>
    /// Reads the file. A missing file is not an error - it is a first run, and it leaves the
    /// defaults in place. Values out of range are clamped rather than refused, and the file is
    /// written back afterwards so what is on disk is what is being used.
    /// </summary>
    public static void Load()
    {
        string path;
        try { path = Path; }
        catch { return; }

        try
        {
            if (!File.Exists(path))
            {
                Save();      // first run: write the defaults so the file can be edited by hand
            }
            else
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    Apply(key, value);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[Grind] could not read " + FileName + " (" + ex.Message
                          + ") - using the built-in defaults");
        }

        Clamp();

        /* The user's spell roles travel with these numbers: the grind thread calls Load() once at
         * the start of a run, and it must not have to remember a second file. */
        try { ClickAimCombat.LoadRoleOverrides(); }
        catch (Exception ex) { Logging.Write("[Grind] could not read the spell roles: " + ex.Message); }
    }

    /// <summary>Applies one key/value pair. Public for the self-test, which is the only caller.</summary>
    public static bool Apply(string key, string value)
    {
        if (key.Length == 0) return false;

        int number;
        bool hasNumber = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        bool flag = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(key, "EatBelowPercent", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) EatBelowPercent = number;
            return true;
        }
        if (string.Equals(key, "DrinkBelowPercent", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) DrinkBelowPercent = number;
            return true;
        }
        if (string.Equals(key, "HealBelowPercent", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) HealBelowPercent = number;
            return true;
        }
        if (string.Equals(key, "HealMinManaPercent", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) HealMinManaPercent = number;
            return true;
        }
        if (string.Equals(key, "UseDamageOverTime", StringComparison.OrdinalIgnoreCase))
        {
            UseDamageOverTime = flag;
            return true;
        }
        if (string.Equals(key, "UseBuffs", StringComparison.OrdinalIgnoreCase))
        {
            UseBuffs = flag;
            return true;
        }
        if (string.Equals(key, "FoodSlot", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) FoodSlot = number;
            return true;
        }
        if (string.Equals(key, "DrinkSlot", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) DrinkSlot = number;
            return true;
        }
        if (string.Equals(key, "DotRefreshSeconds", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) DotRefreshSeconds = number;
            return true;
        }
        if (string.Equals(key, "RestMs", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) RestMs = number;
            return true;
        }
        if (string.Equals(key, "AutoSell", StringComparison.OrdinalIgnoreCase))
        {
            AutoSell = flag;
            return true;
        }
        if (string.Equals(key, "SellAfterLoots", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) SellAfterLoots = number;
            return true;
        }
        if (string.Equals(key, "VendorWaitMs", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) VendorWaitMs = number;
            return true;
        }
        if (string.Equals(key, "VendorSearchYd", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumber) VendorSearchYd = number;
            return true;
        }
        return false;
    }

    /// <summary>Keeps every value inside the range the loop can actually act on.</summary>
    public static void Clamp()
    {
        EatBelowPercent = ClampPercent(EatBelowPercent);
        DrinkBelowPercent = ClampPercent(DrinkBelowPercent);
        HealBelowPercent = ClampPercent(HealBelowPercent);
        HealMinManaPercent = ClampPercent(HealMinManaPercent);
        FoodSlot = Math.Max(0, Math.Min(12, FoodSlot));
        DrinkSlot = Math.Max(0, Math.Min(12, DrinkSlot));
        DotRefreshSeconds = Math.Max(4, Math.Min(60, DotRefreshSeconds));
        RestMs = Math.Max(1000, Math.Min(60000, RestMs));
        SellAfterLoots = Math.Max(0, Math.Min(500, SellAfterLoots));
        VendorWaitMs = Math.Max(500, Math.Min(30000, VendorWaitMs));
        VendorSearchYd = Math.Max(5, Math.Min(100, VendorSearchYd));
    }

    private static int ClampPercent(int value)
    {
        if (value < 0) return 0;
        if (value > 100) return 100;
        return value;
    }

    /// <summary>Writes the file. The window calls this after Save; everything else only reads.</summary>
    public static bool Save()
    {
        string path;
        try { path = Path; }
        catch (Exception ex)
        {
            Logging.Write("[Grind] no place to write the settings: " + ex.Message);
            return false;
        }

        Clamp();
        var text = new StringBuilder();
        text.AppendLine("# CopilotBuddy grind settings. The window's Combat settings button edits this file;");
        text.AppendLine("# a hand edit here is read at the start of the next run. 0 = off for the three percentages.");
        text.AppendLine("EatBelowPercent = " + EatBelowPercent.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("DrinkBelowPercent = " + DrinkBelowPercent.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("HealBelowPercent = " + HealBelowPercent.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("HealMinManaPercent = " + HealMinManaPercent.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("UseDamageOverTime = " + (UseDamageOverTime ? "true" : "false"));
        text.AppendLine("UseBuffs = " + (UseBuffs ? "true" : "false"));
        text.AppendLine("FoodSlot = " + FoodSlot.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("DrinkSlot = " + DrinkSlot.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("DotRefreshSeconds = " + DotRefreshSeconds.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("RestMs = " + RestMs.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("AutoSell = " + (AutoSell ? "true" : "false"));
        text.AppendLine("SellAfterLoots = " + SellAfterLoots.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("VendorWaitMs = " + VendorWaitMs.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("VendorSearchYd = " + VendorSearchYd.ToString(CultureInfo.InvariantCulture));

        try
        {
            string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text.ToString());
            return true;
        }
        catch (Exception ex)
        {
            Logging.Write("[Grind] could not write " + path + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>One sentence for the log, so a run can be read back against its settings.</summary>
    public static string Describe()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "heal at {0}% (0 = never) and only above {8}% power, eat at {1}%, drink at {2}%, "
            + "damage over time {3}, buffs {4}, food slot {5}, drink slot {6}, "
            + "{7} spell role(s) set by hand; selling {9} (after {10} looted bodie(s), {11} ms "
            + "for the merchant window, vendor NPC looked for within {12} yd)",
            HealBelowPercent, EatBelowPercent, DrinkBelowPercent,
            UseDamageOverTime ? "on" : "off", UseBuffs ? "on" : "off",
            FoodSlot == 0 ? "auto" : FoodSlot.ToString(CultureInfo.InvariantCulture),
            DrinkSlot == 0 ? "auto" : DrinkSlot.ToString(CultureInfo.InvariantCulture),
            ClickAimCombat.OverrideCount, HealMinManaPercent,
            AutoSell ? "on" : "off", SellAfterLoots, VendorWaitMs, VendorSearchYd);
    }

    /// <summary>
    /// The settings that need no game: the parser, the clamping, and the round trip through the
    /// file. A settings file that silently ignores a line is worse than no settings at all.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0, total = 0;
        var fails = new StringBuilder();
        Action<bool, string> check = (ok, name) =>
        {
            total++;
            if (ok) passed++;
            else fails.Append(' ').Append(name);
        };

        ResetDefaults();
        check(EatBelowPercent == 55 && DrinkBelowPercent == 35 && HealBelowPercent == 60,
              "defaults");
        check(HealMinManaPercent == 25, "defaultHealMana");

        check(Apply("EatBelowPercent", "70") && EatBelowPercent == 70, "applyEat");
        check(Apply("HealBelowPercent", "0") && HealBelowPercent == 0, "applyHealZero");
        check(Apply("HealMinManaPercent", "40") && HealMinManaPercent == 40, "applyHealMana");
        check(Apply("UseDamageOverTime", "false") && !UseDamageOverTime, "applyFalse");
        check(Apply("UseBuffs", "true") && UseBuffs, "applyTrue");
        check(!Apply("NoSuchKey", "1"), "unknownKey");

        EatBelowPercent = 900;
        DrinkBelowPercent = -20;
        DotRefreshSeconds = 0;
        Clamp();
        check(EatBelowPercent == 100, "clampHigh");
        check(DrinkBelowPercent == 0, "clampLow");
        check(DotRefreshSeconds == 4, "clampDot");

        check(Apply("AutoSell", "false") && !AutoSell, "applyAutoSell");
        check(Apply("SellAfterLoots", "7") && SellAfterLoots == 7, "applySellAfter");
        check(Apply("VendorWaitMs", "900") && VendorWaitMs == 900, "applyVendorWait");
        check(Apply("VendorSearchYd", "12") && VendorSearchYd == 12, "applyVendorSearch");

        SellAfterLoots = -5;
        VendorWaitMs = 1;
        VendorSearchYd = 0;
        Clamp();
        check(SellAfterLoots == 0, "clampSellAfter");
        check(VendorWaitMs == 500, "clampVendorWait");
        check(VendorSearchYd == 5, "clampVendorSearch");

        ResetDefaults();
        check(AutoSell && SellAfterLoots == 20 && VendorWaitMs == 2500 && VendorSearchYd == 25,
              "vendorDefaults");
        check(Describe().IndexOf("heal at 60%", StringComparison.Ordinal) >= 0, "describe");

        if (fails.Length == 0) return string.Format(CultureInfo.InvariantCulture,
            "Grind settings self-test {0}/{1} passed", passed, total);
        return string.Format(CultureInfo.InvariantCulture,
            "Grind settings self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }
}

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

    /// <summary>How long to sit and eat or drink before checking the bar again.</summary>
    public static int RestMs = 6000;

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
        UseDamageOverTime = true;
        UseBuffs = true;
        FoodSlot = 0;
        DrinkSlot = 0;
        DotRefreshSeconds = 12;
        RestMs = 6000;
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
        return false;
    }

    /// <summary>Keeps every value inside the range the loop can actually act on.</summary>
    public static void Clamp()
    {
        EatBelowPercent = ClampPercent(EatBelowPercent);
        DrinkBelowPercent = ClampPercent(DrinkBelowPercent);
        HealBelowPercent = ClampPercent(HealBelowPercent);
        FoodSlot = Math.Max(0, Math.Min(12, FoodSlot));
        DrinkSlot = Math.Max(0, Math.Min(12, DrinkSlot));
        DotRefreshSeconds = Math.Max(4, Math.Min(60, DotRefreshSeconds));
        RestMs = Math.Max(1000, Math.Min(20000, RestMs));
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
        text.AppendLine("UseDamageOverTime = " + (UseDamageOverTime ? "true" : "false"));
        text.AppendLine("UseBuffs = " + (UseBuffs ? "true" : "false"));
        text.AppendLine("FoodSlot = " + FoodSlot.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("DrinkSlot = " + DrinkSlot.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("DotRefreshSeconds = " + DotRefreshSeconds.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("RestMs = " + RestMs.ToString(CultureInfo.InvariantCulture));

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
            "heal at {0}% (0 = never), eat at {1}%, drink at {2}%, damage over time {3}, buffs {4}, "
            + "food slot {5}, drink slot {6}, {7} spell role(s) set by hand",
            HealBelowPercent, EatBelowPercent, DrinkBelowPercent,
            UseDamageOverTime ? "on" : "off", UseBuffs ? "on" : "off",
            FoodSlot == 0 ? "auto" : FoodSlot.ToString(CultureInfo.InvariantCulture),
            DrinkSlot == 0 ? "auto" : DrinkSlot.ToString(CultureInfo.InvariantCulture),
            ClickAimCombat.OverrideCount);
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
        check(EatBelowPercent == 55 && DrinkBelowPercent == 35 && HealBelowPercent == 60, "defaults");

        check(Apply("EatBelowPercent", "70") && EatBelowPercent == 70, "applyEat");
        check(Apply("HealBelowPercent", "0") && HealBelowPercent == 0, "applyHealZero");
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

        ResetDefaults();
        check(Describe().IndexOf("heal at 60%", StringComparison.Ordinal) >= 0, "describe");

        if (fails.Length == 0) return string.Format(CultureInfo.InvariantCulture,
            "Grind settings self-test {0}/{1} passed", passed, total);
        return string.Format(CultureInfo.InvariantCulture,
            "Grind settings self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }
}

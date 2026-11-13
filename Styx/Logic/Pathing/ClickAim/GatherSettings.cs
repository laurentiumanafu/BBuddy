using System;
using System.Globalization;
using System.IO;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>Manual profession settings: skill offsets have not been found on 70291.</summary>
internal static class GatherSettings
{
    public static bool Mining = true;
    public static bool Herbalism;
    // Opt-in: player confirms they have skinning and a suitable skill/tool.
    public static bool Skinning;
    // Separate opt-in: also try nearby confirmed beasts, regardless of who killed them.
    public static bool SkinNearby;
    // Opt-in: legacy settings files do not enable gathering during Grind.
    public static bool GatherWhileGrinding;
    // Standalone GatherBuddy only. The player may be attacked or killed.
    public static bool IgnoreMonsters;
    // First-bar travel skill: standalone Gather; 70938 Ghost Wolf also in Grind.
    public static bool UseMovementSkill;
    public static int MovementSkillSlot;
    // Opt-in only when no profile is loaded; normal profiles always take precedence.
    public static bool RoamWithoutProfile;
    public static int RoamRadiusYd = 250;
    public static uint MiningSkill = 1;
    public static uint HerbalismSkill = 1;
    private static string PathName => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-gather.txt");

    public static void Load()
    {
        // A missing or malformed setting must not retain a previous opt-in.
        Skinning = false;
        SkinNearby = false;
        IgnoreMonsters = false;
        UseMovementSkill = false;
        MovementSkillSlot = 0;
        // Older files may contain AllowBackgroundCursorMovement. It is ignored:
        // mouse-assisted gathering is now always enabled.
        try
        {
            if (!File.Exists(PathName)) return;
            foreach (string line in File.ReadAllLines(PathName))
            {
                string[] parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                string key = parts[0].Trim(), value = parts[1].Trim();
                if (key == "Mining" && bool.TryParse(value, out bool m)) Mining = m;
                else if (key == "Herbalism" && bool.TryParse(value, out bool h)) Herbalism = h;
                else if (key == "Skinning" && bool.TryParse(value, out bool skin)) Skinning = skin;
                else if (key == "SkinNearby" && bool.TryParse(value, out bool nearby)) SkinNearby = nearby;
                else if (key == "GatherWhileGrinding" && bool.TryParse(value, out bool g)) GatherWhileGrinding = g;
                else if (key == "IgnoreMonsters" && bool.TryParse(value, out bool ignore)) IgnoreMonsters = ignore;
                else if (key == "UseMovementSkill" && bool.TryParse(value, out bool movement)) UseMovementSkill = movement;
                else if (key == "MovementSkillSlot" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) && slot >= 1 && slot <= 12) MovementSkillSlot = slot;
                else if (key == "RoamWithoutProfile" && bool.TryParse(value, out bool r)) RoamWithoutProfile = r;
                else if (key == "RoamRadiusYd" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int radius) && radius >= 100 && radius <= 3000) RoamRadiusYd = radius;
                else if (key == "MiningSkill" && uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ms) && ms <= 1000) MiningSkill = ms;
                else if (key == "HerbalismSkill" && uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint hs) && hs <= 1000) HerbalismSkill = hs;
            }
        }
        catch (Exception ex) { Logging.Write("[Gather] cannot read settings: " + ex.Message); }
    }

    public static bool Save()
    {
        try
        {
            string path = PathName;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            File.WriteAllLines(path, new[] { "Mining=" + Mining, "MiningSkill=" + MiningSkill.ToString(CultureInfo.InvariantCulture),
                "Herbalism=" + Herbalism, "HerbalismSkill=" + HerbalismSkill.ToString(CultureInfo.InvariantCulture),
                "Skinning=" + Skinning,
                "SkinNearby=" + SkinNearby,
                "GatherWhileGrinding=" + GatherWhileGrinding,
                "IgnoreMonsters=" + IgnoreMonsters,
                "UseMovementSkill=" + UseMovementSkill,
                "MovementSkillSlot=" + MovementSkillSlot.ToString(CultureInfo.InvariantCulture),
                "RoamWithoutProfile=" + RoamWithoutProfile,
                "RoamRadiusYd=" + RoamRadiusYd.ToString(CultureInfo.InvariantCulture) });
            return true;
        }
        catch (Exception ex) { Logging.Write("[Gather] cannot save settings: " + ex.Message); return false; }
    }
}

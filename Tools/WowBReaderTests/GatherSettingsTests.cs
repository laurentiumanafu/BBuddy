#if WOWB_READER_TESTS
using System;
using System.IO;
using Styx.Logic.Pathing;

internal static class GatherSettingsTests
{
    internal static void Run()
    {
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string folder = Path.Combine(Path.GetTempPath(), "bbuddy-gather-fixture-" + Guid.NewGuid().ToString("N"));
        bool oldMining = GatherSettings.Mining, oldHerbalism = GatherSettings.Herbalism;
        bool oldSkin = GatherSettings.Skinning, oldNearby = GatherSettings.SkinNearby;
        bool oldIgnore = GatherSettings.IgnoreMonsters, oldMovement = GatherSettings.UseMovementSkill;
        int oldSlot = GatherSettings.MovementSkillSlot;
        uint oldMiningSkill = GatherSettings.MiningSkill, oldHerbSkill = GatherSettings.HerbalismSkill;
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder;
            string settings = Path.Combine(folder, "Settings", "clickaim-gather.txt");
            GatherSettings.SkinNearby = true;
            GatherSettings.IgnoreMonsters = GatherSettings.UseMovementSkill = true;
            GatherSettings.MovementSkillSlot = 10;
            GatherSettings.Load(); // absent file must remain usable
            Check(!GatherSettings.SkinNearby, "nearby skinning defaults off with no file");
            Check(!GatherSettings.IgnoreMonsters && !GatherSettings.UseMovementSkill && GatherSettings.MovementSkillSlot == 0,
                  "new standalone options default off and slot unset without a file");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "Mining=False\nHerbalism=True\nHerbalismSkill=5\nAllowBackgroundCursorMovement=False\n");
            GatherSettings.Load();
            Check(!GatherSettings.Mining && GatherSettings.Herbalism && GatherSettings.HerbalismSkill == 5,
                  "legacy false value does not prevent other settings loading");
            Check(GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.OtherApplication, true),
                  "legacy false value cannot disable automatic background movement");
            Check(!GatherSettings.IgnoreMonsters && !GatherSettings.UseMovementSkill && GatherSettings.MovementSkillSlot == 0,
                  "legacy files do not opt in");
            GatherSettings.SkinNearby = true;
            GatherSettings.IgnoreMonsters = true; GatherSettings.UseMovementSkill = true; GatherSettings.MovementSkillSlot = 10;
            Check(GatherSettings.Save(), "save succeeds");
            string saved = File.ReadAllText(settings);
            Check(!saved.Contains("AllowBackgroundCursorMovement=") && saved.Contains("Herbalism=True") && saved.Contains("HerbalismSkill=5"),
                  "obsolete mouse setting removed on save while profession settings persist");
            GatherSettings.Mining = true; GatherSettings.Herbalism = false; GatherSettings.HerbalismSkill = 1; GatherSettings.SkinNearby = false;
            GatherSettings.IgnoreMonsters = false; GatherSettings.UseMovementSkill = false; GatherSettings.MovementSkillSlot = 0;
            GatherSettings.Load();
            Check(GatherSettings.IgnoreMonsters && GatherSettings.UseMovementSkill && GatherSettings.MovementSkillSlot == 10,
                  "standalone options and movement slot round trip");
            Check(!GatherSettings.Mining && GatherSettings.Herbalism && GatherSettings.HerbalismSkill == 5 && GatherSettings.SkinNearby,
                  "profession and independent nearby skinning settings round trip");
            File.WriteAllText(settings, "MovementSkillSlot=13\nIgnoreMonsters=invalid\nUseMovementSkill=invalid\n");
            GatherSettings.Load();
            Check(!GatherSettings.IgnoreMonsters && !GatherSettings.UseMovementSkill && GatherSettings.MovementSkillSlot == 0,
                  "malformed values fail closed");
            Console.WriteLine("Gather settings synthetic fixtures passed.");
        }
        finally
        {
            GatherSettings.Mining = oldMining; GatherSettings.Herbalism = oldHerbalism;
            GatherSettings.Skinning = oldSkin; GatherSettings.SkinNearby = oldNearby;
            GatherSettings.IgnoreMonsters = oldIgnore; GatherSettings.UseMovementSkill = oldMovement; GatherSettings.MovementSkillSlot = oldSlot;
            GatherSettings.MiningSkill = oldMiningSkill; GatherSettings.HerbalismSkill = oldHerbSkill;
            Styx.Helpers.Logging.ApplicationPath = oldPath;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException("Gather settings fixture failed: " + label);
    }
}
#endif

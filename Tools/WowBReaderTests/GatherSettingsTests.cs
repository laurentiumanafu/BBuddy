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
        uint oldMiningSkill = GatherSettings.MiningSkill, oldHerbSkill = GatherSettings.HerbalismSkill;
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder;
            string settings = Path.Combine(folder, "Settings", "clickaim-gather.txt");
            GatherSettings.Load(); // absent file must remain usable
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "Mining=False\nHerbalism=True\nHerbalismSkill=5\nAllowBackgroundCursorMovement=False\n");
            GatherSettings.Load();
            Check(!GatherSettings.Mining && GatherSettings.Herbalism && GatherSettings.HerbalismSkill == 5,
                  "legacy false value does not prevent other settings loading");
            Check(GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.OtherApplication, true),
                  "legacy false value cannot disable automatic background movement");
            Check(GatherSettings.Save(), "save succeeds");
            string saved = File.ReadAllText(settings);
            Check(!saved.Contains("AllowBackgroundCursorMovement=") && saved.Contains("Herbalism=True") && saved.Contains("HerbalismSkill=5"),
                  "obsolete mouse setting removed on save while profession settings persist");
            GatherSettings.Mining = true; GatherSettings.Herbalism = false; GatherSettings.HerbalismSkill = 1;
            GatherSettings.Load();
            Check(!GatherSettings.Mining && GatherSettings.Herbalism && GatherSettings.HerbalismSkill == 5,
                  "profession settings round trip");
            Console.WriteLine("Gather settings synthetic fixtures passed.");
        }
        finally
        {
            GatherSettings.Mining = oldMining; GatherSettings.Herbalism = oldHerbalism;
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

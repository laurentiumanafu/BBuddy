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
        bool oldValue = GatherSettings.AllowBackgroundCursorMovement;
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder;
            string settings = Path.Combine(folder, "Settings", "clickaim-gather.txt");
            GatherSettings.AllowBackgroundCursorMovement = true;
            GatherSettings.Load();
            Check(!GatherSettings.AllowBackgroundCursorMovement, "missing file defaults to strict cursor guard");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "Mining=True\n");
            GatherSettings.AllowBackgroundCursorMovement = true;
            GatherSettings.Load();
            Check(!GatherSettings.AllowBackgroundCursorMovement, "legacy file defaults to strict cursor guard");
            File.WriteAllText(settings, "AllowBackgroundCursorMovement=True\nAllowBackgroundCursorMovement=invalid\n");
            GatherSettings.Load();
            Check(!GatherSettings.AllowBackgroundCursorMovement, "invalid setting defaults to strict cursor guard");
            GatherSettings.AllowBackgroundCursorMovement = true;
            Check(GatherSettings.Save(), "save succeeds");
            GatherSettings.AllowBackgroundCursorMovement = false;
            GatherSettings.Load();
            Check(GatherSettings.AllowBackgroundCursorMovement, "explicit opt-in round trip");
            GatherSettings.AllowBackgroundCursorMovement = false;
            Check(GatherSettings.Save(), "save opt-out succeeds");
            GatherSettings.AllowBackgroundCursorMovement = true;
            GatherSettings.Load();
            Check(!GatherSettings.AllowBackgroundCursorMovement, "explicit opt-out round trip");
            Console.WriteLine("Gather settings synthetic fixtures passed.");
        }
        finally
        {
            GatherSettings.AllowBackgroundCursorMovement = oldValue;
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

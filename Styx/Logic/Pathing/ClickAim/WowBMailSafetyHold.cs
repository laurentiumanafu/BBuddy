using System;
using System.IO;
using System.Text;

namespace Styx.Logic.Pathing;

// Durable interlock: created BEFORE the compose tab/text/input. An uncertain
// attempt remains held for the rest of the trip and through interruptions;
// observed send progress, confirmed safe hotspot return or stopped manual
// inspection can clear it. Clearing does not verify or reset the game's UI.
internal static class WowBMailSafetyHold
{
    internal static string Path => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-mail-hold.txt");

    internal static bool IsHeld()
    {
        try { File.GetAttributes(Path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; } // inaccessible is unknown, not clear
    }
    internal static bool TryBegin(out string reason)
    {
        try
        {
            string path = Path; string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes("Experimental mail may have partial or uncertain state. Hold clears after confirmed safe return or stopped manual inspection; delivery and game UI are unverified.\n");
            f.Write(bytes, 0, bytes.Length); f.Flush(true);
            reason = "hold recorded"; return true;
        }
        catch (IOException) { reason = "hold exists or cannot be created"; return false; }
        catch (Exception ex) { reason = "cannot create hold: " + ex.Message; return false; }
    }
    internal static bool TryClear(out string reason)
    {
        try { File.Delete(Path); reason = "hold cleared"; return true; }
        catch (Exception ex) { reason = "cannot clear hold: " + ex.Message; return false; }
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace Styx.Logic.Pathing;

// User-recorded coordinates and hover candidate, not proof of UI state. The
// object point depends on camera; always verify the live hover before clicking.
internal static class WowBMailUiCalibration
{
    internal sealed class Result
    {
        public int Version { get; set; } = 1;
        public string Player { get; set; } = "";
        public string MailboxHover { get; set; } = "";
        public uint Map { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public int MailboxX { get; set; }
        public int MailboxY { get; set; }
        public int TabX { get; set; }
        public int TabY { get; set; }
        public int SendX { get; set; }
        public int SendY { get; set; }
    }
    internal static string DefaultPath => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-mail-ui.json");
    internal static bool Valid(Result? c)
    {
        if (c == null || c.Version != 1 || c.Width < 200 || c.Height < 200 || c.Width > 32767 || c.Height > 32767 ||
            string.IsNullOrWhiteSpace(c.Player) || string.IsNullOrWhiteSpace(c.MailboxHover) ||
            !double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.Z)) return false;
        bool inside(int x, int y) => x > 0 && y > 0 && x < c.Width - 1 && y < c.Height - 1;
        if (!inside(c.MailboxX, c.MailboxY) || !inside(c.TabX, c.TabY) || !inside(c.SendX, c.SendY)) return false;
        // A mislabeled duplicate control can accidentally send instead of opening the tab.
        long dx = c.TabX - c.SendX, dy = c.TabY - c.SendY;
        return dx * dx + dy * dy >= 400;
    }
    internal static bool TryLoad(out Result? result)
    {
        result = null;
        try
        {
            var f = new FileInfo(DefaultPath);
            if (!f.Exists || f.Length < 2 || f.Length > 8192) return false;
            var value = JsonSerializer.Deserialize<Result>(File.ReadAllText(DefaultPath));
            if (!Valid(value)) return false;
            result = value; return true;
        }
        catch { return false; }
    }
    internal static void Save(Result result)
    {
        if (!Valid(result)) throw new ArgumentException("Incomplete or invalid mail controls.");
        string path = DefaultPath; string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace Styx.Logic.Pathing;

// User-recorded Release Spirit / Accept Resurrection button positions. When a
// valid calibration exists for the current player and client size, the death
// dialog click uses these coordinates instead of the pattern-matched ones.
// Pattern matching still verifies the dialog state before any click; the
// calibration only chooses where to click. Without a calibration the bot
// keeps its default pattern-matching behaviour.
internal static class WowBDeathButtonCalibration
{
    internal sealed class Result
    {
        public int Version { get; set; } = 1;
        public string Player { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int ReleaseX { get; set; }
        public int ReleaseY { get; set; }
        public int AcceptX { get; set; }
        public int AcceptY { get; set; }
    }

    internal static string DefaultPath => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-death-buttons.json");

    internal static bool Valid(Result? c)
    {
        if (c == null || c.Version != 1 || c.Width < 200 || c.Height < 200 || c.Width > 32767 || c.Height > 32767 ||
            string.IsNullOrWhiteSpace(c.Player)) return false;
        bool inside(int x, int y) => x > 0 && y > 0 && x < c.Width - 1 && y < c.Height - 1;
        if (!inside(c.ReleaseX, c.ReleaseY) || !inside(c.AcceptX, c.AcceptY)) return false;
        // The two buttons are stacked vertically; they must be distinct points.
        long dx = c.ReleaseX - c.AcceptX, dy = c.ReleaseY - c.AcceptY;
        if (dx * dx + dy * dy < 400) return false;
        return true;
    }

    internal static bool TryLoad(string player, int width, int height, out Result? result)
    {
        result = null;
        try
        {
            var f = new FileInfo(DefaultPath);
            if (!f.Exists || f.Length < 2 || f.Length > 8192) return false;
            var value = JsonSerializer.Deserialize<Result>(File.ReadAllText(DefaultPath));
            if (!Valid(value)) return false;
            if (value!.Player != player || value.Width != width || value.Height != height) return false;
            result = value; return true;
        }
        catch { return false; }
    }

    internal static void Save(Result result)
    {
        if (!Valid(result)) throw new ArgumentException("Incomplete or invalid death button positions.");
        string path = DefaultPath; string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Styx.Logic.Pathing;

// Geometry research only. A recorded position does not prove the combined bag
// or mailbox is currently open, that the mapping is correct, or authorize input.
internal static class WowBMailGridCalibration
{
    internal readonly struct Point
    {
        internal readonly double X, Y;
        internal Point(double x, double y) { X = x; Y = y; }
    }
    internal sealed class Bag
    {
        internal int Index;
        internal bool Unknown;
        internal string Identity = "";
        internal int Count;
    }
    internal sealed class SavedBag
    {
        public int Candidate { get; set; }
        public string Identity { get; set; } = "";
        public int Count { get; set; }
        public int FirstOrdinal { get; set; }
        public bool Unknown { get; set; }
    }
    internal sealed class Result
    {
        public int Version { get; set; } = 1;
        public bool ResearchOnly { get; set; } = true;
        public string Player { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Columns { get; set; }
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }
        public double PitchX { get; set; }
        public double PitchY { get; set; }
        public int BackpackFirstOrdinal { get; set; }
        public int BackpackVisibleSlots { get; set; }
        public List<SavedBag> Bags { get; set; } = new List<SavedBag>();
    }

    // Anchor and FarRight are the first and last cell centers of the SAME
    // full row. Below is directly beneath Anchor. Measuring horizontal pitch
    // over the full row reduces the accumulated error near the client edge.
    // Rows before the anchor have negative ordinals. First/last markers are
    // recorded per bag and backpack; their identity remains a candidate.
    internal static bool TryBuild(int width, int height, int columns, string player, IReadOnlyList<Bag> bags,
        int backpackSlots, IReadOnlyDictionary<string, Point> points, out Result? result, out string error)
    {
        result = null; error = "";
        if (width < 200 || height < 200 || columns < 2 || columns > 32 || backpackSlots < 1 || backpackSlots > 64 ||
            string.IsNullOrWhiteSpace(player) || bags == null || bags.Count != 5 || points == null)
        { error = "Invalid client size, column count, backpack size, player or candidate list."; return false; }
        if (!points.TryGetValue("anchor", out Point anchor) || !points.TryGetValue("farRight", out Point farRight) ||
            !points.TryGetValue("below", out Point below))
        { error = "Record the first and last cells of the same full row, and the cell immediately below the first."; return false; }
        bool InClient(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X > 0 && p.Y > 0 && p.X < width - 1 && p.Y < height - 1;
        if (!InClient(anchor) || !InClient(farRight) || !InClient(below))
        { error = "A reference point is outside the " + width + "×" + height + " game client; refocus the game and re-record it."; return false; }
        double dx = (farRight.X - anchor.X) / (columns - 1), dy = below.Y - anchor.Y;
        string dimensions = string.Format(CultureInfo.InvariantCulture,
            " Client={0}×{1}, columns={2}, first=({3:F0},{4:F0}), last=({5:F0},{6:F0}), below=({7:F0},{8:F0}), pitch=({9:F1},{10:F1}) px.",
            width, height, columns, anchor.X, anchor.Y, farRight.X, farRight.Y, below.X, below.Y, dx, dy);
        if (dx < 10 || dx > 200)
        { error = "Horizontal spacing is outside 10–200 px. Record the first and last cells of one full row and verify the column count." + dimensions; return false; }
        if (dy < 10 || dy > 200)
        { error = "Vertical spacing is outside 10–200 px. Record the cell directly below the first cell." + dimensions; return false; }
        if (Math.Abs(farRight.Y - anchor.Y) > dy * 0.30)
        { error = "The first and last reference cells are not on the same row. Re-record them at their centers." + dimensions; return false; }
        if (Math.Abs(below.X - anchor.X) > dx * 0.30)
        { error = "The below reference cell is not in the first column. Re-record it directly below the first cell." + dimensions; return false; }
        bool TryOrdinal(Point p, out int ordinal)
        {
            ordinal = 0;
            if (!InClient(p)) return false;
            double colRaw = (p.X - anchor.X) / dx, rowRaw = (p.Y - anchor.Y) / dy;
            if (!double.IsFinite(colRaw) || !double.IsFinite(rowRaw)) return false;
            int col = (int)Math.Round(colRaw), row = (int)Math.Round(rowRaw);
            if (col < 0 || col >= columns || Math.Abs(colRaw - col) > 0.23 || Math.Abs(rowRaw - row) > 0.23 ||
                row < -100 || row > 100) return false;
            ordinal = row * columns + col;
            return true;
        }
        string rangeError = "";
        bool TryRange(string prefix, int count, out int first)
        {
            first = 0;
            if (!points.TryGetValue(prefix + ".first", out Point start) || !points.TryGetValue(prefix + ".last", out Point end))
            { rangeError = prefix + ": record both first and last markers."; return false; }
            if (!TryOrdinal(start, out first))
            { rangeError = string.Format(CultureInfo.InvariantCulture, "{0}: first marker ({1:F0},{2:F0}) is off the measured grid or outside the client.", prefix, start.X, start.Y); return false; }
            if (!TryOrdinal(end, out int last))
            { rangeError = string.Format(CultureInfo.InvariantCulture, "{0}: last marker ({1:F0},{2:F0}) is off the measured grid or outside the client.", prefix, end.X, end.Y); return false; }
            if (last - first != count - 1)
            { rangeError = prefix + ": markers span " + (last - first + 1) + " row-major cells, expected " + count + ". Check first/last and column count."; return false; }
            return true;
        }
        if (!TryRange("backpack", backpackSlots, out int backpackFirst))
        { error = rangeError; return false; }
        var used = new HashSet<int>();
        for (int i = 0; i < backpackSlots; i++) used.Add(backpackFirst + i);
        var seenIndices = new HashSet<int>(); var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = new List<SavedBag>();
        foreach (Bag bag in bags)
        {
            if (bag == null || bag.Index < 0 || bag.Index > 4 || !seenIndices.Add(bag.Index))
            { error = "Candidate bag list is invalid or duplicated."; return false; }
            if (bag.Unknown)
            {
                if (bag.Count != 0 || bag.Identity.Length != 0) { error = "Unknown candidate has unexpected data."; return false; }
                saved.Add(new SavedBag { Candidate = bag.Index, Unknown = true }); continue;
            }
            if (string.IsNullOrWhiteSpace(bag.Identity) || !seenIds.Add(bag.Identity) || bag.Count < 1 || bag.Count > 64)
            { error = "Candidate " + bag.Index + " has an invalid or duplicated identity or size."; return false; }
            if (!TryRange("bag" + bag.Index, bag.Count, out int first))
            { error = rangeError; return false; }
            for (int i = 0; i < bag.Count; i++)
            {
                int n = first + i;
                if (!used.Add(n)) { error = "A bag overlaps the backpack or another bag."; return false; }
                int row = (int)Math.Floor(n / (double)columns), col = n - row * columns;
                if (!InClient(new Point(anchor.X + col * dx, anchor.Y + row * dy)))
                { error = "A calculated bag slot is outside the client."; return false; }
            }
            saved.Add(new SavedBag { Candidate = bag.Index, Identity = bag.Identity, Count = bag.Count, FirstOrdinal = first });
        }
        if (seenIndices.Count != 5) { error = "All candidate identities must be accounted for."; return false; }
        for (int i = 0; i < backpackSlots; i++)
        {
            int n = backpackFirst + i, row = (int)Math.Floor(n / (double)columns), col = n - row * columns;
            if (!InClient(new Point(anchor.X + col * dx, anchor.Y + row * dy)))
            { error = "A calculated backpack slot is outside the client."; return false; }
        }
        result = new Result { Player = player, Width = width, Height = height, Columns = columns,
            AnchorX = anchor.X, AnchorY = anchor.Y, PitchX = dx, PitchY = dy,
            BackpackFirstOrdinal = backpackFirst, BackpackVisibleSlots = backpackSlots, Bags = saved };
        return true;
    }

    internal static string DefaultPath => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-mail-grid.json");
    internal static bool Save(Result result, out string error)
    {
        error = "";
        string tmp = "";
        try
        {
            string path = DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception ex) { error = ex.Message; try { if (tmp.Length != 0 && File.Exists(tmp)) File.Delete(tmp); } catch { } return false; }
    }
}

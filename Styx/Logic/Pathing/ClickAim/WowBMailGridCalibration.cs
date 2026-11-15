using System;
using System.Collections.Generic;
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

    // Anchor is the leftmost cell of any full row; Right and Below are the
    // immediately adjacent cells. Rows before the anchor have negative ordinals.
    // First/last markers are recorded individually per bag and backpack, not
    // guessed from inventory order. All size/identity observations remain candidate.
    internal static bool TryBuild(int width, int height, int columns, string player, IReadOnlyList<Bag> bags,
        int backpackSlots, IReadOnlyDictionary<string, Point> points, out Result? result, out string error)
    {
        result = null; error = "";
        if (width < 200 || height < 200 || columns < 2 || columns > 32 || backpackSlots < 1 || backpackSlots > 64 ||
            string.IsNullOrWhiteSpace(player) || bags == null || bags.Count != 5 || points == null)
        { error = "Invalid client size, column count, backpack size, player or candidate list."; return false; }
        if (!points.TryGetValue("anchor", out Point anchor) || !points.TryGetValue("right", out Point right) ||
            !points.TryGetValue("below", out Point below))
        { error = "Record the reference, right-neighbor and below-neighbor cell centers."; return false; }
        bool InClient(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X > 0 && p.Y > 0 && p.X < width - 1 && p.Y < height - 1;
        if (!InClient(anchor) || !InClient(right) || !InClient(below)) { error = "Grid reference points are outside the client."; return false; }
        double dx = right.X - anchor.X, dy = below.Y - anchor.Y;
        if (dx < 10 || dy < 10 || dx > 200 || dy > 200 || Math.Abs(right.Y - anchor.Y) > 5 || Math.Abs(below.X - anchor.X) > 5 ||
            anchor.X + (columns - 1) * dx >= width - 1)
        { error = "Grid reference points are not adjacent or the column count extends past the client."; return false; }
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
        bool TryRange(string prefix, int count, out int first)
        {
            first = 0;
            return points.TryGetValue(prefix + ".first", out Point start) && points.TryGetValue(prefix + ".last", out Point end) &&
                   TryOrdinal(start, out first) && TryOrdinal(end, out int last) && last - first == count - 1;
        }
        if (!TryRange("backpack", backpackSlots, out int backpackFirst))
        { error = "Backpack first/last markers do not match its visible slot count and grid."; return false; }
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
            if (string.IsNullOrWhiteSpace(bag.Identity) || !seenIds.Add(bag.Identity) || bag.Count < 1 || bag.Count > 64 ||
                !TryRange("bag" + bag.Index, bag.Count, out int first))
            { error = "Candidate " + bag.Index + " has an invalid identity, size or first/last markers."; return false; }
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

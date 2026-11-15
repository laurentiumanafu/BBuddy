using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Single user-supervised attachment experiment only. A stored grid and raw
// GUIDs do not establish the active UI, item identity, mailability or success.
internal static class WowBMailGridLookup
{
    internal sealed class Bag
    {
        internal int Candidate;
        internal bool Unknown;
        internal WowBGuid Identity;
        internal WowBGuid[] Slots = Array.Empty<WowBGuid>();
    }
    internal readonly struct Match
    {
        internal readonly int Candidate, Slot;
        internal readonly WowBGuid Item;
        internal Match(int candidate, int slot, WowBGuid item) { Candidate = candidate; Slot = slot; Item = item; }
    }

    private static WowBMailGridCalibration.Point Center(WowBMailGridCalibration.Result grid, int ordinal)
    {
        int row = (int)Math.Floor(ordinal / (double)grid.Columns);
        int col = ordinal - row * grid.Columns;
        return new WowBMailGridCalibration.Point(grid.AnchorX + col * grid.PitchX, grid.AnchorY + row * grid.PitchY);
    }

    internal static bool Validate(WowBMailGridCalibration.Result? g, string player, int width, int height, IReadOnlyList<Bag> live)
    {
        if (g == null || g.Version != 1 || !g.ResearchOnly || g.Player != player || string.IsNullOrWhiteSpace(player) ||
            g.Width != width || g.Height != height || g.Width < 200 || g.Height < 200 || g.Columns < 2 || g.Columns > 32 ||
            !double.IsFinite(g.AnchorX) || !double.IsFinite(g.AnchorY) || !double.IsFinite(g.PitchX) || !double.IsFinite(g.PitchY) ||
            g.PitchX < 10 || g.PitchX > 200 || g.PitchY < 10 || g.PitchY > 200 ||
            g.BackpackVisibleSlots < 1 || g.BackpackVisibleSlots > 64 || Math.Abs((long)g.BackpackFirstOrdinal) > 3200 ||
            g.Bags == null || g.Bags.Count != 5 || live == null || live.Count != 5) return false;
        var points = new Dictionary<string, WowBMailGridCalibration.Point> {
            ["anchor"] = new(g.AnchorX, g.AnchorY), ["farRight"] = new(g.AnchorX + (g.Columns - 1) * g.PitchX, g.AnchorY),
            ["below"] = new(g.AnchorX, g.AnchorY + g.PitchY),
            ["backpack.first"] = Center(g, g.BackpackFirstOrdinal),
            ["backpack.last"] = Center(g, g.BackpackFirstOrdinal + g.BackpackVisibleSlots - 1)
        };
        var candidates = new List<WowBMailGridCalibration.Bag>();
        var seen = new HashSet<int>();
        foreach (var saved in g.Bags)
        {
            if (saved == null || saved.Candidate < 0 || saved.Candidate > 4 || !seen.Add(saved.Candidate) ||
                Math.Abs((long)saved.FirstOrdinal) > 3200) return false;
            Bag? actual = null;
            foreach (var b in live) if (b != null && b.Candidate == saved.Candidate) { if (actual != null) return false; actual = b; }
            if (actual == null || actual.Slots == null || actual.Unknown != saved.Unknown ||
                (saved.Unknown && (!actual.Identity.IsEmpty || actual.Slots.Length != 0 || saved.Identity != "" || saved.Count != 0 || saved.FirstOrdinal != 0)) ||
                (!saved.Unknown && (actual.Identity.IsEmpty || !string.Equals(saved.Identity, actual.Identity.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                    saved.Count != actual.Slots.Length || saved.Count < 1 || saved.Count > 64))) return false;
            candidates.Add(new WowBMailGridCalibration.Bag { Index = saved.Candidate, Identity = saved.Identity, Unknown = saved.Unknown, Count = saved.Count });
            if (!saved.Unknown)
            {
                points["bag" + saved.Candidate + ".first"] = Center(g, saved.FirstOrdinal);
                points["bag" + saved.Candidate + ".last"] = Center(g, saved.FirstOrdinal + saved.Count - 1);
            }
        }
        return WowBMailGridCalibration.TryBuild(g.Width, g.Height, g.Columns, player, candidates,
            g.BackpackVisibleSlots, points, out _, out _);
    }

    internal static bool TryLoad(string path, string player, int width, int height, IReadOnlyList<Bag> live, out WowBMailGridCalibration.Result? grid)
    {
        grid = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 65536) return false;
            var candidate = JsonSerializer.Deserialize<WowBMailGridCalibration.Result>(File.ReadAllText(path));
            if (!Validate(candidate, player, width, height, live)) return false;
            grid = candidate; return true;
        }
        catch { return false; }
    }

    internal static bool Same(IReadOnlyList<Bag> a, IReadOnlyList<Bag> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            Bag x = a[i], y = b[i];
            if (x == null || y == null || x.Candidate != y.Candidate || x.Unknown != y.Unknown ||
                x.Identity != y.Identity || x.Slots == null || y.Slots == null || x.Slots.Length != y.Slots.Length) return false;
            for (int j = 0; j < x.Slots.Length; j++) if (x.Slots[j] != y.Slots[j]) return false;
        }
        return true;
    }

    internal readonly struct PreviewSlot
    {
        internal readonly int Candidate, Slot;
        internal readonly WowBGuid Item;
        internal readonly double X, Y;
        internal PreviewSlot(int candidate, int slot, WowBGuid item, double x, double y)
        { Candidate = candidate; Slot = slot; Item = item; X = x; Y = y; }
    }

    // A partial research preview of nonzero entries in candidates 0–3 only.
    // Unknown candidates (especially 4) and the unobserved backpack mean this
    // is never a complete-inventory or mailable-items verdict.
    internal static bool TryPreview(WowBMailGridCalibration.Result grid, string player, int width, int height,
        IReadOnlyList<Bag> bags, out List<PreviewSlot> preview, out int observedCount, out bool unknownPrimary, out bool candidate4Unknown)
    {
        preview = new List<PreviewSlot>(); observedCount = 0; unknownPrimary = true; candidate4Unknown = true;
        if (!Validate(grid, player, width, height, bags)) return false;
        var seen = new HashSet<WowBGuid>();
        foreach (Bag bag in bags)
            foreach (WowBGuid item in bag.Slots)
                if (!item.IsEmpty && !seen.Add(item)) return false;
        // Candidate 4's zero identity is unknown; even nonzero identity does
        // not yet establish whether it is another equipped bag.
        unknownPrimary = false;
        foreach (Bag bag in bags) if (bag.Candidate == 4) candidate4Unknown = bag.Unknown;
        for (int candidate = 0; candidate <= 3; candidate++)
        {
            Bag? bag = null; WowBMailGridCalibration.SavedBag? saved = null;
            foreach (Bag item in bags) if (item.Candidate == candidate) bag = item;
            foreach (var item in grid.Bags) if (item.Candidate == candidate) saved = item;
            if (bag == null || saved == null) return false;
            if (bag.Unknown) { unknownPrimary = true; continue; }
            for (int index = 0; index < bag.Slots.Length; index++)
            {
                WowBGuid item = bag.Slots[index];
                if (item.IsEmpty) continue;
                observedCount++;
                if (preview.Count == WowBMailBatchPolicy.BatchLimit) continue;
                var p = Center(grid, saved.FirstOrdinal + index);
                preview.Add(new PreviewSlot(candidate, index, item, p.X, p.Y));
            }
        }
        return true;
    }

    // Only user-established candidates 0–3; never the backpack, empty raw
    // slot, or candidate 4. Nonzero is an observation, not mailable proof.
    internal static bool TryMatch(WowBMailGridCalibration.Result grid, IReadOnlyList<Bag> bags, double x, double y, out Match match)
    {
        match = default;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        bool found = false;
        foreach (var saved in grid.Bags)
        {
            if (saved.Unknown || saved.Candidate < 0 || saved.Candidate > 3) continue;
            foreach (var bag in bags)
            {
                if (bag.Candidate != saved.Candidate || bag.Unknown) continue;
                for (int i = 0; i < bag.Slots.Length; i++)
                {
                    var p = Center(grid, saved.FirstOrdinal + i);
                    if (Math.Abs(x - p.X) > grid.PitchX * 0.23 || Math.Abs(y - p.Y) > grid.PitchY * 0.23) continue;
                    if (bag.Slots[i].IsEmpty || found) return false;
                    match = new Match(saved.Candidate, i, bag.Slots[i]); found = true;
                }
            }
        }
        return found;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Explicit user classifications of individual 70755 bag instances, scoped to
// a character. A size, index or GUID bit pattern does not establish bag family.
internal enum WowBBagKind { Unknown, Ordinary, Specialty }
internal static class WowBBagClassification
{
    private static readonly object Gate = new object();
    private static string PathName => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-bag-kinds-70755.txt");
    private static string Key(WowBGuid player, WowBGuid bag) => player + "|" + bag;

    private static bool ValidGuid(string text)
    {
        return text.Length == 34 && text.StartsWith("0x", StringComparison.Ordinal) &&
            ulong.TryParse(text.Substring(2, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hi) &&
            ulong.TryParse(text.Substring(18, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong lo) &&
            (hi != 0 || lo != 0);
    }

    private static bool Read(out Dictionary<string, WowBBagKind> kinds)
    {
        kinds = new Dictionary<string, WowBBagKind>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(PathName)) return true;
            string[] lines = File.ReadAllLines(PathName);
            if (lines.Length > 512) return false;
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] fields = line.Split('|');
                if (fields.Length != 3 || !ValidGuid(fields[0]) || !ValidGuid(fields[1]) ||
                    !Enum.TryParse(fields[2], false, out WowBBagKind kind) ||
                    (kind != WowBBagKind.Ordinary && kind != WowBBagKind.Specialty) ||
                    !kinds.TryAdd(fields[0] + "|" + fields[1], kind)) return false;
            }
            return true;
        }
        catch { kinds.Clear(); return false; }
    }

    internal static bool TryGet(WowBGuid player, WowBGuid bag, out WowBBagKind kind)
    {
        kind = WowBBagKind.Unknown;
        lock (Gate)
        {
            if (player.IsEmpty || bag.IsEmpty || !Read(out var kinds)) return false;
            return kinds.TryGetValue(Key(player, bag), out kind) && kind != WowBBagKind.Unknown;
        }
    }

    internal static bool Save(WowBGuid player, WowBGuid bag, WowBBagKind kind, out string message)
    {
        message = "";
        if (player.IsEmpty || bag.IsEmpty || (kind != WowBBagKind.Ordinary && kind != WowBBagKind.Specialty))
        { message = "Invalid bag or category."; return false; }
        lock (Gate)
        {
            if (!Read(out var kinds)) { message = "Cannot read bag classifications; no changes saved."; return false; }
            kinds[Key(player, bag)] = kind;
            try
            {
                string path = PathName;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var lines = new List<string>();
                    foreach (var pair in kinds) lines.Add(pair.Key + "|" + pair.Value);
                    lines.Sort(StringComparer.Ordinal);
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return true;
            }
            catch (Exception ex) { message = "Cannot save bag classification: " + ex.Message; return false; }
        }
    }
}

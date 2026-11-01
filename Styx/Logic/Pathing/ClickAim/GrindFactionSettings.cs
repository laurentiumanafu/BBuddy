using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Styx.Logic.Pathing;

// Explicit user-supplied template IDs, not a live reaction/attackability verdict.
internal sealed class GrindFactionRules
{
    public bool Enabled { get; }
    public string KeyText { get; }
    private readonly HashSet<uint> _keys;

    private GrindFactionRules(bool enabled, HashSet<uint> keys)
    {
        Enabled = enabled;
        _keys = keys;
        var sorted = new List<uint>(keys);
        sorted.Sort();
        KeyText = string.Join(", ", sorted);
    }

    public bool Allows(bool readable, uint key)
        => !Enabled || (readable && key != 0 && _keys.Contains(key));

    public static bool TryCreate(bool enabled, string? text, out GrindFactionRules rules, out string error)
    {
        rules = new GrindFactionRules(false, new HashSet<uint>());
        error = "";
        if (text == null || text.Length > 2048) { error = "Faction list is too long or missing."; return false; }
        var keys = new HashSet<uint>();
        string[] parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
            { error = "Use positive decimal faction-template IDs separated by commas or spaces."; return false; }
            keys.Add(id);
            if (keys.Count > 128) { error = "At most 128 different IDs are supported."; return false; }
        }
        if (enabled && keys.Count == 0) { error = "Add at least one faction-template ID before enabling the filter."; return false; }
        rules = new GrindFactionRules(enabled, keys);
        return true;
    }

    public static GrindFactionRules Disabled() => new GrindFactionRules(false, new HashSet<uint>());
}

internal static class GrindFactionSettings
{
    // Immutable policy activated at Grind start; opening or saving the dialog never changes a running policy.
    public static GrindFactionRules Rules { get; private set; } = GrindFactionRules.Disabled();
    public static string PathName => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-factions.txt");

    public static bool TryLoad(out GrindFactionRules rules, out string error)
    {
        error = "";
        rules = GrindFactionRules.Disabled();
        try
        {
            string[] lines;
            try { lines = File.ReadAllLines(PathName); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            bool enabled = false, seenEnabled = false, seenKeys = false;
            string keys = "";
            foreach (string line in lines)
            {
                string[] pair = line.Split('=', 2);
                if (pair.Length != 2) { error = "Malformed faction settings line."; return false; }
                string name = pair[0].Trim();
                if (name == "Enabled")
                {
                    if (seenEnabled || !bool.TryParse(pair[1].Trim(), out enabled))
                    { error = "Invalid or duplicate Enabled setting."; return false; }
                    seenEnabled = true;
                }
                else if (name == "Keys")
                {
                    if (seenKeys) { error = "Duplicate Keys setting."; return false; }
                    seenKeys = true; keys = pair[1].Trim();
                }
            }
            if (!seenEnabled || !seenKeys) { error = "Faction settings are incomplete."; return false; }
            if (!GrindFactionRules.TryCreate(enabled, keys, out GrindFactionRules parsed, out error)) return false;
            rules = parsed;
            return true;
        }
        catch (Exception ex) { error = "Cannot read faction settings: " + ex.Message; return false; }
    }

    public static void Activate(GrindFactionRules rules) => Rules = rules;

    public static bool Save(bool enabled, string text, out string error)
    {
        if (!GrindFactionRules.TryCreate(enabled, text, out GrindFactionRules parsed, out error)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName) ?? ".");
            File.WriteAllLines(PathName, new[] { "Enabled=" + parsed.Enabled, "Keys=" + parsed.KeyText });
            return true;
        }
        catch (Exception ex) { error = "Cannot save faction settings: " + ex.Message; return false; }
    }
}

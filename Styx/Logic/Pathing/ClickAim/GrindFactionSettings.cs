using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Styx.Logic.Pathing;

internal enum GrindFactionMode { Off, Allow, Blacklist }

// Explicit user-supplied template IDs, not a live reaction/attackability verdict.
internal sealed class GrindFactionRules
{
    public GrindFactionMode Mode { get; }
    public bool Enabled => Mode != GrindFactionMode.Off;
    public string AllowedKeyText { get; }
    public string BlacklistKeyText { get; }
    public string KeyText => Mode == GrindFactionMode.Blacklist ? BlacklistKeyText : AllowedKeyText;
    private readonly HashSet<uint> _allowed, _blacklisted;

    private GrindFactionRules(GrindFactionMode mode, HashSet<uint> allowed, HashSet<uint> blacklisted)
    {
        Mode = mode; _allowed = allowed; _blacklisted = blacklisted;
        AllowedKeyText = Format(allowed); BlacklistKeyText = Format(blacklisted);
    }
    private static string Format(HashSet<uint> keys)
    { var sorted = new List<uint>(keys); sorted.Sort(); return string.Join(", ", sorted); }

    public bool Allows(bool readable, uint key)
        => !Enabled || (readable && key != 0 &&
            (Mode == GrindFactionMode.Allow ? _allowed.Contains(key) : !_blacklisted.Contains(key)));

    private static bool Parse(string? text, out HashSet<uint> keys, out string error)
    {
        keys = new HashSet<uint>(); error = "";
        if (text == null || text.Length > 2048) { error = "Faction list is too long or missing."; return false; }
        foreach (string part in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
            { error = "Use positive decimal faction-template IDs separated by commas or spaces."; return false; }
            keys.Add(id);
            if (keys.Count > 128) { error = "At most 128 different IDs per list are supported."; return false; }
        }
        return true;
    }
    public static bool TryCreate(GrindFactionMode mode, string? allowedText, string? blacklistText,
                                 out GrindFactionRules rules, out string error)
    {
        rules = Disabled(); error = "";
        if (mode != GrindFactionMode.Off && mode != GrindFactionMode.Allow && mode != GrindFactionMode.Blacklist)
        { error = "Invalid faction filter mode."; return false; }
        if (!Parse(allowedText, out HashSet<uint> allowed, out error) ||
            !Parse(blacklistText, out HashSet<uint> blacklisted, out error)) return false;
        if ((mode == GrindFactionMode.Allow && allowed.Count == 0) ||
            (mode == GrindFactionMode.Blacklist && blacklisted.Count == 0))
        { error = "The selected faction filter needs at least one ID. Choose Off to disable it."; return false; }
        rules = new GrindFactionRules(mode, allowed, blacklisted); return true;
    }
    // Legacy API and old settings files contained only an enabled allowlist.
    public static bool TryCreate(bool enabled, string? text, out GrindFactionRules rules, out string error)
        => TryCreate(enabled ? GrindFactionMode.Allow : GrindFactionMode.Off, text, "", out rules, out error);
    public static GrindFactionRules Disabled() => new GrindFactionRules(GrindFactionMode.Off, new HashSet<uint>(), new HashSet<uint>());
}

internal static class GrindFactionSettings
{
    // Immutable policy activated at Grind start; saving never changes a running policy.
    public static GrindFactionRules Rules { get; private set; } = GrindFactionRules.Disabled();
    public static string PathName => Path.Combine(Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-factions.txt");

    public static bool TryLoad(out GrindFactionRules rules, out string error)
    {
        rules = GrindFactionRules.Disabled(); error = "";
        try
        {
            string[] lines;
            try { lines = File.ReadAllLines(PathName); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            bool enabled = false, seenEnabled = false, seenKeys = false, seenMode = false, seenBlacklist = false;
            string keys = "", blacklist = ""; GrindFactionMode mode = GrindFactionMode.Off;
            foreach (string line in lines)
            {
                string[] pair = line.Split('=', 2);
                if (pair.Length != 2) { error = "Malformed faction settings line."; return false; }
                string name = pair[0].Trim(), value = pair[1].Trim();
                if (name == "Enabled")
                { if (seenEnabled || !bool.TryParse(value, out enabled)) { error = "Invalid or duplicate Enabled setting."; return false; } seenEnabled = true; }
                else if (name == "Keys")
                { if (seenKeys) { error = "Duplicate Keys setting."; return false; } seenKeys = true; keys = value; }
                else if (name == "BlacklistKeys")
                { if (seenBlacklist) { error = "Duplicate BlacklistKeys setting."; return false; } seenBlacklist = true; blacklist = value; }
                else if (name == "Mode")
                {
                    if (seenMode || !Enum.TryParse(value, false, out mode) || (mode != GrindFactionMode.Off && mode != GrindFactionMode.Allow && mode != GrindFactionMode.Blacklist))
                    { error = "Invalid or duplicate Mode setting."; return false; } seenMode = true;
                }
            }
            if (!seenKeys) { error = "Faction settings are incomplete."; return false; }
            if (seenMode)
            {
                if (seenEnabled || !seenBlacklist) { error = "Faction settings modes are ambiguous or incomplete."; return false; }
                return GrindFactionRules.TryCreate(mode, keys, blacklist, out rules, out error);
            }
            if (!seenEnabled || seenBlacklist) { error = "Faction settings are incomplete."; return false; }
            // Legacy unchecked populated allowlists intentionally remained active.
            if (!GrindFactionRules.TryCreate(enabled, keys, out GrindFactionRules parsed, out error)) return false;
            if (!enabled && parsed.AllowedKeyText.Length > 0)
                return GrindFactionRules.TryCreate(true, keys, out rules, out error);
            rules = parsed; return true;
        }
        catch (Exception ex) { error = "Cannot read faction settings: " + ex.Message; return false; }
    }
    public static void Activate(GrindFactionRules rules) => Rules = rules;
    public static bool Save(GrindFactionMode mode, string allowed, string blacklist, out string error)
    {
        if (!GrindFactionRules.TryCreate(mode, allowed, blacklist, out GrindFactionRules parsed, out error)) return false;
        try
        {
            string path = PathName;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[] { "Mode=" + parsed.Mode, "Keys=" + parsed.AllowedKeyText, "BlacklistKeys=" + parsed.BlacklistKeyText });
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        }
        catch (Exception ex) { error = "Cannot save faction settings: " + ex.Message; return false; }
    }
    public static bool SaveList(string text, out string error)
        => Save(string.IsNullOrWhiteSpace(text) ? GrindFactionMode.Off : GrindFactionMode.Allow, text, "", out error);
}

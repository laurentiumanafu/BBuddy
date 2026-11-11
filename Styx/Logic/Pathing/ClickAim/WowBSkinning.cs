using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Explicitly user-confirmed beast entry candidates, shared by the 70707 family.
/// The GUID entry bits are a candidate on these clients, not a verified creature-type
/// field. A confirmation applies to other spawns with the same candidate entry.
/// No entry, skill, corpse lootability or skinning success is inferred here.
/// </summary>
internal static class WowBSkinning
{
    private static readonly object Gate = new object();
    private static readonly HashSet<uint> Beast70291 = new HashSet<uint>();
    private static readonly HashSet<uint> Beast70707 = new HashSet<uint>();
    private static string PathName => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(ClickAimCalibration.DefaultPath) ?? "Settings", "clickaim-beasts.txt");

    internal static uint EntryCandidate(WowBGuid guid) => (uint)((guid.High >> 6) & 0x7FFFFFUL);
    private static HashSet<uint> Entries(bool experimental70707) => experimental70707 ? Beast70707 : Beast70291;
    internal static bool IsConfirmed(bool experimental70707, WowBGuid guid)
    {
        lock (Gate) return !guid.IsEmpty && EntryCandidate(guid) != 0 && Entries(experimental70707).Contains(EntryCandidate(guid));
    }
    internal static int Count(bool experimental70707) { lock (Gate) return Entries(experimental70707).Count; }

    internal static void Load()
    {
        lock (Gate) LoadLocked();
    }

    private static void LoadLocked()
    {
        Beast70291.Clear(); Beast70707.Clear();
        try
        {
            if (!File.Exists(PathName)) return;
            foreach (string line in File.ReadAllLines(PathName))
            {
                string[] pair = line.Split('=', 2);
                if (pair.Length != 2 || !uint.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint entry)
                    || entry == 0 || entry > 0x7FFFFF) continue;
                if (pair[0].Trim() == "70291") Beast70291.Add(entry);
                else if (pair[0].Trim() == "70707") Beast70707.Add(entry);
            }
        }
        catch (Exception ex) { Beast70291.Clear(); Beast70707.Clear(); Logging.Write("[Skin] could not read confirmed beasts: " + ex.Message); }
    }

    internal static bool RememberSelected(out string message)
    {
        message = "";
        if (ClickAimUi.Running || ClickAimUi.Paused) { message = "Stop the bot first."; return false; }
        WowBReader? reader = ObjectManager.WowB;
        if (reader == null) { message = "Attach to wowb.exe first."; return false; }
        if (!WowBKnownMerchants.CaptureSelected(out WowBGuid guid, out _, out message)) return false;
        if (ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused)
        { message = "Client or bot state changed; nothing saved."; return false; }
        uint entry = EntryCandidate(guid);
        if (entry == 0) { message = "The selected NPC has no usable entry candidate."; return false; }
        lock (Gate)
        {
            if (ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused)
            { message = "Client or bot state changed; nothing saved."; return false; }
            HashSet<uint> set = Entries(reader.IsExperimental70707);
            bool added = set.Add(entry);
            if (!Save())
            {
                if (added) set.Remove(entry);
                message = "Could not save the beast list.";
                return false;
            }
        }
        message = "Saved beast entry candidate " + entry.ToString(CultureInfo.InvariantCulture)
            + " for " + (reader.IsExperimental70707 ? "70707/70755/70790/70862/70888" : "70291")
            + ". Other spawns with this entry will be treated as the same beast type.";
        return true;
    }

    internal static bool Clear()
    {
        lock (Gate)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) return false;
            var old70291 = new HashSet<uint>(Beast70291); var old70707 = new HashSet<uint>(Beast70707);
            Beast70291.Clear(); Beast70707.Clear();
            if (Save()) return true;
            Beast70291.UnionWith(old70291); Beast70707.UnionWith(old70707);
            return false;
        }
    }

    private static bool Save()
    {
        try
        {
            string path = PathName;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? "Settings");
            var lines = new List<string>();
            foreach (uint entry in Beast70291) lines.Add("70291=" + entry.ToString(CultureInfo.InvariantCulture));
            foreach (uint entry in Beast70707) lines.Add("70707=" + entry.ToString(CultureInfo.InvariantCulture));
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(path, lines);
            return true;
        }
        catch (Exception ex) { Logging.Write("[Skin] could not save confirmed beasts: " + ex.Message); return false; }
    }
}

// Per Grind session, do not repeatedly interact with the same body. Full identity
// and addresses distinguish reuse; when full, refuse new nearby attempts rather
// than clearing the set and re-clicking an old corpse.
internal sealed class WowBSkinAttempts
{
    private const int Limit = 512;
    private readonly HashSet<(WowBGuid Guid, ulong Record, ulong Unit, uint Map)> _seen = new();

    private static (WowBGuid, ulong, ulong, uint) Key(WowBObservedUnit unit, uint map)
        => (unit.Guid, unit.RecordAddress, unit.UnitAddress, map);
    internal bool Full => _seen.Count >= Limit;
    internal bool Contains(WowBObservedUnit unit, uint map) => _seen.Contains(Key(unit, map));
    internal bool Remember(WowBObservedUnit unit, uint map)
    {
        if (unit.Guid.IsEmpty || unit.RecordAddress == 0 || unit.UnitAddress == 0 || map == uint.MaxValue ||
            _seen.Count >= Limit || Contains(unit, map)) return false;
        return _seen.Add(Key(unit, map));
    }
    internal void Forget(WowBObservedUnit unit, uint map) => _seen.Remove(Key(unit, map));
    internal void Clear() => _seen.Clear();
}

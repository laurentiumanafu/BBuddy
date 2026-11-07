using System;
using System.Collections.Generic;
using System.Globalization;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Read-only 70862 research. A player GUID in an NPC object can represent many
// relationships, not necessarily its target or an attack. NEVER authorize an
// action from these observations. Reads and snapshots are non-atomic.
internal static class WowBAttackerResearch
{
    private const int MaxUnits = 4, UnitBytes = 0x2800, RecordBytes = 0x100, ChunkWords = 128;
    private const int ProbeLimitMs = 250;

    internal static List<int> FindGuidWords(uint[] words, WowBGuid guid)
    {
        var found = new List<int>();
        if (words == null || guid.IsEmpty) return found;
        uint a = (uint)guid.Low, b = (uint)(guid.Low >> 32), c = (uint)guid.High, d = (uint)(guid.High >> 32);
        for (int i = 0; i + 3 < words.Length; i++)
            if (words[i] == a && words[i + 1] == b && words[i + 2] == c && words[i + 3] == d) found.Add(i * 4);
        return found;
    }

    private static IEnumerable<string> FormatOffsets(IEnumerable<int> offsets)
    {
        foreach (int offset in offsets) yield return "+0x" + offset.ToString("X", CultureInfo.InvariantCulture);
    }

    // Three-word overlap preserves GUIDs spanning chunks. Unreadable chunks
    // are unknown; scans are bounded and never used to authorize actions.
    private static string Scan(WowBReader reader, ulong address, int size, WowBGuid player, Func<bool> stop, int started)
    {
        var matches = new SortedSet<int>();
        int unreadable = 0, read = 0;
        bool partial = false, capped = false;
        for (int offset = 0; offset < size; offset += (ChunkWords - 3) * 4)
        {
            if (stop() || unchecked(Environment.TickCount - started) >= ProbeLimitMs) { partial = true; break; }
            int words = Math.Min(ChunkWords, (size - offset) / 4);
            if (words < 4) break;
            if (!reader.TryReadWords(address + (ulong)offset, words, out uint[] chunk)) { unreadable++; continue; }
            read++;
            foreach (int hit in FindGuidWords(chunk, player))
            {
                int absolute = offset + hit;
                if (matches.Contains(absolute)) continue;
                if (matches.Count < 16) matches.Add(absolute); else capped = true;
            }
        }
        string locations = matches.Count == 0 ? "none observed" : string.Join(",", FormatOffsets(matches));
        return locations + " (chunks read=" + read + ", unreadable=" + unreadable +
            (partial ? ", partial" : "") + (capped ? ", match list capped" : "") + ")";
    }

    internal static void Observe(WowBReader reader, Memory memory, WowBObservedUnit player,
        List<WowBObservedUnit> blocked, Func<bool> stop, Func<bool> combatStillActive)
    {
        if (!reader.IsExperimental70862 || blocked.Count == 0 || player.Guid.IsEmpty || stop() ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader) return;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out uint map)) return;
            int started = Environment.TickCount;
            int count = Math.Min(blocked.Count, MaxUnits);
            for (int i = 0; i < count; i++)
            {
                if (stop() || unchecked(Environment.TickCount - started) >= ProbeLimitMs) break;
                WowBObservedUnit candidate = blocked[i];
                if (candidate.Guid.IsEmpty || candidate.IsLocalPlayer || candidate.HasPlayerVtable) continue;
                string record = Scan(reader, candidate.RecordAddress, RecordBytes, player.Guid, stop, started);
                string unit = Scan(reader, candidate.UnitAddress, UnitBytes, player.Guid, stop, started);
                if (stop() || ObjectManager.Wow != memory || ObjectManager.WowB != reader || !combatStillActive() ||
                    !reader.TryReadMapId(out uint afterMap) || afterMap != map ||
                    !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                    !after.TryGetLocalPlayer(out WowBObservedUnit self) || self.Guid != player.Guid ||
                    self.RecordAddress != player.RecordAddress || self.UnitAddress != player.UnitAddress ||
                    !after.TryGetUnit(candidate.Guid, out WowBObservedUnit now) || now.IsLocalPlayer || now.HasPlayerVtable ||
                    now.RecordAddress != candidate.RecordAddress || now.UnitAddress != candidate.UnitAddress || stop()) return;
                double distance = ClickAimMath.Len(now.Location.X - self.Location.X, now.Location.Y - self.Location.Y);
                bool fields = reader.TryGetUnitFields(now.UnitAddress, out WowBReader.WowBUnitFields stats);
                bool faction = reader.TryReadFactionTemplateId(now.UnitAddress, out uint key);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Attacker research] blocked NPC {0} at {1:F1} yd, health {2}, faction key {3}; player GUID references: record {4}; unit {5}. Read-only candidates, NOT attacker/target proof or an action authorization.",
                    now.Guid, distance, fields ? stats.Health.ToString(CultureInfo.InvariantCulture) : "unreadable",
                    faction ? key.ToString(CultureInfo.InvariantCulture) : "unreadable", record, unit));
            }
        }
        catch (Exception ex) { Logging.Write("[Attacker research] read-only observation failed: " + ex.Message); }
    }
}

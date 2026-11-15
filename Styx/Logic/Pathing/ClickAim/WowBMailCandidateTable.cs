using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70938 table interpretation, not proof of mailbox/UI state or
// server outcome. One live empty/nonzero/empty trace and static code support
// these byte ranges; their meanings and all 16 entries remain unverified.
internal static class WowBMailCandidateTable
{
    internal const int Count = 16, Stride = 40, Size = Count * Stride;
    internal readonly struct Expected
    {
        internal readonly WowBGuid Item, Bag;
        internal Expected(WowBGuid item, WowBGuid bag) { Item = item; Bag = bag; }
    }
    private static WowBGuid ReadGuid(byte[] raw, int p) =>
        new WowBGuid(BitConverter.ToUInt64(raw, p), BitConverter.ToUInt64(raw, p + 8));

    // A complete all-zero record is the only accepted empty-like value here.
    // Anything partially populated, duplicate, unmatched, or beyond 12 is
    // Unknown. This is a correlation, not independent UI confirmation.
    internal static bool Matches(byte[]? raw, IReadOnlyList<Expected>? expected)
    {
        if (raw == null || raw.Length != Size || expected == null || expected.Count > 12) return false;
        var ids = new HashSet<WowBGuid>();
        foreach (Expected item in expected)
            if (item.Item.IsEmpty || item.Bag.IsEmpty || !ids.Add(item.Item)) return false;
        var seen = new HashSet<WowBGuid>();
        for (int i = 0; i < Count; i++)
        {
            int p = i * Stride; bool zero = true;
            for (int j = 0; j < Stride; j++) if (raw[p + j] != 0) { zero = false; break; }
            if (zero) continue;
            WowBGuid item = ReadGuid(raw, p), bag = ReadGuid(raw, p + 16);
            if (item.IsEmpty || bag.IsEmpty || !seen.Add(item)) return false;
            bool matched = false;
            foreach (Expected wanted in expected)
                if (wanted.Item == item && wanted.Bag == bag) { matched = true; break; }
            if (!matched) return false;
        }
        return seen.Count == expected.Count;
    }
}

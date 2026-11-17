using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70938 candidate, not a verified aura API. Two user recordings
// associated ID 408505 and slot+0x104 with stacks 1, 3, 4 and 5. Other ranks,
// inline buffers, expiration semantics and client builds are not established.
internal static class WowBMaelstromWeapon
{
    internal const uint CandidateId = 408505;
    internal const int ActiveCountOffset = 0x1A60, ClockOffset = 0x1C10, IdOffset = 0xE8,
        DurationOffset = 0xF4, ExpiryOffset = 0xF8, StackOffset = 0x104;
    internal const int AttemptBackoffMs = 5000;

    // Published Forever rank costs, not a live spell-cost read. Talent or
    // client differences can still make this estimate inaccurate.
    internal static uint ManaEstimate(int id) => id switch
    {
        403 => 15U, 529 => 30U, 548 => 45U, 915 => 60U, 943 => 85U,
        6041 => 110U, 10391 => 135U, 10392 => 160U, 15207 => 190U, 15208 => 220U, _ => 0U
    };

    internal static bool IsFive(byte[]? bytes, uint count, uint clock)
    {
        int stride = WowBCombatResearch.ExternalCandidateStride;
        if (bytes == null || bytes.Length == 0 || bytes.Length % stride != 0 ||
            bytes.Length > WowBCombatResearch.External70938MaxSlots * stride || count == 0) return false;
        var ids = new HashSet<uint>();
        uint stack = 0, duration = 0, expiry = 0;
        for (int i = 0; i < bytes.Length; i += stride)
        {
            uint id = BitConverter.ToUInt32(bytes, i + IdOffset);
            if (id == 0) continue;
            if (!ids.Add(id)) return false;
            if (id == CandidateId)
            {
                stack = BitConverter.ToUInt32(bytes, i + StackOffset);
                duration = BitConverter.ToUInt32(bytes, i + DurationOffset);
                expiry = BitConverter.ToUInt32(bytes, i + ExpiryOffset);
            }
        }
        // The two recordings showed a 30000 duration and a future expiry
        // relative to player+0x1C10; these semantics remain experimental.
        int remaining = unchecked((int)(expiry - clock));
        return ids.Count == count && stack == 5 && duration == 30000 && remaining > 0 && remaining <= 30000;
    }

    // The inherited external reader double-checks header, committed region and
    // bytes. Count is read around it. Failure, inline storage and unknown IDs
    // withhold Bolt; none of them establish absence of the real buff.
    internal static bool ReadFive(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        if (!reader.Uses70938Features || player.Guid.IsEmpty || player.UnitAddress < 0x10000 ||
            player.UnitAddress > 0x00007FFFFFFFFFFFUL - (ulong)(ClockOffset + 4)) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? clockBefore = memory.ReadBytes64(player.UnitAddress + ClockOffset, 4);
            byte[]? before = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            var sample = WowBCombatResearch.ReadExternalCandidate(memory, player.UnitAddress, out _, true, true);
            byte[]? after = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            byte[]? clockAfter = memory.ReadBytes64(player.UnitAddress + ClockOffset, 4);
            if (before == null || after == null || before.Length != 4 || after.Length != 4 || sample == null ||
                clockBefore == null || clockAfter == null || clockBefore.Length != 4 || clockAfter.Length != 4) return false;
            uint count = BitConverter.ToUInt32(before, 0), firstClock = BitConverter.ToUInt32(clockBefore, 0), lastClock = BitConverter.ToUInt32(clockAfter, 0);
            return count == BitConverter.ToUInt32(after, 0) && unchecked(lastClock - firstClock) <= 1000 &&
                IsFive(sample.Bytes, count, lastClock);
        }
        catch { return false; }
    }
}

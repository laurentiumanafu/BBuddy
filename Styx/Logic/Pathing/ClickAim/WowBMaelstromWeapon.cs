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
    // Lightning Bolt is spent at four stacks as well as five: the proc is
    // consumed either way, and waiting for the fifth stack wastes the window.
    internal const uint MinimumStacks = 4;
    // Maelstrom Weapon caps at five stacks. The stack word is only a count
    // for this aura; for other auras the same offset holds an unrelated float
    // (a large value), and a torn read can surface that float here too. A
    // value above the cap is therefore not a stack count and must not fire:
    // without this bound a garbage float (>= 4) would spend the Bolt early.
    internal const uint MaximumStacks = 5;
    // The expiry word is stamped from the client clock when a stack lands, but
    // the sample and the clock are read non-atomically, so a just-refreshed
    // buff can read a little over its duration (30082 was recorded at the
    // five-stack transition). Allow slack above the duration instead of
    // withholding the cast in exactly that window.
    internal const int ExpirySlackMs = 2000;

    // Published Forever rank costs, not a live spell-cost read. Talent or
    // client differences can still make this estimate inaccurate.
    internal static uint ManaEstimate(int id) => id switch
    {
        403 => 15U, 529 => 30U, 548 => 45U, 915 => 60U, 943 => 85U,
        6041 => 110U, 10391 => 135U, 10392 => 160U, 15207 => 190U, 15208 => 220U, _ => 0U
    };

    internal static bool IsReady(byte[]? bytes, uint count, uint clock)
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
        return ids.Count == count && stack >= MinimumStacks && stack <= MaximumStacks && duration == 30000 &&
            remaining > 0 && remaining <= (long)duration + ExpirySlackMs;
    }

    // Diagnostic for the withheld log: reports what the candidate actually
    // saw (storage mode, active count, unique IDs, the Maelstrom stack,
    // duration and remaining time) so a silent withhold is distinguishable
    // from a wrong stack read. Unknown is reported, never as absence.
    internal static string Diagnose(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        try
        {
            if (!reader.Uses70938Features || player.Guid.IsEmpty || player.UnitAddress < 0x10000) return "reader/player unavailable";
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? clock = memory.ReadBytes64(player.UnitAddress + ClockOffset, 4);
            byte[]? countBytes = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            var sample = WowBCombatResearch.ReadExternalCandidate(memory, player.UnitAddress, out string reason, true, true);
            if (sample == null) return "aura sample unreadable (" + reason + ")";
            if (clock == null || clock.Length != 4 || countBytes == null || countBytes.Length != 4) return "clock/count unreadable";
            uint count = BitConverter.ToUInt32(countBytes, 0), now = BitConverter.ToUInt32(clock, 0);
            int stride = WowBCombatResearch.ExternalCandidateStride;
            var ids = new HashSet<uint>();
            uint stack = 0, duration = 0, expiry = 0;
            bool found = false;
            int duplicates = 0, slotIndex = -1;
            for (int i = 0; i + stride <= sample.Bytes.Length; i += stride)
            {
                uint id = BitConverter.ToUInt32(sample.Bytes, i + IdOffset);
                if (id == 0) continue;
                if (!ids.Add(id)) duplicates++;
                if (id == CandidateId)
                {
                    found = true;
                    slotIndex = i / stride;
                    stack = BitConverter.ToUInt32(sample.Bytes, i + StackOffset);
                    duration = BitConverter.ToUInt32(sample.Bytes, i + DurationOffset);
                    expiry = BitConverter.ToUInt32(sample.Bytes, i + ExpiryOffset);
                }
            }
            bool inline = sample.Address == player.UnitAddress + 0x608UL;
            int remaining = unchecked((int)(expiry - now));
            return "mode=" + (inline ? "inline" : "external") + " slots=" + (sample.Bytes.Length / stride) +
                " count=" + count + " uniqueIds=" + ids.Count + " duplicates=" + duplicates +
                " maelstromFound=" + found + " slot=" + slotIndex + " stack=" + stack + " duration=" + duration +
                " remaining=" + remaining + " ready=" + IsReady(sample.Bytes, count, now);
        }
        catch { return "diagnostic read failed"; }
    }

    // The inherited external reader double-checks header, committed region and
    // bytes. Count is read around it. Failure, inline storage and unknown IDs
    // withhold Bolt; none of them establish absence of the real buff.
    internal static bool ReadReady(Memory memory, WowBReader reader, WowBObservedUnit player)
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
                IsReady(sample.Bytes, count, lastClock);
        }
        catch { return false; }
    }
}

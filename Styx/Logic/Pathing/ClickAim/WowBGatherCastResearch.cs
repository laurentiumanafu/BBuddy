using System;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Experimental 70938 ID-3564 mining and ID-10768 skinning pattern observer.
/// Read-only; not a general casting API or proof of completion, loot or failure.
/// </summary>
internal static class WowBGatherCastResearch
{
    private const ulong WindowOffset = 0x3C0;
    private const int WindowSize = 0x78;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;

    // Based on four correlated intervals in one 70938 mining recording. Other
    // ranks, professions, interrupts, clocks and layouts remain unverified.
    internal static bool Matches(byte[]? data, out uint start, out uint end)
    {
        start = end = 0;
        if (data == null || data.Length != WindowSize) return false;
        uint first = BitConverter.ToUInt32(data, 0x00);
        uint second = BitConverter.ToUInt32(data, 0x2C);
        start = BitConverter.ToUInt32(data, 0x58);
        end = BitConverter.ToUInt32(data, 0x5C);
        return first == 3564U && second == first && start != 0 && end != 0 &&
               unchecked(end - start) == 3200U;
    }

    // One user-labelled skinning recording showed both ID words 10768 and
    // a 3000-unit interval. Other ranks, haste and interruptions are unknown.
    internal static bool MatchesSkinning(byte[]? data, out uint start, out uint end)
    {
        start = end = 0;
        if (data == null || data.Length != WindowSize) return false;
        uint first = BitConverter.ToUInt32(data, 0x00);
        uint second = BitConverter.ToUInt32(data, 0x2C);
        start = BitConverter.ToUInt32(data, 0x58);
        end = BitConverter.ToUInt32(data, 0x5C);
        return first == 10768U && second == first && start != 0 && end != 0 &&
               unchecked(end - start) == 3000U;
    }

    internal static bool TryBegin(Memory? memory, WowBReader? reader, out WowBObservedUnit player)
    {
        player = default;
        try
        {
            return memory != null && reader?.IsExperimental70938 == true && reader.ExperimentalGrindActive &&
                ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
                reader.TryRead70888RecoveryPlayer(out player, out _, out _) &&
                ObjectManager.Wow == memory && ObjectManager.WowB == reader;
        }
        catch { player = default; return false; }
    }

    internal enum Observation { Unknown, Idle, Mining, Skinning }

    // Stable zeros are only a narrow candidate for inactivity. Other or
    // partially updated cast clusters are Unknown, never an absent cast.
    internal static Observation Classify(byte[]? data, out uint start, out uint end)
    {
        start = end = 0;
        if (data == null || data.Length != WindowSize) return Observation.Unknown;
        if (Matches(data, out start, out end))
            return BitConverter.ToUInt32(data, 0x64) == 0 && BitConverter.ToUInt32(data, 0x70) == 0 &&
                   BitConverter.ToUInt32(data, 0x74) == 0 ? Observation.Mining : Observation.Unknown;
        if (MatchesSkinning(data, out start, out end))
            return BitConverter.ToUInt32(data, 0x64) == 0 && BitConverter.ToUInt32(data, 0x70) == 0 &&
                   BitConverter.ToUInt32(data, 0x74) == 0 ? Observation.Skinning : Observation.Unknown;
        if (BitConverter.ToUInt32(data, 0) == 0 && BitConverter.ToUInt32(data, 0x2C) == 0 &&
            BitConverter.ToUInt32(data, 0x58) == 0 && BitConverter.ToUInt32(data, 0x5C) == 0 &&
            BitConverter.ToUInt32(data, 0x64) == 0 && BitConverter.ToUInt32(data, 0x70) == 0 &&
            BitConverter.ToUInt32(data, 0x74) == 0) return Observation.Idle;
        return Observation.Unknown;
    }

    internal static Observation Observe(Memory memory, WowBReader reader, WowBObservedUnit player)
        => Observe(memory, reader, player, out _, out _);

    internal static Observation Observe(Memory memory, WowBReader reader, WowBObservedUnit player, out uint start, out uint end)
    {
        start = end = 0;
        try
        {
            if (!reader.IsExperimental70938 || !reader.ExperimentalGrindActive ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                player.UnitAddress < 0x10000UL || player.UnitAddress > UserLimit - WindowOffset - WindowSize) return Observation.Unknown;
            using var uncached = memory.TemporaryCacheState(false);
            if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return Observation.Unknown;
            byte[]? first = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            byte[]? second = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            Observation one = Classify(first, out uint start1, out uint end1);
            Observation two = Classify(second, out uint start2, out uint end2);
            if (one == Observation.Unknown || one != two || start1 != start2 || end1 != end2 ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader || !reader.ExperimentalGrindActive) return Observation.Unknown;
            start = start1; end = end1;
            return one;
        }
        catch { return Observation.Unknown; }
    }
}

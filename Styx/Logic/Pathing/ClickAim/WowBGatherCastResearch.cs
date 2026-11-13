using System;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Read-only 70938 diagnostic for one observed ID-3564 mining pattern. This is
/// not a general casting API, nor proof of completed casts, loot or failure.
/// Never use the result to authorize movement, clicks, retries or blacklisting.
/// </summary>
internal static class WowBGatherCastResearch
{
    private const ulong WindowOffset = 0x3C0;
    private const int WindowSize = 0x60;
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

    internal static bool TryObserve(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        try
        {
            if (!reader.IsExperimental70938 || !reader.ExperimentalGrindActive ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                player.UnitAddress < 0x10000UL || player.UnitAddress > UserLimit - WindowOffset - WindowSize) return false;
            using var uncached = memory.TemporaryCacheState(false);
            if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return false;
            byte[]? first = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            byte[]? second = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            return Matches(first, out uint start1, out uint end1) &&
                Matches(second, out uint start2, out uint end2) && start1 == start2 && end1 == end2 &&
                WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) &&
                ObjectManager.Wow == memory && ObjectManager.WowB == reader && reader.ExperimentalGrindActive;
        }
        catch { return false; }
    }
}

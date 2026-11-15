using System;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70938-only candidate from one interrupted recording per cast.
// Unrecognized, partial, unstable, failed or mismatched reads are Unknown.
// Neither this candidate nor its clock is a verified general casting API.
internal static class WowBTownCastCandidate
{
    private const ulong WindowOffset = 0x3C0, ClockOffset = 0x1C10;
    private const int WindowSize = 0x78;
    private const ulong Limit = 0x00007FFFFFFFFFFFUL;
    internal enum State { Unknown, Idle, AstralRecall, Hearthstone }

    internal static State Classify(byte[]? data, out uint start, out uint end)
    {
        start = end = 0;
        if (data == null || data.Length != WindowSize) return State.Unknown;
        uint first = BitConverter.ToUInt32(data, 0), second = BitConverter.ToUInt32(data, 0x2C);
        start = BitConverter.ToUInt32(data, 0x58); end = BitConverter.ToUInt32(data, 0x5C);
        if (BitConverter.ToUInt32(data, 0x64) != 0 || BitConverter.ToUInt32(data, 0x70) != 0 ||
            BitConverter.ToUInt32(data, 0x74) != 0) return State.Unknown;
        if (first == 0 && second == 0 && start == 0 && end == 0) return State.Idle;
        if (first != second || start == 0 || end == 0 || unchecked(end - start) != 10000U) return State.Unknown;
        return first == 556 ? State.AstralRecall : first == 8690 ? State.Hearthstone : State.Unknown;
    }

    internal static State Observe(Memory memory, WowBReader reader, WowBObservedUnit player,
        out uint start, out uint end, out uint clock)
    {
        start = end = clock = 0;
        try
        {
            if (!reader.IsExperimental70938 || !reader.ExperimentalGrindActive ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                player.UnitAddress < 0x10000 || player.UnitAddress > Limit - ClockOffset - 4) return State.Unknown;
            using var uncached = memory.TemporaryCacheState(false);
            if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return State.Unknown;
            byte[]? a = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            byte[]? b = memory.ReadBytes64(player.UnitAddress + WindowOffset, WindowSize);
            byte[]? c = memory.ReadBytes64(player.UnitAddress + ClockOffset, 4);
            byte[]? d = memory.ReadBytes64(player.UnitAddress + ClockOffset, 4);
            State one = Classify(a, out uint s1, out uint e1), two = Classify(b, out uint s2, out uint e2);
            if (one == State.Unknown || one != two || s1 != s2 || e1 != e2 || c == null || d == null || c.Length != 4 || d.Length != 4 ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader || !reader.ExperimentalGrindActive) return State.Unknown;
            uint c1 = BitConverter.ToUInt32(c, 0), c2 = BitConverter.ToUInt32(d, 0);
            if (c1 == 0 || unchecked(c2 - c1) > 500U) return State.Unknown;
            if (one != State.Idle && unchecked(c2 - s1) > 11000U) return State.Unknown;
            start = s1; end = e1; clock = c2; return one;
        }
        catch { return State.Unknown; }
    }
}

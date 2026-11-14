#if WOWB_READER_TESTS
using System;
using System.Threading;
using GreenMagic;
using Styx.Logic.Pathing;

internal static class CooldownResearchTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("Cooldown research: " + name); }
    internal static void Run()
    {
        var mem = new Memory();
        ulong h0 = mem.ImageBase64 + 0x48EC7E0 + 0x10, h1 = h0 + 0xB8, node = 0x50000000;
        mem.U64(h0, node); mem.U64(h1, 1); mem.Bytes(node, new byte[0x44]);
        mem.U64(node + 8, 1); mem.U32(node + 0x10, 17364); mem.U32(node + 0x20, 8000);
        string observed = WowBCooldownResearch.Capture(mem, CancellationToken.None);
        Check(observed.Contains("stable (research only") && observed.Contains("10=000043D4") && observed.Contains("20=00001F40"), "stable candidate");
        mem.U64(node + 8, node);
        Check(WowBCooldownResearch.Capture(mem, CancellationToken.None).Contains("unknown"), "cycle rejected");
        mem.U64(node + 8, 1);
        mem.ReadableRangeAllowed = false;
        Check(WowBCooldownResearch.Capture(mem, CancellationToken.None).Contains("unknown"), "unreadable rejected");
        mem.ReadableRangeAllowed = true;
        int reads = 0;
        mem.OnRead = (address, count) => { if (address == node && ++reads == 2) mem.U32(node + 0x20, 7000); };
        Check(WowBCooldownResearch.Capture(mem, CancellationToken.None).Contains("unknown"), "changed node rejected");
        mem.OnRead = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check(WowBCooldownResearch.Capture(mem, cancelled.Token).Contains("unknown"), "cancelled rejected");
    }
}
#endif

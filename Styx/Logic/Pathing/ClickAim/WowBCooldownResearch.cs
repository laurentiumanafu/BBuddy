using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using GreenMagic;

namespace Styx.Logic.Pathing;

// The two candidate heads and node layout remain experimental static leads,
// not a complete cooldown API. The rotation uses only a narrow observed
// Stormstrike transition and falls back to its estimate on uncertainty.
internal static class WowBCooldownResearch
{
    private const ulong CandidateRva = 0x48EC7E0, UserLimit = 0x00007FFFFFFFFFFFUL;
    private const int Stride = 0xB8, NodeSize = 0x44, MaxNodes = 64, BudgetMs = 100;

    internal static string Capture(Memory memory, CancellationToken cancel)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            if (memory.ImageBase64 < 0x10000 || memory.ImageBase64 > UserLimit - CandidateRva - 2 * Stride)
                return "cooldown candidate unknown: invalid image base";
            string? first = ReadPass(memory, cancel, watch);
            string? second = first == null ? null : ReadPass(memory, cancel, watch);
            if (first == null || second == null || first != second)
                return "cooldown candidate unknown: unreadable, changing, incomplete, cancelled or time limit";
            return "cooldown candidate stable (research only; not readiness): " + first;
        }
        catch { return "cooldown candidate unknown: read exception"; }
    }

    // Same bounded double-read as the recorder; an empty successful result is
    // not a readiness claim. Callers must separately validate player/session/map.
    internal static bool TryReadStormstrike(Memory memory, CancellationToken cancel, out List<WowBStormstrikeReset.Node> nodes)
    {
        nodes = new List<WowBStormstrikeReset.Node>();
        try
        {
            if (memory.ImageBase64 < 0x10000 || memory.ImageBase64 > UserLimit - CandidateRva - 2 * Stride) return false;
            using var uncached = memory.TemporaryCacheState(false);
            var watch = Stopwatch.StartNew();
            var firstNodes = new List<WowBStormstrikeReset.Node>();
            string? first = ReadPass(memory, cancel, watch, firstNodes);
            string? second = first == null ? null : ReadPass(memory, cancel, watch);
            if (first == null || second == null || first != second || cancel.IsCancellationRequested || watch.ElapsedMilliseconds > BudgetMs) return false;
            nodes = firstNodes;
            return true;
        }
        catch { return false; }
    }

    private static string? ReadPass(Memory memory, CancellationToken cancel, Stopwatch watch, List<WowBStormstrikeReset.Node>? stormNodes = null)
    {
        var text = new StringBuilder();
        for (int state = 0; state < 2; state++)
        {
            if (cancel.IsCancellationRequested || watch.ElapsedMilliseconds > BudgetMs) return null;
            ulong headAddress = memory.ImageBase64 + CandidateRva + (ulong)(state * Stride + 0x10);
            if (!memory.IsCommittedReadableRange64(headAddress, 8)) return null;
            byte[]? head = memory.ReadBytes64(headAddress, 8);
            if (head == null || head.Length != 8) return null;
            ulong node = BitConverter.ToUInt64(head, 0);
            text.Append(" state").Append(state).Append(" head=0x").Append(node.ToString("X", CultureInfo.InvariantCulture));
            var visited = new HashSet<ulong>();
            int count = 0;
            while (node != 0 && (node & 1) == 0)
            {
                if (cancel.IsCancellationRequested || watch.ElapsedMilliseconds > BudgetMs || count++ >= MaxNodes ||
                    node < 0x10000 || node > UserLimit - NodeSize || (node & 7) != 0 || !visited.Add(node) ||
                    !memory.IsCommittedReadableRange64(node, NodeSize)) return null;
                byte[]? bytes = memory.ReadBytes64(node, NodeSize);
                if (bytes == null || bytes.Length != NodeSize) return null;
                if (stormNodes != null && BitConverter.ToUInt32(bytes, 0x10) == 17364)
                    stormNodes.Add(new WowBStormstrikeReset.Node(state, node, bytes));
                text.Append(" [0x").Append(node.ToString("X", CultureInfo.InvariantCulture));
                for (int offset = 0x10; offset <= 0x40; offset += 4)
                    text.Append(' ').Append(offset.ToString("X2", CultureInfo.InvariantCulture)).Append('=')
                        .Append(BitConverter.ToUInt32(bytes, offset).ToString("X8", CultureInfo.InvariantCulture));
                node = BitConverter.ToUInt64(bytes, 8);
                text.Append(" next=0x").Append(node.ToString("X", CultureInfo.InvariantCulture)).Append(']');
            }
            text.Append(" end=0x").Append(node.ToString("X", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}

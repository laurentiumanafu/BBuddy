using System;
using System.Text;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Stopped-only static lead collection, never an attachment or send verdict.
// The RVA, 40-byte stride and 16-entry bound come from the matching decoded
// 70938 HasSendMailItem/GetSendMailItem code, not live validation.
internal static class WowBMailAttachmentResearch
{
    private const ulong TableRva = 0x4E6CA90, MarkerRva = 0x26B4F78;
    private const string Marker = "FFC883F8107338488D0C80488D05067B7B02488D04C84885C0";
    private const int Count = 16, Stride = 40;
    private static bool Equal(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    internal static string Capture()
    {
        Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
        int generation = ClickAimAbort.Generation;
        bool sameSession() => !ClickAimUi.Running && !ClickAimUi.Paused && ClickAimAbort.Generation == generation &&
            ObjectManager.Wow == memory && ObjectManager.WowB == reader;
        if (memory == null || reader?.IsExperimental70938 != true || !memory.Is64BitTarget || !sameSession())
            return "Stop the bot and attach to build 70938 before the mail candidate snapshot.";
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? world) || world == null ||
                !world.TryGetLocalPlayer(out WowBObservedUnit player) ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) || !sameSession())
                return "Build, player or stopped-session identity unavailable; no mail candidate snapshot.";
            ulong marker = memory.GetAbsolute64(MarkerRva), table = memory.GetAbsolute64(TableRva);
            byte[]? signature = memory.ReadBytes64(marker, Marker.Length / 2);
            if (signature == null || Convert.ToHexString(signature) != Marker ||
                !memory.IsCommittedReadableRange64(table, Count * Stride))
                return "Mail code marker or candidate table unreadable/mismatched; no snapshot.";
            byte[]? first = memory.ReadBytes64(table, Count * Stride);
            byte[]? second = memory.ReadBytes64(table, Count * Stride);
            if (first == null || first.Length != Count * Stride || !Equal(first, second) ||
                !Equal(signature, memory.ReadBytes64(marker, Marker.Length / 2)) || !sameSession() ||
                !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) || !sameSession())
                return "Mail candidate bytes or session changed/unreadable; no snapshot.";
            var report = new StringBuilder();
            report.AppendLine("70938 read-only mail candidate snapshot; player=" + player.Guid + "; table RVA=0x4E6CA90; stride=40; count=16.");
            report.AppendLine("Raw bytes only; no attachment, empty-slot, recipient, tab or send result is proven. Reads are not atomic.");
            for (int i = 0; i < Count; i++)
                report.AppendLine("candidate[" + (i + 1) + "] raw=" + Convert.ToHexString(first, i * Stride, Stride));
            return report.ToString();
        }
        catch (Exception ex) { return "Mail candidate snapshot unavailable: " + ex.Message; }
    }
}

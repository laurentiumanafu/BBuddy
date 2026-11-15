using System;
using System.Collections.Generic;
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

    private static WowBGuid GuidAt(byte[] data, int offset) =>
        new WowBGuid(BitConverter.ToUInt64(data, offset), BitConverter.ToUInt64(data, offset + 8));

    private static bool SameBags(List<WowBContainerResearchProbe.MailBagObservation> a,
        List<WowBContainerResearchProbe.MailBagObservation> b)
    {
        if (a.Count != 5 || b.Count != 5) return false;
        for (int i = 0; i < 5; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.CandidateIndex != i || y.CandidateIndex != i || x.Unknown != y.Unknown ||
                x.Bag != y.Bag || x.Slots.Length != y.Slots.Length) return false;
            for (int j = 0; j < x.Slots.Length; j++) if (x.Slots[j] != y.Slots[j]) return false;
        }
        return true;
    }

    // Raw equality comparisons only: the offsets are not yet validated fields.
    // A zero/unknown candidate or no match is never evidence of absence.
    private static void DescribeCorrelations(StringBuilder report, byte[] table,
        List<WowBContainerResearchProbe.MailBagObservation> bags)
    {
        report.AppendLine("Experimental raw 16-byte equality comparisons; no item/attachment semantics proven. Backpack not observed.");
        foreach (var bag in bags)
            if (bag.Unknown) report.AppendLine("bag candidate[" + bag.CandidateIndex + "] Unknown; not evidence of absence.");
        for (int i = 0; i < Count; i++)
        {
            WowBGuid first = GuidAt(table, i * Stride), second = GuidAt(table, i * Stride + 16);
            if (first.IsEmpty && second.IsEmpty) continue;
            foreach (var bag in bags)
            {
                if (bag.Unknown) continue;
                if (!second.IsEmpty && second == bag.Bag)
                    report.AppendLine("mail candidate[" + (i + 1) + "] +0x10 equals bag candidate[" + bag.CandidateIndex + "] identity.");
                if (!first.IsEmpty)
                    for (int slot = 0; slot < bag.Slots.Length; slot++)
                        if (first == bag.Slots[slot]) report.AppendLine("mail candidate[" + (i + 1) + "] +0x00 equals bag candidate[" + bag.CandidateIndex + "] slot " + (slot + 1) + " raw identity.");
            }
        }
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
            bool haveFirstBags = WowBInventoryProbe.TryReadMailCandidateBags(out WowBGuid firstPlayer, out var firstBags) &&
                firstPlayer == player.Guid && firstBags != null && sameSession();
            byte[]? first = memory.ReadBytes64(table, Count * Stride);
            byte[]? second = memory.ReadBytes64(table, Count * Stride);
            bool haveSecondBags = WowBInventoryProbe.TryReadMailCandidateBags(out WowBGuid secondPlayer, out var secondBags) &&
                secondPlayer == player.Guid && secondBags != null && sameSession();
            byte[]? last = memory.ReadBytes64(table, Count * Stride);
            if (first == null || first.Length != Count * Stride || !Equal(first, second) || !Equal(first, last) ||
                !Equal(signature, memory.ReadBytes64(marker, Marker.Length / 2)) || !sameSession() ||
                !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) || !sameSession())
                return "Mail candidate bytes or session changed/unreadable; no snapshot.";
            var report = new StringBuilder();
            report.AppendLine("70938 read-only mail candidate snapshot; player=" + player.Guid + "; table RVA=0x4E6CA90; stride=40; count=16.");
            report.AppendLine("Raw bytes only; no attachment, empty-slot, recipient, tab or send result is proven. Reads are not atomic.");
            for (int i = 0; i < Count; i++)
                report.AppendLine("candidate[" + (i + 1) + "] raw=" + Convert.ToHexString(first, i * Stride, Stride));
            if (haveFirstBags && haveSecondBags && firstBags != null && secondBags != null && SameBags(firstBags, secondBags))
                DescribeCorrelations(report, first, secondBags);
            else report.AppendLine("Bag comparison unavailable or changed; no identity correlation. Unknown is not absence.");
            return report.ToString();
        }
        catch (Exception ex) { return "Mail candidate snapshot unavailable: " + ex.Message; }
    }
}

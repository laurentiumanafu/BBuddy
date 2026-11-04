using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// 70707 candidate observations. No Lua/client calls. The same guarded
// subtotal is also used experimentally by Grind's auto-selling policy.
internal static class WowBContainerResearchProbe
{
    private const ulong IdentityTableRva = 0x6D41CA0;
    // The helper reads the tag/high qword at +0x490 + 16*i. A live
    // comparison matched the complete 128-bit value eight bytes earlier.
    // This grouping is a research hypothesis, not a validated slot layout.
    private const ulong SlotsOffset = 0x488, CapacityOffset = 0xAC0;
    private const int CandidateIdentities = 5, SlotStride = 16, MaxSlots = 64;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;
    private static readonly (ulong Rva, string Bytes)[] Markers = {
        (0xD5C7FC, "E86FAE8001"), (0xD5C820, "8B80C00A0000"),
        (0x2569186, "486390C00A0000"), (0x2569194, "4C8D8090040000"),
        (0x25691A0, "498B00")
    };
    private static readonly (ulong Rva, string Bytes)[] Markers70755 = {
        (0xD0EE1C, "E8FF2B7601"), (0xD0EE40, "8B80C00A0000"),
        (0x2473536, "486390C00A0000"), (0x2473544, "4C8D8090040000"),
        (0x2473550, "498B00")
    };
    private sealed class Candidate
    {
        public WowBGuid Guid;
        public ulong Data;
        public uint Count;
        public byte[] Slots = Array.Empty<byte>();
    }
    private static Memory? _previousMemory;
    private static WowBReader? _previousReader;
    private static WowBGuid _previousPlayer;
    private static Candidate?[]? _previous;

    private static bool Same(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static bool UserRange(ulong p, ulong width) => p > 0x10000 && p <= UserLimit - width;
    // Packed four-byte-aligned objects were observed in other 70707 diagnostics.
    // Alignment alone never establishes an object's identity or type.
    private static bool Pointer(ulong p, ulong width) => UserRange(p, width) && (p & 3) == 0;
    private static WowBGuid GuidAt(byte[] b, int offset) => new WowBGuid(BitConverter.ToUInt64(b, offset), BitConverter.ToUInt64(b, offset + 8));
    // Code pages can be temporarily unreadable on 70707. Report unreadable
    // markers in the diagnostic and require independent build/player guards;
    // any readable mismatch rejects even the experimental active observation.
    private static bool Signatures(Memory memory, bool newer, out string report)
    {
        bool match = true;
        var statuses = new List<string>();
        foreach (var marker in newer ? Markers70755 : Markers)
        {
            byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(marker.Rva), marker.Bytes.Length / 2);
            string status;
            if (bytes == null || bytes.Length * 2 != marker.Bytes.Length) status = "unreadable";
            else if (Convert.ToHexString(bytes) == marker.Bytes) status = "matched";
            else { status = "MISMATCH"; match = false; }
            statuses.Add($"0x{marker.Rva:X}={status}");
        }
        report = string.Join(", ", statuses);
        return match;
    }
    private static bool SamePlayer(Memory memory, WowBReader reader, WowBObservedUnit player, bool active)
    {
        return ClickAimUi.Running == active && !ClickAimUi.Paused && ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
            reader.IsExperimental70707 && WowBGrindFactions.TraceBuild70707(memory, reader) &&
            WowBGrindFactions.TraceIdentity70707(memory, reader, player, true);
    }
    private static void Forget() { _previousMemory = null; _previousReader = null; _previous = null; }

    // Search a bounded record window for pointers whose candidate headers repeat
    // BOTH the pool record and the full 128-bit identity. This is a diagnostic
    // heuristic, not a proof that the result is the resolver's object. Deduplicate
    // identical pointers and fail closed on multiple distinct matches.
    private static bool FindCandidate(Memory memory, WowBReader.WowBPoolObject record, WowBGuid guid,
        out ulong data, out ulong sourceOffset, out byte[]? pointerBytes, out byte[]? header, out string detail)
    {
        data = 0; sourceOffset = 0; pointerBytes = null; header = null;
        var candidates = new HashSet<ulong>();
        var pointerLeads = new List<string>();
        string rawD8 = "unreadable", d8Header = "unavailable";
        int unreadableFields = 0, unavailableHeaders = 0, changedFields = 0;
        var fieldReads = new List<(ulong Offset, byte[] Value)>();
        if (!UserRange(record.RecordPtr, 0x188)) { detail = "record window outside user range"; return false; }
        for (ulong offset = 0x28; offset <= 0x180; offset += 8)
        {
            byte[]? value = memory.ReadBytes64(record.RecordPtr + offset, 8);
            if (value == null || value.Length != 8) { unreadableFields++; continue; }
            fieldReads.Add((offset, value));
            ulong address = BitConverter.ToUInt64(value, 0);
            if (offset == WowBOffsets.RecordDataBlockPtrOffset)
            {
                rawD8 = $"0x{address:X}";
                if (UserRange(address, 0x20))
                {
                    byte[]? sample = memory.ReadBytes64(address, 0x20);
                    byte[]? again = memory.ReadBytes64(address, 0x20);
                    d8Header = Same(sample, again) ? Convert.ToHexString(sample!) : "unreadable/changed";
                }
            }
            if (!Pointer(address, CapacityOffset + 4)) continue;
            if (pointerLeads.Count < 12) pointerLeads.Add($"+0x{offset:X}:0x{address:X}");
            byte[]? sampleHeader = memory.ReadBytes64(address, 0x20);
            if (sampleHeader == null || sampleHeader.Length != 0x20) { unavailableHeaders++; continue; }
            if (BitConverter.ToUInt64(sampleHeader, 8) != record.RecordPtr || GuidAt(sampleHeader, 0x10) != guid) continue;
            if (!Same(sampleHeader, memory.ReadBytes64(address, 0x20)) ||
                !Same(value, memory.ReadBytes64(record.RecordPtr + offset, 8))) { unavailableHeaders++; continue; }
            if (!candidates.Add(address)) continue;
            data = address; sourceOffset = offset; pointerBytes = value; header = sampleHeader;
        }
        foreach (var field in fieldReads)
            if (!Same(field.Value, memory.ReadBytes64(record.RecordPtr + field.Offset, 8))) changedFields++;
        detail = $"record+0xD8={rawD8} raw32={d8Header}; pointer-like fields=[{string.Join(",", pointerLeads)}] (max 12); identity-matching headers={candidates.Count}; unreadable fields={unreadableFields}; unavailable headers={unavailableHeaders}; changed fields={changedFields}";
        return candidates.Count == 1 && unreadableFields == 0 && unavailableHeaders == 0 && changedFields == 0;
    }

    // Caller holds the inventory probe lock and has disabled the memory cache.
    public static string Capture(Memory memory, WowBReader reader, WowBObservedUnit player,
        out (int CandidateSize, int ZeroTags, int Resolved, int ZeroIdentities)? subtotal, bool active = false)
    {
        subtotal = null;
        try
        {
            if (!SamePlayer(memory, reader, player, active))
            { Forget(); return "Container research: experimental player, build or stopped state unavailable; no container candidates captured.\n"; }
            if (!Signatures(memory, reader.IsExperimental70755, out string markerBefore))
            { Forget(); return "Container research: readable code marker mismatch (" + markerBefore + "); no container candidates captured.\n"; }
            ulong table = memory.GetAbsolute64(reader.IsExperimental70755 ? Wow70755Offsets.ContainerIdentityTableCandidate : IdentityTableRva);
            byte[]? identityBytes = memory.ReadBytes64(table, CandidateIdentities * SlotStride);
            if (identityBytes == null || identityBytes.Length != CandidateIdentities * SlotStride)
            { Forget(); return "Container research: candidate identity table unreadable; no container candidates captured.\n"; }
            List<WowBReader.WowBPoolObject> records = reader.EnumeratePoolObjects();
            var next = new Candidate?[CandidateIdentities];
            var text = new StringBuilder();
            text.AppendLine($"{reader.ExperimentalBuildNumber} container research: indexes 0..4 are static candidates, not user bag numbers; no bag/full verdict.");
            text.AppendLine("Container code markers before: " + markerBefore + "; unreadable is not confirmation of a match.");
            bool comparable = _previousMemory == memory && _previousReader == reader && _previousPlayer == player.Guid && _previous != null;
            for (int i = 0; i < CandidateIdentities; i++)
            {
                WowBGuid guid = GuidAt(identityBytes, i * SlotStride);
                if (guid.IsEmpty) { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] raw identity zero; meaning unknown.\n", i); continue; }
                int matches = 0;
                WowBReader.WowBPoolObject record = default;
                foreach (var item in records) if (item.Guid == guid) { matches++; record = item; }
                if (matches != 1 || !reader.IsPoolRecordCurrent(record))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] identity={1} pool matches={2}; object unavailable/ambiguous.\n", i, guid, matches); continue; }
                if (!FindCandidate(memory, record, guid, out ulong data, out ulong sourceOffset, out byte[]? pointerBytes, out byte[]? header, out string detail))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] no unique stable identity-matching header; {1}. Unknown.\n", i, detail); continue; }
                text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] record scan {1}; selected source=+0x{2:X} (unverified).\n", i, detail, sourceOffset);
                byte[]? capacity = memory.ReadBytes64(data + CapacityOffset, 4);
                if (capacity == null || capacity.Length != 4)
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] +0xAC0 unreadable.\n", i); continue; }
                uint count = BitConverter.ToUInt32(capacity, 0);
                if (count > MaxSlots)
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] raw +0xAC0={1} exceeds diagnostic cap {2}; unknown.\n", i, count, MaxSlots); continue; }
                int length = checked((int)count * SlotStride);
                byte[]? slots = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
                byte[]? slotsAgain = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
                byte[]? recordGuid = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
                if (!Same(slots, slotsAgain) || !Same(header, memory.ReadBytes64(data, 0x20)) ||
                    !Same(capacity, memory.ReadBytes64(data + CapacityOffset, 4)) ||
                    !Same(pointerBytes, memory.ReadBytes64(record.RecordPtr + sourceOffset, 8)) ||
                    recordGuid == null || recordGuid.Length != 16 || GuidAt(recordGuid, 0) != guid || !reader.IsPoolRecordCurrent(record))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] unstable or unreadable; discarded.\n", i); continue; }
                var current = new Candidate { Guid = guid, Data = data, Count = count, Slots = slots! };
                next[i] = current;
                int zeroTags = 0;
                for (int j = 0; j < (int)count; j++) if ((BitConverter.ToUInt64(slots!, j * SlotStride + 8) >> 58) == 0) zeroTags++;
                Candidate? prior = comparable ? _previous![i] : null;
                bool sameObject = prior != null && prior.Guid == guid && prior.Data == data && prior.Count == count;
                text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] identity={1} object=0x{2:X} raw+0xAC0={3}; second-qword high-six-zero={4}; previous={5}. NOT verified capacity/free slots.\n",
                    i, guid, data, count, zeroTags, sameObject ? "comparable" : "none or changed identity/size");
                if (!sameObject)
                    text.AppendFormat(CultureInfo.InvariantCulture, "  raw+0x488 candidate 16-byte entries={0}\n", Convert.ToHexString(slots!));
                else
                {
                    int changed = 0;
                    for (int j = 0; j < (int)count; j++)
                    {
                        string oldValue = Convert.ToHexString(prior!.Slots, j * SlotStride, SlotStride);
                        string newValue = Convert.ToHexString(slots!, j * SlotStride, SlotStride);
                        if (oldValue != newValue) { changed++; text.AppendFormat(CultureInfo.InvariantCulture, "  entry[{0}] {1} -> {2}\n", j, oldValue, newValue); }
                    }
                    text.AppendFormat(CultureInfo.InvariantCulture, "  changed entries={0}; zero tag is not a verified empty slot.\n", changed);
                }
            }
            bool markersAfterValid = Signatures(memory, reader.IsExperimental70755, out string markerAfter);
            if (!Same(identityBytes, memory.ReadBytes64(table, CandidateIdentities * SlotStride)) || !markersAfterValid ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                !after.TryGetLocalPlayer(out WowBObservedUnit final) || final.Guid != player.Guid ||
                final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress || !SamePlayer(memory, reader, final, active))
            { Forget(); return "Container research: identity table, player, attachment, code or bot state changed; discarded. Code markers after: " + markerAfter + ".\n"; }
            // Aggregate only nonzero identity-table entries that all resolved
            // uniquely and stably. Zero identities, unscanned indexes, category
            // overlap and specialty bags remain unknown, so this is not a total.
            int sizeSum = 0, zeroSum = 0, resolved = 0, zeroIdentities = 0;
            bool allResolved = true;
            var identities = new HashSet<WowBGuid>();
            var dataAddresses = new HashSet<ulong>();
            for (int i = 0; i < CandidateIdentities; i++)
            {
                WowBGuid guid = GuidAt(identityBytes, i * SlotStride);
                if (guid.IsEmpty) { zeroIdentities++; continue; }
                Candidate? item = next[i];
                if (item == null || !identities.Add(guid) || !dataAddresses.Add(item.Data)) { allResolved = false; continue; }
                resolved++;
                sizeSum += (int)item.Count;
                for (int j = 0; j < (int)item.Count; j++)
                    if ((BitConverter.ToUInt64(item.Slots, j * SlotStride + 8) >> 58) == 0) zeroSum++;
            }
            if (allResolved && resolved > 0) subtotal = (sizeSum, zeroSum, resolved, zeroIdentities);
            if (!active) { _previousMemory = memory; _previousReader = reader; _previousPlayer = player.Guid; _previous = next; }
            text.AppendLine("Container code markers after: " + markerAfter + "; unreadable is not confirmation of a match.");
            text.AppendLine("Container reads are non-atomic; unresolved/zero candidates are unknown. Grind may use the subtotal experimentally for selling on 70707/70755.");
            return text.ToString();
        }
        catch (Exception ex) { subtotal = null; Forget(); return "Container research unavailable: " + ex.Message + "\n"; }
    }
}

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
    private static readonly (ulong Rva, string Bytes)[] Markers70790 = {
        (0xD8E69C, "E8AF6C8701"), (0xD8E6C0, "8B80C00A0000"),
        (0x2606E66, "486390C00A0000"), (0x2606E74, "4C8D8090040000"),
        (0x2606E80, "498B00")
    };
    private static readonly (ulong Rva, string Bytes)[] Markers70862 = {
        (0xD0FD5C, "E8FF3F7601"), (0xD0FD80, "8B80C00A0000"),
        (0x2475876, "486390C00A0000"), (0x2475884, "4C8D8090040000"),
        (0x2475890, "498B00")
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
    private static bool _lastComplete;

    internal sealed class BagChoice
    {
        internal readonly WowBGuid Player, Bag;
        internal readonly int Index;
        internal readonly uint Size;
        internal BagChoice(WowBGuid player, WowBGuid bag, int index, uint size)
        { Player = player; Bag = bag; Index = index; Size = size; }
        public override string ToString()
        {
            WowBBagKind kind = WowBBagClassification.TryGet(Player, Bag, out WowBBagKind saved) ? saved : WowBBagKind.Unknown;
            return "Candidate " + Index + " — " + Size + " candidate slots — " + kind + " (" + Bag + ")";
        }
    }
    internal static void InvalidateChoices() => _lastComplete = false;
    internal static List<BagChoice> LastChoices(Memory memory, WowBReader reader)
    {
        var result = new List<BagChoice>();
        if (!_lastComplete || _previous == null || _previousMemory != memory || _previousReader != reader ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused)
            return result;
        for (int i = 0; i < _previous.Length; i++)
            if (_previous[i] is Candidate item) result.Add(new BagChoice(_previousPlayer, item.Guid, i, item.Count));
        return result;
    }

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
    private static bool Signatures(Memory memory, int build, out string report)
    {
        bool match = true;
        var statuses = new List<string>();
        foreach (var marker in build == 70862 ? Markers70862 : build == 70790 ? Markers70790 : build == 70755 ? Markers70755 : Markers)
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
    private static void Forget() { _previousMemory = null; _previousReader = null; _previous = null; _lastComplete = false; }

    // A fully free or reserved region cannot currently contain a committed
    // object header. Only 70862 may additionally exclude MEM_RESERVE; a failed
    // query or committed (including guard/no-access) region remains unknown.
    // Re-query before resolving and again before publishing a subtotal.
    private static bool HeaderUncommitted(Memory memory, WowBReader reader, ulong address)
    {
        if (memory.IsMemoryRangeFree64(address, 0x20)) return true;
        return reader.IsExperimental70862 && memory.TryQueryMemoryRangeState64(address, 0x20, out uint state, out _) && state == 0x2000;
    }

    // Search a bounded record window for pointers whose candidate headers repeat
    // BOTH the pool record and the full 128-bit identity. This is a diagnostic
    // heuristic, not a proof that the result is the resolver's object. Deduplicate
    // identical pointers and fail closed on multiple distinct matches.
    private static bool FindCandidate(Memory memory, WowBReader reader, List<WowBReader.WowBPoolObject> records,
        WowBReader.WowBPoolObject record, WowBGuid guid, out ulong data, out ulong sourceOffset,
        out byte[]? pointerBytes, out byte[]? header, out bool exploratory, out string detail,
        out List<ulong> excludedHeaders)
    {
        data = 0; sourceOffset = 0; pointerBytes = null; header = null; exploratory = false;
        excludedHeaders = new List<ulong>();
        // The 70790 broad scan can overlap separately occupied records. Treat an
        // unreadable pointer-like qword as GUID data only when a stable neighboring
        // pool record proves that exact address is part of its full GUID. Readable
        // headers are still checked normally; no record stride is assumed.
        var neighborFields = new Dictionary<ulong, (WowBReader.WowBPoolObject Record, byte[] Guid)>();
        var ambiguousFields = new HashSet<ulong>();
        var usedNeighbors = new List<(WowBReader.WowBPoolObject Record, byte[] Guid)>();
        if (reader.IsExperimental70790 || reader.IsExperimental70862)
        {
            foreach (var other in records)
            {
                if (other.RecordPtr <= record.RecordPtr || other.RecordPtr - record.RecordPtr > 0x168 ||
                    !reader.IsPoolRecordCurrent(other)) continue;
                byte[]? otherGuid = memory.ReadBytes64(other.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
                if (otherGuid == null || otherGuid.Length != 16 || GuidAt(otherGuid, 0) != other.Guid ||
                    !Same(otherGuid, memory.ReadBytes64(other.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16)) ||
                    !reader.IsPoolRecordCurrent(other)) continue;
                ulong low = other.RecordPtr - record.RecordPtr + WowBOffsets.RecordGuidLoOffset;
                foreach (ulong offset in new[] { low, low + 8 })
                {
                    if (offset < 0x28 || offset > 0x180 || (offset & 7) != 0 || ambiguousFields.Contains(offset)) continue;
                    if (!neighborFields.TryAdd(offset, (other, otherGuid))) { neighborFields.Remove(offset); ambiguousFields.Add(offset); }
                }
            }
        }
        var candidates = new HashSet<ulong>();
        var pointerLeads = new List<string>();
        var unavailableLeads = new List<string>();
        string rawD8 = "unreadable", d8Header = "unavailable";
        int unreadableFields = 0, unavailableHeaders = 0, changedFields = 0, neighborGuidFields = 0, reservedHeaders = 0;
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
            if (sampleHeader == null || sampleHeader.Length != 0x20)
            {
                if (neighborFields.TryGetValue(offset, out var neighbor))
                {
                    int guidOffset = checked((int)(record.RecordPtr + offset - neighbor.Record.RecordPtr - WowBOffsets.RecordGuidLoOffset));
                    if (BitConverter.ToUInt64(neighbor.Guid, guidOffset) == address)
                    { usedNeighbors.Add(neighbor); neighborGuidFields++; continue; }
                }
                // MEM_FREE has no object data. On 70862, MEM_RESERVE is also
                // uncommitted; every byte of the header must be in one region.
                if ((reader.IsExperimental70790 || reader.IsExperimental70862) && memory.IsMemoryRangeFree64(address, 0x20))
                { excludedHeaders.Add(address); continue; }
                string mapping = "";
                if (reader.IsExperimental70862)
                {
                    bool queried = memory.TryQueryMemoryRangeState64(address, 0x20, out uint state, out uint protect);
                    if (queried && state == 0x2000) // MEM_RESERVE: no committed storage
                    { excludedHeaders.Add(address); reservedHeaders++; continue; }
                    mapping = queried ? $" (state=0x{state:X}, protect=0x{protect:X})" : " (mapping unavailable or split)";
                }
                unavailableHeaders++;
                unavailableLeads.Add($"+0x{offset:X}:0x{address:X}{mapping}");
                continue;
            }
            if (BitConverter.ToUInt64(sampleHeader, 8) != record.RecordPtr || GuidAt(sampleHeader, 0x10) != guid) continue;
            if (!Same(sampleHeader, memory.ReadBytes64(address, 0x20)) ||
                !Same(value, memory.ReadBytes64(record.RecordPtr + offset, 8)))
            { unavailableHeaders++; unavailableLeads.Add($"+0x{offset:X}:0x{address:X} (changed)"); continue; }
            if (!candidates.Add(address)) continue;
            data = address; sourceOffset = offset; pointerBytes = value; header = sampleHeader;
        }
        foreach (var field in fieldReads)
            if (!Same(field.Value, memory.ReadBytes64(record.RecordPtr + field.Offset, 8))) changedFields++;
        foreach (var neighbor in usedNeighbors)
            if (!reader.IsPoolRecordCurrent(neighbor.Record) ||
                !Same(neighbor.Guid, memory.ReadBytes64(neighbor.Record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16)) ||
                !reader.IsPoolRecordCurrent(neighbor.Record)) changedFields++;
        foreach (ulong address in excludedHeaders)
            if (!HeaderUncommitted(memory, reader, address)) changedFields++;
        detail = $"record+0xD8={rawD8} raw32={d8Header}; pointer-like fields=[{string.Join(",", pointerLeads)}] (max 12); identity-matching headers={candidates.Count}; unreadable fields={unreadableFields}; unavailable headers={unavailableHeaders}; unavailable fields=[{string.Join(",", unavailableLeads)}]; confirmed free headers={excludedHeaders.Count - reservedHeaders}; confirmed reserved headers={reservedHeaders}; verified neighbor GUID fields={neighborGuidFields}; changed fields={changedFields}";
        // A unique observed header is useful for stopped research even if other
        // pointer-like values were unreadable. It is NOT a resolved container.
        exploratory = candidates.Count == 1 && unreadableFields == 0 && changedFields == 0 && unavailableHeaders > 0;
        return candidates.Count == 1 && unreadableFields == 0 && unavailableHeaders == 0 && changedFields == 0;
    }

    // Some bounded scan windows can extend into other occupied pool records.
    // Report verified GUID-field overlaps for stopped research only; this is not
    // proof of record size and does not relax any resolution/selling guard.
    private static string DescribeRecordOverlaps(Memory memory, WowBReader reader,
        WowBReader.WowBPoolObject record, List<WowBReader.WowBPoolObject> records)
    {
        var overlaps = new List<string>();
        foreach (var other in records)
        {
            if (other.RecordPtr <= record.RecordPtr || other.RecordPtr - record.RecordPtr > 0x168 ||
                !reader.IsPoolRecordCurrent(other)) continue;
            byte[]? guid = memory.ReadBytes64(other.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
            if (guid == null || guid.Length != 16 || GuidAt(guid, 0) != other.Guid ||
                !Same(guid, memory.ReadBytes64(other.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16)) ||
                !reader.IsPoolRecordCurrent(other)) continue;
            ulong delta = other.RecordPtr - record.RecordPtr;
            overlaps.Add($"+0x{delta + WowBOffsets.RecordGuidLoOffset:X}=other-record(+0x{delta:X}) GUID-low");
            if (overlaps.Count == 8) break;
        }
        return overlaps.Count == 0 ? "" : "  STOPPED RESEARCH ONLY: scan-window overlap " +
            string.Join(", ", overlaps) + "; record sizes unproven, resolution unchanged.\n";
    }

    // Read-only stopped research only. This never adds a BagChoice, contributes
    // to a subtotal, or authorizes selling; unreadable alternatives remain unknown.
    private static string DescribeExploratory(Memory memory, WowBReader reader, WowBObservedUnit player,
        WowBReader.WowBPoolObject record, WowBGuid guid, ulong data, ulong sourceOffset, byte[] pointerBytes, byte[] header)
    {
        if (ClickAimUi.Running || ClickAimUi.Paused || !SamePlayer(memory, reader, player, false) ||
            !reader.IsPoolRecordCurrent(record) || !UserRange(data, CapacityOffset + 4)) return "";
        byte[]? capacity = memory.ReadBytes64(data + CapacityOffset, 4);
        if (capacity == null || capacity.Length != 4) return "  exploratory candidate capacity unreadable; no subtotal.\n";
        uint count = BitConverter.ToUInt32(capacity, 0);
        if (count > MaxSlots || !UserRange(data, SlotsOffset + (ulong)count * SlotStride))
            return "  exploratory candidate capacity outside bounds; no subtotal.\n";
        int length = checked((int)count * SlotStride);
        byte[]? slots = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
        byte[]? slotsAgain = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
        byte[]? recordGuid = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
        if (!Same(slots, slotsAgain) || !Same(capacity, memory.ReadBytes64(data + CapacityOffset, 4)) ||
            !Same(header, memory.ReadBytes64(data, 0x20)) ||
            !Same(pointerBytes, memory.ReadBytes64(record.RecordPtr + sourceOffset, 8)) ||
            recordGuid == null || recordGuid.Length != 16 || GuidAt(recordGuid, 0) != guid ||
            !reader.IsPoolRecordCurrent(record) || !SamePlayer(memory, reader, player, false))
            return "  exploratory candidate changed or became unreadable; no subtotal.\n";
        int zeros = 0;
        for (int i = 0; i < (int)count; i++) if ((BitConverter.ToUInt64(slots!, i * SlotStride + 8) >> 58) == 0) zeros++;
        return string.Format(CultureInfo.InvariantCulture,
            "  STOPPED RESEARCH ONLY: identity={0} unique observed header at record+0x{1:X}, candidate size={2}, high-qword zero-tags={3}; raw entries={4}. Other pointers were unreadable: unresolved, NOT counted or classifiable.\n",
            guid, sourceOffset, count, zeros, Convert.ToHexString(slots!));
    }

    // Caller holds the inventory probe lock and has disabled the memory cache.
    public static string Capture(Memory memory, WowBReader reader, WowBObservedUnit player,
        out (int CandidateSize, int ZeroTags, int Resolved, int ZeroIdentities, int OrdinarySize, int OrdinaryZeroTags, int Unclassified)? subtotal, bool active = false)
    {
        subtotal = null;
        try
        {
            if (!SamePlayer(memory, reader, player, active))
            { Forget(); return "Container research: experimental player, build or stopped state unavailable; no container candidates captured.\n"; }
            if (!Signatures(memory, reader.ExperimentalBuildNumber, out string markerBefore))
            { Forget(); return "Container research: readable code marker mismatch (" + markerBefore + "); no container candidates captured.\n"; }
            ulong table = memory.GetAbsolute64(reader.IsExperimental70862 ? Wow70862Offsets.ContainerIdentityTableCandidate : reader.IsExperimental70790 ? Wow70790Offsets.ContainerIdentityTableCandidate : reader.IsExperimental70755 ? Wow70755Offsets.ContainerIdentityTableCandidate : IdentityTableRva);
            byte[]? identityBytes = memory.ReadBytes64(table, CandidateIdentities * SlotStride);
            if (identityBytes == null || identityBytes.Length != CandidateIdentities * SlotStride)
            { Forget(); return "Container research: candidate identity table unreadable; no container candidates captured.\n"; }
            List<WowBReader.WowBPoolObject> records = reader.EnumeratePoolObjects();
            var next = new Candidate?[CandidateIdentities];
            var text = new StringBuilder();
            text.AppendLine($"{reader.ExperimentalBuildNumber} container research: indexes 0..4 are static candidates, not user bag numbers; no bag/full verdict.");
            text.AppendLine("Container code markers before: " + markerBefore + "; unreadable is not confirmation of a match.");
            bool comparable = _previousMemory == memory && _previousReader == reader && _previousPlayer == player.Guid && _previous != null;
            var allExcludedHeaders = new List<ulong>();
            for (int i = 0; i < CandidateIdentities; i++)
            {
                WowBGuid guid = GuidAt(identityBytes, i * SlotStride);
                if (guid.IsEmpty) { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] raw identity zero; meaning unknown.\n", i); continue; }
                int matches = 0;
                WowBReader.WowBPoolObject record = default;
                foreach (var item in records) if (item.Guid == guid) { matches++; record = item; }
                if (matches != 1 || !reader.IsPoolRecordCurrent(record))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] identity={1} pool matches={2}; object unavailable/ambiguous.\n", i, guid, matches); continue; }
                if (!FindCandidate(memory, reader, records, record, guid, out ulong data, out ulong sourceOffset, out byte[]? pointerBytes, out byte[]? header, out bool exploratory, out string detail, out List<ulong> excludedHeaders))
                {
                    text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] no unique stable identity-matching header; {1}. Unknown.\n", i, detail);
                    if (!active && (reader.IsExperimental70790 || reader.IsExperimental70862) && exploratory && pointerBytes != null && header != null)
                    {
                        text.Append(DescribeExploratory(memory, reader, player, record, guid, data, sourceOffset, pointerBytes, header));
                        text.Append(DescribeRecordOverlaps(memory, reader, record, records));
                    }
                    continue;
                }
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
                allExcludedHeaders.AddRange(excludedHeaders);
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
            bool markersAfterValid = Signatures(memory, reader.ExperimentalBuildNumber, out string markerAfter);
            bool excludedHeadersStillUncommitted = true;
            foreach (ulong address in allExcludedHeaders)
                if (!HeaderUncommitted(memory, reader, address)) { excludedHeadersStillUncommitted = false; break; }
            if (!excludedHeadersStillUncommitted || !Same(identityBytes, memory.ReadBytes64(table, CandidateIdentities * SlotStride)) || !markersAfterValid ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                !after.TryGetLocalPlayer(out WowBObservedUnit final) || final.Guid != player.Guid ||
                final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress || !SamePlayer(memory, reader, final, active))
            { Forget(); return "Container research: memory map, identity table, player, attachment, code or bot state changed; discarded. Code markers after: " + markerAfter + ".\n"; }
            // Aggregate only nonzero identity-table entries that all resolved
            // uniquely and stably. Zero identities, unscanned indexes, category
            // overlap and specialty bags remain unknown, so this is not a total.
            int sizeSum = 0, zeroSum = 0, resolved = 0, zeroIdentities = 0;
            int ordinarySize = 0, ordinaryZero = 0, unclassified = 0;
            var classifications = new Dictionary<WowBGuid, WowBBagKind>();
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
                int zeros = 0;
                for (int j = 0; j < (int)item.Count; j++)
                    if ((BitConverter.ToUInt64(item.Slots, j * SlotStride + 8) >> 58) == 0) zeros++;
                zeroSum += zeros;
                if (reader.UsesOrdinaryBagPolicy)
                {
                    WowBBagKind kind = WowBBagClassification.TryGet(player.Guid, guid, out WowBBagKind saved) ? saved : WowBBagKind.Unknown;
                    classifications.Add(guid, kind);
                    if (kind == WowBBagKind.Ordinary) { ordinarySize += (int)item.Count; ordinaryZero += zeros; }
                    else if (kind == WowBBagKind.Unknown) unclassified++;
                    text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] user category={1}; classification is per bag instance, not a detected item type.\n", i, kind);
                }
            }
            // A concurrent settings edit must not turn an observation into a false zero.
            foreach (var pair in classifications)
            {
                WowBBagKind current = WowBBagClassification.TryGet(player.Guid, pair.Key, out WowBBagKind saved) ? saved : WowBBagKind.Unknown;
                if (current != pair.Value) allResolved = false;
            }
            // A stable empty candidate table can represent a backpack with no
            // additional bags on 70755/70790. Zero identities remain unclassified,
            // so this still cannot prove the table is complete.
            bool complete = allResolved && (resolved > 0 || reader.UsesOrdinaryBagPolicy);
            if (complete) subtotal = (sizeSum, zeroSum, resolved, zeroIdentities, ordinarySize, ordinaryZero, unclassified);
            if (!active) { _previousMemory = memory; _previousReader = reader; _previousPlayer = player.Guid; _previous = next; _lastComplete = complete; }
            text.AppendLine("Container code markers after: " + markerAfter + "; unreadable is not confirmation of a match.");
            text.AppendLine("Container reads are non-atomic; unresolved/zero candidates are unknown. Grind may use the subtotal experimentally for selling on 70707/70755/70790/70862.");
            return text.ToString();
        }
        catch (Exception ex) { subtotal = null; Forget(); return "Container research unavailable: " + ex.Message + "\n"; }
    }
}

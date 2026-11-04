using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// 70707 research only. No Lua/client calls and no gameplay or selling decisions.
// All fields and pool-to-object relationships remain candidates until live-tested.
internal static class WowBContainerResearchProbe
{
    private const ulong IdentityTableRva = 0x6D41CA0;
    private const ulong SlotsOffset = 0x490, CapacityOffset = 0xAC0;
    private const int CandidateIdentities = 5, SlotStride = 16, MaxSlots = 64;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;
    private static readonly (ulong Rva, string Bytes)[] Markers = {
        (0xD5C7FC, "E86FAE8001"), (0xD5C820, "8B80C00A0000"),
        (0x2569186, "486390C00A0000"), (0x2569194, "4C8D8090040000"),
        (0x25691A0, "498B00")
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
    private static bool Pointer(ulong p, ulong width) => p > 0x10000 && (p & 7) == 0 && p <= UserLimit - width;
    private static WowBGuid GuidAt(byte[] b, int offset) => new WowBGuid(BitConverter.ToUInt64(b, offset), BitConverter.ToUInt64(b, offset + 8));
    private static bool Signatures(Memory memory)
    {
        foreach (var marker in Markers)
        {
            byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(marker.Rva), marker.Bytes.Length / 2);
            if (bytes == null || Convert.ToHexString(bytes) != marker.Bytes) return false;
        }
        return true;
    }
    private static bool SamePlayer(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        return !ClickAimUi.Running && !ClickAimUi.Paused && ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
            reader.IsExperimental70707 && WowBGrindFactions.TraceBuild70707(memory, reader) &&
            WowBGrindFactions.TraceIdentity70707(memory, reader, player, true);
    }
    private static void Forget() { _previousMemory = null; _previousReader = null; _previous = null; }

    // Caller holds the inventory probe lock and has disabled the memory cache.
    public static string Capture(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        try
        {
            if (!SamePlayer(memory, reader, player) || !Signatures(memory))
            { Forget(); return "Container research: 70707 markers or player unavailable; no container candidates captured.\n"; }
            ulong table = memory.GetAbsolute64(IdentityTableRva);
            byte[]? identityBytes = memory.ReadBytes64(table, CandidateIdentities * SlotStride);
            if (identityBytes == null || identityBytes.Length != CandidateIdentities * SlotStride)
            { Forget(); return "Container research: candidate identity table unreadable; no container candidates captured.\n"; }
            List<WowBReader.WowBPoolObject> records = reader.EnumeratePoolObjects();
            var next = new Candidate?[CandidateIdentities];
            var text = new StringBuilder();
            text.AppendLine("70707 container research: indexes 0..4 are static candidates, not user bag numbers; no bag/full verdict.");
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
                byte[]? pointerBytes = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
                if (pointerBytes == null || pointerBytes.Length != 8)
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] data pointer unreadable.\n", i); continue; }
                ulong data = BitConverter.ToUInt64(pointerBytes, 0);
                if (!Pointer(data, CapacityOffset + 4))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] data pointer outside bounds.\n", i); continue; }
                byte[]? header = memory.ReadBytes64(data, 0x20);
                byte[]? capacity = memory.ReadBytes64(data + CapacityOffset, 4);
                if (header == null || header.Length != 0x20 || BitConverter.ToUInt64(header, 8) != record.RecordPtr || GuidAt(header, 0x10) != guid ||
                    capacity == null || capacity.Length != 4)
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] header identity or +0xAC0 unreadable/mismatched.\n", i); continue; }
                uint count = BitConverter.ToUInt32(capacity, 0);
                if (count > MaxSlots)
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] raw +0xAC0={1} exceeds diagnostic cap {2}; unknown.\n", i, count, MaxSlots); continue; }
                int length = checked((int)count * SlotStride);
                byte[]? slots = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
                byte[]? slotsAgain = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(data + SlotsOffset, length);
                byte[]? recordGuid = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
                if (!Same(slots, slotsAgain) || !Same(header, memory.ReadBytes64(data, 0x20)) ||
                    !Same(capacity, memory.ReadBytes64(data + CapacityOffset, 4)) ||
                    !Same(pointerBytes, memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8)) ||
                    recordGuid == null || recordGuid.Length != 16 || GuidAt(recordGuid, 0) != guid || !reader.IsPoolRecordCurrent(record))
                { text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] unstable or unreadable; discarded.\n", i); continue; }
                var current = new Candidate { Guid = guid, Data = data, Count = count, Slots = slots! };
                next[i] = current;
                int zeroTags = 0;
                for (int j = 0; j < (int)count; j++) if ((BitConverter.ToUInt64(slots!, j * SlotStride) >> 58) == 0) zeroTags++;
                Candidate? prior = comparable ? _previous![i] : null;
                bool sameObject = prior != null && prior.Guid == guid && prior.Data == data && prior.Count == count;
                text.AppendFormat(CultureInfo.InvariantCulture, "container-candidate[{0}] identity={1} object=0x{2:X} raw+0xAC0={3}; first-qword high-six-zero={4}; previous={5}. NOT verified capacity/free slots.\n",
                    i, guid, data, count, zeroTags, sameObject ? "comparable" : "none or changed identity/size");
                if (!sameObject)
                    text.AppendFormat(CultureInfo.InvariantCulture, "  raw+0x490 entries={0}\n", Convert.ToHexString(slots!));
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
            if (!Same(identityBytes, memory.ReadBytes64(table, CandidateIdentities * SlotStride)) || !Signatures(memory) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                !after.TryGetLocalPlayer(out WowBObservedUnit final) || final.Guid != player.Guid ||
                final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress || !SamePlayer(memory, reader, final))
            { Forget(); return "Container research: identity table, player, attachment, code or bot state changed; discarded.\n"; }
            _previousMemory = memory; _previousReader = reader; _previousPlayer = player.Guid; _previous = next;
            text.AppendLine("Container reads are non-atomic; unresolved/zero candidates are unknown. No selling or gameplay uses these observations.");
            return text.ToString();
        }
        catch (Exception ex) { Forget(); return "Container research unavailable: " + ex.Message + "\n"; }
    }
}

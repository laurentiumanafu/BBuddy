using System;
using System.Collections.Generic;
using Bots.Gatherbuddy;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Read-only 70291 resource observations. The game-object vtable, position and entry
/// agree with two copper nodes and one Peacebloom; this does not establish lootability,
/// profession level, line of sight, success or permission to interact. A snapshot is
/// non-atomic and may miss nodes or go stale immediately after it is returned.
/// </summary>
internal static class GatherNodeScanner
{
    // Candidates observed on both 4- and 8-byte aligned node objects on build 70291.
    private const ulong NodeVtableCandidate = 0x50B1838;
    private const int HeaderSize = 0x94;

    internal readonly struct Node
    {
        public readonly WowBGuid Guid;
        public readonly uint Entry;
        public readonly string Name;
        public readonly uint RequiredSkill;
        public readonly bool IsHerb;
        public readonly bool DefaultBlacklisted;
        public readonly WoWPoint Position;
        public readonly double Distance;

        public Node(WowBGuid guid, GatherableNode kind, bool isHerb, WoWPoint position, double distance)
        {
            Guid = guid;
            Entry = kind.Entry;
            Name = kind.Name;
            RequiredSkill = kind.RequiredSkill;
            IsHerb = isHerb;
            DefaultBlacklisted = GatherableNodes.DefaultBlacklistedEntries.Contains(kind.Entry);
            Position = position;
            Distance = distance;
        }
    }

    private static uint GuidEntry(WowBGuid guid) => (uint)((guid.High >> 6) & 0x7FFFFFUL);

    private static GatherableNode? Classify(uint entry, out bool isHerb)
    {
        isHerb = true;
        foreach (GatherableNode item in GatherableNodes.Herbs)
            if (item.Entry == entry) return item;
        isHerb = false;
        foreach (GatherableNode item in GatherableNodes.Minerals)
            if (item.Entry == entry) return item;
        return null;
    }

    // Demand independent GUID and object-entry agreement, known vtable and backpointer.
    // A match is only an observation, never proof of harvestability.
    private static bool MatchingHeader(byte[]? header, ulong imageBase, ulong record, WowBGuid guid,
                                       out uint entry)
    {
        entry = 0;
        if (header == null || header.Length != HeaderSize || guid.IsEmpty) return false;
        if (BitConverter.ToUInt64(header, 0) != imageBase + NodeVtableCandidate
            || BitConverter.ToUInt64(header, 8) != record
            || new WowBGuid(BitConverter.ToUInt64(header, 16), BitConverter.ToUInt64(header, 24)) != guid)
            return false;
        uint observed = BitConverter.ToUInt32(header, 0x90);
        if (observed == 0 || observed != GuidEntry(guid)) return false;
        entry = observed;
        return true;
    }

    /// <summary>
    /// Capture nearby candidates around the current player. False means the player or
    /// process could not be read; true plus an empty list is still not proof of an empty
    /// area. Uses no Lua, interaction, process writes or legacy object wrappers.
    /// </summary>
    public static bool TryCapture(double radius, out List<Node> nodes)
    {
        nodes = new List<Node>();
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !memory.Is64BitTarget || radius <= 0 || !double.IsFinite(radius))
            return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null
                || !snap.TryGetLocalPlayer(out WowBObservedUnit player)) return false;
            var found = new HashSet<WowBGuid>();
            foreach (WowBReader.WowBPoolObject record in reader.EnumeratePoolObjects())
            {
                if (record.Guid.IsEmpty || found.Contains(record.Guid)) continue;
                uint guidEntry = GuidEntry(record.Guid);
                GatherableNode? kind = Classify(guidEntry, out bool herb);
                if (kind == null) continue;
                byte[]? ptrBytes = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
                if (ptrBytes == null || ptrBytes.Length != 8) continue;
                ulong ptr = BitConverter.ToUInt64(ptrBytes, 0);
                if (ptr <= 0x10000UL || ptr >= 0x00007FFFFFFFFF00UL || (ptr & 3UL) != 0) continue;
                byte[]? header = memory.ReadBytes64(ptr, HeaderSize);
                if (!MatchingHeader(header, memory.ImageBase64, record.RecordPtr, record.Guid, out uint entry)
                    || entry != kind.Entry) continue;
                byte[]? position = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordPositionOffset, 12);
                if (position == null || position.Length != 12) continue;
                float x = BitConverter.ToSingle(position, 0), y = BitConverter.ToSingle(position, 4),
                      z = BitConverter.ToSingle(position, 8);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
                    || Math.Abs(x) >= 100000 || Math.Abs(y) >= 100000 || Math.Abs(z) >= 100000) continue;
                double dx = x - player.Location.X, dy = y - player.Location.Y, dz = z - player.Location.Z;
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance > radius) continue;
                // Reduce stale-pointer risk. Full GUID and pool-slot rechecks do not
                // eliminate ABA races or prove future validity.
                byte[]? ptrAfter = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
                byte[]? guidAfter = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
                byte[]? headerAfter = memory.ReadBytes64(ptr, HeaderSize);
                if (ptrAfter == null || ptrAfter.Length != 8 || BitConverter.ToUInt64(ptrAfter, 0) != ptr
                    || guidAfter == null || guidAfter.Length != 16
                    || new WowBGuid(BitConverter.ToUInt64(guidAfter, 0), BitConverter.ToUInt64(guidAfter, 8)) != record.Guid
                    || !MatchingHeader(headerAfter, memory.ImageBase64, record.RecordPtr, record.Guid, out uint entryAfter)
                    || entryAfter != entry || !reader.IsPoolRecordCurrent(record)) continue;
                found.Add(record.Guid);
                nodes.Add(new Node(record.Guid, kind, herb, new WoWPoint(x, y, z), distance));
            }
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            {
                nodes.Clear();
                return false;
            }
            nodes.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return true;
        }
        catch
        {
            nodes.Clear();
            return false;
        }
    }

    public static string SelfTest()
    {
        int passed = 0, total = 0;
        Action<bool> checkFixture = value => { total++; if (value) passed++; };
        var copper = new WowBGuid(0x1440UL, 0x2C0118002001B0C0UL);
        var herb = new WowBGuid(0x1282UL, 0x2C01180020019480UL);
        checkFixture(GuidEntry(copper) == 1731 && GuidEntry(herb) == 1618);
        GatherableNode? copperKind = Classify(1731, out bool copperHerb);
        GatherableNode? herbKind = Classify(1618, out bool herbHerb);
        checkFixture(copperKind != null && !copperHerb && herbKind != null && herbHerb);
        checkFixture(Classify(0, out _) == null);
        const ulong image = 0x100000UL, rec = 0x200000UL;
        byte[] data = new byte[HeaderSize];
        Array.Copy(BitConverter.GetBytes(image + NodeVtableCandidate), 0, data, 0, 8);
        Array.Copy(BitConverter.GetBytes(rec), 0, data, 8, 8);
        Array.Copy(BitConverter.GetBytes(copper.Low), 0, data, 16, 8);
        Array.Copy(BitConverter.GetBytes(copper.High), 0, data, 24, 8);
        Array.Copy(BitConverter.GetBytes(1731u), 0, data, 0x90, 4);
        checkFixture(MatchingHeader(data, image, rec, copper, out uint entry) && entry == 1731);
        checkFixture(!MatchingHeader(data, image, rec + 8, copper, out _));
        checkFixture(!MatchingHeader(data, image, rec, herb, out _));
        Array.Copy(BitConverter.GetBytes(1618u), 0, data, 0x90, 4);
        checkFixture(!MatchingHeader(data, image, rec, copper, out _));
        return "[Gather] node scanner self-test " + passed + "/" + total + " (no live-client validation)";
    }
}

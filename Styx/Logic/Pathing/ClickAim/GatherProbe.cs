using System;
using System.Collections.Generic;
using System.Threading;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Read-only experiment, not a node detector. The mouseover address is an unverified
/// candidate; even a matching pool GUID is not proof that it belongs to the hovered node.
/// No interactions, Lua, memory writes or legacy object wrappers are used.
/// </summary>
internal static class GatherProbe
{
    private static int _active;

    public static bool Start()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        try
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "GatherProbe" };
            thread.Start();
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    // Diagnostic only: some game-object headers are 4-byte rather than 8-byte aligned.
    // Do not confuse readable memory or this structural match with proof of object type.
    private static bool TryCandidateHeader(GreenMagic.Memory memory, WowBReader.WowBPoolObject record,
                                           out ulong pointer, out ulong vtable)
    {
        pointer = 0;
        vtable = 0;
        byte[]? ptrBytes = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
        if (ptrBytes == null || ptrBytes.Length != 8) return false;
        ulong ptr = BitConverter.ToUInt64(ptrBytes, 0);
        if (ptr <= 0x10000UL || ptr >= 0x00007FFFFFFFFFDFUL || (ptr & 3UL) != 0) return false;
        byte[]? header = memory.ReadBytes64(ptr, 32);
        byte[]? guidBytes = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
        byte[]? again = memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
        if (header == null || header.Length != 32 || guidBytes == null || guidBytes.Length != 16
            || again == null || again.Length != 8 || BitConverter.ToUInt64(again, 0) != ptr) return false;
        ulong vt = BitConverter.ToUInt64(header, 0);
        var headerGuid = new WowBGuid(BitConverter.ToUInt64(header, 16), BitConverter.ToUInt64(header, 24));
        var recordGuid = new WowBGuid(BitConverter.ToUInt64(guidBytes, 0), BitConverter.ToUInt64(guidBytes, 8));
        if (vt < memory.ImageBase64 || BitConverter.ToUInt64(header, 8) != record.RecordPtr
            || headerGuid != record.Guid || recordGuid != record.Guid) return false;
        pointer = ptr;
        vtable = vt;
        return true;
    }

    // Two observations: Copper Vein 1731 and a purported herb 1618. This is still a
    // hypothesis, used only for logging and never to authorize an interaction.
    private static uint CandidateEntry(WowBGuid guid) => (uint)((guid.High >> 6) & 0x7FFFFFUL);

    public static string SelfTest()
    {
        uint copper = CandidateEntry(new WowBGuid(0x1295UL, 0x2C0118002001B0C0UL));
        uint herb = CandidateEntry(new WowBGuid(0x1282UL, 0x2C01180020019480UL));
        bool ok = copper == 1731 && herb == 1618
                  && DescribeCandidate(copper).Contains("Copper Vein")
                  && DescribeCandidate(herb).Contains("Peacebloom");
        return "[Gather probe] captured GUID candidate fixtures: " + (ok ? "pass" : "FAIL")
               + " (does not validate live node types)";
    }

    private static string DescribeCandidate(uint entry)
    {
        foreach (Bots.Gatherbuddy.GatherableNode node in Bots.Gatherbuddy.GatherableNodes.Herbs)
            if (node.Entry == entry) return "possible herb " + node.Name + " (legacy ID)";
        foreach (Bots.Gatherbuddy.GatherableNode node in Bots.Gatherbuddy.GatherableNodes.Minerals)
            if (node.Entry == entry) return "possible ore " + node.Name + " (legacy ID)";
        return "no legacy herb/ore ID match";
    }

    private static void PrintNearbyCandidates(WowBReader reader, GreenMagic.Memory memory)
    {
        if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null
            || !snap.TryGetLocalPlayer(out WowBObservedUnit me))
        {
            Logging.Write("[Gather probe] player snapshot failed; cannot rank pool records by distance");
            return;
        }
        var candidates = new List<(double Distance, WowBReader.WowBPoolObject Record, ulong Object, ulong Rva)>();
        foreach (WowBReader.WowBPoolObject obj in reader.EnumeratePoolObjects())
        {
            if (!TryCandidateHeader(memory, obj, out ulong ptr, out ulong vt)) continue;
            ulong rva = vt - memory.ImageBase64;
            if (rva == WowBOffsets.PlayerObjectVtable || rva == WowBOffsets.CGUnitFnTable) continue;
            byte[]? coords = memory.ReadBytes64(obj.RecordPtr + WowBOffsets.RecordPositionOffset, 12);
            if (coords == null || coords.Length != 12) continue;
            float x = BitConverter.ToSingle(coords, 0), y = BitConverter.ToSingle(coords, 4),
                  z = BitConverter.ToSingle(coords, 8);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            double d = ClickAimMath.Len(x - me.Location.X, y - me.Location.Y);
            if (d > 60 || Math.Abs(z - me.Location.Z) > 40) continue;
            candidates.Add((d, obj, ptr, rva));
        }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        Logging.Write("[Gather probe] " + candidates.Count + " structurally matching non-unit pool records "
                      + "within 60 yd (unverified types and positions); nearest 20:");
        for (int i = 0; i < candidates.Count && i < 20; i++)
        {
            var c = candidates[i];
            uint entry = CandidateEntry(c.Record.Guid);
            Logging.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[Gather probe] {0:F1} yd {1} obj=0x{2:X} vtableRva=0x{3:X} "
                + "candidateEntry={4} ({5})", c.Distance, c.Record, c.Object, c.Rva,
                entry, DescribeCandidate(entry)));
        }
    }

    private static void Run()
    {
        try
        {
            var memory = ObjectManager.Wow;
            var reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget)
            {
                Logging.Write("[Gather probe] attach to wowb.exe first");
                return;
            }
            using var uncached = memory.TemporaryCacheState(false);
            ulong address = memory.GetAbsolute64(WowBOffsets.MouseoverGuidCandidate);
            byte[]? baseline = memory.ReadBytes64(address, 16);
            // Census first; the eight-second hover interval starts only after it completes.
            PrintNearbyCandidates(reader, memory);
            if (GatherNodeScanner.TryCapture(100.0, out List<GatherNodeScanner.Node> nodes))
            {
                Logging.Write("[Gather probe] matching-layout resource candidates within 100 yd: "
                              + nodes.Count + " (read-only; depleted state and skills unknown)");
                for (int i = 0; i < nodes.Count && i < 20; i++)
                {
                    GatherNodeScanner.Node node = nodes[i];
                    Logging.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[Gather probe] {0:F1} yd {1} entry={2} {3} skill={4}{5} guid={6}",
                        node.Distance, node.IsHerb ? "herb" : "ore", node.Entry, node.Name,
                        node.RequiredSkill, node.DefaultBlacklisted ? " (legacy blacklist)" : "", node.Guid));
                }
            }
            else
                Logging.Write("[Gather probe] resource scan could not validate the player; not an empty-area result");
            Logging.Write("[Gather probe] read-only: within 8 seconds put the game cursor over a visible "
                          + "herb or ore node and leave it there. No click or Lua is used.");
            Thread.Sleep(8000);
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            {
                Logging.Write("[Gather probe] client changed during the sample; discarded");
                return;
            }
            byte[]? sample = memory.ReadBytes64(address, 16);
            if (sample == null || sample.Length != 16)
            {
                Logging.Write("[Gather probe] mouseover GUID candidate could not be read");
                return;
            }
            WowBGuid guid = new WowBGuid(BitConverter.ToUInt64(sample, 0), BitConverter.ToUInt64(sample, 8));
            string before = baseline != null && baseline.Length == 16
                ? new WowBGuid(BitConverter.ToUInt64(baseline, 0), BitConverter.ToUInt64(baseline, 8)).ToString()
                : "unreadable";
            Logging.Write("[Gather probe] candidate RVA 0x" + WowBOffsets.MouseoverGuidCandidate.ToString("X")
                          + " before=" + before + " hovered=" + guid
                          + " (candidate only; a value is not proof of a node)");
            if (guid.IsEmpty || !reader.TryFindObjectByGuid(guid, out WowBReader.WowBPoolObject obj))
            {
                Logging.Write("[Gather probe] candidate is empty or not found in the pool. "
                              + "It may be the wrong offset; no node was identified.");
                return;
            }
            // Recheck both the pool record and mouseover, since neither scan is atomic.
            byte[]? stillHovered = memory.ReadBytes64(address, 16);
            byte[]? recordGuid = memory.ReadBytes64(obj.RecordPtr + WowBOffsets.RecordGuidLoOffset, 16);
            if (stillHovered == null || recordGuid == null || stillHovered.Length != 16 || recordGuid.Length != 16
                || new WowBGuid(BitConverter.ToUInt64(stillHovered, 0), BitConverter.ToUInt64(stillHovered, 8)) != guid
                || new WowBGuid(BitConverter.ToUInt64(recordGuid, 0), BitConverter.ToUInt64(recordGuid, 8)) != guid)
            {
                Logging.Write("[Gather probe] mouseover or record changed during the scan; discarded");
                return;
            }
            uint candidateEntry = CandidateEntry(guid);
            Logging.Write("[Gather probe] matching pool record " + obj + "; candidate GUID entry="
                          + candidateEntry + " (" + DescribeCandidate(candidateEntry)
                          + "; high GUID >> 6 & 0x7FFFFF, not verified)");
            if (reader.TryGetObjectTransform(obj, out float x, out float y, out float z, out float facing))
                Logging.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[Gather probe] candidate record position ({0:F2}, {1:F2}, {2:F2}), facing {3:F2}; "
                    + "position is not yet validated for game objects", x, y, z, facing));
            Logging.Write("[Gather probe] record +0x00: " + reader.DumpWords(obj.RecordPtr, 64));
            if (TryCandidateHeader(memory, obj, out ulong objectPtr, out ulong vtable))
            {
                ulong rva = vtable >= memory.ImageBase64 ? vtable - memory.ImageBase64 : 0;
                Logging.Write("[Gather probe] structurally matching object=0x" + objectPtr.ToString("X")
                              + " vtable RVA=0x" + rva.ToString("X") + " (type unknown)");
                byte[]? entryBytes = memory.ReadBytes64(objectPtr + 0x90, 4);
                if (entryBytes != null && entryBytes.Length == 4)
                    Logging.Write("[Gather probe] candidate object +0x90=" + BitConverter.ToUInt32(entryBytes, 0)
                                  + "; GUID-derived candidate=" + candidateEntry + " (neither is verified)");
                Logging.Write("[Gather probe] object +0x00: " + reader.DumpWords(objectPtr, 64));
                Logging.Write("[Gather probe] object +0x200: " + reader.DumpWords(objectPtr + 0x200, 32));
            }
            else
                Logging.Write("[Gather probe] no 4-byte-aligned object header passed the backpointer/GUID checks; "
                              + "game objects may have another layout");
            Logging.Write("[Gather probe] complete. Compare separate samples over a herb, ore, and empty ground.");
        }
        catch (Exception ex)
        {
            Logging.Write("[Gather probe] sample failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }
}

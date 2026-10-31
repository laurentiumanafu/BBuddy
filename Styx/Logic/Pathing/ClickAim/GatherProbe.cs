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

    private static void PrintNearbyCandidates(WowBReader reader, ulong imageBase)
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
            if (!reader.TryGetUnitObject(obj.RecordPtr, out ulong ptr, out ulong vt, out bool isPlayer)) continue;
            ulong rva = vt >= imageBase ? vt - imageBase : 0;
            if (isPlayer || rva == WowBOffsets.CGUnitFnTable || rva == 0) continue;
            if (!reader.TryGetObjectTransform(obj, out float x, out float y, out float z, out _)
                || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
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
            Logging.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[Gather probe] {0:F1} yd {1} obj=0x{2:X} vtableRva=0x{3:X}",
                c.Distance, c.Record, c.Object, c.Rva));
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
            PrintNearbyCandidates(reader, memory.ImageBase64);
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
            Logging.Write("[Gather probe] matching pool record " + obj + "; candidate GUID low 24 bits="
                          + guid.EntryId + " (not a verified entry ID on this client)");
            if (reader.TryGetObjectTransform(obj, out float x, out float y, out float z, out float facing))
                Logging.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[Gather probe] candidate record position ({0:F2}, {1:F2}, {2:F2}), facing {3:F2}; "
                    + "position is not yet validated for game objects", x, y, z, facing));
            Logging.Write("[Gather probe] record +0x00: " + reader.DumpWords(obj.RecordPtr, 64));
            if (reader.TryGetUnitObject(obj.RecordPtr, out ulong objectPtr, out ulong vtable, out _))
            {
                ulong rva = vtable >= memory.ImageBase64 ? vtable - memory.ImageBase64 : 0;
                Logging.Write("[Gather probe] structurally matching object=0x" + objectPtr.ToString("X")
                              + " vtable RVA=0x" + rva.ToString("X") + " (type unknown)");
                Logging.Write("[Gather probe] object +0x00: " + reader.DumpWords(objectPtr, 64));
                Logging.Write("[Gather probe] object +0x200: " + reader.DumpWords(objectPtr + 0x200, 32));
            }
            else
                Logging.Write("[Gather probe] no aligned object header passed the unit-style backpointer/GUID checks; "
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

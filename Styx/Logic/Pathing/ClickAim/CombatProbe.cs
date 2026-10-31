using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Read-only comparative samples of the player, not a combat-state detector. A changed
/// bit may represent a target, health, power, animation, PvP or another unrelated state.
/// </summary>
internal static class CombatProbe
{
    private static int _active;
    private static int _sequence;
    private static GreenMagic.Memory? _previousProcess;
    private static WowBGuid _previousGuid;
    private static ulong _previousUnit;
    private static readonly Dictionary<int, uint[]> Previous = new Dictionary<int, uint[]>();
    private const int ChunkWords = 32;
    private const int ObjectBytes = 0x2400;
    private const int RecordKey = 0x10000;
    private const int MaxChangesPrinted = 120;

    public static bool Start()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        try
        {
            var thread = new Thread(Sample) { IsBackground = true, Name = "CombatProbe" };
            thread.Start();
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    private static void Sample()
    {
        try
        {
            var memory = ObjectManager.Wow;
            var reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget)
            {
                Logging.Write("[Combat probe] attach to wowb.exe first");
                return;
            }
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null
                || !snap.TryGetLocalPlayer(out WowBObservedUnit me)
                || !reader.TryGetUnitFields(me.UnitAddress, out WowBReader.WowBUnitFields fields))
            {
                Logging.Write("[Combat probe] player snapshot unavailable; sample discarded");
                return;
            }
            var current = new Dictionary<int, uint[]>();
            for (int offset = 0; offset < ObjectBytes; offset += ChunkWords * 4)
            {
                if (reader.TryReadWords(me.UnitAddress + (ulong)offset, ChunkWords, out uint[] unitWords))
                    current[offset] = unitWords;
            }
            for (int offset = 0; offset < 0x100; offset += ChunkWords * 4)
            {
                if (reader.TryReadWords(me.RecordAddress + (ulong)offset, ChunkWords, out uint[] recordWords))
                    current[RecordKey + offset] = recordWords;
            }

            // Discard a non-atomic observation if the player identity or unit pointer changed.
            byte[]? guidBytes = memory.ReadBytes64(me.RecordAddress + WowBOffsets.RecordGuidLoOffset, 16);
            byte[]? ptrBytes = memory.ReadBytes64(me.RecordAddress + WowBOffsets.RecordDataBlockPtrOffset, 8);
            if (current.Count == 0 || ObjectManager.Wow != memory || ObjectManager.WowB != reader
                || guidBytes == null || guidBytes.Length != 16 || ptrBytes == null || ptrBytes.Length != 8
                || new WowBGuid(BitConverter.ToUInt64(guidBytes, 0), BitConverter.ToUInt64(guidBytes, 8)) != me.Guid
                || BitConverter.ToUInt64(ptrBytes, 0) != me.UnitAddress)
            {
                Logging.Write("[Combat probe] player changed or reads failed during capture; sample discarded");
                return;
            }
            bool comparable = _previousProcess == memory && _previousGuid == me.Guid && _previousUnit == me.UnitAddress;
            if (!comparable) { Previous.Clear(); _sequence = 0; }
            _sequence++;
            Logging.Write("[Combat probe] sample " + _sequence + " player=" + me.Guid + " " + fields
                          + "; unit/record chunks read=" + current.Count
                          + ". State is not inferred: note whether you were idle or in combat.");
            if (Previous.Count == 0)
            {
                Logging.Write("[Combat probe] baseline saved. Press again while a monster is attacking, "
                              + "and again after combat ends; compare repeated idle/fight cycles.");
            }
            else
            {
                int changed = 0, printed = 0, comparableChunks = 0;
                // Start at the area containing observed flags/power, so a noisy header
                // cannot consume the print budget before the interesting comparison.
                int unitChunks = ObjectBytes / (ChunkWords * 4);
                for (int step = 0; step < unitChunks + 2; step++)
                {
                    int key = step < unitChunks
                        ? (0x1B80 + step * ChunkWords * 4) % ObjectBytes
                        : RecordKey + (step - unitChunks) * ChunkWords * 4;
                    if (!current.TryGetValue(key, out uint[]? values)
                        || !Previous.TryGetValue(key, out uint[]? old) || old.Length != values.Length) continue;
                    comparableChunks++;
                    for (int i = 0; i < old.Length; i++)
                    {
                        uint after = values[i];
                        if (old[i] == after) continue;
                        changed++;
                        if (printed >= MaxChangesPrinted) continue;
                        bool record = key >= RecordKey;
                        int offset = (record ? key - RecordKey : key) + i * 4;
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Combat probe] {0}+0x{1:X}: 0x{2:X8} -> 0x{3:X8}, xor=0x{4:X8}",
                            record ? "record" : "unit", offset, old[i], after, old[i] ^ after));
                        printed++;
                    }
                }
                Logging.Write("[Combat probe] " + changed + " changed words in " + comparableChunks
                              + " comparable chunks; printed " + printed
                              + ". Changes alone do not establish a combat offset.");
            }
            Previous.Clear();
            foreach (KeyValuePair<int, uint[]> chunk in current) Previous.Add(chunk.Key, chunk.Value);
            _previousProcess = memory;
            _previousGuid = me.Guid;
            _previousUnit = me.UnitAddress;
        }
        catch (Exception ex) { Logging.Write("[Combat probe] sample failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}

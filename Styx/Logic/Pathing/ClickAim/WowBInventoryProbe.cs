using System;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Research only. The observed +0x9440 base and array format are static leads,
// not a validated player inventory, slot list, item ID or free-slot count.
internal static class WowBInventoryProbe
{
    private const ulong CandidateOffset = 0x9440, LookupRva = 0x290CFC0, HandlerRva = 0x2F5F2B0;
    private const int HeaderSize = 0x28, EntrySize = 16, MaxEntries = 256;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;
    private const string LookupSignature = "48895C2408574883EC208BDA488BF9";
    private const string HandlerSignature = "48895C24084889742418574881EC2004";
    private static readonly object Gate = new object();
    private static Memory? _previousMemory;
    private static WowBReader? _previousReader;
    private static WowBGuid _previousPlayer;
    private static ulong _previousUnit, _previousArray;
    private static byte[]? _previousEntries;

    private static bool Same(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static string Capture()
    {
        lock (Gate)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) return "Stop the bot and routes before the inventory probe.";
            Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget) return "Attach to wowb.exe 70291 and enter the world first.";
            try
            {
                using var uncached = memory.TemporaryCacheState(false);
                byte[]? lookup = memory.ReadBytes64(memory.GetAbsolute64(LookupRva), 15);
                byte[]? handler = memory.ReadBytes64(memory.GetAbsolute64(HandlerRva), 16);
                if (lookup == null || handler == null || Convert.ToHexString(lookup) != LookupSignature || Convert.ToHexString(handler) != HandlerSignature)
                    return "Inventory trace signatures unreadable or different; no probe captured.";
                if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                    !before.TryGetLocalPlayer(out WowBObservedUnit player) || !player.IsLocalPlayer || !player.HasPlayerVtable ||
                    !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, player) || player.UnitAddress > UserLimit - CandidateOffset - HeaderSize)
                    return "Player or build identity unavailable; no probe captured.";
                ulong candidate = player.UnitAddress + CandidateOffset;
                byte[]? header = memory.ReadBytes64(candidate, HeaderSize);
                if (header == null || header.Length != HeaderSize) return "Candidate header unreadable; no probe captured.";
                uint count = BitConverter.ToUInt32(header, 0);
                ulong array = BitConverter.ToUInt64(header, 8);
                if (count > MaxEntries || (count != 0 && (array < 0x10000 || array > UserLimit - (ulong)count * EntrySize)))
                    return "Candidate count or array address outside diagnostic bounds; no probe captured.";
                int length = checked((int)count * EntrySize);
                byte[]? first = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(array, length);
                byte[]? second = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(array, length);
                byte[]? headerAfter = memory.ReadBytes64(candidate, HeaderSize);
                if (!Same(first, second) || !Same(header, headerAfter) || ClickAimUi.Running || ClickAimUi.Paused ||
                    ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                    !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                    !after.TryGetLocalPlayer(out WowBObservedUnit final) || after.LocalGuid != before.LocalGuid ||
                    final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress ||
                    !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, final) || ClickAimUi.Running || ClickAimUi.Paused)
                    return "Candidate, player, attachment or bot state changed/unreadable; probe discarded.";

                bool comparable = _previousMemory == memory && _previousReader == reader && _previousPlayer == player.Guid &&
                    _previousUnit == player.UnitAddress && _previousArray == array && _previousEntries != null && _previousEntries.Length == length;
                var text = new StringBuilder();
                text.AppendLine("70291 read-only inventory research snapshot; candidate only, not a bag or full-slot verdict.");
                text.AppendFormat(CultureInfo.InvariantCulture, "UTC {0:O}; player GUID {1}; player object 0x{2:X}; candidate header +0x{3:X}=0x{4:X}; raw header={5}\n",
                    DateTime.UtcNow, player.Guid, player.UnitAddress, CandidateOffset, candidate, Convert.ToHexString(header));
                text.AppendFormat(CultureInfo.InvariantCulture, "candidate count={0}; array=0x{1:X}; stride=16; previous={2}\n", count, array, comparable ? "comparable" : "none or different context");
                int changed = 0;
                for (int i = 0; i < (int)count; i++)
                {
                    int offset = i * EntrySize;
                    string current = Convert.ToHexString(first!, offset, EntrySize);
                    if (comparable)
                    {
                        string prior = Convert.ToHexString(_previousEntries!, offset, EntrySize);
                        if (prior != current) { changed++; text.AppendFormat(CultureInfo.InvariantCulture, "entry[{0}] {1} -> {2}\n", i, prior, current); }
                    }
                    else text.AppendFormat(CultureInfo.InvariantCulture, "entry[{0}] {1}\n", i, current);
                }
                if (comparable) text.AppendFormat(CultureInfo.InvariantCulture, "changed entries={0}; identical bytes do not establish a slot was empty.\n", changed);
                text.AppendLine("Uninterpreted data and process addresses are sensitive. Reads are non-atomic; no inventory, item, bag capacity or mail eligibility is verified.");
                _previousMemory = memory; _previousReader = reader; _previousPlayer = player.Guid;
                _previousUnit = player.UnitAddress; _previousArray = array; _previousEntries = first;
                return text.ToString();
            }
            catch (Exception ex) { return "Inventory probe failed: " + ex.Message; }
        }
    }
}

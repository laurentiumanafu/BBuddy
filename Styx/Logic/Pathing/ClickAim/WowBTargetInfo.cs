using System;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Read-only vendor/NPC investigation, never used to select, move, sell or fight.
internal static class WowBTargetInfo
{
    public static string Capture()
    {
        if (ClickAimUi.Running || ClickAimUi.Paused) return "Stop the bot before capturing target information.";
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !memory.Is64BitTarget) return "Attach to wowb.exe 70291 and enter the world first.";
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetLocalPlayer(out WowBObservedUnit me) || !before.TryGetSelectedTarget(out WowBObservedUnit target) ||
                target.IsLocalPlayer || target.HasPlayerVtable || target.Guid.IsEmpty)
                return "Select an NPC first; the selected NPC must be present in the validated pool snapshot.";
            if (!WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, target))
                return "70291 signatures or selected NPC identity could not be verified; no target data captured.";

            var text = new StringBuilder();
            text.AppendLine("70291 read-only target diagnostic; raw fields are observations, not vendor or hostility proof.");
            text.AppendFormat(CultureInfo.InvariantCulture, "UTC {0:O}; target GUID {1}; local GUID {2}\n", before.CapturedUtc, target.Guid, me.Guid);
            text.AppendFormat(CultureInfo.InvariantCulture, "record=0x{0:X}; unit/data=0x{1:X}; player-vtable={2}; local={3}\n", target.RecordAddress, target.UnitAddress, target.HasPlayerVtable, target.IsLocalPlayer);
            text.AppendFormat(CultureInfo.InvariantCulture, "target position=({0:F3}, {1:F3}, {2:F3}), facing={3:F4}; player=({4:F3}, {5:F3}, {6:F3}); horizontal distance={7:F2} yd\n",
                target.Location.X, target.Location.Y, target.Location.Z, target.Facing, me.Location.X, me.Location.Y, me.Location.Z,
                ClickAimMath.Len(target.Location.X - me.Location.X, target.Location.Y - me.Location.Y));
            if (reader.TryGetUnitFields(target.UnitAddress, out WowBReader.WowBUnitFields fields))
                text.AppendLine("unit fields: " + fields + " (max power candidate)");
            else text.AppendLine("unit fields: unreadable");
            bool factionReadable = reader.TryReadFactionTemplateId(target.UnitAddress, out uint faction);
            if (factionReadable)
                text.AppendLine("faction-template key: " + faction.ToString(CultureInfo.InvariantCulture) + " (not reaction or merchant status)");
            else text.AppendLine("faction-template key: unreadable");
            text.AppendLine("NPC name, merchant status, inventory and attacker identity: not validated on 70291.");
            text.AppendLine("Raw record +0x000..0x0FF and unit/data +0x0000..0x21FF; 32-bit little-endian words, uninterpreted:");
            AppendWindows(text, reader, "record", target.RecordAddress, 0x100);
            AppendWindows(text, reader, "unit", target.UnitAddress, 0x2200);
            if (ClickAimUi.Running || ClickAimUi.Paused || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                after.LocalGuid != before.LocalGuid || after.TargetGuid != before.TargetGuid ||
                !after.TryGetSelectedTarget(out WowBObservedUnit final) || final.Guid != target.Guid ||
                final.RecordAddress != target.RecordAddress || final.UnitAddress != target.UnitAddress ||
                !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, final) || reader.ReadTargetGuid() != target.Guid ||
                (factionReadable && (!reader.TryReadFactionTemplateId(target.UnitAddress, out uint finalFaction) || finalFaction != faction)) ||
                ClickAimUi.Running || ClickAimUi.Paused)
                return "Target, faction key, attachment or bot state changed during capture; target data discarded.";
            text.AppendLine("End target diagnostic. Reads are non-atomic; addresses and data may change. Log contains process addresses and GUIDs.");
            return text.ToString();
        }
        catch (Exception ex) { return "Target info could not be read: " + ex.Message; }
    }

    private static void AppendWindows(StringBuilder text, WowBReader reader, string label, ulong address, int size)
    {
        for (int offset = 0; offset < size; offset += 64)
        {
            text.Append(label).Append(" +0x").Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append(':');
            if (!reader.TryReadWords(address + (ulong)offset, 16, out uint[] words)) text.Append(" <unreadable>");
            else foreach (uint word in words) text.Append(' ').Append(word.ToString("X8", CultureInfo.InvariantCulture));
            text.AppendLine();
        }
    }
}

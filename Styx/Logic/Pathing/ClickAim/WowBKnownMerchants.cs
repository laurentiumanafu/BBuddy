using System;
using System.Globalization;
using GreenMagic;
using Styx.WoWInternals;
using Styx.Offsets;

namespace Styx.Logic.Pathing;

// The user confirms individual NPCs. No observed 70291 field is yet proven to
// mean merchant type; neither +0x2084 nor a faction or unit flag is that proof.
internal static class WowBKnownMerchants
{
    public static bool CaptureSelected(out WowBGuid guid, out uint map, out string reason)
    {
        guid = default; map = 0; reason = "";
        if (ClickAimUi.Running || ClickAimUi.Paused) { reason = "Stop the bot first."; return false; }
        Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !memory.Is64BitTarget) { reason = "Attach to wowb.exe 70291 first."; return false; }
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!ReadMap(reader, out uint firstMap) || !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetSelectedTarget(out WowBObservedUnit selected) || selected.IsLocalPlayer || selected.HasPlayerVtable ||
                !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, selected) || !LivingCandidate(memory, reader, selected))
            { reason = "Select a living NPC and ensure client signatures and map are readable."; return false; }
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                after.LocalGuid != before.LocalGuid || after.TargetGuid != before.TargetGuid ||
                !after.TryGetSelectedTarget(out WowBObservedUnit final) || final.Guid != selected.Guid ||
                final.RecordAddress != selected.RecordAddress || final.UnitAddress != selected.UnitAddress ||
                !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, final) || reader.ReadTargetGuid() != selected.Guid ||
                !ReadMap(reader, out uint finalMap) || finalMap != firstMap || !LivingCandidate(memory, reader, final) || ClickAimUi.Running || ClickAimUi.Paused)
            { reason = "Selection, map or attachment changed; nothing was recorded."; return false; }
            guid = selected.Guid; map = firstMap; return true;
        }
        catch (Exception ex) { reason = "Merchant selection could not be read: " + ex.Message; return false; }
    }

    // Stopped-bot setup only. Returns structural 70707 candidates for the
    // user-confirmed town plan; this does not authorize movement on its own.
    public static bool TryReadExperimentalTown(out uint map, out double x, out double y, out double z)
    {
        map = 0; x = y = z = 0;
        Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !reader.IsExperimental70707 || ClickAimUi.Running || ClickAimUi.Paused) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadExperimentalMapCandidate(out uint firstMap) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetLocalPlayer(out WowBObservedUnit first) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                !after.TryGetLocalPlayer(out WowBObservedUnit last) || first.Guid != last.Guid ||
                first.RecordAddress != last.RecordAddress || first.UnitAddress != last.UnitAddress ||
                !reader.TryReadExperimentalMapCandidate(out uint secondMap) || firstMap != secondMap ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused) return false;
            map = firstMap; x = last.Location.X; y = last.Location.Y; z = last.Location.Z;
            return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
        }
        catch { return false; }
    }

    private static bool ReadMap(WowBReader reader, out uint map)
        => reader.IsExperimental70707 && !reader.ExperimentalGrindActive
            ? reader.TryReadExperimentalMapCandidate(out map) : reader.TryReadMapId(out map);

    private static bool LivingCandidate(Memory memory, WowBReader reader, WowBObservedUnit unit)
    {
        if (!reader.IsExperimental70707 || reader.ExperimentalGrindActive)
            return reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields) && fields.Health > 0;
        byte[]? first = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.HealthCandidateOffset, 16);
        byte[]? second = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.HealthCandidateOffset, 16);
        if (first == null || second == null || first.Length != 16 || second.Length != 16 ||
            Convert.ToHexString(first) != Convert.ToHexString(second)) return false;
        ulong hp = BitConverter.ToUInt64(first, 0), max = BitConverter.ToUInt64(first, 8);
        return hp > 0 && max > 0 && hp <= max && max <= uint.MaxValue &&
            WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, unit);
    }

    // 70938 GUIDs contain an observed type/entry candidate in the high half;
    // the low half may differ after a server restart or respawn. This is NOT
    // proof of the same NPC. Only the town-scoped, unique fallback in Grind may
    // use it; exact GUIDs remain preferred and other builds stay exact-only.
    public static bool Same70938HighCandidate(VendorFile.VendorPoint point, WowBGuid guid)
    {
        if (guid.IsEmpty || guid.High == 0) return false;
        foreach (string recorded in point.MerchantGuids)
            if (recorded.Length == 34 && recorded.StartsWith("0x", StringComparison.Ordinal) &&
                ulong.TryParse(recorded.Substring(2, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong high) &&
                high == guid.High) return true;
        return false;
    }

    public static bool IsRecordedLive(VendorFile.VendorPoint point, WowBObservedUnit unit, Memory? memory, WowBReader? reader,
                                      bool allow70938Candidate = false)
    {
        if (memory == null || reader == null || unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable ||
            (!point.MerchantGuids.Contains(unit.Guid.ToString()) &&
             !(allow70938Candidate && reader.IsExperimental70938 && Same70938HighCandidate(point, unit.Guid)))) return false;
        try
        {
            return WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, unit) &&
                reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields) && fields.Health > 0 &&
                WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, unit);
        }
        catch { return false; }
    }
}

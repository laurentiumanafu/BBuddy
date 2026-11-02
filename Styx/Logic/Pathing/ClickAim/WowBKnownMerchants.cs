using System;
using GreenMagic;
using Styx.WoWInternals;

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
            if (!reader.TryReadMapId(out uint firstMap) || !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetSelectedTarget(out WowBObservedUnit selected) || selected.IsLocalPlayer || selected.HasPlayerVtable ||
                !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, selected) ||
                !reader.TryGetUnitFields(selected.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health == 0)
            { reason = "Select a living NPC and ensure the 70291 signatures and map are readable."; return false; }
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                after.LocalGuid != before.LocalGuid || after.TargetGuid != before.TargetGuid ||
                !after.TryGetSelectedTarget(out WowBObservedUnit final) || final.Guid != selected.Guid ||
                final.RecordAddress != selected.RecordAddress || final.UnitAddress != selected.UnitAddress ||
                !WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, final) || reader.ReadTargetGuid() != selected.Guid ||
                !reader.TryReadMapId(out uint finalMap) || finalMap != firstMap || ClickAimUi.Running || ClickAimUi.Paused)
            { reason = "Selection, map or attachment changed; nothing was recorded."; return false; }
            guid = selected.Guid; map = firstMap; return true;
        }
        catch (Exception ex) { reason = "Merchant selection could not be read: " + ex.Message; return false; }
    }

    public static bool IsRecordedLive(VendorFile.VendorPoint point, WowBObservedUnit unit, Memory? memory, WowBReader? reader)
    {
        if (memory == null || reader == null || unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable ||
            !point.MerchantGuids.Contains(unit.Guid.ToString())) return false;
        try
        {
            return WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, unit) &&
                reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields) && fields.Health > 0 &&
                WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, unit);
        }
        catch { return false; }
    }
}

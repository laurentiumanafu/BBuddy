#if WOWB_READER_TESTS
using System;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

internal static class ExperimentalOffsetsTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception("70707 fixture: " + message);
    }

    internal static void Run()
    {
        var memory = new Memory();
        ulong module = memory.ImageBase64;
        var legacyPlayer = new WowBGuid(1, 2);
        var player = new WowBGuid(3, 4);
        var target = new WowBGuid(5, 6);
        memory.Guid(module + WowBOffsets.PlayerGuid, legacyPlayer);
        memory.Guid(module + Wow70707Offsets.PlayerGuid, player);
        memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
        memory.U64(module + Wow70707Offsets.ObjectManager, 0x20000000);
        memory.U64(module + Wow70707Offsets.HandlePools, 0x21000000);
        memory.U64(module + Wow70707Offsets.HandlePools + 8, 0x20000000);
        memory.U64(module + Wow70707Offsets.MapRoot, 0x30000000);
        memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 7);
        memory.U64(module + WowBOffsets.MapRoot, 0x31000000);
        memory.U32(0x31000000 + WowBOffsets.MapIdOffset, 1);
        memory.U64(module + Wow70707Offsets.CameraWorldFrameGlobal, 0x32000000);
        memory.U64(0x32000000 + Wow70707Offsets.CameraPointerOffset, 0x33000000);
        memory.F32(0x33000000 + Wow70707Offsets.CameraZoomOffset, 15.0f);

        // One synthetic 70291-shaped pool record at the candidate 70707 root;
        // this only checks routing and rejection, not the real client's layout.
        memory.U64(0x21000000 + WowBOffsets.HandlePoolArrayOffset, 0x22000000);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 1);
        memory.U32(0x22000000, (uint)WowBOffsets.HandleOccupiedBit);
        memory.U32(0x22000004, 0);
        memory.U64(0x22000008, 0x23000000);
        memory.Guid(0x23000000 + WowBOffsets.RecordGuidLoOffset, player);

        var legacy = new WowBReader(memory);
        var experimental = new WowBReader(memory, experimental70707: true);
        Require(legacy.ReadLocalGuid() == legacyPlayer, "legacy reads original player RVA");
        Require(experimental.ReadLocalGuid() == player && experimental.ReadTargetGuid() == target, "candidate GUID RVAs");
        Require(experimental.ReadLastTargetGuid().IsEmpty, "unknown last-target RVA is not guessed");
        experimental.ReadPlayerName();
        Require(memory.LastStringReadAddress == module + Wow70707Offsets.PlayerNamePrimary, "candidate name RVA");
        Require(experimental.ObjectManagerPtr == 0x20000000, "candidate object-manager RVA");
        Require(experimental.EnumeratePoolObjects().Count == 1, "candidate pool RVA");
        Require(!experimental.TryGetPlayerObject(out _), "unverified player record is not accepted for movement");
        Require(legacy.TryReadMapId(out uint map) && map == 1, "legacy map path preserved");
        Require(!experimental.TryReadMapId(out _), "unknown 70707 map field fails closed");
        Require(!experimental.TryGetCameraPosition(out _, out _, out _), "unknown 70707 camera position fails closed");
        memory.EnableCache();
        string diagnostic = experimental.DescribeExperimental70707();
        Require(memory.CacheEnabled, "candidate diagnostic restores cache state");
        Require(diagnostic.Contains("objectManager=0x20000000") && diagnostic.Contains("pool1=0x20000000") &&
                diagnostic.Contains("camera=0x33000000") && diagnostic.Contains("zoomCandidate=15") &&
                diagnostic.Contains("mapRoot+0x20Candidate=7"), "candidate diagnostic uses roots");
        memory.U64(0x23000000 + WowBOffsets.RecordDataBlockPtrOffset, 0x24000000);
        memory.U64(0x24000000, module + 0x41DC3A8); // unknown candidate must remain rejected
        memory.U64(0x24000008, 0x23000000);
        memory.Guid(0x24000010, player);
        memory.F32(0x23000000 + WowBOffsets.RecordPositionOffset, 100);
        memory.F32(0x23000000 + WowBOffsets.RecordPositionOffset + 4, 200);
        memory.F32(0x23000000 + WowBOffsets.RecordPositionOffset + 8, 300);
        memory.F32(0x23000000 + WowBOffsets.RecordPositionOffset + 12, 1);
        memory.F32(0x23000000 + WowBOffsets.RecordPositionOffset + 16, 2);
        string recordDiagnostic = experimental.DiagnoseExperimentalPlayer();
        Require(recordDiagnostic.Contains("vtable-not-in-unit-filter") && recordDiagnostic.Contains("vtableRva=0x41DC3A8") &&
                recordDiagnostic.Contains("rawRecord+0x60=(100,200,300)"), "player record diagnostic does not require known vtable");
        Require(!experimental.TryGetPlayerObject(out _), "diagnostic does not authorize unverified player vtable");
        Require(memory.CacheEnabled, "player record diagnostic restores cache state");

        // Fake examples for the two observed vtables: not proof of the client layout.
        memory.U64(0x24000000, module + Wow70707Offsets.PlayerVtable);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 2);
        memory.U32(0x22000010, (uint)WowBOffsets.HandleOccupiedBit);
        memory.U32(0x22000014, 0);
        memory.U64(0x22000018, 0x23001000);
        memory.Guid(0x23001000 + WowBOffsets.RecordGuidLoOffset, target);
        ulong targetUnit = 0x24001004; // exercise the observed 4-byte alignment
        memory.U64(0x23001000 + WowBOffsets.RecordDataBlockPtrOffset, targetUnit);
        memory.U64(targetUnit, module + Wow70707Offsets.UnitVtable);
        memory.U64(targetUnit + 8, 0x23001000);
        memory.Guid(targetUnit + 0x10, target);
        memory.F32(0x23001000 + WowBOffsets.RecordPositionOffset, 105);
        memory.F32(0x23001000 + WowBOffsets.RecordPositionOffset + 4, 201);
        memory.F32(0x23001000 + WowBOffsets.RecordPositionOffset + 8, 300);
        memory.F32(0x23001000 + WowBOffsets.RecordPositionOffset + 12, 1);
        memory.F32(0x23001000 + WowBOffsets.RecordPositionOffset + 16, 2);
        for (int i = 0; i < 16; i++)
        {
            memory.U32(0x24000000 + 0x1E20UL + (ulong)(i * 4), 0);
            memory.U32(targetUnit + 0x1E20UL + (ulong)(i * 4), 0);
        }
        memory.U64(0x24000000 + Wow70707Offsets.HealthCandidateOffset, 148);
        memory.U64(0x24000000 + Wow70707Offsets.MaxHealthCandidateOffset, 148);
        memory.U32(0x24000000 + Wow70707Offsets.LevelCandidateOffset, 4);
        memory.U32(0x24000000 + Wow70707Offsets.FactionKeyCandidateOffset, 3);
        memory.U64(targetUnit + Wow70707Offsets.HealthCandidateOffset, 55);
        memory.U64(targetUnit + Wow70707Offsets.MaxHealthCandidateOffset, 55);
        memory.U32(targetUnit + Wow70707Offsets.LevelCandidateOffset, 2);
        memory.U32(targetUnit + Wow70707Offsets.FactionKeyCandidateOffset, 36);
        Require(experimental.IsKnownUnitVtable(Wow70707Offsets.PlayerVtable) &&
                experimental.IsKnownUnitVtable(Wow70707Offsets.UnitVtable) &&
                !experimental.IsKnownUnitVtable(WowBOffsets.CGUnitFnTable) &&
                !legacy.IsKnownUnitVtable(Wow70707Offsets.UnitVtable), "vtables are build-specific");
        Require(experimental.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) && snapshot != null &&
                snapshot.Units.Count == 2 && snapshot.TryGetUnit(player, out WowBObservedUnit observedPlayer) &&
                observedPlayer.IsLocalPlayer && observedPlayer.HasPlayerVtable && !observedPlayer.CandidateHealth.HasValue &&
                snapshot.TryGetUnit(target, out WowBObservedUnit observedTarget) && !observedTarget.CandidateHealth.HasValue,
                "70707 diagnostic snapshot omits unverified health");
        Require(experimental.DiagnoseExperimentalPlayer().Contains("raw candidates hp=148/148, level=4, factionKey=3") &&
                experimental.DiagnoseTarget(target).Contains("raw candidates hp=55/55, level=2, factionKey=36"),
                "bounded candidate words formatted without enabling gameplay");
        ObjectManager.Wow = memory; ObjectManager.WowB = experimental;
        memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, default); // no selection required
        string candidateScan = WowBGrindFactions.ScanNearby();
        Require(candidateScan.Contains("RESEARCH ONLY") && candidateScan.Contains("Candidate 36 | 1 unit(s)") &&
                candidateScan.Contains("Grind remains blocked") && candidateScan.Contains(target.ToString()), "70707 candidate scan without target");
        int reads = 0;
        memory.OnRead = (address, count) =>
        {
            if (address == targetUnit + Wow70707Offsets.FactionKeyCandidateOffset && count == 4 && ++reads == 2)
                memory.U32(address, 44);
        };
        candidateScan = WowBGrindFactions.ScanNearby();
        memory.OnRead = null;
        Require(!candidateScan.Contains("Candidate 36 |") && !candidateScan.Contains("Candidate 44 |") &&
                candidateScan.Contains("Unreadable/changed candidates: 1"), "unstable 70707 key omitted");
        memory.U32(targetUnit + Wow70707Offsets.FactionKeyCandidateOffset, 0);
        Require(!WowBGrindFactions.ScanNearby().Contains("Candidate 0 |"), "zero 70707 candidate not reported as known");
        memory.U32(targetUnit + Wow70707Offsets.FactionKeyCandidateOffset, 36);
        memory.U64(targetUnit, module + 0x41DC3A8);
        Require(!WowBGrindFactions.ScanNearby().Contains("Candidate 36 |"), "unknown 70707 vtable excluded");
        memory.U64(targetUnit, module + Wow70707Offsets.UnitVtable);
        ClickAimUi.Running = true;
        Require(WowBGrindFactions.ScanNearby().Contains("Stop the bot"), "70707 candidate scan denied while running");
        ClickAimUi.Running = false;
        ObjectManager.Wow = null; ObjectManager.WowB = null;
        memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
        memory.OnRead = (address, count) =>
        {
            if (address == 0x24000000 + 0x1E20 && count == 64)
                memory.U64(0x23000000 + WowBOffsets.RecordDataBlockPtrOffset, 0x24000008);
        };
        Require(!experimental.DiagnoseExperimentalPlayer().Contains("rawUnit+0x1E20..0x1E5C="), "changed identity discards candidate words");
        memory.OnRead = null;
        memory.U64(0x23000000 + WowBOffsets.RecordDataBlockPtrOffset, 0x24000000);
        memory.OnRead = (address, count) =>
        {
            if (address == targetUnit + 0x1E20 && count == 64)
                memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, default);
        };
        Require(!experimental.DiagnoseTarget(target).Contains("raw candidates hp="), "changed selected GUID discards candidate words");
        memory.OnRead = null;
        memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
        Require(!experimental.TryGetPlayerObject(out _) && !experimental.TryGetUnitFields(0x24000000, out _) &&
                !experimental.TryReadFactionTemplateId(0x24000000, out _) && !experimental.TryGetUnitStats(0x23000000, out _),
                "70707 diagnostic observations do not authorize movement or legacy combat fields");
        memory.U64(targetUnit + 8, 0x23000000); // mismatched target back-pointer
        Require(experimental.TryCaptureWorldSnapshot(out snapshot) && snapshot != null && snapshot.Units.Count == 1,
                "mismatched target identity is rejected");
        Require(legacy.DescribeExperimental70707() == "70707 candidate profile is not active", "legacy profile not switched");
    }
}
#endif

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
        Require(!legacy.TryReadExperimentalMapCandidate(out _) && experimental.TryReadExperimentalMapCandidate(out uint mapCandidate) && mapCandidate == 7,
                "candidate map is available only as a 70707 diagnostic");
        memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 0);
        Require(experimental.TryReadExperimentalMapCandidate(out mapCandidate) && mapCandidate == 0, "candidate map zero is a valid observation");
        int mapReads = 0;
        memory.OnRead = (address, count) =>
        {
            if (address == 0x30000000 + Wow70707Offsets.MapIdCandidateOffset && count == 4 && ++mapReads == 2) memory.U32(address, 1);
        };
        Require(!experimental.TryReadExperimentalMapCandidate(out _), "changing candidate map is rejected");
        memory.OnRead = null;
        memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 100001);
        Require(!experimental.TryReadExperimentalMapCandidate(out _), "out-of-range candidate map rejected");
        memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 7);
        int mapRootReads = 0;
        memory.OnRead = (address, count) =>
        {
            if (address == module + Wow70707Offsets.MapRoot && count == 8 && ++mapRootReads == 2) memory.U64(address, 0x30001000);
        };
        Require(!experimental.TryReadExperimentalMapCandidate(out _), "changing candidate root rejected");
        memory.OnRead = null;
        memory.U64(module + Wow70707Offsets.MapRoot, 0x30000000);
        Require(!experimental.TryGetCameraPosition(out _, out _, out _), "unknown 70707 camera position fails closed");
        for (int i = 0; i < 12; i++) memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + (ulong)(i * 8), 0);
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 6603);
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 5176);
        Require(experimental.ReadActionBar().Count == 0, "70707 candidate action bar cannot drive gameplay");
        Require(experimental.DescribeExperimentalActionBar().Contains("1:0x00000000000019CB 2:0x0000000000001438"),
                "raw candidate action array is reported");
        int actionReads = 0;
        memory.OnRead = (address, count) =>
        {
            if (address == module + Wow70707Offsets.StoredActionArrayCandidate && count == 96 && ++actionReads == 2)
                memory.U64(address, 42);
        };
        Require(experimental.DescribeExperimentalActionBar().Contains("changed; discarded"), "unstable action array discarded");
        memory.OnRead = null;
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 6603);
        memory.OnRead = (address, count) =>
        {
            if (address == module + Wow70707Offsets.StoredActionArrayCandidate && count == 96)
                memory.Guid(module + Wow70707Offsets.PlayerGuid, default);
        };
        Require(experimental.DescribeExperimentalActionBar().Contains("unavailable/changed"), "changed action-bar player discarded");
        memory.OnRead = null;
        memory.Guid(module + Wow70707Offsets.PlayerGuid, player);
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
        for (int i = 0; i < 8; i++)
        {
            memory.U32(0x24000000 + Wow70707Offsets.PowerArrayCandidateOffset + (ulong)(i * 4), (uint)(i + 10));
            memory.U32(0x24000000 + Wow70707Offsets.MaxPowerArrayCandidateOffset + (ulong)(i * 4), (uint)(i + 20));
        }
        Require(experimental.DiagnoseExperimentalPlayer().Contains("rawUnit+0x1BC0=[10,11,12,13,14,15,16,17]") &&
                experimental.DiagnoseExperimentalPlayer().Contains("rawUnit+0x20F0=[20,21,22,23,24,25,26,27]"), "indexed power candidates logged");
        int powerReads = 0;
        memory.OnRead = (address, count) =>
        {
            if (address == 0x24000000 + Wow70707Offsets.PowerArrayCandidateOffset && count == 32 && ++powerReads == 2)
                memory.U32(address, 99);
        };
        Require(!experimental.DiagnoseExperimentalPlayer().Contains("rawUnit+0x1BC0="), "changing power candidates discarded");
        memory.OnRead = null;
        memory.U32(0x24000000 + Wow70707Offsets.PowerArrayCandidateOffset, 10);
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
                candidateScan.Contains("explicitly selected allowlisted monster") && candidateScan.Contains(target.ToString()), "70707 candidate scan without target");
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
        // Synthetic opt-in is scoped to one selected, allowed GUID and thread.
        Require(GrindFactionSettings.Save(GrindFactionMode.Allow, "36", "", out _), "save trial allowlist");
        Require(!WowBGrindFactions.PrepareForGrind(out _), "70707 trial refuses missing signatures");
        memory.Bytes(module + 0x2080D2C, Convert.FromHexString("488D0DCD38A004E808A843FE8B93541E0000"));
        memory.Bytes(module + 0x20AAF97, Convert.FromHexString("F2480F2A83281E0000"));
        memory.Bytes(module + 0x2890305, Convert.FromHexString("448B8C86C01B0000"));
        memory.Bytes(module + 0xA538FD, Convert.FromHexString("488D0D5CB32E06488D34C1488B04C1"));
        Require(!WowBGrindFactions.PrepareForGrind(out _), "70707 trial refuses missing target");
        memory.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 403);
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 16, 331);
        Require(GrindFactionSettings.Save(GrindFactionMode.Allow, "31", "", out _), "save different allowlist");
        Require(!WowBGrindFactions.PrepareForGrind(out _), "70707 trial refuses unlisted target");
        Require(GrindFactionSettings.Save(GrindFactionMode.Allow, "36", "", out _), "restore trial allowlist");
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 5176);
        Require(!WowBGrindFactions.PrepareForGrind(out _) && !experimental.TryReadMapId(out _), "wrong bar rejects and revokes trial");
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 403);
        memory.U32(0x24000000 + 0x1E20, 0x100);
        Require(!WowBGrindFactions.PrepareForGrind(out _) && !experimental.TryReadMapId(out _), "unsupported power type rejects and revokes trial");
        memory.U32(0x24000000 + 0x1E20, 0);
        Require(WowBGrindFactions.PrepareForGrind(out string trialError), "70707 synthetic trial start: " + trialError);
        Require(experimental.TryReadMapId(out mapCandidate) && mapCandidate == 7 && experimental.TryGetPlayerObject(out _) &&
                experimental.TryGetUnitFields(0x24000000, out var selfFields) && selfFields.Health == 148 && selfFields.Power == 10 && selfFields.MaxPower == 20 &&
                experimental.TryGetUnitFields(targetUnit, out var targetFields) && targetFields.Health == 55 && targetFields.Level == 2 &&
                experimental.ReadActionBar().Count == 3, "trial candidate reads");
        WowBObservedUnit selectedUnit = default;
        Require(experimental.TryCaptureWorldSnapshot(out snapshot) && snapshot != null && snapshot.TryGetUnit(target, out selectedUnit) &&
                WowBGrindFactions.AllowsPull(experimental, selectedUnit, out _), "selected allowlisted target accepted");
        var differentUnit = new WowBObservedUnit(new WowBGuid(7, 8), selectedUnit.RecordAddress, selectedUnit.UnitAddress,
            false, false, selectedUnit.Location, selectedUnit.Facing, null);
        Require(!WowBGrindFactions.AllowsPull(experimental, differentUnit, out _), "another GUID cannot use the trial allowlist");
        bool otherThreadReadable = true;
        var other = new System.Threading.Thread(() => { otherThreadReadable = experimental.TryReadMapId(out _) || experimental.ReadActionBar().Count != 0; });
        other.Start(); other.Join();
        Require(!otherThreadReadable, "other threads cannot use experimental gameplay reads");
        memory.U32(targetUnit + Wow70707Offsets.FactionKeyCandidateOffset, 31);
        Require(snapshot!.TryGetUnit(target, out selectedUnit) && !WowBGrindFactions.AllowsPull(experimental, selectedUnit, out _), "changed key denied");
        memory.U32(targetUnit + Wow70707Offsets.FactionKeyCandidateOffset, 36);
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 5176);
        Require(!WowBGrindFactions.Experimental70707BarSafe(experimental), "changed action bar withholds trial input");
        memory.U64(module + Wow70707Offsets.StoredActionArrayCandidate + 8, 403);
        bool claimedThreadReadable = false;
        var worker = new System.Threading.Thread(() =>
        {
            WowBGrindFactions.ClaimExperimentalGrindThread();
            claimedThreadReadable = experimental.TryReadMapId(out _) && WowBGrindFactions.Experimental70707BarSafe(experimental);
        });
        worker.Start(); worker.Join();
        Require(claimedThreadReadable && !experimental.TryReadMapId(out _), "trial transfers to the worker thread");
        WowBGrindFactions.EndExperimental70707();
        Require(!experimental.TryReadMapId(out _) && !experimental.TryGetPlayerObject(out _) && experimental.ReadActionBar().Count == 0 &&
                !experimental.TryGetUnitFields(targetUnit, out _), "session revocation closes gameplay reads");
        System.IO.File.Delete(GrindFactionSettings.PathName);
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

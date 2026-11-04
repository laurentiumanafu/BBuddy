#if WOWB_READER_TESTS
using System;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

internal static class Experimental70790Tests
{
    private static void Require(bool ok, string message) { if (!ok) throw new Exception("70790 fixture: " + message); }
    public static void Run()
    {
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bbuddy-bag-class-test-" + Guid.NewGuid().ToString("N"));
        Styx.Helpers.Logging.ApplicationPath = tempPath;
        var memory = new Memory(); ulong image = memory.ImageBase64;
        var player = new WowBGuid(101, 202); var target = new WowBGuid(303, 404);
        memory.Guid(image + Wow70790Offsets.PlayerGuid, player);
        memory.Guid(image + Wow70790Offsets.PlayerTargetGuid, target);
        memory.Guid(image + Wow70707Offsets.PlayerGuid, new WowBGuid(909, 808));
        memory.U64(image + Wow70790Offsets.ObjectManager, 0x20000000);
        memory.U64(image + Wow70790Offsets.HandlePools, 0x21000000);
        memory.U64(image + Wow70790Offsets.HandlePools + 8, 0x20000000);
        memory.U64(image + Wow70790Offsets.MapRoot, 0x30000000); memory.U32(0x30000020, 7);
        memory.U64(image + Wow70790Offsets.CameraWorldFrameGlobal, 0x32000000);
        memory.U64(0x32000480, 0x33000000); memory.F32(0x33000274, 15);
        memory.U64(0x21000000 + WowBOffsets.HandlePoolArrayOffset, 0x22000000);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 2);
        ulong[] records = { 0x23000000, 0x23001000 }, units = { 0x24000000, 0x24001000 };
        WowBGuid[] guids = { player, target };
        for (int i = 0; i < 2; i++)
        {
            ulong slot = 0x22000000UL + (ulong)(i * 16);
            memory.U32(slot, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(slot + 4, 0); memory.U64(slot + 8, records[i]);
            memory.Guid(records[i] + WowBOffsets.RecordGuidLoOffset, guids[i]);
            memory.U64(records[i] + WowBOffsets.RecordDataBlockPtrOffset, units[i]);
            memory.U64(units[i], image + (i == 0 ? Wow70790Offsets.PlayerVtable : Wow70790Offsets.UnitVtable));
            memory.U64(units[i] + 8, records[i]); memory.Guid(units[i] + 16, guids[i]);
            for (int j = 0; j < 5; j++) memory.F32(records[i] + WowBOffsets.RecordPositionOffset + (ulong)(j * 4), i + j + 1);
        }
        var reader = new WowBReader(memory, experimental70790: true);
        var old = new WowBReader(memory, experimental70707: true);
        var previous = new WowBReader(memory, experimental70755: true);
        Require(reader.ExperimentalBuildNumber == 70790 && reader.IsExperimental70790 && !reader.IsExperimental70755 &&
            reader.IsExperimental70707 && reader.UsesOrdinaryBagPolicy && reader.ReadLocalGuid() == player && reader.ReadTargetGuid() == target, "separate player/target globals and ordinary bag policy");
        Require(old.ReadLocalGuid() != player && !old.IsKnownUnitVtable(Wow70790Offsets.PlayerVtable), "70707 does not route through 70790 globals");
        Require(previous.ReadLocalGuid() != player && !previous.IsKnownUnitVtable(Wow70790Offsets.PlayerVtable), "70755 does not route through 70790 globals");
        bool rejectedMultiple = false;
        try { _ = new WowBReader(memory, experimental70755: true, experimental70790: true); }
        catch (ArgumentException) { rejectedMultiple = true; }
        Require(rejectedMultiple, "multiple experimental profiles rejected");
        Require(reader.MouseoverGuidCandidateRva == Wow70790Offsets.MouseoverGuidCandidate && reader.GameObjectVtableCandidateRva == Wow70790Offsets.GameObjectVtableCandidate, "mouseover and node vtable routing");
        reader.ReadPlayerName(); Require(memory.LastStringReadAddress == image + Wow70790Offsets.PlayerNamePrimary, "name RVA");
        Require(reader.ObjectManagerPtr == 0x20000000 && reader.TryReadExperimentalMapCandidate(out uint map) && map == 7, "manager and map roots");
        Require(reader.TryCaptureWorldSnapshot(out var snapshot) && snapshot != null && snapshot.Units.Count == 2, "candidate pool and vtables");
        memory.U64(units[0], image + Wow70707Offsets.PlayerVtable);
        Require(!reader.TryCaptureWorldSnapshot(out _), "70707 player vtable rejected on 70790");
        memory.U64(units[0], image + Wow70790Offsets.PlayerVtable);
        ObjectManager.Wow = memory; ObjectManager.WowB = reader;
        Require(!WowBGrindFactions.TraceBuild70707(memory, reader), "missing 70790 markers rejected");
        memory.Bytes(image + 0x211826C, Convert.FromHexString("488D0DCD3FA104E878303AFE8B93541E0000"));
        memory.Bytes(image + 0x2142757, Convert.FromHexString("F2480F2A83281E0000"));
        memory.Bytes(image + 0x2931DA5, Convert.FromHexString("448B8C86C01B0000"));
        memory.Bytes(image + 0xA6B06D, Convert.FromHexString("488D0D4CB83706488D34C1488B04C1"));
        Require(WowBGrindFactions.TraceBuild70707(memory, reader), "70790 build markers routed separately");
        memory.Bytes(image + 0x2931DA5, new byte[8]);
        Require(!WowBGrindFactions.TraceBuild70707(memory, reader), "readable 70790 signature mismatch rejected");
        memory.Bytes(image + 0x2931DA5, Convert.FromHexString("448B8C86C01B0000"));
        memory.Bytes(image + Wow70790Offsets.InventoryLookup, Convert.FromHexString("48895C2408574883EC2080792000"));
        memory.Bytes(image + Wow70790Offsets.InventoryHandler, Convert.FromHexString("48895C24084889742418574881EC2004"));
        memory.Bytes(image + 0x2606EB6, Convert.FromHexString("4038A880940000"));
        memory.Bytes(image + 0x2606EC9, Convert.FromHexString("0FB6B8047D0000"));
        memory.Bytes(image + 0x2606ED4, Convert.FromHexString("83C723"));
        memory.Bytes(image + 0xD8E69C, Convert.FromHexString("E8AF6C8701"));
        memory.Bytes(image + 0xD8E6C0, Convert.FromHexString("8B80C00A0000"));
        memory.Bytes(image + 0x2606E66, Convert.FromHexString("486390C00A0000"));
        memory.Bytes(image + 0x2606E74, Convert.FromHexString("4C8D8090040000"));
        memory.Bytes(image + 0x2606E80, Convert.FromHexString("498B00"));
        ulong inv = units[0] + 0x9460, array = 0x33010000, bagRecord = 0x23002000, bagData = 0x25000000;
        var bag = new WowBGuid(1234, 5678);
        memory.Bytes(inv, new byte[0x28]); memory.U32(inv, 51); memory.U64(inv + 8, array);
        memory.Bytes(inv + 0x20, new byte[] { 1 }); memory.Bytes(units[0] + 0x7D04, new byte[] { 16 });
        memory.Bytes(array, new byte[51 * 16]);
        memory.Bytes(image + Wow70790Offsets.ContainerIdentityTableCandidate, new byte[80]);
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate, bag);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3);
        memory.U32(0x22000020, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000024, 0); memory.U64(0x22000028, bagRecord);
        memory.Bytes(bagRecord + 0x28, new byte[0x160]); memory.Guid(bagRecord + WowBOffsets.RecordGuidLoOffset, bag);
        memory.U64(bagRecord + WowBOffsets.RecordDataBlockPtrOffset, bagData);
        memory.Bytes(bagData, new byte[0x20]); memory.U64(bagData + 8, bagRecord); memory.Guid(bagData + 0x10, bag);
        memory.U32(bagData + 0xAC0, 2); memory.Bytes(bagData + 0x488, new byte[32]);
        string bagReport = WowBInventoryProbe.Capture();
        Require(bagReport.Contains("70790 research subtotal: candidate sizes=18, zero-tags=18") &&
            bagReport.Contains("0x2606E66=matched"), "70790 bag markers, table and subtotal routing");
        var choices = WowBInventoryProbe.ObservedBags();
        Require(choices.Count == 1 && choices[0].Bag == bag && choices[0].Player == player, "stable stopped snapshot exposes the observed bag instance");
        ClickAimUi.Running = true;
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unclassified bag cannot authorize selling");
        Require(!WowBInventoryProbe.ClassifyBag(choices[0], WowBBagKind.Specialty, out _), "running classification rejected");
        ClickAimUi.Running = false;
        Require(WowBInventoryProbe.ClassifyBag(choices[0], WowBBagKind.Specialty, out _), "confirm specialty instance");
        Require(WowBBagClassification.TryGet(player, bag, out var bagKind) && bagKind == WowBBagKind.Specialty, "specialty persists");
        Require(!WowBBagClassification.TryGet(new WowBGuid(1, 2), bag, out _), "classification is scoped to character");
        Require(WowBInventoryProbe.Capture().Contains("ordinary-bag candidate subtotal: sizes=16, zero-tags=16"), "specialty zeros excluded from stopped subtotal");
        ClickAimUi.Running = true;
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out int tags, out WowBGuid owner) && tags == 16 && owner == player,
            "70790 active selling subtotal excludes specialty slots");
        memory.Bytes(image + 0x2606E66, new byte[7]);
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "readable 70790 bag marker mismatch fails closed");
        memory.Bytes(image + 0x2606E66, Convert.FromHexString("486390C00A0000"));
        int reads = 0;
        memory.OnRead = (address, count) => { if (address == units[0] + 0x7D04 && count == 1 && ++reads == 2) memory.Bytes(address, new byte[] { 15 }); };
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "changing 70790 subtotal fails closed");
        memory.OnRead = null; memory.Bytes(units[0] + 0x7D04, new byte[] { 16 });
        ClickAimUi.Running = false;
        // A second, ordinary bag must count, and cannot be assumed ordinary merely from size/index.
        var ordinaryBag = new WowBGuid(3333, 4444);
        ulong record2 = 0x23003000, data2 = 0x26000000;
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 4);
        memory.U32(0x22000030, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000034, 0); memory.U64(0x22000038, record2);
        memory.Bytes(record2 + 0x28, new byte[0x160]); memory.Guid(record2 + WowBOffsets.RecordGuidLoOffset, ordinaryBag);
        memory.U64(record2 + WowBOffsets.RecordDataBlockPtrOffset, data2);
        memory.Bytes(data2, new byte[0x20]); memory.U64(data2 + 8, record2); memory.Guid(data2 + 0x10, ordinaryBag);
        memory.U32(data2 + 0xAC0, 2); memory.Bytes(data2 + 0x488, new byte[32]);
        Require(WowBInventoryProbe.Capture().Contains("classify every observed bag"), "new bag requires classification");
        var both = WowBInventoryProbe.ObservedBags();
        Require(both.Count == 2, "both candidate identities observed");
        ClickAimUi.Running = true; Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "new ordinary bag is unknown, not guessed"); ClickAimUi.Running = false;
        Require(WowBInventoryProbe.ClassifyBag(both[1], WowBBagKind.Ordinary, out _), "confirm ordinary bag");
        Require(WowBInventoryProbe.Capture().Contains("ordinary-bag candidate subtotal: sizes=18, zero-tags=18"), "ordinary bag included, specialty excluded");
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, new WowBGuid(99, 88));
        Require(!WowBInventoryProbe.ClassifyBag(both[1], WowBBagKind.Specialty, out _), "stale selected identity cannot be classified");
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        // Reuse the 70755 character/bag-instance classifications across the update.
        string classesPath = System.IO.Path.Combine(tempPath, "Settings", "clickaim-bag-kinds-70755.txt");
        string validClasses = System.IO.File.ReadAllText(classesPath);
        System.IO.File.WriteAllText(classesPath, "invalid classification");
        ClickAimUi.Running = true; Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "malformed classification file is unknown"); ClickAimUi.Running = false;
        System.IO.File.WriteAllText(classesPath, validClasses);
        ClickAimUi.Running = true;
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out _) && tags == 18, "active mixed bag count");
        byte[] filled = new byte[51 * 16];
        for (int i = 35; i < 51; i++) BitConverter.GetBytes(1UL << 58).CopyTo(filled, i * 16 + 8);
        memory.Bytes(array, filled);
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out _) && tags == 2, "ordinary bag space prevents premature selling");
        byte[] filledOrdinary = new byte[32]; BitConverter.GetBytes(1UL << 58).CopyTo(filledOrdinary, 8); BitConverter.GetBytes(1UL << 58).CopyTo(filledOrdinary, 24);
        memory.Bytes(data2 + 0x488, filledOrdinary);
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out _) && tags == 0, "ordinary bags full despite specialty space");
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, new WowBGuid(99, 88));
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unresolved changed identity fails closed");
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        memory.Bytes(image + Wow70790Offsets.ContainerIdentityTableCandidate, new byte[80]);
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out _) && tags == 0, "stable empty table supports backpack-only case");
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate, bag);
        memory.Guid(image + Wow70790Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        ClickAimUi.Running = false;
        memory.Bytes(array, new byte[51 * 16]);
        for (int i = 0; i < 12; i++) memory.U64(image + Wow70790Offsets.StoredActionArrayCandidate + (ulong)(8 * i), 0);
        memory.U64(image + Wow70790Offsets.StoredActionArrayCandidate, 6603);
        memory.U64(units[0] + Wow70707Offsets.HealthCandidateOffset, 148);
        memory.U64(units[0] + Wow70707Offsets.MaxHealthCandidateOffset, 148);
        memory.U32(units[0] + Wow70707Offsets.LevelCandidateOffset, 4);
        memory.U32(units[0] + Wow70707Offsets.FactionKeyCandidateOffset, 3);
        memory.U32(units[0] + 0x1E58, 0);
        memory.U32(units[0] + 0x1E20, 0);
        memory.U32(units[0] + Wow70707Offsets.PowerArrayCandidateOffset, 10);
        memory.U32(units[0] + Wow70707Offsets.MaxPowerArrayCandidateOffset, 20);
        memory.Bytes(units[0] + Wow70707Offsets.GhostFlagsCandidateOffset, new byte[] { 0 });
        Require(GrindFactionSettings.Save(GrindFactionMode.Allow, "36", "", out _), "save fixture faction list");
        Require(WowBGrindFactions.PrepareForGrind(out string reason), "70790 guarded synthetic startup: " + reason);
        Require(reader.TryReadMapId(out map) && map == 7 && reader.ReadActionBar().Count == 1 &&
            reader.TryGetUnitFields(units[0], out var fields) && fields.Health == 148 && fields.Power == 10,
            "70790 session reads candidate gameplay fields and its own action array");
        memory.U64(image + Wow70790Offsets.StoredActionArrayCandidate, 1);
        Require(!WowBGrindFactions.Experimental70707BarSafe(reader), "changed 70790 action bar invalidates the session guard");
        memory.U64(image + Wow70790Offsets.StoredActionArrayCandidate, 6603);
        WowBGrindFactions.EndExperimental70707();
        Require(!reader.TryReadMapId(out _), "ending the session revokes gameplay reads");
        ObjectManager.Wow = null; ObjectManager.WowB = null;
        Styx.Helpers.Logging.ApplicationPath = oldPath;
        if (System.IO.Directory.Exists(tempPath)) System.IO.Directory.Delete(tempPath, true);
        Console.WriteLine("70790 synthetic build routing and snapshot fixtures passed (no live-client validation)");
    }
}
#endif

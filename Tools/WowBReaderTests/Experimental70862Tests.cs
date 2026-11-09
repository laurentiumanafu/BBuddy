#if WOWB_READER_TESTS
using System;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

internal static class Experimental70862Tests
{
    private static void Require(bool ok, string message) { if (!ok) throw new Exception("70862 fixture: " + message); }
    public static void Run()
    {
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bbuddy-bag-class-test-" + Guid.NewGuid().ToString("N"));
        Styx.Helpers.Logging.ApplicationPath = tempPath;
        var memory = new Memory(); ulong image = memory.ImageBase64;
        var player = new WowBGuid(101, 202); var target = new WowBGuid(303, 404);
        memory.Guid(image + Wow70862Offsets.PlayerGuid, player);
        memory.Guid(image + Wow70862Offsets.PlayerTargetGuid, target);
        memory.Guid(image + Wow70707Offsets.PlayerGuid, new WowBGuid(909, 808));
        memory.U64(image + Wow70862Offsets.ObjectManager, 0x20000000);
        memory.U64(image + Wow70862Offsets.HandlePools, 0x21000000);
        memory.U64(image + Wow70862Offsets.HandlePools + 8, 0x20000000);
        memory.U64(image + Wow70862Offsets.MapRoot, 0x30000000); memory.U32(0x30000020, 7);
        memory.U64(image + Wow70862Offsets.CameraWorldFrameGlobal, 0x32000000);
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
            memory.U64(units[i], image + (i == 0 ? Wow70862Offsets.PlayerVtable : Wow70862Offsets.UnitVtable));
            memory.U64(units[i] + 8, records[i]); memory.Guid(units[i] + 16, guids[i]);
            for (int j = 0; j < 5; j++) memory.F32(records[i] + WowBOffsets.RecordPositionOffset + (ulong)(j * 4), i + j + 1);
        }
        var reader = new WowBReader(memory, experimental70862: true);
        var old = new WowBReader(memory, experimental70707: true);
        var previous = new WowBReader(memory, experimental70755: true);
        var prior = new WowBReader(memory, experimental70790: true);
        Require(reader.ExperimentalBuildNumber == 70862 && reader.IsExperimental70862 && !reader.IsExperimental70790 && !reader.IsExperimental70755 &&
            reader.IsExperimental70707 && reader.UsesOrdinaryBagPolicy && reader.ReadLocalGuid() == player && reader.ReadTargetGuid() == target, "separate player/target globals and ordinary bag policy");
        Require(old.ReadLocalGuid() != player && !old.IsKnownUnitVtable(Wow70862Offsets.PlayerVtable), "70707 does not route through 70862 globals");
        Require(previous.ReadLocalGuid() != player && !previous.IsKnownUnitVtable(Wow70862Offsets.PlayerVtable), "70755 does not route through 70862 globals");
        Require(prior.ReadLocalGuid() != player && !prior.IsKnownUnitVtable(Wow70862Offsets.PlayerVtable), "70790 does not route through 70862 globals");
        bool rejectedMultiple = false;
        try { _ = new WowBReader(memory, experimental70755: true, experimental70862: true); }
        catch (ArgumentException) { rejectedMultiple = true; }
        Require(rejectedMultiple, "multiple experimental profiles rejected");
        Require(reader.MouseoverGuidCandidateRva == Wow70862Offsets.MouseoverGuidCandidate && reader.GameObjectVtableCandidateRva == Wow70862Offsets.GameObjectVtableCandidate, "mouseover and node vtable routing");
        reader.ReadPlayerName(); Require(memory.LastStringReadAddress == image + Wow70862Offsets.PlayerNamePrimary, "name RVA");
        Require(reader.ObjectManagerPtr == 0x20000000 && reader.TryReadExperimentalMapCandidate(out uint map) && map == 7, "manager and map roots");
        Require(reader.TryCaptureWorldSnapshot(out var snapshot) && snapshot != null && snapshot.Units.Count == 2, "candidate pool and vtables");
        memory.U64(units[0], image + Wow70707Offsets.PlayerVtable);
        Require(!reader.TryCaptureWorldSnapshot(out _), "70707 player vtable rejected on 70862");
        memory.U64(units[0], image + Wow70862Offsets.PlayerVtable);
        ObjectManager.Wow = memory; ObjectManager.WowB = reader;
        Require(!WowBGrindFactions.TraceBuild70707(memory, reader), "missing 70862 markers rejected");
        memory.Bytes(image + 0x1F9298C, Convert.FromHexString("488D0D2D29A004E8288552FE8B93541E0000"));
        memory.Bytes(image + 0x1FBCA47, Convert.FromHexString("F2480F2A83281E0000"));
        memory.Bytes(image + 0x27997E5, Convert.FromHexString("448B8C86C01B0000"));
        memory.Bytes(image + 0xA2B9FD, Convert.FromHexString("488D0D3C772206488D34C1488B04C1"));
        Require(WowBGrindFactions.TraceBuild70707(memory, reader), "70862 build markers routed separately");
        memory.Bytes(image + 0x27997E5, new byte[8]);
        Require(!WowBGrindFactions.TraceBuild70707(memory, reader), "readable 70862 signature mismatch rejected");
        memory.Bytes(image + 0x27997E5, Convert.FromHexString("448B8C86C01B0000"));
        memory.Bytes(image + Wow70862Offsets.InventoryLookup, Convert.FromHexString("48895C2408574883EC2080792000"));
        memory.Bytes(image + Wow70862Offsets.InventoryHandler, Convert.FromHexString("48895C24084889742418574881EC2004"));
        memory.Bytes(image + 0x24758C6, Convert.FromHexString("4038A880940000"));
        memory.Bytes(image + 0x24758D9, Convert.FromHexString("0FB6B8047D0000"));
        memory.Bytes(image + 0x24758E4, Convert.FromHexString("83C723"));
        memory.Bytes(image + 0xD0FD5C, Convert.FromHexString("E8FF3F7601"));
        memory.Bytes(image + 0xD0FD80, Convert.FromHexString("8B80C00A0000"));
        memory.Bytes(image + 0x2475876, Convert.FromHexString("486390C00A0000"));
        memory.Bytes(image + 0x2475884, Convert.FromHexString("4C8D8090040000"));
        memory.Bytes(image + 0x2475890, Convert.FromHexString("498B00"));
        ulong inv = units[0] + 0x9460, array = 0x33010000, bagRecord = 0x23002000, bagData = 0x25000000;
        var bag = new WowBGuid(1234, 5678);
        memory.Bytes(inv, new byte[0x28]); memory.U32(inv, 51); memory.U64(inv + 8, array);
        memory.Bytes(inv + 0x20, new byte[] { 1 }); memory.Bytes(units[0] + 0x7D04, new byte[] { 16 });
        memory.Bytes(array, new byte[51 * 16]);
        memory.Bytes(image + Wow70862Offsets.ContainerIdentityTableCandidate, new byte[80]);
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate, bag);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3);
        memory.U32(0x22000020, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000024, 0); memory.U64(0x22000028, bagRecord);
        memory.Bytes(bagRecord + 0x28, new byte[0x160]); memory.Guid(bagRecord + WowBOffsets.RecordGuidLoOffset, bag);
        memory.U64(bagRecord + WowBOffsets.RecordDataBlockPtrOffset, bagData);
        memory.Bytes(bagData, new byte[0x20]); memory.U64(bagData + 8, bagRecord); memory.Guid(bagData + 0x10, bag);
        memory.U32(bagData + 0xAC0, 2); memory.Bytes(bagData + 0x488, new byte[32]);
        string bagReport = WowBInventoryProbe.Capture();
        Require(bagReport.Contains("70862 research subtotal: candidate sizes=18, zero-tags=18") &&
            bagReport.Contains("0x2475876=matched"), "70862 bag markers, table and subtotal routing");
        var choices = WowBInventoryProbe.ObservedBags();
        Require(choices.Count == 1 && choices[0].Bag == bag && choices[0].Player == player, "stable stopped snapshot exposes the observed bag instance");
        // A pointer-like value that cannot be read must not prevent a stopped
        // diagnostic of the one observed header, but must never authorize selling.
        memory.U64(bagRecord + 0x40, 0x145D8);
        string exploratory = WowBInventoryProbe.Capture();
        Require(exploratory.Contains("STOPPED RESEARCH ONLY") && exploratory.Contains("candidate size=2, high-qword zero-tags=2") &&
            exploratory.Contains("research subtotal unavailable") && WowBInventoryProbe.ObservedBags().Count == 0,
            "unreadable alternative reports raw research only, without subtotal or choices");
        Require(!WowBInventoryProbe.ClassifyBag(choices[0], WowBBagKind.Specialty, out _), "unresolved candidate cannot be classified");
        // Regression for the 2026-11-09 log: a reserved (not committed) address
        // has no present header. No assumption is made about that log's actual map.
        const ulong alternative = 0x258000000E0;
        memory.U64(bagRecord + 0x40, alternative);
        memory.HeaderStates[alternative] = 0x1000; // committed but unreadable
        string committed = WowBInventoryProbe.Capture();
        Require(committed.Contains("state=0x1000") && committed.Contains("research subtotal unavailable") &&
            WowBInventoryProbe.ObservedBags().Count == 0, "committed unreadable remains unknown");
        memory.HeaderStates[alternative] = 0x2000; // MEM_RESERVE, whole header
        string reserved = WowBInventoryProbe.Capture();
        Require(reserved.Contains("confirmed reserved headers=1") && reserved.Contains("70862 research subtotal: candidate sizes=18, zero-tags=18") &&
            WowBInventoryProbe.ObservedBags().Count == 1, "stable reserved range cannot hold a current header");
        int stateQueries = 0;
        memory.OnStateQuery = address => {
            if (address == alternative && ++stateQueries == 3) memory.HeaderStates[address] = 0x1000;
        };
        Require(WowBInventoryProbe.Capture().Contains("memory map, identity table") && WowBInventoryProbe.ObservedBags().Count == 0,
            "reserved region becoming committed before publication fails closed");
        memory.OnStateQuery = null; memory.HeaderStates.Remove(alternative);
        ClickAimUi.Running = true;
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unqueried alternative never authorizes active selling");
        ClickAimUi.Running = false;
        // Reproduce the two pointer-like values observed in the 2026-11-06 log.
        // The simulated OS query must affirm the entire 32-byte header is free;
        // unconfirmed unreadable pages still block all choices and selling.
        memory.U64(bagRecord + 0x40, 0);
        memory.U64(bagRecord + 0x58, 0x1FFFE0D4); memory.U64(bagRecord + 0x118, 0x8FFFFDFC);
        memory.FreeHeaders.Add(0x1FFFE0D4);
        string partiallyFree = WowBInventoryProbe.Capture();
        Require(partiallyFree.Contains("confirmed free headers=1") && partiallyFree.Contains("+0x118:0x8FFFFDFC") &&
            partiallyFree.Contains("research subtotal unavailable"), "unknown second address still blocks subtotal");
        memory.FreeHeaders.Add(0x8FFFFDFC);
        string freeReport = WowBInventoryProbe.Capture();
        Require(freeReport.Contains("confirmed free headers=2") && freeReport.Contains("70862 research subtotal: candidate sizes=18, zero-tags=18") &&
            WowBInventoryProbe.ObservedBags().Count == 1, "stable OS-confirmed free ranges do not hide a matching bag");
        int queries = 0;
        memory.OnFreeQuery = address => {
            if (address == 0x1FFFE0D4 && ++queries == 3) memory.FreeHeaders.Remove(address);
        };
        Require(WowBInventoryProbe.Capture().Contains("memory map, identity table") && WowBInventoryProbe.ObservedBags().Count == 0,
            "mapping changed before snapshot completion fails closed");
        memory.OnFreeQuery = null; memory.FreeHeaders.Clear();
        memory.U64(bagRecord + 0x58, 0); memory.U64(bagRecord + 0x118, 0);
        memory.U64(bagRecord + 0x40, 0x145D8);
        // A nearby separately occupied record can place its GUID inside the
        // broad scan window. Diagnose it without treating it as a resolved bag.
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 4);
        memory.U32(0x22000030, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000034, 0);
        memory.U64(0x22000038, bagRecord + 0xC0);
        memory.U64(bagRecord + 0x28, bagData);
        memory.Guid(bagRecord + 0xD8, new WowBGuid(0x145D8, 0xC01180000000000));
        string overlap = WowBInventoryProbe.Capture();
        Require(overlap.Contains("+0xD8=other-record(+0xC0) GUID-low") && overlap.Contains("research subtotal unavailable") &&
            overlap.Contains("verified neighbor GUID fields=1") && WowBInventoryProbe.ObservedBags().Count == 0,
            "neighbor overlap does not hide a different unreadable field");
        memory.U64(bagRecord + 0x40, 0);
        string resolvedOverlap = WowBInventoryProbe.Capture();
        Require(resolvedOverlap.Contains("verified neighbor GUID fields=1") &&
            resolvedOverlap.Contains("70862 research subtotal: candidate sizes=18, zero-tags=18") &&
            WowBInventoryProbe.ObservedBags().Count == 1, "stable neighbor GUID is not an unreadable alternative");
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 5);
        memory.U32(0x22000040, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000044, 0);
        memory.U64(0x22000048, bagRecord + 0xC0);
        Require(WowBInventoryProbe.Capture().Contains("research subtotal unavailable"), "ambiguous neighbor ownership fails closed");
        memory.U32(0x22000040, 0); memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 4);
        memory.OnRead = (address, count) => {
            if (address == 0x145D8 && count == 0x20) memory.U64(bagRecord + 0xE0, 0xC01180000000001);
        };
        string changedNeighbor = WowBInventoryProbe.Capture();
        Require(changedNeighbor.Contains("research subtotal unavailable") &&
            WowBInventoryProbe.ObservedBags().Count == 0, "changed neighbor identity fails closed: " + changedNeighbor);
        memory.OnRead = null; memory.Guid(bagRecord + 0xD8, new WowBGuid(0x145D8, 0xC01180000000000));
        memory.OnRead = (address, count) => {
            if (address == 0x145D8 && count == 0x20) memory.U32(0x22000030, 0);
        };
        Require(WowBInventoryProbe.Capture().Contains("research subtotal unavailable"), "changed neighbor pool slot fails closed");
        memory.OnRead = null; memory.U32(0x22000030, (uint)WowBOffsets.HandleOccupiedBit);
        ulong neighborPointsToHeader = 0x25020000;
        memory.Bytes(neighborPointsToHeader, new byte[0x20]); memory.U64(neighborPointsToHeader + 8, bagRecord);
        memory.Guid(neighborPointsToHeader + 0x10, bag);
        memory.Guid(bagRecord + 0xD8, new WowBGuid(neighborPointsToHeader, 0xC01180000000000));
        Require(WowBInventoryProbe.Capture().Contains("research subtotal unavailable"),
            "readable matching header in neighbor GUID remains ambiguous");
        memory.U32(0x22000030, 0); memory.U64(bagRecord + 0x28, 0);
        memory.U64(bagRecord + 0xD8, bagData); memory.U64(bagRecord + 0xE0, 0);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3);
        memory.U64(bagRecord + 0x40, 0x145D8);
        ClickAimUi.Running = true;
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unreadable alternative cannot authorize selling");
        ClickAimUi.Running = false;
        ulong duplicateData = 0x25010000;
        memory.U64(bagRecord + 0x48, duplicateData); memory.Bytes(duplicateData, new byte[0x20]);
        memory.U64(duplicateData + 8, bagRecord); memory.Guid(duplicateData + 0x10, bag);
        Require(!WowBInventoryProbe.Capture().Contains("STOPPED RESEARCH ONLY"), "ambiguous identity headers receive no exploratory candidate");
        memory.U64(bagRecord + 0x40, 0); memory.U64(bagRecord + 0x48, 0);
        WowBInventoryProbe.Capture(); choices = WowBInventoryProbe.ObservedBags();
        Require(choices.Count == 1, "choices return only after the ambiguity is gone");
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
            "70862 active selling subtotal excludes specialty slots");
        memory.U64(bagRecord + 0x40, alternative); memory.HeaderStates[alternative] = 0x2000;
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out owner) && tags == 16 && owner == player,
            "active subtotal accepts a rechecked reserved header for a classified bag");
        memory.HeaderStates[alternative] = 0x1000;
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "committed unreadable alternative blocks active subtotal");
        memory.HeaderStates[alternative] = 0x2000;
        int activeQueries = 0;
        memory.OnStateQuery = address => {
            if (address == alternative && ++activeQueries == 3) memory.HeaderStates[address] = 0x1000;
        };
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "reserved-to-committed change blocks active subtotal");
        memory.OnStateQuery = null; memory.HeaderStates.Remove(alternative); memory.U64(bagRecord + 0x40, 0);
        memory.U64(bagRecord + 0x58, 0x1FFFE0D4); memory.U64(bagRecord + 0x118, 0x8FFFFDFC);
        memory.FreeHeaders.Add(0x1FFFE0D4); memory.FreeHeaders.Add(0x8FFFFDFC);
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out owner) && tags == 16 && owner == player,
            "active subtotal permits OS-confirmed free alternatives while excluding specialty bag");
        memory.FreeHeaders.Remove(0x8FFFFDFC);
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unknown alternative blocks active subtotal");
        memory.FreeHeaders.Clear(); memory.U64(bagRecord + 0x58, 0); memory.U64(bagRecord + 0x118, 0);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 4);
        memory.U32(0x22000030, (uint)WowBOffsets.HandleOccupiedBit); memory.U64(0x22000038, bagRecord + 0xC0);
        memory.U64(bagRecord + 0x28, bagData);
        memory.Guid(bagRecord + 0xD8, new WowBGuid(0x145D8, 0xC01180000000000));
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out owner) && tags == 16 && owner == player,
            "classified specialty with verified neighbor overlap still excludes its slots");
        memory.OnRead = (address, count) => {
            if (address == 0x145D8 && count == 0x20) memory.U32(0x22000030, 0);
        };
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unstable neighbor blocks active selling");
        memory.OnRead = null; memory.U32(0x22000030, 0); memory.U64(bagRecord + 0x28, 0);
        memory.U64(bagRecord + 0xD8, bagData); memory.U64(bagRecord + 0xE0, 0);
        memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3);
        memory.U64(bagRecord + 0x40, 0x145D8);
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unreadable alternative blocks selling even for classified bag");
        memory.U64(bagRecord + 0x40, 0);
        memory.Bytes(image + 0x2475876, new byte[7]);
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "readable 70862 bag marker mismatch fails closed");
        memory.Bytes(image + 0x2475876, Convert.FromHexString("486390C00A0000"));
        int reads = 0;
        memory.OnRead = (address, count) => { if (address == units[0] + 0x7D04 && count == 1 && ++reads == 2) memory.Bytes(address, new byte[] { 15 }); };
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "changing 70862 subtotal fails closed");
        memory.OnRead = null; memory.Bytes(units[0] + 0x7D04, new byte[] { 16 });
        ClickAimUi.Running = false;
        // A second, ordinary bag must count, and cannot be assumed ordinary merely from size/index.
        var ordinaryBag = new WowBGuid(3333, 4444);
        ulong record2 = 0x23003000, data2 = 0x26000000;
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
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
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, new WowBGuid(99, 88));
        Require(!WowBInventoryProbe.ClassifyBag(both[1], WowBBagKind.Specialty, out _), "stale selected identity cannot be classified");
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
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
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, new WowBGuid(99, 88));
        Require(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unresolved changed identity fails closed");
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        memory.Bytes(image + Wow70862Offsets.ContainerIdentityTableCandidate, new byte[80]);
        Require(WowBInventoryProbe.TryCaptureSellingSubtotal(out tags, out _) && tags == 0, "stable empty table supports backpack-only case");
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate, bag);
        memory.Guid(image + Wow70862Offsets.ContainerIdentityTableCandidate + 16, ordinaryBag);
        ClickAimUi.Running = false;
        memory.Bytes(array, new byte[51 * 16]);
        for (int i = 0; i < 12; i++) memory.U64(image + Wow70862Offsets.StoredActionArrayCandidate + (ulong)(8 * i), 0);
        memory.U64(image + Wow70862Offsets.StoredActionArrayCandidate, 6603);
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
        Require(WowBGrindFactions.PrepareForGrind(out string reason), "70862 guarded synthetic startup: " + reason);
        Require(reader.TryReadMapId(out map) && map == 7 && reader.ReadActionBar().Count == 1 &&
            reader.TryGetUnitFields(units[0], out var fields) && fields.Health == 148 && fields.Power == 10,
            "70862 session reads candidate gameplay fields and its own action array");
        ulong actionAddress = image + Wow70862Offsets.StoredActionArrayCandidate;
        memory.U64(actionAddress, 1);
        Require(!WowBGrindFactions.Experimental70707BarSafe(reader), "changed 70862 action bar invalidates the session guard");
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out string barReason, 300) &&
            barReason.Contains("slot 1") && barReason.Contains("00000000000019CB") && barReason.Contains("0000000000000001"),
            "persistent changed bar stops and reports old and new raw values");
        memory.Remove(actionAddress);
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason, 300) && barReason.Contains("unavailable"),
            "unreadable bar fails closed with diagnostic");
        memory.U64(actionAddress, 1);
        int actionReads = 0;
        memory.OnRead = (address, count) => {
            if (address == actionAddress && count == 96 && ++actionReads == 3) memory.U64(actionAddress, 6603);
        };
        Require(WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason) && actionReads >= 4,
            "transient mismatch resumes only after matching stable original bar");
        memory.OnRead = null;
        // The previous 1.2-second retry would have stopped before this recovery.
        memory.U64(actionAddress, 0); actionReads = 0;
        memory.OnRead = (address, count) => {
            if (address == actionAddress && count == 96 && ++actionReads == 19) memory.U64(actionAddress, 6603);
        };
        Require(WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason, 2500) && actionReads >= 20,
            "all-zero bar restored after more than 1.2 seconds resumes only on original values");
        memory.OnRead = null;
        memory.U64(actionAddress, 0);
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason, 300) &&
            barReason.Contains("timed out") && barReason.Contains("0000000000000000"), "persistent zero bar times out without rebaselining");
        int stopPolls = 0;
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => ++stopPolls >= 3, out barReason, 2500) &&
            barReason.Contains("stopped"), "STOP during a mismatched bar wait aborts promptly");
        memory.U64(actionAddress, 6603);
        memory.Guid(image + Wow70862Offsets.PlayerGuid, default);
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason, 300) && barReason.Contains("identity"),
            "matching bar with unknown player cannot resume");
        memory.Guid(image + Wow70862Offsets.PlayerGuid, new WowBGuid(555, 666));
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason, 300) && barReason.Contains("character changed"),
            "matching bar with a different character cannot resume");
        memory.Guid(image + Wow70862Offsets.PlayerGuid, player);
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => true, out barReason), "STOP interrupts bar recovery even if bar matches");
        bool stoppedDuringRead = false;
        memory.OnRead = (address, count) => { if (address == actionAddress && count == 96) stoppedDuringRead = true; };
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => stoppedDuringRead, out barReason), "STOP during matching reads prevents resume");
        memory.OnRead = null;
        ObjectManager.WowB = null;
        Require(!WowBGrindFactions.WaitForExperimentalBar(reader, () => false, out barReason), "attachment change prevents bar recovery");
        ObjectManager.WowB = reader;
        WowBGrindFactions.EndExperimental70707();
        Require(!reader.TryReadMapId(out _), "ending the session revokes gameplay reads");
        ObjectManager.Wow = null; ObjectManager.WowB = null;
        Styx.Helpers.Logging.ApplicationPath = oldPath;
        if (System.IO.Directory.Exists(tempPath)) System.IO.Directory.Delete(tempPath, true);
        Console.WriteLine("70862 synthetic build routing and snapshot fixtures passed (no live-client validation)");
    }
}
#endif

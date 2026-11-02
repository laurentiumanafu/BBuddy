#if WOWB_READER_TESTS
using System;
using GreenMagic;
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
                diagnostic.Contains("camera=0x33000000") && diagnostic.Contains("zoomCandidate=15"), "candidate diagnostic uses roots");
        Require(legacy.DescribeExperimental70707() == "70707 candidate profile is not active", "legacy profile not switched");
    }
}
#endif

#if WOWB_READER_TESTS
using System;
using System.IO;
using System.Text.Json;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing
{
internal static class ProbeTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Probe test failed: " + message);
    }

    public static void Run()
    {
        var memory = new Memory();
        ulong module = memory.ImageBase64, pool = 0x20000000, slots = 0x20010000;
        ulong playerRecord = 0x30000000, npcRecord = 0x30001000, playerUnit = 0x40000000, npcUnit = 0x40001000;
        var player = new WowBGuid(7, 0x0801000000000000);
        var npc = new WowBGuid(42, 0x1234000000000000);
        memory.Guid(module + WowBOffsets.PlayerGuid, player);
        memory.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
        memory.U64(module + WowBOffsets.HandlePools, pool);
        memory.U64(pool + WowBOffsets.HandlePoolArrayOffset, slots);
        memory.U32(pool + WowBOffsets.HandlePoolCapacityOffset, 2);
        AddUnit(0, playerRecord, playerUnit, player, WowBOffsets.PlayerObjectVtable);
        AddUnit(1, npcRecord, npcUnit, npc, WowBOffsets.CGUnitFnTable);
        memory.Bytes(playerUnit + 0x1E00, new byte[256]);
        memory.Bytes(npcUnit + 0x1E00, new byte[256]);
        memory.U32(playerUnit + WowBOffsets.UnitDataFactionTemplateOffset, 3);
        memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 4);
        memory.Bytes(module + 0x1D63190, Convert.FromHexString("488BC4554157488D68A14881ECB80000"));
        memory.Bytes(module + 0x27EE18D, Convert.FromHexString("488D0DCC553505E8F7A7E6FD8B935C1E0000"));

        ulong rootAddress = module + WowBOffsets.FactionTemplateTableRva;
        ulong storageAddress = 0x210000, fieldsAddress = 0x220000, scalarVectorsAddress = 0x230000;
        ulong pointerArrayAddress = 0x240000, rowsAddress = 0x250000, indexAddress = 0x260000;
        ulong scalarAddress = 0x270000, enemiesAddress = 0x280000, friendsAddress = 0x290000;
        ulong vtable = module + 0x4E480A8;
        byte[] root = new byte[0x220], storage = new byte[0x100], fields = new byte[7 * 24];
        byte[] scalarVectors = new byte[7 * 24], pointerArray = new byte[7 * 8], rows = new byte[14], index = new byte[8];
        byte[] scalar = new byte[8], enemies = new byte[32], friends = new byte[32];
        U64(root, 8, 0x300000); U64(root, 0x10, fieldsAddress); U64(root, 0x18, 7);
        U64(root, 0x28, scalarVectorsAddress); U64(root, 0x30, 7); U64(root, 0x40, pointerArrayAddress);
        U64(root, 0x48, 7); U64(root, 0x160, storageAddress);
        U64(storage, 0, vtable); U64(storage, 8, rowsAddress); U32(storage, 0x38, 7);
        U32(storage, 0x40, 1); U32(storage, 0x44, 10); U32(storage, 0x48, 2);
        U64(storage, 0xB0, indexAddress); U64(storage, 0xB8, 2);
        int[] modes = { 1, 5, 1, 3, 1, 4, 4 }, offsets = { 0, 12, 27, 31, 35, 39, 46 }, widths = { 12, 15, 4, 4, 4, 7, 8 };
        for (int i = 0; i < 7; i++)
        {
            U32(fields, i * 24 + 8, (uint)modes[i]); U32(fields, i * 24 + 12, (uint)offsets[i]);
            U32(fields, i * 24 + 16, (uint)widths[i]);
        }
        U32(fields, 3 * 24 + 4, 8); U32(fields, 5 * 24 + 4, 32); U32(fields, 6 * 24 + 4, 32);
        U32(fields, 5 * 24 + 20, 8); U32(fields, 6 * 24 + 20, 8);
        U64(scalarVectors, 3 * 24, scalarAddress); U64(scalarVectors, 3 * 24 + 8, 2);
        U64(pointerArray, 5 * 8, enemiesAddress); U64(pointerArray, 6 * 8, friendsAddress);
        U32(scalar, 0, 2);
        ulong pRow = 3UL | (72UL << 12) | (3UL << 27) | (12UL << 35);
        ulong nRow = 47UL | (2UL << 27) | (4UL << 35);
        Array.Copy(BitConverter.GetBytes(pRow), 0, rows, 0, 7); Array.Copy(BitConverter.GetBytes(nRow), 0, rows, 7, 7);
        BitConverter.GetBytes((ushort)2).CopyTo(index, 0); BitConverter.GetBytes((ushort)0).CopyTo(index, 2);
        BitConverter.GetBytes((ushort)3).CopyTo(index, 4); BitConverter.GetBytes((ushort)1).CopyTo(index, 6);
        memory.Bytes(rootAddress, root); memory.Bytes(storageAddress, storage); memory.Bytes(vtable, new byte[0x40]);
        memory.Bytes(fieldsAddress, fields); memory.Bytes(scalarVectorsAddress, scalarVectors); memory.Bytes(pointerArrayAddress, pointerArray);
        memory.Bytes(rowsAddress, rows); memory.Bytes(indexAddress, index); memory.Bytes(scalarAddress, scalar);
        memory.Bytes(enemiesAddress, enemies); memory.Bytes(friendsAddress, friends);
        ObjectManager.Wow = memory; ObjectManager.WowB = new WowBReader(memory);
        string temp = Path.Combine(Path.GetTempPath(), "bbuddy-probe-test-" + Guid.NewGuid().ToString("N"));
        Styx.Helpers.Logging.ApplicationPath = temp;
        try
        {
            FactionTests.Run(memory, player, npc, npcUnit, npcRecord);
            InventoryTests.Run(memory, playerUnit);
            VendorTests.Run(memory, player, npc, npcUnit, npcRecord);
            string result = WowBReactionProbe.Capture("friendly");
            Check(result.Contains("target→player 4, player→target 4") && result.Contains("NOT live reaction"), "base comparison displayed");
            string path = WowBReactionProbe.PathName;
            string[] saved = File.ReadAllLines(path);
            Check(saved.Length == 1, "one observation saved");
            using JsonDocument parsed = JsonDocument.Parse(saved[0]);
            JsonElement comparison = parsed.RootElement.GetProperty("templateComparison");
            Check(comparison.GetProperty("targetKey").GetUInt32() == 4 && comparison.GetProperty("playerKey").GetUInt32() == 3 &&
                comparison.GetProperty("targetToPlayer").GetInt32() == 4 && comparison.GetProperty("playerToTarget").GetInt32() == 4,
                "comparison fields saved");
            memory.Remove(module + 0x27EE18D);
            result = WowBReactionProbe.Capture("other_observation");
            Check(result.Contains("comparison unavailable") && File.ReadAllLines(path).Length == 2, "missing signature unavailable");
            memory.Bytes(module + 0x27EE18D, Convert.FromHexString("488D0DCC553505E8F7A7E6FD8B935C1E0000"));
            memory.U64(storageAddress, module + 0x123456);
            result = WowBReactionProbe.Capture("friendly");
            Check(result.Contains("comparison unavailable") && File.ReadAllLines(path).Length == 3, "wrong vtable unavailable");
            memory.U64(storageAddress, vtable);
            memory.Remove(friendsAddress + 3);
            result = WowBReactionProbe.Capture("friendly");
            Check(result.Contains("comparison unavailable") && File.ReadAllLines(path).Length == 4, "unreadable palette unavailable");
            memory.Bytes(friendsAddress, friends);
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + WowBOffsets.UnitDataFactionTemplateOffset && count == 4)
                    memory.Guid(module + WowBOffsets.PlayerTargetGuid, player);
            };
            result = WowBReactionProbe.Capture("friendly");
            memory.OnRead = null;
            Check(result.Contains("changed during sampling") && File.ReadAllLines(path).Length == 4,
                "target change during comparison prevents save");
        }
        finally
        {
            ObjectManager.Wow = null; ObjectManager.WowB = null;
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        void AddUnit(uint i, ulong record, ulong unit, WowBGuid guid, ulong table)
        {
            ulong slot = slots + i * WowBOffsets.HandlePoolElementStride;
            memory.U32(slot, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(slot + 4, 0x12345678);
            memory.U64(slot + WowBOffsets.HandlePoolRecordOffset, record);
            memory.Guid(record + WowBOffsets.RecordGuidLoOffset, guid);
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, unit);
            memory.U64(unit + WowBOffsets.UnitObjectBackPtrOffset, record);
            memory.Guid(unit + WowBOffsets.UnitObjectGuidLoOffset, guid);
            memory.U64(unit, module + table);
            memory.F32(record + WowBOffsets.RecordPositionOffset, 1);
            memory.F32(record + WowBOffsets.RecordPositionOffset + 4, 2);
            memory.F32(record + WowBOffsets.RecordPositionOffset + 8, 3);
            memory.F32(record + WowBOffsets.RecordPositionOffset + 12, 0);
            memory.F32(record + WowBOffsets.RecordPositionOffset + 16, 1);
        }
    }

    private static void U32(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static void U64(byte[] bytes, int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
}
}
#endif

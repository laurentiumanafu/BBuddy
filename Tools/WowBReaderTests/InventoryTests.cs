#if WOWB_READER_TESTS
using System;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing
{
    internal static class InventoryTests
    {
        private static void Check(bool condition, string text) { if (!condition) throw new Exception("Inventory probe fixture failed: " + text); }
        public static void Run(Memory memory, ulong playerUnit)
        {
            ulong header = playerUnit + 0x9440, array = 0x33000000;
            memory.Bytes(memory.GetAbsolute64(0x290CFC0), Convert.FromHexString("48895C2408574883EC208BDA488BF9"));
            memory.Bytes(memory.GetAbsolute64(0x2F5F2B0), Convert.FromHexString("48895C24084889742418574881EC2004"));
            memory.Bytes(header, new byte[0x28]); memory.U32(header, 2); memory.U64(header + 8, array);
            byte[] entries = new byte[32]; entries[0] = 123; memory.Bytes(array, entries);
            memory.EnableCache();
            string first = WowBInventoryProbe.Capture();
            Check(memory.CacheEnabled, "cache restored after capture"); memory.DisableCache();
            Check(first.Contains("previous=none or different context") && first.Contains("entry[0] 7B000000000000000000000000000000"), "bounded raw candidate capture");
            entries[0] = 0; entries[16] = 123; memory.Bytes(array, entries);
            string moved = WowBInventoryProbe.Capture();
            Check(moved.Contains("previous=comparable") && moved.Contains("changed entries=2"), "same bytes moved between indices");
            ClickAimUi.Running = true; Check(WowBInventoryProbe.Capture().Contains("Stop the bot"), "running denied"); ClickAimUi.Running = false;
            memory.U32(header, 257); Check(WowBInventoryProbe.Capture().Contains("outside diagnostic bounds"), "oversize denied"); memory.U32(header, 2);
            memory.U64(header + 8, 9); Check(WowBInventoryProbe.Capture().Contains("outside diagnostic bounds"), "invalid pointer denied"); memory.U64(header + 8, array);
            int reads = 0;
            memory.OnRead = (address, length) => { if (address == array && length == 32 && ++reads == 2) { entries[1] ^= 1; memory.Bytes(array, entries); } };
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "changing array discarded"); memory.OnRead = null;
            memory.OnRead = null;
            int headerReads = 0;
            memory.OnRead = (address, length) => { if (address == header && length == 0x28 && ++headerReads == 2) memory.U32(header, 3); };
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "changing header discarded"); memory.OnRead = null; memory.U32(header, 2);
            memory.Remove(array + 2);
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "unreadable candidate not interpreted as empty"); memory.Bytes(array, entries);
            memory.Remove(memory.GetAbsolute64(0x290CFC0));
            Check(WowBInventoryProbe.Capture().Contains("signatures unreadable"), "wrong build denied");
            Console.WriteLine("Inventory research probe synthetic fixtures passed");
        }

        public static void Run70707(Memory memory, ulong playerUnit, WowBGuid player)
        {
            ulong module = memory.ImageBase64, header = playerUnit + 0x9460, array = 0x33010000;
            memory.Bytes(module + 0x2162D00, Convert.FromHexString("48895C2408574883EC2080792000"));
            memory.Bytes(module + 0x26CAA90, Convert.FromHexString("48895C24084889742418574881EC2004"));
            memory.Bytes(header, new byte[0x28]); memory.U32(header, 2); memory.U64(header + 8, array);
            byte[] entries = new byte[32]; entries[0] = 123; memory.Bytes(array, entries);
            string first = WowBInventoryProbe.Capture();
            Check(first.StartsWith("70707 read-only") && first.Contains("+0x9460") && first.Contains("not proven") &&
                first.Contains("entry[0] 7B000000000000000000000000000000"), "70707 bounded candidate snapshot");
            entries[0] = 0; entries[16] = 123; memory.Bytes(array, entries);
            Check(WowBInventoryProbe.Capture().Contains("changed entries=2"), "70707 comparison records two changes");
            ClickAimUi.Paused = true; Check(WowBInventoryProbe.Capture().Contains("Stop the bot"), "70707 paused denied"); ClickAimUi.Paused = false;
            memory.U32(header, 257); Check(WowBInventoryProbe.Capture().Contains("outside diagnostic bounds"), "70707 oversize denied"); memory.U32(header, 2);
            memory.U64(header + 8, 9); Check(WowBInventoryProbe.Capture().Contains("outside diagnostic bounds"), "70707 bad pointer denied"); memory.U64(header + 8, array);
            int reads = 0;
            memory.OnRead = (address, length) => { if (address == array && length == 32 && ++reads == 2) { entries[1] ^= 1; memory.Bytes(array, entries); } };
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "70707 changing entries discarded"); memory.OnRead = null;
            int headerReads = 0;
            memory.OnRead = (address, length) => { if (address == header && length == 0x28 && ++headerReads == 2) memory.U32(header, 3); };
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "70707 changing header discarded"); memory.OnRead = null; memory.U32(header, 2);
            memory.Remove(array + 2); Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "70707 unreadable bytes discarded"); memory.Bytes(array, entries);
            memory.OnRead = (address, length) => { if (address == array && length == 32) memory.Guid(module + Wow70707Offsets.PlayerGuid, default); };
            Check(WowBInventoryProbe.Capture().Contains("probe discarded"), "70707 player change discarded"); memory.OnRead = null;
            memory.Guid(module + Wow70707Offsets.PlayerGuid, player);
            memory.Remove(module + 0x2162D00);
            Check(WowBInventoryProbe.Capture().Contains("lookup=unreadable"), "70707 inaccessible code reported with separate build guards");
            memory.Bytes(module + 0x2162D00, Convert.FromHexString("48895C2408574883EC2080792000"));
            memory.Bytes(module + 0x26CAA90, new byte[16]);
            Check(WowBInventoryProbe.Capture().Contains("signatures unreadable or different"), "70707 readable signature mismatch denied");
            memory.Bytes(module + 0x26CAA90, Convert.FromHexString("48895C24084889742418574881EC2004"));
            memory.Bytes(module + 0x2890305, new byte[8]);
            Check(WowBInventoryProbe.Capture().Contains("identity unavailable"), "70707 build marker mismatch denied");
            memory.Bytes(module + 0x2890305, Convert.FromHexString("448B8C86C01B0000"));

            // Synthetic container candidate: this exercises bounds and races, not live bag identity.
            memory.Bytes(module + 0xD5C7FC, Convert.FromHexString("E86FAE8001"));
            memory.Bytes(module + 0xD5C820, Convert.FromHexString("8B80C00A0000"));
            memory.Bytes(module + 0x2569186, Convert.FromHexString("486390C00A0000"));
            memory.Bytes(module + 0x2569194, Convert.FromHexString("4C8D8090040000"));
            memory.Bytes(module + 0x25691A0, Convert.FromHexString("498B00"));
            ulong table = module + 0x6D41CA0, record = 0x23002000, data = 0x25000000;
            WowBGuid container = new WowBGuid(1234, 5678);
            memory.Bytes(table, new byte[80]); memory.Guid(table + 16, container);
            memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3);
            memory.U32(0x22000020, (uint)WowBOffsets.HandleOccupiedBit);
            memory.U32(0x22000024, 0); memory.U64(0x22000028, record);
            memory.Guid(record + WowBOffsets.RecordGuidLoOffset, container);
            memory.Bytes(record + 0x28, new byte[0x160]);
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, data);
            memory.Bytes(data, new byte[0x20]); memory.U64(data + 8, record); memory.Guid(data + 0x10, container);
            memory.U32(data + 0xAC0, 2);
            byte[] contents = new byte[32]; BitConverter.GetBytes(1UL << 58).CopyTo(contents, 0);
            memory.Bytes(data + 0x490, contents);
            string baseline = WowBInventoryProbe.Capture();
            Check(baseline.Contains("container-candidate[1]") && baseline.Contains("raw+0xAC0=2") &&
                baseline.Contains("first-qword high-six-zero=1") && baseline.Contains("NOT verified capacity/free slots"), "container candidate baseline");
            Array.Clear(contents, 0, 32); BitConverter.GetBytes(1UL << 58).CopyTo(contents, 16); memory.Bytes(data + 0x490, contents);
            string changed = WowBInventoryProbe.Capture();
            Check(changed.Contains("previous=comparable") && changed.Contains("  changed entries=2; zero tag"), "container entry comparison");
            memory.U32(data + 0xAC0, 65);
            Check(WowBInventoryProbe.Capture().Contains("exceeds diagnostic cap"), "oversized container candidate rejected");
            memory.U32(data + 0xAC0, 2);
            int slotReads = 0;
            memory.OnRead = (address, count) => { if (address == data + 0x490 && count == 32 && ++slotReads == 2) { contents[2] ^= 1; memory.Bytes(address, contents); } };
            Check(WowBInventoryProbe.Capture().Contains("unstable or unreadable; discarded"), "changing container contents discarded"); memory.OnRead = null;
            memory.Remove(data + 0x490);
            Check(WowBInventoryProbe.Capture().Contains("unstable or unreadable; discarded"), "unreadable slots unknown"); memory.Bytes(data + 0x490, contents);
            int tableReads = 0;
            memory.OnRead = (address, count) => { if (address == table && count == 80 && ++tableReads == 2) memory.Guid(table + 16, default); };
            Check(WowBInventoryProbe.Capture().Contains("identity table, player, attachment, code or bot state changed; discarded"), "changing identity table discarded"); memory.OnRead = null; memory.Guid(table + 16, container);
            memory.U64(data + 8, 0x23004000);
            Check(WowBInventoryProbe.Capture().Contains("no unique stable identity-matching header"), "container backpointer mismatch rejected"); memory.U64(data + 8, record);
            ulong fourAligned = 0x26000004;
            memory.Bytes(fourAligned, new byte[0x20]); memory.U64(fourAligned + 8, record); memory.Guid(fourAligned + 0x10, container);
            memory.U32(fourAligned + 0xAC0, 2); memory.Bytes(fourAligned + 0x490, contents);
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, fourAligned);
            Check(WowBInventoryProbe.Capture().Contains("selected source=+0xD8") &&
                WowBInventoryProbe.Capture().Contains("object=0x26000004"), "four-byte-aligned identity-matching candidate diagnosed");
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, 7);
            string invalidPointer = WowBInventoryProbe.Capture();
            Check(invalidPointer.Contains("record+0xD8=0x7") && invalidPointer.Contains("no unique stable identity-matching header"), "invalid pointer reported as unknown");
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, 0); memory.U64(record + 0xE0, data);
            Check(WowBInventoryProbe.Capture().Contains("selected source=+0xE0"), "bounded alternative pointer candidate diagnosed");
            memory.Remove(record + 0x180);
            Check(WowBInventoryProbe.Capture().Contains("unreadable fields=1"), "incomplete record scan is unknown"); memory.U64(record + 0x180, 0);
            memory.U64(record + 0xE8, fourAligned);
            Check(WowBInventoryProbe.Capture().Contains("identity-matching headers=2"), "ambiguous candidate objects rejected");
            memory.U64(record + 0xE8, 0x27000000);
            Check(WowBInventoryProbe.Capture().Contains("unavailable headers=1"), "unreadable possible header makes candidate unknown");
            memory.U64(record + 0xE8, 0); memory.U64(record + 0xE0, 0); memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, data);
            int alternativeReads = 0;
            memory.OnRead = (address, count) => { if (address == record + 0xE0 && count == 8 && ++alternativeReads == 2) memory.U64(address, fourAligned); };
            Check(WowBInventoryProbe.Capture().Contains("changed fields=1"), "changing alternative record field makes candidate unknown");
            memory.OnRead = null; memory.U64(record + 0xE0, 0);
            memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 4);
            memory.U32(0x22000030, (uint)WowBOffsets.HandleOccupiedBit); memory.U32(0x22000034, 0); memory.U64(0x22000038, 0x23003000);
            memory.Guid(0x23003000 + WowBOffsets.RecordGuidLoOffset, container);
            Check(WowBInventoryProbe.Capture().Contains("pool matches=2; object unavailable/ambiguous"), "duplicate container identities rejected");
            memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 3); memory.U32(0x22000030, 0);
            memory.OnRead = (address, count) => { if (address == data + 0x490 && count == 32) memory.Guid(module + Wow70707Offsets.PlayerGuid, default); };
            Check(WowBInventoryProbe.Capture().Contains("identity table, player, attachment, code or bot state changed; discarded"), "changed player during container read discarded");
            memory.OnRead = null; memory.Guid(module + Wow70707Offsets.PlayerGuid, player);
            memory.Remove(module + 0x2569186);
            Check(WowBInventoryProbe.Capture().Contains("markers or player unavailable"), "unreadable container code marker unavailable");
            memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 2);
            memory.U32(0x22000020, 0);
            Console.WriteLine("70707 inventory and container probe synthetic fixtures passed (no live inventory validation)");
        }
    }
}
#endif

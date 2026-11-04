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
            Console.WriteLine("70707 inventory probe synthetic fixtures passed (no live inventory validation)");
        }
    }
}
#endif

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
            var sellPolicy = new WowBBagSellPolicy();
            Check(!sellPolicy.Observe(16) && !sellPolicy.Observe(0), "one zero alone does not sell");
            Check(!sellPolicy.Observe(null) && !sellPolicy.Observe(0) && sellPolicy.Observe(0), "unknown resets consecutive zero observations");
            sellPolicy.TripCompleted();
            Check(!sellPolicy.Observe(0) && !sellPolicy.Observe(0), "completed replay waits for space instead of looping");
            Check(!sellPolicy.Observe(1) && !sellPolicy.Observe(0) && sellPolicy.Observe(0), "space rearms next full bag trip");
            sellPolicy.Reset();
            Check(!sellPolicy.Observe(-1) && !sellPolicy.Observe(0), "negative and reset cannot fabricate a full observation");
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

            // Synthetic special-index candidate only, not a claim about backpack or bank layout.
            memory.Bytes(module + 0x25691D6, Convert.FromHexString("4038A880940000"));
            memory.Bytes(module + 0x25691E9, Convert.FromHexString("0FB6B8047D0000"));
            memory.Bytes(module + 0x25691F4, Convert.FromHexString("83C723"));
            memory.U32(header, 51); memory.Bytes(header + 0x20, new byte[] { 1 });
            memory.Bytes(playerUnit + 0x7D04, new byte[] { 16 });
            byte[] rootEntries = new byte[51 * 16];
            for (int i = 35; i < 50; i++) BitConverter.GetBytes(1UL << 58).CopyTo(rootEntries, i * 16 + 8);
            memory.Bytes(array, rootEntries);
            string rootOneEmpty = WowBInventoryProbe.Capture();
            Check(rootOneEmpty.Contains("raw player+0x7D04=16") && rootOneEmpty.Contains("high-qword high-six-zero=1") &&
                rootOneEmpty.Contains("NOT a free-slot verdict"), "bounded special-index candidate reported");
            Array.Clear(rootEntries, 36 * 16 + 8, 8); memory.Bytes(array, rootEntries);
            Check(WowBInventoryProbe.Capture().Contains("high-qword high-six-zero=2"), "special-index tag comparison");
            memory.Bytes(header + 0x20, new byte[] { 0 });
            Check(WowBInventoryProbe.Capture().Contains("range unavailable or gated"), "special-index flag gate honored");
            memory.Bytes(header + 0x20, new byte[] { 1 });
            int specialReads = 0;
            memory.OnRead = (address, length) => { if (address == playerUnit + 0x7D04 && length == 1 && ++specialReads == 2) memory.Bytes(address, new byte[] { 17 }); };
            Check(WowBInventoryProbe.Capture().Contains("size, array, player or bot state unreadable/changed"), "changing special-index size rejected");
            memory.OnRead = null; memory.Bytes(playerUnit + 0x7D04, new byte[] { 16 });
            memory.Bytes(module + 0x25691D6, new byte[7]);
            Check(WowBInventoryProbe.Capture().Contains("special-index research: readable signature mismatch"), "special-index signature mismatch rejected");
            memory.Bytes(module + 0x25691D6, Convert.FromHexString("4038A880940000"));
            memory.U32(header, 2); memory.Bytes(header + 0x20, new byte[] { 0 }); memory.Bytes(array, entries);

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
            byte[] contents = new byte[32]; BitConverter.GetBytes(1UL << 58).CopyTo(contents, 8);
            memory.Bytes(data + 0x488, contents);
            string baseline = WowBInventoryProbe.Capture();
            Check(baseline.Contains("container-candidate[1]") && baseline.Contains("raw+0xAC0=2") &&
                baseline.Contains("second-qword high-six-zero=1") && baseline.Contains("NOT verified capacity/free slots"), "container candidate baseline");
            memory.U32(header, 51); memory.Bytes(header + 0x20, new byte[] { 1 }); memory.Bytes(array, rootEntries);
            string subtotal = WowBInventoryProbe.Capture();
            Check(subtotal.Contains("research subtotal: candidate sizes=18, zero-tags=3") &&
                subtotal.Contains("1 resolved container candidates, 4 zero identities not classified") &&
                subtotal.Contains("NOT a verified total or free-slot verdict"), "bounded research-only subtotal");
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "selling read denied while stopped");
            ClickAimUi.Running = true;
            Check(WowBInventoryProbe.TryCaptureSellingSubtotal(out int availableTags, out var sellingPlayer) && availableTags == 3 && sellingPlayer == player, "running selling observation has same subtotal");
            long clock = 100;
            (int ZeroTags, WowBGuid Player)? sample = (0, player);
            var monitor = new WowBBagSellMonitor(() => clock, () => System.Threading.Tasks.Task.FromResult(sample));
            var activeReader = ObjectManager.WowB!;
            Check(!monitor.Poll(memory, activeReader, out _), "async monitor does not block on first sample");
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "first zero does not trigger");
            clock += 5000; Check(!monitor.Poll(memory, activeReader, out _), "second sample is scheduled off-thread");
            clock++; Check(monitor.Poll(memory, activeReader, out _), "two fresh zero samples trigger");
            monitor.TripCompleted();
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "completed trip clears pending trigger");
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "still full after trip does not loop");
            sample = (1, player); clock += 5000; monitor.Poll(memory, activeReader, out _);
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "space rearms after trip");
            sample = (0, player); clock += 5000; monitor.Poll(memory, activeReader, out _);
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "fresh zero after space needs confirmation");
            clock += 16000; Check(!monitor.Poll(memory, activeReader, out _), "stale zero cannot trigger");
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "stale sample broke zero streak");
            monitor.Reset(); sample = (0, player); clock++; monitor.Poll(memory, activeReader, out _);
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "first player zero");
            sample = (0, new WowBGuid(987, 654)); clock += 5000; monitor.Poll(memory, activeReader, out _);
            clock++; Check(!monitor.Poll(memory, activeReader, out _), "player change resets zero streak");
            var delayed = new System.Threading.Tasks.TaskCompletionSource<(int ZeroTags, WowBGuid Player)?>();
            var oldRun = new WowBBagSellMonitor(() => clock, () => delayed.Task);
            Check(!oldRun.Poll(memory, activeReader, out _), "unfinished scan does not block");
            oldRun.Reset(); delayed.SetResult((0, player)); clock++;
            Check(!oldRun.Poll(memory, activeReader, out _), "old run result discarded");
            clock++; Check(!oldRun.Poll(memory, activeReader, out _), "only one new-run zero observed");
            byte[] filledRoot = (byte[])rootEntries.Clone();
            for (int i = 35; i < 51; i++) BitConverter.GetBytes(1UL << 58).CopyTo(filledRoot, i * 16 + 8);
            byte[] filledBag = (byte[])contents.Clone(); BitConverter.GetBytes(1UL << 58).CopyTo(filledBag, 24);
            memory.Bytes(array, filledRoot); memory.Bytes(data + 0x488, filledBag);
            Check(WowBInventoryProbe.TryCaptureSellingSubtotal(out int fullTags, out _) && fullTags == 0, "zero candidate subtotal observed while running");
            memory.Guid(table + 32, container);
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "duplicate candidate makes selling subtotal unknown");
            memory.Guid(table + 32, new WowBGuid(4321, 8765));
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "unresolved candidate makes selling subtotal unknown");
            memory.Guid(table + 32, default);
            memory.Bytes(playerUnit + 0x7D04, new byte[] { 0 });
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "zero-size player range cannot trigger selling");
            memory.Bytes(playerUnit + 0x7D04, new byte[] { 16 });
            memory.Bytes(module + 0xD5C820, new byte[6]);
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "mismatched code cannot trigger selling");
            memory.Bytes(module + 0xD5C820, Convert.FromHexString("8B80C00A0000"));
            memory.OnRead = (address, length) => { if (address == data + 0x488 && length == 32) memory.U64(array + 35 * 16 + 8, 0); };
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "changed player range during scan cannot trigger selling");
            memory.OnRead = null; memory.Bytes(array, filledRoot);
            memory.OnRead = (address, length) => { if (address == data + 0x488 && length == 32) ClickAimUi.Running = false; };
            Check(!WowBInventoryProbe.TryCaptureSellingSubtotal(out _, out _), "stop during selling read discards observation");
            memory.OnRead = null; ClickAimUi.Running = false; memory.Bytes(array, rootEntries); memory.Bytes(data + 0x488, contents);
            memory.Guid(table + 32, new WowBGuid(4321, 8765));
            string incompleteSubtotal = WowBInventoryProbe.Capture();
            Check(incompleteSubtotal.Contains("research subtotal unavailable") && !incompleteSubtotal.Contains("candidate sizes=18"), "missing nonzero identity prevents subtotal");
            memory.Guid(table + 32, container);
            Check(WowBInventoryProbe.Capture().Contains("research subtotal unavailable"), "duplicate table identity prevents subtotal");
            memory.Guid(table + 32, default);
            memory.U32(data + 0xAC0, 65);
            Check(WowBInventoryProbe.Capture().Contains("research subtotal unavailable"), "oversized container prevents subtotal");
            memory.U32(data + 0xAC0, 2);
            memory.OnRead = (address, length) => { if (address == data + 0x488 && length == 32) memory.U64(array + 35 * 16 + 8, 0); };
            Check(WowBInventoryProbe.Capture().Contains("research subtotal unavailable: player-range candidate changed"), "player-range changes after container read prevent subtotal");
            memory.OnRead = null; memory.Bytes(array, rootEntries);
            memory.OnRead = (address, length) => { if (address == data + 0x488 && length == 32) memory.Bytes(module + 0x25691D6, new byte[7]); };
            Check(WowBInventoryProbe.Capture().Contains("research subtotal unavailable: player-range candidate changed"), "late special-index marker mismatch prevents subtotal");
            memory.OnRead = null; memory.Bytes(module + 0x25691D6, Convert.FromHexString("4038A880940000"));
            memory.U32(header, 2); memory.Bytes(header + 0x20, new byte[] { 0 }); memory.Bytes(array, entries);
            Array.Clear(contents, 0, 32); BitConverter.GetBytes(1UL << 58).CopyTo(contents, 24); memory.Bytes(data + 0x488, contents);
            string changed = WowBInventoryProbe.Capture();
            Check(changed.Contains("previous=comparable") && changed.Contains("  changed entries=2; zero tag"), "container entry comparison");
            // Reproduce the observed split-half case: six 16-byte candidates
            // start at +0x488; the helper's tag qword is at +0x490.
            memory.U32(data + 0xAC0, 6);
            byte[] six = new byte[96]; memory.Bytes(data + 0x488, six);
            Check(WowBInventoryProbe.Capture().Contains("second-qword high-six-zero=6"), "six empty-looking entries are only a candidate");
            BitConverter.GetBytes(0x8B21UL).CopyTo(six, 80);
            BitConverter.GetBytes(0x0C01180000000000UL).CopyTo(six, 88);
            memory.Bytes(data + 0x488, six);
            string oneMoved = WowBInventoryProbe.Capture();
            Check(oneMoved.Contains("second-qword high-six-zero=5") && oneMoved.Contains("changed entries=1") &&
                oneMoved.Contains("entry[5] 00000000000000000000000000000000 -> 218B000000000000000000000018010C"),
                "full candidate identity changes in one aligned entry");
            memory.U32(data + 0xAC0, 2); memory.Bytes(data + 0x488, contents);
            memory.U32(data + 0xAC0, 65);
            Check(WowBInventoryProbe.Capture().Contains("exceeds diagnostic cap"), "oversized container candidate rejected");
            memory.U32(data + 0xAC0, 2);
            int slotReads = 0;
            memory.OnRead = (address, count) => { if (address == data + 0x488 && count == 32 && ++slotReads == 2) { contents[2] ^= 1; memory.Bytes(address, contents); } };
            Check(WowBInventoryProbe.Capture().Contains("unstable or unreadable; discarded"), "changing container contents discarded"); memory.OnRead = null;
            memory.Remove(data + 0x488);
            Check(WowBInventoryProbe.Capture().Contains("unstable or unreadable; discarded"), "unreadable slots unknown"); memory.Bytes(data + 0x488, contents);
            int tableReads = 0;
            memory.OnRead = (address, count) => { if (address == table && count == 80 && ++tableReads == 2) memory.Guid(table + 16, default); };
            Check(WowBInventoryProbe.Capture().Contains("identity table, player, attachment, code or bot state changed; discarded"), "changing identity table discarded"); memory.OnRead = null; memory.Guid(table + 16, container);
            memory.U64(data + 8, 0x23004000);
            Check(WowBInventoryProbe.Capture().Contains("no unique stable identity-matching header"), "container backpointer mismatch rejected"); memory.U64(data + 8, record);
            ulong fourAligned = 0x26000004;
            memory.Bytes(fourAligned, new byte[0x20]); memory.U64(fourAligned + 8, record); memory.Guid(fourAligned + 0x10, container);
            memory.U32(fourAligned + 0xAC0, 2); memory.Bytes(fourAligned + 0x488, contents);
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
            memory.OnRead = (address, count) => { if (address == data + 0x488 && count == 32) memory.Guid(module + Wow70707Offsets.PlayerGuid, default); };
            Check(WowBInventoryProbe.Capture().Contains("identity table, player, attachment, code or bot state changed; discarded"), "changed player during container read discarded");
            memory.OnRead = null; memory.Guid(module + Wow70707Offsets.PlayerGuid, player);
            memory.Remove(module + 0x2569186);
            string missingMarker = WowBInventoryProbe.Capture();
            Check(missingMarker.Contains("0x2569186=unreadable") && missingMarker.Contains("raw+0xAC0=2") &&
                missingMarker.Contains("unreadable is not confirmation of a match"), "unreadable container marker reported while identity guards remain active");
            memory.Bytes(module + 0xD5C820, new byte[6]);
            string mismatchedMarker = WowBInventoryProbe.Capture();
            Check(mismatchedMarker.Contains("readable code marker mismatch") && mismatchedMarker.Contains("0xD5C820=MISMATCH") &&
                mismatchedMarker.Contains("0x2569186=unreadable") && !mismatchedMarker.Contains("raw+0xAC0=2"),
                "readable mismatch rejected even when another marker unreadable");
            memory.Bytes(module + 0xD5C820, Convert.FromHexString("8B80C00A0000"));
            memory.Bytes(module + 0x2569186, Convert.FromHexString("486390C00A0000"));
            memory.OnRead = (address, count) => { if (address == data + 0x488 && count == 32) memory.Bytes(module + 0xD5C820, new byte[6]); };
            Check(WowBInventoryProbe.Capture().Contains("0xD5C820=MISMATCH") &&
                WowBInventoryProbe.Capture().Contains("readable code marker mismatch"), "late readable code mismatch discards and blocks next capture");
            memory.OnRead = null; memory.Bytes(module + 0xD5C820, Convert.FromHexString("8B80C00A0000"));
            memory.OnRead = (address, count) => { if (address == table && count == 80) memory.Remove(module + 0x2569186); };
            string lateUnreadable = WowBInventoryProbe.Capture();
            Check(lateUnreadable.Contains("0x2569186=matched") && lateUnreadable.Contains("0x2569186=unreadable") &&
                lateUnreadable.Contains("raw+0xAC0=2"), "marker transitions to unreadable reported without discarding guarded research");
            memory.OnRead = null;
            memory.U32(0x21000000 + WowBOffsets.HandlePoolCapacityOffset, 2);
            memory.U32(0x22000020, 0);
            Console.WriteLine("70707 inventory and container probe synthetic fixtures passed (no live inventory validation)");
        }
    }
}
#endif

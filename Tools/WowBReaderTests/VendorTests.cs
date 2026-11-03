#if WOWB_READER_TESTS
using System;
using System.IO;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing
{
    internal static class VendorTests
    {
        private static void Check(bool value, string name) { if (!value) throw new Exception("Vendor test failed: " + name); }
        public static void Run(Memory memory, WowBGuid player, WowBGuid npc, ulong unit, ulong record)
        {
            string path = Path.Combine(Styx.Helpers.Logging.ApplicationPath, "vendor-fixture.xml");
            Check(VendorFile.Add(1, 2, 3, out _, path) == 1, "create old vendor point");
            Check(VendorFile.AddStep(0, new VendorFile.Step { Kind = "Key", Vk = 66, WaitMs = 300 }, out _, path), "record original click plan");
            Check(VendorFile.Read(path)[0].TownMap == null, "legacy point requires explicit town");
            Check(VendorFile.SetTown(0, -100, 200, 30, 0, out _, path), "set town map zero");
            Check(!VendorFile.AddMerchant(0, npc.ToString(), 1, out _, path), "wrong map rejected");
            Check(!VendorFile.AddMerchant(0, "garbage", 0, out _, path), "invalid guid rejected");
            Check(VendorFile.AddMerchant(0, npc.ToString(), 0, out _, path) && VendorFile.AddMerchant(0, npc.ToString(), 0, out _, path), "record and deduplicate merchant");
            var point = VendorFile.Read(path)[0];
            Check(point.TownMap == 0 && point.X == -100 && point.Steps.Count == 1 && point.MerchantGuids.Count == 1, "town retains old click plan and full guid");
            var reader = ObjectManager.WowB!;
            memory.U32(unit + WowBOffsets.UnitDataHealthOffset, 100);
            memory.U32(unit + WowBOffsets.UnitDataPowerOffset, 0);
            memory.U32(unit + WowBOffsets.UnitDataMaxPowerOffset, 0);
            memory.U64(memory.GetAbsolute64(WowBOffsets.MapRoot), 0x32000000);
            memory.U32(0x32000000 + WowBOffsets.MapIdOffset, 0);
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), npc);
            bool captured = WowBKnownMerchants.CaptureSelected(out var found, out uint map, out string why);
            Check(captured && found == npc && map == 0, "user selected NPC capture: " + why);
            Check(reader.TryCaptureWorldSnapshot(out var snap) && snap != null && snap.TryGetUnit(npc, out var candidate) && WowBKnownMerchants.IsRecordedLive(point, candidate, memory, reader), "only recorded live merchant eligible");
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), player);
            Check(!WowBKnownMerchants.CaptureSelected(out _, out _, out _), "player cannot be recorded as merchant");
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), npc);
            ClickAimUi.Running = true;
            Check(!WowBKnownMerchants.CaptureSelected(out _, out _, out _), "record while running denied");
            ClickAimUi.Running = false;
            snap!.TryGetUnit(npc, out var current);
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, unit + 8);
            Check(!WowBKnownMerchants.IsRecordedLive(point, current, memory, reader), "identity change denied");
            memory.U64(record + WowBOffsets.RecordDataBlockPtrOffset, unit);
            string xml = File.ReadAllText(path).Replace("<Step ", "<step ").Replace("</Step>", "</step>").Replace("<Merchant ", "<merchant ");
            File.WriteAllText(path, xml);
            Check(VendorFile.ClearSteps(0, out _, path) && VendorFile.Read(path)[0].Steps.Count == 0 && VendorFile.Read(path)[0].MerchantGuids.Count == 1, "clearing legacy lowercase steps keeps merchant");
            Check(VendorFile.SetTown(0, 3, 4, 5, 1, out _, path) && VendorFile.Read(path)[0].MerchantGuids.Count == 0, "new town clears lowercase merchant confirmations");
            File.WriteAllText(path, "<Root><Vendor Name=\"legacy\" X=\"1\" Y=\"2\" Z=\"3\" /></Root>");
            Check(VendorFile.SetTown(0, 8, 9, 10, 1, out _, path) && VendorFile.Read(path)[0].X == 8, "legacy direct vendor indexed consistently");
            File.WriteAllText(path, "<Root><Vendor TownMap=\"1\" X=\"invalid\" Y=\"2\" Z=\"3\" /></Root>");
            bool rejected = false; try { VendorFile.Read(path); } catch (FormatException) { rejected = true; }
            Check(rejected, "invalid town coordinates fail closed");
            Console.WriteLine("Vendor town and selected merchant synthetic fixtures passed");
        }
    }
}
#endif

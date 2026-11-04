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

        private static void RunCopyTests(WowBGuid npc)
        {
            string path = Path.Combine(Styx.Helpers.Logging.ApplicationPath, "vendor-copy-fixture.xml");
            Check(VendorFile.Add(1, 2, 3, out _, path) == 1 && VendorFile.Add(7, 8, 9, out _, path) == 2, "copy fixture points");
            Check(VendorFile.SetTown(0, 1, 2, 3, 1, out _, path) && VendorFile.SetTown(1, 7, 8, 9, 0, out _, path), "copy fixture towns");
            Check(VendorFile.AddMerchant(0, npc.ToString(), 1, out _, path) &&
                VendorFile.AddMerchant(1, new WowBGuid(77, 88).ToString(), 0, out _, path), "copy fixture separate merchants");
            Check(VendorFile.AddStep(0, new VendorFile.Step { Kind = "Key", Vk = 66, WaitMs = 600 }, out _, path) &&
                VendorFile.AddStep(0, new VendorFile.Step { Kind = "RightClick", Fx = 0.3456, Fy = 0.6789, WaitMs = 350 }, out _, path) &&
                VendorFile.AddStep(0, new VendorFile.Step { Kind = "Wait", WaitMs = 1234 }, out _, path), "mixed source steps");
            Check(VendorFile.AddStep(1, new VendorFile.Step { Kind = "LeftClick", Fx = 0.1, Fy = 0.2, WaitMs = 700 }, out _, path), "existing destination plan");
            string before = File.ReadAllText(path);
            Check(!VendorFile.CopySteps(0, 1, false, out _, path) && File.ReadAllText(path) == before, "replacement requires consent and does not write");
            Check(!VendorFile.CopySteps(0, 0, true, out _, path) && !VendorFile.CopySteps(-1, 0, true, out _, path) &&
                !VendorFile.CopySteps(0, 2, true, out _, path) && File.ReadAllText(path) == before, "same or missing points rejected without changes");
            var original = VendorFile.Read(path);
            Check(VendorFile.CopySteps(0, 1, true, out _, path, original[0], original[1]), "copy approved replacement");
            var after = VendorFile.Read(path);
            Check(after[0].Steps.Count == 3 && after[1].Steps.Count == 3 &&
                after[1].Steps[0].Kind == "Key" && after[1].Steps[0].Vk == 66 && after[1].Steps[0].WaitMs == 600 &&
                after[1].Steps[1].Kind == "RightClick" && after[1].Steps[1].Fx == 0.3456 && after[1].Steps[1].Fy == 0.6789 &&
                after[1].Steps[1].WaitMs == 350 && after[1].Steps[2].Kind == "Wait" && after[1].Steps[2].WaitMs == 1234,
                "step order, clicks, key and wait copied");
            Check(after[1].TownMap == 0 && after[1].X == 7 && after[1].Y == 8 && after[1].Z == 9 &&
                after[1].MerchantGuids.Count == 1 && after[1].MerchantGuids[0] == new WowBGuid(77, 88).ToString() &&
                after[0].TownMap == 1 && after[0].MerchantGuids[0] == npc.ToString(), "each point retains its own location and merchant");
            Check(VendorFile.RemoveStep(0, 0, out _, path) && VendorFile.Read(path)[1].Steps.Count == 3, "copied steps are independent");
            var stale = VendorFile.Read(path);
            Check(VendorFile.RemoveStep(1, 0, out _, path), "simulate change while confirming");
            string changed = File.ReadAllText(path);
            Check(!VendorFile.CopySteps(0, 1, true, out _, path, stale[0], stale[1]) && File.ReadAllText(path) == changed,
                "stale destination confirmation is rejected");
            Check(VendorFile.ClearSteps(0, out _, path), "empty source fixture");
            changed = File.ReadAllText(path);
            Check(!VendorFile.CopySteps(0, 1, true, out _, path) && File.ReadAllText(path) == changed, "empty source rejected");
            string invalid = "<Root><Vendor Name=\"bad\"><Step Kind=\"bogus\"/></Vendor><Vendor Name=\"target\"><Step Kind=\"Wait\" WaitMs=\"10\"/></Vendor></Root>";
            File.WriteAllText(path, invalid);
            Check(!VendorFile.CopySteps(0, 1, true, out _, path) && File.ReadAllText(path) == invalid, "invalid source cannot overwrite target");
            File.WriteAllText(path, "<Root><Vendor>");
            Check(!VendorFile.CopySteps(0, 1, true, out _, path) && File.ReadAllText(path) == "<Root><Vendor>", "malformed file is not rewritten");
            string many = "<Root><Vendor>" + string.Concat(System.Linq.Enumerable.Repeat("<Step Kind=\"Wait\" WaitMs=\"10\"/>", VendorFile.MaxPlanSteps + 1)) +
                "</Vendor><Vendor><Step Kind=\"Wait\" WaitMs=\"10\"/></Vendor></Root>";
            File.WriteAllText(path, many);
            Check(!VendorFile.CopySteps(0, 1, true, out _, path) && File.ReadAllText(path) == many, "oversized source rejected");
            File.WriteAllText(path, "<Root keep=\"root\"><Vendor><step Kind=\"Wait\" WaitMs=\"10\"/></Vendor><Vendor keep=\"target\"><Other keep=\"child\"/><step Kind=\"Wait\" WaitMs=\"30\"/></Vendor></Root>");
            Check(VendorFile.CopySteps(0, 1, true, out _, path) && VendorFile.Read(path)[1].Steps.Count == 1 &&
                VendorFile.Read(path)[1].Steps[0].WaitMs == 10, "legacy lowercase step replacement");
            var doc = System.Xml.Linq.XDocument.Load(path);
            var root = doc.Root!;
            var target = System.Linq.Enumerable.Last(root.Elements("Vendor"));
            Check((string?)root.Attribute("keep") == "root" && (string?)target.Attribute("keep") == "target" &&
                (string?)target.Element("Other")?.Attribute("keep") == "child", "unrelated XML preserved");
            Check(VendorFile.ClearSteps(1, out _, path) && VendorFile.CopySteps(0, 1, false, out _, path) &&
                VendorFile.Read(path)[1].Steps.Count == 1, "copy to empty destination without replacement");
            var oldSource = VendorFile.Read(path);
            Check(VendorFile.ClearSteps(0, out _, path), "simulate source change while confirming");
            changed = File.ReadAllText(path);
            Check(!VendorFile.CopySteps(0, 1, true, out _, path, oldSource[0], oldSource[1]) && File.ReadAllText(path) == changed,
                "stale source confirmation is rejected");
        }

        public static void Run(Memory memory, WowBGuid player, WowBGuid npc, ulong unit, ulong record)
        {
            RunCopyTests(npc);
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

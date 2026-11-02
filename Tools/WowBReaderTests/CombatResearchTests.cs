#if WOWB_READER_TESTS
using System;
using System.IO;
using System.Linq;
using System.Threading;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

internal static class CombatResearchTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("Combat research: " + name); }
    internal static void Run()
    {
        var mem = new Memory();
        ulong module = mem.ImageBase64, pool = 0x20000000, array = 0x20010000;
        ulong playerRecord = 0x30000000, targetRecord = 0x30001000, playerUnit = 0x40000000, targetUnit = 0x40010000;
        WowBGuid player = new(7, 0x0801000000000000), target = new(9, 0x1234000000000000);
        mem.Guid(module + Wow70707Offsets.PlayerGuid, player);
        mem.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
        mem.U64(module + Wow70707Offsets.HandlePools, pool);
        mem.U64(pool + WowBOffsets.HandlePoolArrayOffset, array);
        mem.U32(pool + WowBOffsets.HandlePoolCapacityOffset, 2);
        void Add(int slot, ulong record, ulong unit, WowBGuid guid, ulong vtable)
        {
            mem.U32(array + (ulong)(slot * 16), (uint)WowBOffsets.HandleOccupiedBit);
            mem.U32(array + (ulong)(slot * 16 + 4), 0);
            mem.U64(array + (ulong)(slot * 16 + 8), record);
            mem.Bytes(record, new byte[0x100]);
            mem.Guid(record + WowBOffsets.RecordGuidLoOffset, guid);
            mem.U64(record + WowBOffsets.RecordDataBlockPtrOffset, unit);
            mem.F32(record + WowBOffsets.RecordPositionOffset, 1);
            mem.F32(record + WowBOffsets.RecordPositionOffset + 4, 2);
            mem.F32(record + WowBOffsets.RecordPositionOffset + 8, 3);
            mem.F32(record + WowBOffsets.RecordPositionOffset + 16, 0);
            mem.Bytes(unit, new byte[WowBCombatResearch.WindowSize]);
            mem.U64(unit, module + vtable);
            mem.U64(unit + WowBOffsets.UnitObjectBackPtrOffset, record);
            mem.Guid(unit + 0x10, guid);
        }
        Add(0, playerRecord, playerUnit, player, Wow70707Offsets.PlayerVtable);
        Add(1, targetRecord, targetUnit, target, Wow70707Offsets.UnitVtable);
        mem.Bytes(module + Wow70707Offsets.StoredActionArrayCandidate, new byte[96]);
        mem.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 403);
        mem.Bytes(module + 0x2080D2C, Convert.FromHexString("488D0DCD38A004E808A843FE8B93541E0000"));
        mem.Bytes(module + 0x20AAF97, Convert.FromHexString("F2480F2A83281E0000"));
        var reader = new WowBReader(mem, experimental70707: true);
        var oldMemory = ObjectManager.Wow; var oldReader = ObjectManager.WowB;
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string folder = Path.Combine(Path.GetTempPath(), "bbuddy-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder; ObjectManager.Wow = mem; ObjectManager.WowB = reader;
            Check(reader.TryCaptureWorldSnapshot(out var snapshot) && snapshot != null && snapshot.TryGetLocalPlayer(out _) && snapshot.TryGetSelectedTarget(out _), "synthetic world");
            Check(WowBGrindFactions.TraceBuild70707(mem, reader), "partial signatures");
            Check(WowBCombatResearch.Capture("ignored", CancellationToken.None, 0, 10).Contains("Invalid"), "bounded sample count");
            ClickAimUi.Running = true;
            Check(WowBCombatResearch.Capture("ignored", CancellationToken.None, 1, 10).Contains("Stop the bot"), "running denied");
            ClickAimUi.Running = false;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(WowBCombatResearch.Capture("ignored", cancelled.Token, 1, 10).Contains("cancelled"), "pre-cancelled capture");
            }
            int reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2) mem.U32(playerUnit + 0x150, 123);
            };
            string result = WowBCombatResearch.Capture("mage Fireball\nnot a second line", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("completed: 3 sample(s)"), "capture completed");
            string path = Directory.GetFiles(Path.Combine(folder, "Logs", "CombatResearch"), "*.txt").Single();
            string data = File.ReadAllText(path);
            Check(data.Contains("label=mage Fireball not a second line") && data.Contains("bar slot=1 raw=0x0000000000000193") &&
                data.Contains("player+0x0000 baseline") && data.Contains("target+0x0000 baseline") &&
                data.Contains("+0x0150:00000000>0000007B") && data.Contains("sample=2 ") &&
                data.Contains("end: completed; samples=3"), "baseline, change, timestamps, action bar and label");
            Check(!reader.HasExperimentalGrindSession && reader.ReadActionBar().Count == 0, "trace does not arm gameplay");
            mem.Remove(playerUnit + 0x103);
            var sparse = WowBCombatResearch.ReadWindow(mem, playerUnit);
            Check(sparse[1] == null && sparse[0] != null && sparse[2] != null, "unreadable chunk stays null");
            string diff = WowBCombatResearch.Diff(null, sparse, "player");
            Check(diff.Contains("player+0x0100 unreadable") && !diff.Contains("player+0x0100 baseline"), "no fabricated zero data");
            mem.Bytes(playerUnit + 0x100, new byte[0x100]);
            var restored = WowBCombatResearch.ReadWindow(mem, playerUnit);
            Check(WowBCombatResearch.Diff(sparse, restored, "player").Contains("player+0x0100 baseline"), "restored chunk gets new baseline");
            reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2)
                    mem.Guid(module + Wow70707Offsets.PlayerTargetGuid, new WowBGuid(99, 88));
            };
            result = WowBCombatResearch.Capture("target change", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("changed during read") && result.Contains("1 sample(s)"), "target change discards current sample");
            path = Directory.GetFiles(Path.Combine(folder, "Logs", "CombatResearch"), "*.txt").Single(p => File.ReadAllText(p).Contains("label=target change"));
            data = File.ReadAllText(path);
            Check(data.Contains("sample=0 ") && !data.Contains("sample=1 ") && data.Contains("last sample discarded"), "no mixed-target sample written");
            mem.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
            reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2) ClickAimUi.Running = true;
            };
            result = WowBCombatResearch.Capture("bot start", CancellationToken.None, 3, 10);
            mem.OnRead = null; ClickAimUi.Running = false;
            Check(result.Contains("changed during read") && result.Contains("1 sample(s)"), "bot start stops sampling safely");
            reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2)
                    mem.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 133);
            };
            result = WowBCombatResearch.Capture("bar change", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("action-bar preview") && result.Contains("1 sample(s)"), "bar change discards sample");
            mem.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 403);
            mem.Guid(module + Wow70707Offsets.PlayerTargetGuid, default);
            result = WowBCombatResearch.Capture("self buff", CancellationToken.None, 1, 10);
            Check(result.Contains("completed: 1 sample(s)"), "self-buff works without target");
            path = Directory.GetFiles(Path.Combine(folder, "Logs", "CombatResearch"), "*.txt").Single(p => File.ReadAllText(p).Contains("label=self buff"));
            data = File.ReadAllText(path);
            Check(data.Contains("target window not captured") && !data.Contains("target+0x0000 baseline"), "no fabricated target window");
            mem.Guid(module + Wow70707Offsets.PlayerTargetGuid, target);
            mem.Bytes(module + 0x20AAF97, new byte[9]);
            Check(WowBCombatResearch.Capture("bad signature", CancellationToken.None, 1, 10).Contains("signatures"), "signature mismatch denied");
            ObjectManager.WowB = new WowBReader(mem);
            Check(WowBCombatResearch.Capture("wrong build", CancellationToken.None, 1, 10).Contains("70707"), "other builds not probed");
            Console.WriteLine("Combat research synthetic fixtures passed.");
        }
        finally
        {
            mem.OnRead = null; ClickAimUi.Running = false; ClickAimUi.Paused = false;
            ObjectManager.Wow = oldMemory; ObjectManager.WowB = oldReader; Styx.Helpers.Logging.ApplicationPath = oldPath;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
#endif

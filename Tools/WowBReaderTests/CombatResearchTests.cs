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
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2)
                { mem.U32(playerUnit + 0x150, 123); mem.U32(playerUnit + 0x26D8, 0x10); }
            };
            string result = WowBCombatResearch.Capture("mage Fireball\nnot a second line", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("completed: 3 sample(s)"), "capture completed");
            string path = Directory.GetFiles(Path.Combine(folder, "Logs", "CombatResearch"), "*.txt").Single();
            string data = File.ReadAllText(path);
            Check(data.Contains("label=mage Fireball not a second line") && data.Contains("bar slot=1 raw=0x0000000000000193") &&
                data.Contains("player+0x0000 baseline") && data.Contains("target+0x0000 baseline") &&
                data.Contains("player+0x2600 baseline") && data.Contains("+0x26D8:00000000>00000010") &&
                data.Contains("+0x0150:00000000>0000007B") && data.Contains("sample=2 ") &&
                data.Contains("end: completed; samples=3"), "baseline, change, timestamps, action bar and label");
            Check(!reader.HasExperimentalGrindSession && reader.ReadActionBar().Count == 0, "trace does not arm gameplay");
            Check(WowBCombatResearch.Capture("external on 70707", CancellationToken.None, 1, 10, includeExternalArray: true)
                .Contains("limited to 70862"), "external probe is build gated");

            // The following synthetic bytes exercise only the bounded candidate
            // reader; they do not validate the live 70862 header or layout.
            ulong externalBase = 0x50000000;
            int externalSize = 17 * WowBCombatResearch.ExternalCandidateStride;
            mem.Bytes(externalBase, new byte[externalSize]);
            mem.U64(playerUnit + 0x5F0, externalBase);
            mem.U64(playerUnit + 0x5F8, 17);
            mem.U64(playerUnit + 0x600, 24);
            mem.U32(externalBase + 0xE8, 324);
            var ext = WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out string extReason);
            Check(ext != null && ext.Bytes.Length == externalSize && extReason == "", "stable bounded external candidate");
            Check(WowBCombatResearch.FormatExternalCandidate(null, ext!).Contains("external baseline"), "initial baseline");
            mem.U32(externalBase + 0xE8, 1234);
            var extChanged = WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason);
            Check(extChanged != null && WowBCombatResearch.FormatExternalCandidate(ext, extChanged).Contains("+0x00E8:00000144>000004D2"), "candidate changes");
            Check(WowBCombatResearch.FormatExternalCandidate(extChanged, extChanged!).Contains("external stable"), "stable candidate");
            mem.ReadableRangeAllowed = false;
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null && extReason.Contains("committed"), "unreadable region denied");
            mem.ReadableRangeAllowed = true;
            mem.U64(playerUnit + 0x5F8, 25);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null, "excess length denied");
            int grownSize = 28 * WowBCombatResearch.ExternalCandidateStride;
            mem.Bytes(externalBase, new byte[grownSize]);
            mem.U64(playerUnit + 0x5F8, 28); mem.U64(playerUnit + 0x600, 36);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null, "70862 bounds unchanged");
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason, true)?.Bytes.Length == grownSize, "70888 28-slot header accepted only with readable stable data");
            mem.U64(playerUnit + 0x5F8, 29);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason, true) == null, "70888 length above observed bound denied");
            mem.U64(playerUnit + 0x5F8, 28); mem.U64(playerUnit + 0x600, 37);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason, true) == null, "70888 capacity above observed bound denied");
            mem.U64(playerUnit + 0x5F8, 28); mem.U64(playerUnit + 0x600, 36); mem.ReadableRangeAllowed = false;
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason, true) == null, "70888 grown region must be readable");
            mem.ReadableRangeAllowed = true;
            int grownReads = 0;
            mem.OnRead = (address, count) => { if (address == externalBase && count == grownSize && ++grownReads == 2) mem.U32(externalBase + 0xE8, 4321); };
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason, true) == null, "70888 grown data must be stable");
            mem.OnRead = null;
            mem.U64(playerUnit + 0x5F8, 17);
            mem.U64(playerUnit + 0x600, 16);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null, "length beyond capacity denied");
            mem.U64(playerUnit + 0x600, 24);
            mem.U64(playerUnit + 0x5F0, playerUnit + 0x608);
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null, "inline pointer denied");
            mem.U64(playerUnit + 0x5F0, externalBase);
            int externalReads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == externalBase && count == externalSize && ++externalReads == 2)
                    mem.U32(externalBase + 0xE8, 4321);
            };
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null && extReason.Contains("unstable"), "changing data discarded");
            mem.OnRead = null;
            int headerReads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit + 0x5F0 && count == 24 && ++headerReads == 3)
                    mem.U64(playerUnit + 0x5F8, 16);
            };
            Check(WowBCombatResearch.ReadExternalCandidate(mem, playerUnit, out extReason) == null && extReason.Contains("changed"), "changed header discarded");
            mem.OnRead = null;
            mem.U64(playerUnit + 0x5F0, 0);
            mem.U64(playerUnit + 0x5F8, 0);
            mem.U64(playerUnit + 0x600, 0);
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
            Check(result.Contains("changed during read") && result.Contains("selected target GUID unavailable or changed") && result.Contains("1 sample(s)"), "target change discards current sample with reason");
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
            Check(result.Contains("changed during read") && result.Contains("bot or route started or paused") && result.Contains("1 sample(s)"), "bot start stops sampling safely with reason");
            reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2)
                    mem.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 133);
            };
            result = WowBCombatResearch.Capture("bar change", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("action-bar preview unavailable or changed") && result.Contains("1 sample(s)"), "bar change discards sample with reason");
            mem.U64(module + Wow70707Offsets.StoredActionArrayCandidate, 403);
            reads = 0;
            mem.OnRead = (address, count) =>
            {
                if (address == playerUnit && count == WowBCombatResearch.WindowSize && ++reads == 2)
                    mem.U64(playerUnit + WowBOffsets.UnitObjectBackPtrOffset, targetRecord);
            };
            result = WowBCombatResearch.Capture("identity change", CancellationToken.None, 3, 10);
            mem.OnRead = null;
            Check(result.Contains("local player identity or vtable unavailable or changed") && result.Contains("1 sample(s)"), "player identity change gives reason and discards sample");
            mem.U64(playerUnit + WowBOffsets.UnitObjectBackPtrOffset, playerRecord);
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

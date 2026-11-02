#if WOWB_READER_TESTS
using System;
using System.IO;
using System.Linq;
using GreenMagic;
using Styx.Offsets;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

internal static class DeathCheckpointTests
{
    internal static void RunActive70707(Memory memory, ulong unit, WowBGuid player)
    {
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string folder = Path.Combine(Path.GetTempPath(), "bbuddy-death-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder;
            string directory = Path.Combine(folder, "Logs", "CorpseRecovery");
            var deadAtStart = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0);
            if (!deadAtStart.Poll(out _) || Directory.Exists(directory)) throw new Exception("Starting dead must not save a checkpoint");
            var changedMap = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            if (changedMap.Poll(out bool wasObserved) || !wasObserved) throw new Exception("Living observation marked dead or unreadable");
            memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 8);
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0);
            if (!changedMap.Poll(out wasObserved) || !wasObserved || Directory.Exists(directory))
                throw new Exception("Changed map must not save a checkpoint");
            memory.U32(0x30000000 + Wow70707Offsets.MapIdCandidateOffset, 7);
            var tracker = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            if (tracker.Poll(out _)) throw new Exception("Living observation marked dead");
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0);
            if (!tracker.Poll(out _)) throw new Exception("Dead observation missed");
            string path = Directory.GetFiles(directory).Single();
            string data = File.ReadAllText(path);
            if (!data.Contains("player=" + player) || !data.Contains("map=7") || !data.Contains("x=100") || !data.Contains("y=200") || !data.Contains("z=300"))
                throw new Exception("Checkpoint contents incorrect");
            if (!tracker.Poll(out _) || Directory.GetFiles(directory).Length != 1) throw new Exception("Duplicate checkpoint");
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            Console.WriteLine("70707 synthetic checkpoint capture passed (not a live death test).");
        }
        finally
        {
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            Styx.Helpers.Logging.ApplicationPath = oldPath;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    internal static void Run()
    {
        var a = new WowBGuid(1, 2);
        var b = new WowBGuid(1, 3);
        bool Check(bool alive = true, bool saved = false, WowBGuid? id = null, uint map = 7,
                   int age = 100, uint hp = 0, uint max = 100, double x = 1, double y = 2, double z = 3)
            => WowBDeathCheckpoint.MayRecord(alive, saved, a, 7, age, id ?? a, map, hp, max, x, y, z);
        if (!Check() || Check(alive: false) || Check(saved: true) || Check(id: b) || Check(id: default(WowBGuid)) ||
            Check(map: 8) || Check(age: -1) || Check(age: 30001) || !Check(age: 30000) || Check(hp: 1) ||
            Check(max: 0) || Check(x: double.NaN) || Check(y: double.PositiveInfinity) || Check(z: 100000))
            throw new Exception("Death checkpoint policy fixture failed");
        Console.WriteLine("Death checkpoint policy fixtures passed (no live death or recovery test).");
    }
}
#endif

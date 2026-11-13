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
            ulong ghostAddress = unit + Wow70707Offsets.GhostFlagsCandidateOffset;
            var ghostTracker = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 1);
            memory.Bytes(ghostAddress, new byte[] { Wow70707Offsets.GhostFlagCandidate });
            if (!ghostTracker.Poll(out bool ghostObserved) || !ghostObserved || Directory.Exists(directory))
                throw new Exception("Ghost candidate must stop without saving a corpse at the graveyard");
            memory.Remove(ghostAddress);
            if (!ghostTracker.Poll(out ghostObserved) || !ghostObserved || Directory.Exists(directory))
                throw new Exception("Unreadable ghost candidate must stop without saving a checkpoint");
            memory.Bytes(ghostAddress, new byte[] { 0 });
            var transitionTracker = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            if (transitionTracker.Poll(out _)) throw new Exception("Living observation unexpectedly vetoed");
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0);
            int ghostReads = 0;
            memory.OnRead = (address, count) =>
            {
                if (address == ghostAddress && count == 1 && ++ghostReads == 3)
                    memory.Bytes(ghostAddress, new byte[] { Wow70707Offsets.GhostFlagCandidate });
            };
            if (!transitionTracker.Poll(out _) || Directory.Exists(directory))
                throw new Exception("Ghost transition must not save a graveyard position");
            memory.OnRead = null;
            memory.Bytes(ghostAddress, new byte[] { 0 });
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
            // A no-checkpoint zero observation must not poison a later valid
            // alive-to-zero observation while the owner is holding position.
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            if (deadAtStart.Poll(out _) || deadAtStart.LastObservation != "alive candidate")
                throw new Exception("Unconfirmed zero should allow later alive evidence");
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0);
            if (!deadAtStart.Poll(out _) || !deadAtStart.Recorded.HasValue)
                throw new Exception("Later valid alive-to-zero transition must record a checkpoint");
            // Isolate the original file count checks below.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            var tracker = new WowBDeathCheckpoint();
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            if (tracker.Poll(out _)) throw new Exception("Living observation marked dead");
            int samples = 0;
            if (!WowBDeathCheckpoint.ObserveSafetyExit(
                    () => { if (++samples == 3) memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 0); return tracker.Poll(out _); },
                    () => false, ms => false) || samples != 3)
                throw new Exception("A death after a combat safety exit must reach the real checkpoint tracker");
            var recorded = tracker.Recorded;
            if (!recorded.HasValue || recorded.Value.Player != player || recorded.Value.Map != 7 ||
                recorded.Value.Position.X != 100 || recorded.Value.Position.Y != 200 || recorded.Value.Position.Z != 300 ||
                !deadAtStart.Recorded.HasValue || changedMap.Recorded.HasValue || ghostTracker.Recorded.HasValue || transitionTracker.Recorded.HasValue)
                throw new Exception("In-memory recovery candidate was missing or created without a valid death observation");
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
            memory.OnRead = null;
            memory.Bytes(unit + Wow70707Offsets.GhostFlagsCandidateOffset, new byte[] { 0 });
            memory.U64(unit + Wow70707Offsets.HealthCandidateOffset, 148);
            Styx.Helpers.Logging.ApplicationPath = oldPath;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    internal static void RunActive70888(Memory memory, WowBReader reader, ulong unit, WowBGuid player)
    {
        string oldPath = Styx.Helpers.Logging.ApplicationPath;
        string folder = Path.Combine(Path.GetTempPath(), "bbuddy-70888-death-" + Guid.NewGuid().ToString("N"));
        ulong manaMax = unit + Wow70707Offsets.MaxPowerArrayCandidateOffset;
        ulong hp = unit + Wow70707Offsets.HealthCandidateOffset;
        ulong target = memory.ImageBase64 + (reader.IsExperimental70938 ? Wow70938Offsets.PlayerTargetGuid : Wow70888Offsets.PlayerTargetGuid);
        ulong guid = memory.ImageBase64 + (reader.IsExperimental70938 ? Wow70938Offsets.PlayerGuid : Wow70888Offsets.PlayerGuid);
        WowBGuid originalTarget = reader.ReadTargetGuid();
        try
        {
            Styx.Helpers.Logging.ApplicationPath = folder;
            memory.U64(hp, 148);
            var tracker = new WowBDeathCheckpoint();
            if (tracker.Poll(out bool observed) || !observed || tracker.LastObservation != "alive candidate")
                throw new Exception("70888 alive observation missing");
            memory.Remove(hp);
            if (tracker.Poll(out observed) || observed || tracker.LastObservation != "local position or health bytes unreadable")
                throw new Exception("70888 unreadable recovery health must be reported, not treated as death");
            memory.U64(hp, 148);
            memory.U32(manaMax, 0); // plausible source of the original full-vitals failure, not proof of it
            memory.U64(hp, 0);
            if (reader.TryGetUnitFields(unit, out _)) throw new Exception("combat vitals must still reject invalid mana");
            ulong ghostAddress = unit + Wow70707Offsets.GhostFlagsCandidateOffset;
            memory.Bytes(ghostAddress, new byte[] { Wow70707Offsets.GhostFlagCandidate });
            if (!tracker.Poll(out _) || tracker.Recorded.HasValue || tracker.LastObservation != "ghost candidate set")
                throw new Exception("70888 ghost must not be saved as a corpse location");
            memory.Remove(ghostAddress);
            if (!tracker.Poll(out _) || tracker.Recorded.HasValue || tracker.LastObservation != "ghost flag unavailable or changed")
                throw new Exception("unreadable 70888 ghost state must not authorize a checkpoint");
            memory.Bytes(ghostAddress, new byte[] { 0 });
            int targetReads = 0, targetChanges = 0;
            memory.OnRead = (address, count) => {
                if (address == target) targetReads++;
                if (address == hp && count == 16)
                    memory.Guid(target, ++targetChanges % 2 == 0 ? originalTarget : new WowBGuid(777, 888));
            };
            if (!tracker.Poll(out observed) || !observed || !tracker.Recorded.HasValue ||
                tracker.LastObservation != "zero-health checkpoint recorded" ||
                tracker.Recorded.Value.Player != player || tracker.Recorded.Value.Map != 7 || targetReads != 0 || targetChanges < 2)
                throw new Exception("70888 checkpoint must use stable local health, not target or mana");
            memory.OnRead = null;
            int hpReads = 0;
            memory.OnRead = (address, count) => {
                if (address == hp && count == 16 && ++hpReads == 2) memory.U64(hp, 1);
            };
            if (reader.TryRead70888RecoveryPlayer(out _, out _, out _, out string changingReason) || changingReason != "local health changed during read")
                throw new Exception("changing recovery health must be rejected and diagnosed");
            memory.OnRead = null;
            memory.U64(hp, 0);
            memory.U64(unit + Wow70707Offsets.MaxHealthCandidateOffset, 0);
            if (reader.TryRead70888RecoveryPlayer(out _, out _, out _, out string invalidReason) || invalidReason != "local health or position candidate invalid")
                throw new Exception("unknown maximum health must not be treated as death");
            memory.U64(unit + Wow70707Offsets.MaxHealthCandidateOffset, 148);
            memory.Guid(guid, new WowBGuid(999, 888));
            if (reader.TryRead70888RecoveryPlayer(out _, out _, out _))
                throw new Exception("changed local identity must fail closed");
        }
        finally
        {
            memory.OnRead = null;
            memory.Guid(target, originalTarget);
            memory.Guid(guid, player);
            memory.U64(hp, 148);
            memory.U64(unit + Wow70707Offsets.MaxHealthCandidateOffset, 148);
            memory.U32(manaMax, 20);
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
        if (!WowBDeathCheckpoint.MayBeginRecovery(true, true, 12, 12, false, false) ||
            WowBDeathCheckpoint.MayBeginRecovery(false, true, 12, 12, false, false) ||
            WowBDeathCheckpoint.MayBeginRecovery(true, false, 12, 12, false, false) ||
            WowBDeathCheckpoint.MayBeginRecovery(true, true, 12, 13, false, false) ||
            WowBDeathCheckpoint.MayBeginRecovery(true, true, 12, 12, true, false) ||
            WowBDeathCheckpoint.MayBeginRecovery(true, true, 12, 12, false, true))
            throw new Exception("Recovery must not clear a newer STOP or proceed without its checkpoint");
        int polls = 0, waits = 0;
        bool stopped = false;
        bool Wait(int milliseconds) { if (milliseconds != 100) throw new Exception("Unexpected sampling interval"); waits++; return stopped; }
        bool PollAfterDelay() { polls++; return polls == 4; }
        if (!WowBDeathCheckpoint.ObserveSafetyExit(PollAfterDelay, () => stopped, Wait) || polls != 4 || waits != 3)
            throw new Exception("A delayed death veto should be observed before the safety abort");
        polls = waits = 0;
        if (WowBDeathCheckpoint.ObserveSafetyExit(() => { polls++; return false; }, () => stopped, Wait) || polls != 21 || waits != 20)
            throw new Exception("Alive or unreadable state must time out without authorizing recovery");
        polls = waits = 0;
        if (!WowBDeathCheckpoint.ObserveSafetyExit(() => ++polls == 41, () => stopped, Wait, 200) || polls != 41 || waits != 40)
            throw new Exception("70888 safety pause must catch a death later than the old two-second window");
        polls = waits = 0;
        if (WowBDeathCheckpoint.ObserveSafetyExit(() => { polls++; return false; }, () => stopped, Wait, 200) || polls != 201 || waits != 200)
            throw new Exception("extended unreadable/alive safety pause must remain bounded");
        polls = waits = 0; stopped = true;
        if (WowBDeathCheckpoint.ObserveSafetyExit(() => { polls++; return true; }, () => stopped, Wait) || polls != 0)
            throw new Exception("STOP before the safety pause must suppress polling and recovery");
        stopped = false;
        if (WowBDeathCheckpoint.ObserveSafetyExit(() => { stopped = true; return true; }, () => stopped, Wait))
            throw new Exception("STOP during a death poll must win over its result");
        stopped = false; polls = 0;
        if (WowBDeathCheckpoint.ObserveSafetyExit(() => { polls++; return false; }, () => stopped,
                ms => { stopped = true; return true; }) || polls != 1)
            throw new Exception("STOP during a safety wait must end it immediately");
        Console.WriteLine("Death checkpoint, safety-exit and recovery STOP policy fixtures passed (no live death or recovery test).");
    }
}
#endif

using System;
using System.Globalization;
using System.IO;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Best-effort recording and 70707 ghost-candidate veto. This class itself
// never sends input. Both game fields and positions are non-atomic.
internal sealed class WowBDeathCheckpoint
{
    internal readonly struct Candidate
    {
        internal readonly WowBGuid Player;
        internal readonly uint Map;
        internal readonly WoWPoint Position;
        internal Candidate(WowBGuid player, uint map, WoWPoint position) { Player = player; Map = map; Position = position; }
    }
    internal Candidate? Recorded { get; private set; }
    // Diagnostic only; never authorizes recovery or input.
    internal string LastObservation { get; private set; } = "not sampled";

    private WowBGuid _aliveGuid;
    private uint _aliveMap;
    private int _aliveAt;
    private bool _hasAlive, _saved, _hasGhostWarning;
    private int _lastGhostWarning;
    private const int AliveEvidenceMaxMs = 30000;

    private void WarnGhost(string message)
    {
        int now = Environment.TickCount;
        if (_hasGhostWarning && unchecked(now - _lastGhostWarning) < 5000) return;
        _hasGhostWarning = true; _lastGhostWarning = now;
        Logging.Write(message);
    }

    internal static bool MayBeginRecovery(bool pending, bool hasCheckpoint, int deathGeneration, int currentGeneration,
                                          bool abortRequested, bool routeStopped)
        => pending && hasCheckpoint && deathGeneration == currentGeneration && !abortRequested && !routeStopped;

    // A combat safety exit can race the next scheduled death sample. While no
    // gameplay input is being sent, allow bounded short waits for a checkpoint
    // or ghost veto. A STOP wins, including one that arrives during a poll.
    // An unreadable/alive result never authorizes recovery.
    internal static bool ObserveSafetyExit(Func<bool> poll, Func<bool> cancelled, Func<int, bool> wait, int intervals = 20)
    {
        const int intervalMs = 100;
        intervals = Math.Clamp(intervals, 0, 200);
        for (int i = 0; i <= intervals; i++)
        {
            if (cancelled()) return false;
            bool veto = poll();
            if (cancelled()) return false;
            if (veto) return true;
            if (i == intervals || wait(intervalMs)) return false;
        }
        return false;
    }

    internal static bool MayRecord(bool hasAlive, bool alreadySaved, WowBGuid aliveGuid, uint aliveMap,
                                   int ageMs, WowBGuid currentGuid, uint currentMap,
                                   uint health, uint maxHealth, double x, double y, double z)
        => hasAlive && !alreadySaved && !currentGuid.IsEmpty && currentGuid == aliveGuid && currentMap == aliveMap
           && ageMs >= 0 && ageMs <= AliveEvidenceMaxMs && health == 0 && maxHealth > 0
           && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
           && Math.Abs(x) < 100000 && Math.Abs(y) < 100000 && Math.Abs(z) < 100000;

    // 70888 recovery must not depend on target selection or mana remaining
    // readable during death. Older builds retain their established path.
    internal static bool ReadRecoveryPlayer(WowBReader reader, out WowBObservedUnit player, out uint health, out uint maxHealth)
        => ReadRecoveryPlayer(reader, out player, out health, out maxHealth, out _);

    private static bool ReadRecoveryPlayer(WowBReader reader, out WowBObservedUnit player, out uint health, out uint maxHealth, out string reason)
    {
        player = default; health = maxHealth = 0;
        if (reader.IsExperimental70888)
            return reader.TryRead70888RecoveryPlayer(out player, out health, out maxHealth, out reason);
        reason = "local snapshot or vitals unavailable";
        if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
            !snapshot.TryGetLocalPlayer(out player) ||
            !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields)) return false;
        health = fields.Health; maxHealth = fields.MaxHealth;
        if (maxHealth == 0 || health > maxHealth) return false;
        reason = "";
        return true;
    }

    // False means either alive or unreadable, never proof of life. True vetoes
    // gameplay for zero health, a set ghost candidate or an unreadable ghost byte.
    // Recorded coordinates are not proven corpse coordinates.
    internal bool Poll(out bool observed)
    {
        observed = false;
        LastObservation = "attachment unavailable";
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            LastObservation = "map unavailable or unstable";
            if (!reader.TryReadMapId(out uint map)) return false;
            if (!ReadRecoveryPlayer(reader, out WowBObservedUnit player, out uint health, out uint maxHealth, out string readReason))
            { LastObservation = readReason; return false; }
            LastObservation = "invalid player or attachment changed";
            if (player.Guid.IsEmpty || maxHealth == 0 || health > maxHealth ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            if (reader.IsExperimental70707)
            {
                if (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghostFlag))
                {
                    WarnGhost("[Corpse] " + reader.ExperimentalBuildNumber + " ghost flag candidate unavailable or changed; gameplay remains stopped rather than guessing");
                    LastObservation = "ghost flag unavailable or changed";
                    observed = true;
                    return true;
                }
                if (ghostFlag)
                {
                    WarnGhost("[Corpse] " + reader.ExperimentalBuildNumber + " ghost flag candidate is set; no new corpse position will be inferred");
                    LastObservation = "ghost candidate set";
                    observed = true;
                    return true;
                }
            }
            if (health > 0)
            {
                _aliveGuid = player.Guid; _aliveMap = map; _aliveAt = Environment.TickCount; _hasAlive = true;
                LastObservation = "alive candidate";
                observed = true;
                return false;
            }
            LastObservation = "zero-health revalidation unavailable or changed";
            if (!ReadRecoveryPlayer(reader, out WowBObservedUnit current, out uint healthAfter, out uint maxAfter) ||
                maxAfter != maxHealth || healthAfter != health || current.Guid != player.Guid ||
                current.RecordAddress != player.RecordAddress || current.UnitAddress != player.UnitAddress ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            if (reader.IsExperimental70707 &&
                (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, current, out bool ghostAfter) || ghostAfter))
            {
                WarnGhost("[Corpse] ghost candidate changed or became unavailable during 0-health observation; not saving a location");
                LastObservation = "ghost changed during zero-health revalidation";
                observed = true;
                return true;
            }

            int now = Environment.TickCount;
            WoWPoint position = current.Location;
            if (MayRecord(_hasAlive, _saved, _aliveGuid, _aliveMap, unchecked(now - _aliveAt), current.Guid, map,
                          healthAfter, maxAfter, position.X, position.Y, position.Z))
            {
                // One write per run; never overwrite an earlier candidate at a graveyard.
                _saved = true;
                Recorded = new Candidate(current.Guid, map, position);
                LastObservation = "zero-health checkpoint recorded";
                Save(current.Guid, map, reader.ExperimentalBuildNumber, position);
            }
            else if (!_saved)
            {
                _saved = true;
                LastObservation = "zero-health without eligible alive history or position";
                Logging.Write("[Corpse] 0-health candidate but no recent matching alive observation; no checkpoint saved");
            }
            else LastObservation = "zero-health checkpoint already handled";
            observed = true;
            return true;
        }
        catch (Exception ex) { LastObservation = "checkpoint observation exception"; Logging.Write("[Corpse] checkpoint observation failed: " + ex.Message); return false; }
    }

    private static void Save(WowBGuid player, uint map, int experimentalBuild, WoWPoint position)
    {
        try
        {
            string folder = Path.Combine(Logging.ApplicationPath, "Logs", "CorpseRecovery");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] {
                "WOWB DEATH CHECKPOINT v1; candidate only, not verified corpse or ghost state",
                "UTC=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                "player=" + player, "client=" + (experimentalBuild != 0 ? experimentalBuild.ToString(CultureInfo.InvariantCulture) : "wowb-legacy-offsets"),
                "map=" + map.ToString(CultureInfo.InvariantCulture),
                "x=" + position.X.ToString("R", CultureInfo.InvariantCulture),
                "y=" + position.Y.ToString("R", CultureInfo.InvariantCulture),
                "z=" + position.Z.ToString("R", CultureInfo.InvariantCulture)
            });
            Logging.Write("[Corpse] saved a 0-health location candidate to " + path + "; location and state remain unverified candidates");
        }
        catch (Exception ex) { Logging.Write("[Corpse] could not save death location: " + ex.Message); }
    }
}

using System;
using System.Globalization;
using System.IO;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Best-effort recording and 70707 ghost-candidate veto only. Never release,
// move or resurrect the character. Both game fields and positions are non-atomic.
internal sealed class WowBDeathCheckpoint
{
    private WowBGuid _aliveGuid;
    private uint _aliveMap;
    private int _aliveAt;
    private bool _hasAlive, _saved;
    private const int AliveEvidenceMaxMs = 30000;

    internal static bool MayRecord(bool hasAlive, bool alreadySaved, WowBGuid aliveGuid, uint aliveMap,
                                   int ageMs, WowBGuid currentGuid, uint currentMap,
                                   uint health, uint maxHealth, double x, double y, double z)
        => hasAlive && !alreadySaved && !currentGuid.IsEmpty && currentGuid == aliveGuid && currentMap == aliveMap
           && ageMs >= 0 && ageMs <= AliveEvidenceMaxMs && health == 0 && maxHealth > 0
           && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
           && Math.Abs(x) < 100000 && Math.Abs(y) < 100000 && Math.Abs(z) < 100000;

    // False means either alive or unreadable, never proof of life. True vetoes
    // gameplay for zero health, a set ghost candidate or an unreadable ghost byte.
    // Recorded coordinates are not proven corpse coordinates.
    internal bool Poll(out bool observed)
    {
        observed = false;
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out uint map) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? first) || first == null ||
                !first.TryGetLocalPlayer(out WowBObservedUnit player) || player.Guid.IsEmpty ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields before) ||
                before.MaxHealth == 0 || before.Health > before.MaxHealth ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            if (reader.IsExperimental70707)
            {
                if (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghostFlag))
                {
                    Logging.Write("[Corpse] 70707 ghost flag candidate unavailable or changed; stopping rather than guessing");
                    observed = true;
                    return true;
                }
                if (ghostFlag)
                {
                    Logging.Write("[Corpse] 70707 ghost flag candidate is set; stopping for manual recovery");
                    observed = true;
                    return true;
                }
            }
            if (before.Health > 0)
            {
                _aliveGuid = player.Guid; _aliveMap = map; _aliveAt = Environment.TickCount; _hasAlive = true;
                observed = true;
                return false;
            }
            if (!reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields after) ||
                after.MaxHealth != before.MaxHealth || after.Health != before.Health ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? second) || second == null ||
                !second.TryGetLocalPlayer(out WowBObservedUnit current) || current.Guid != player.Guid ||
                current.RecordAddress != player.RecordAddress || current.UnitAddress != player.UnitAddress ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            if (reader.IsExperimental70707 &&
                (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, current, out bool ghostAfter) || ghostAfter))
            {
                Logging.Write("[Corpse] ghost candidate changed or became unavailable during 0-health observation; not saving a location");
                observed = true;
                return true;
            }

            int now = Environment.TickCount;
            WoWPoint position = current.Location;
            if (MayRecord(_hasAlive, _saved, _aliveGuid, _aliveMap, unchecked(now - _aliveAt), current.Guid, map,
                          after.Health, after.MaxHealth, position.X, position.Y, position.Z))
            {
                // One write per run; never overwrite an earlier candidate at a graveyard.
                _saved = true;
                Save(current.Guid, map, reader.IsExperimental70707, position);
            }
            else if (!_saved)
            {
                _saved = true;
                Logging.Write("[Corpse] 0-health candidate but no recent matching alive observation; no checkpoint saved");
            }
            observed = true;
            return true;
        }
        catch (Exception ex) { Logging.Write("[Corpse] checkpoint observation failed: " + ex.Message); return false; }
    }

    private static void Save(WowBGuid player, uint map, bool build70707, WoWPoint position)
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
                "player=" + player, "client=" + (build70707 ? "70707" : "wowb-legacy-offsets"),
                "map=" + map.ToString(CultureInfo.InvariantCulture),
                "x=" + position.X.ToString("R", CultureInfo.InvariantCulture),
                "y=" + position.Y.ToString("R", CultureInfo.InvariantCulture),
                "z=" + position.Z.ToString("R", CultureInfo.InvariantCulture)
            });
            Logging.Write("[Corpse] saved a 0-health location candidate to " + path + "; automatic release and recovery are not yet enabled");
        }
        catch (Exception ex) { Logging.Write("[Corpse] could not save death location: " + ex.Message); }
    }
}

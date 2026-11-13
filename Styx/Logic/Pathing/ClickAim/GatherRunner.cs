using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Xml.Linq;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>Experimental ground gatherer for 70291. No Lua or process writes.</summary>
internal static class GatherRunner
{
    // This is a filter on the client's loaded object pool, not a guarantee
    // that nodes are streamed or mesh-reachable at this distance.
    private const double SearchYd = 1000.0;
    // Optional Grind gathering has a separate, shorter discovery radius.
    private const double GrindSearchYd = 150.0;
    private const double InteractYd = 5.0;
    private const int RetryMs = 90000;
    private const int InteractionRetryMs = 20000;
    // A missed hover is not evidence of depletion. Back off for the exact GUID
    // without suppressing new spawns at the same position.
    private const int UnavailableRetryMs = 120000;
    private const int MaxGatherAttempts = 4;
    // 70938 Classic veins may require several successful mining casts.
    private const int ClassicGatherAttempts = 5;
    // Covers a 2.5 s mining cast plus time for the loot window / auto-loot.
    private const int GatherHoldMs = 6000;
    private const int MiningStartWaitMs = 2500, MiningLootWaitMs = 1000, MiningCastLimitMs = 12000;
    // Only throttle degenerate routes whose points are repeatedly already reached.
    // Normal hotspot transitions have no fixed delay.
    private const int FastHotspotMs = 100;
    private const int HotspotLoopPauseMs = 250;
    private const int MaxFastHotspots = 8;

    internal static bool ShouldPauseHotspotLoop(int fastCount, int hotspotCount)
        => fastCount >= Math.Min(MaxFastHotspots, Math.Max(1, hotspotCount));

    internal static string HotspotSelfTest()
    {
        bool ok = !ShouldPauseHotspotLoop(1, 668) && !ShouldPauseHotspotLoop(7, 668)
               && ShouldPauseHotspotLoop(8, 668) && ShouldPauseHotspotLoop(1, 1)
               && !ShouldPauseHotspotLoop(2, 3) && ShouldPauseHotspotLoop(3, 3);
        return "[Gather] hotspot loop fixtures: " + (ok ? "pass" : "FAIL") + " (not a live movement test)";
    }
    private static volatile bool _running;
    private static volatile bool _stop;
    private static readonly Dictionary<WowBGuid, int> Retry = new Dictionary<WowBGuid, int>();
    // Only the exact observed GUID is excluded, and only for this gathering
    // session. A new spawn at the same position remains eligible.
    private static readonly HashSet<WowBGuid> FailedHarvestNodes = new HashSet<WowBGuid>();
    // Counts only posted interactions followed by an uninterrupted wait and
    // a successful scan still observing this exact GUID, across short retries.
    private static readonly Dictionary<WowBGuid, int> ObservedHarvestAttempts = new Dictionary<WowBGuid, int>();
    private static readonly Dictionary<WowBGuid, int> MissingMiningStarts = new Dictionary<WowBGuid, int>();
    private static readonly List<(double X, double Y, double Z, double Radius)> Blackspots = new List<(double, double, double, double)>();
    private static int _lastScan;
    private static int _lastCombatCheck;
    private static int _lastSummary;
    private static int _lastSafetySummary;
    private static GatherNodeScanner.Node? _pending;
    // Grind-only 70938 commitment. Standalone GatherBuddy retains its own flow.
    private static GatherNodeScanner.Node? _grindCommitted;
    private static int _grindMissingScans, _grindApproachFailures, _grindReportAt;
    internal static bool HasGrindCommitment => _grindCommitted.HasValue;
    internal static void ClearGrindCommitment()
    { _grindCommitted = null; _grindMissingScans = _grindApproachFailures = 0; _grindReportAt = 0; }
    private static uint _sessionMap;
    // Frozen when standalone GatherBuddy starts; Grind's gathering never reads these.
    private static bool _ignoreMonsters, _movementEnabled, _movementAttempted, _nodeDefence;
    private static int _movementSlot;
    private static WowBGhostWolfTravel? _ghostWolfTravel;
    private static WowBDeathCheckpoint? _deathCheckpoint;
    private static bool _recoveryReady;
    private static int _deathStopGeneration;

    internal static bool StopIfDeadOrGhost()
    {
        if (_deathCheckpoint == null || !_deathCheckpoint.Poll(out _)) return false;
        lock (StartStopGate)
        {
            if (_stop || ClickAimAbort.Requested) return true;
            _deathStopGeneration = _stopGeneration;
            _recoveryReady = ObjectManager.WowB?.IsExperimental70707 == true && _deathCheckpoint.Recorded.HasValue;
            _stop = true;
        }
        ClickAimKeySteer.ReleaseHeld();
        Logging.Write(_recoveryReady ? "[Gather] death checkpoint recorded; preparing experimental recovery" :
            "[Gather] death/ghost safety check stopped gathering; no usable recovery checkpoint");
        return true;
    }

    public static bool Running => _running;
    private static readonly object StartStopGate = new object();
    private static int _stopGeneration, _starting;

    private static bool RecoverIfPending(ClickAimMover mover)
    {
        WowBDeathCheckpoint.Candidate candidate;
        lock (StartStopGate)
        {
            if (!WowBDeathCheckpoint.MayBeginRecovery(_recoveryReady, _deathCheckpoint?.Recorded != null,
                    _deathStopGeneration, _stopGeneration, ClickAimAbort.Requested, RouteRunner.StopRequested)) return false;
            candidate = _deathCheckpoint!.Recorded!.Value;
            _recoveryReady = false;
            _stop = false; // only our own death stop, never a user STOP
        }
        bool recovered = WowBCorpseRecovery.TryRecover(candidate, mover, () => !_stop && !ClickAimAbort.Requested);
        if (!recovered) { _stop = true; return false; }
        _deathCheckpoint = new WowBDeathCheckpoint();
        _pending = null;
        _movementAttempted = false;
        _nodeDefence = false;
        GrindRunner.ClearGatherFightAfterRecovery();
        return !_stop && !ClickAimAbort.Requested;
    }

    public static void Stop()
    {
        lock (StartStopGate)
        {
            _stopGeneration++;
            _stop = true;
            ClickAimAbort.Request();
            RouteRunner.StopRequested = true;
        }
        GrindRunner.Stop();
    }

    public static bool Start(string profile)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return false;
        bool started = false;
        WowBReader? experimentalReader = null;
        try
        {
            int generation;
            lock (StartStopGate) generation = _stopGeneration;
            if (_running) return false;
            if (ObjectManager.WowB?.IsExperimental70707 == true)
            {
                if (!WowBGrindFactions.PrepareForGrind(out string reason))
                { Logging.Write("[Gather] experimental preparation failed: " + reason); return false; }
                experimentalReader = WowBGrindFactions.ExperimentalBoundReader;
                if (experimentalReader == null) return false;
                Logging.Write("[Gather] EXPERIMENTAL " + experimentalReader.ExperimentalBuildNumber + ": static node vtable candidate; " +
                    (experimentalReader.Uses70862Features ? "defence bypasses faction and may attack the wrong NPC, including a friendly" : "faction-filtered defence") +
                    "; monitor closely and use STOP.");
            }
            started = StartPrepared(profile, generation, experimentalReader);
            return started;
        }
        finally
        {
            if (!started && experimentalReader != null) WowBGrindFactions.EndExperimental70707();
            Volatile.Write(ref _starting, 0);
        }
    }

    private static bool StartPrepared(string profile, int generation, WowBReader? experimentalReader)
    {
        GatherSettings.Load();
        List<RoutePoint> points = new List<RoutePoint>();
        MeshRoam? roam = null;
        if (string.IsNullOrWhiteSpace(profile))
        {
            if (!GatherSettings.RoamWithoutProfile || !MeshRoam.TryCreate(out roam) || roam == null) return false;
            Blackspots.Clear();
        }
        else
        {
            try
            {
                points = ProfileRoute.Load(profile, 0, false, false);
                LoadBlackspots(profile);
            }
            catch (Exception ex) { Logging.Write("[Gather] profile could not be read: " + ex.Message); return false; }
            if (points.Count == 0) { Logging.Write("[Gather] profile has no hotspots"); return false; }
        }
        if ((!GatherSettings.Mining || GatherSettings.MiningSkill == 0)
            && (!GatherSettings.Herbalism || GatherSettings.HerbalismSkill == 0))
        {
            Logging.Write("[Gather] select at least one profession and its skill in Gather settings");
            return false;
        }
        if (!RouteRunner.MeshReady())
        {
            Logging.Write("[Gather] mesh navigation is unavailable; refusing to gather without meshes");
            return false;
        }
        lock (StartStopGate)
        {
            if (generation != _stopGeneration || _running ||
                (experimentalReader != null && (ObjectManager.WowB != experimentalReader || !experimentalReader.ExperimentalGrindActive))) return false;
            _stop = false;
            RouteRunner.StopRequested = false;
            ClickAimAbort.Clear();
            _ignoreMonsters = GatherSettings.IgnoreMonsters;
            _movementEnabled = GatherSettings.UseMovementSkill;
            _movementSlot = GatherSettings.MovementSkillSlot;
            _ghostWolfTravel = new WowBGhostWolfTravel(_movementEnabled, _movementSlot);
            _movementAttempted = false;
            _nodeDefence = false;
            Retry.Clear();
            FailedHarvestNodes.Clear();
            ObservedHarvestAttempts.Clear();
            MissingMiningStarts.Clear();
            GatherInteraction.ResetHoverHint();
            _pending = null;
            _lastScan = unchecked(Environment.TickCount - 1500);
            _lastCombatCheck = unchecked(Environment.TickCount - 500);
            _lastSummary = unchecked(Environment.TickCount - 15000);
            GrindRunner.PrepareGatherDefence();
            _running = true;
            try
            {
                var thread = new Thread(() => RunGuarded(points, roam, experimentalReader)) { IsBackground = true, Name = "GatherRunner" };
                thread.Start();
                return true;
            }
            catch { _running = false; throw; }
        }
    }

    private static void LoadBlackspots(string path)
    {
        Blackspots.Clear();
        XDocument doc = XDocument.Load(path);
        foreach (XElement element in doc.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "Blackspot", StringComparison.OrdinalIgnoreCase)) continue;
            if (!double.TryParse((string?)element.Attribute("X"), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse((string?)element.Attribute("Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.TryParse((string?)element.Attribute("Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
                || !double.TryParse((string?)element.Attribute("Radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || !double.IsFinite(r) || r <= 0 || r > 1000)
                throw new FormatException("A profile Blackspot has invalid X/Y/Z/Radius; refusing to ignore it.");
            Blackspots.Add((x, y, z, r));
        }
    }

    private static bool IsBlackspot(double x, double y)
    {
        foreach (var spot in Blackspots)
            if (ClickAimMath.Len(x - spot.X, y - spot.Y) <= spot.Radius) return true;
        return false;
    }

    private static bool Ready(GatherNodeScanner.Node node)
    {
        if (FailedHarvestNodes.Contains(node.Guid) || IsBlackspot(node.Position.X, node.Position.Y)) return false;
        if (!GatherNodeScanner.IsEligible(node, GatherSettings.Mining, GatherSettings.MiningSkill,
                                          GatherSettings.Herbalism, GatherSettings.HerbalismSkill)) return false;
        return !Retry.TryGetValue(node.Guid, out int until) || unchecked(Environment.TickCount - until) >= 0;
    }

    private static bool FindNode(out GatherNodeScanner.Node node, double radius = SearchYd)
    {
        node = default;
        bool scanOk = GatherNodeScanner.TryCapture(radius, out List<GatherNodeScanner.Node> nodes, out GatherNodeScanner.ScanStats stats);
        int eligible = 0, blackspotted = 0, coolingDown = 0, skillExcluded = 0, failedHarvest = 0;
        foreach (GatherNodeScanner.Node candidate in nodes)
        {
            if (!GatherNodeScanner.IsEligible(candidate, GatherSettings.Mining, GatherSettings.MiningSkill,
                                               GatherSettings.Herbalism, GatherSettings.HerbalismSkill)) { skillExcluded++; continue; }
            if (IsBlackspot(candidate.Position.X, candidate.Position.Y)) { blackspotted++; continue; }
            if (FailedHarvestNodes.Contains(candidate.Guid)) { failedHarvest++; continue; }
            if (!Ready(candidate)) { coolingDown++; continue; }
            if (eligible++ == 0) node = candidate;
        }
        if (unchecked(Environment.TickCount - _lastSummary) >= 15000)
        {
            _lastSummary = Environment.TickCount;
            if (!scanOk) Logging.Write("[Gather] node scan failed; no conclusion about nearby resources");
            else Logging.Write("[Gather] scan within " + radius.ToString("F0", CultureInfo.InvariantCulture) + " yd: " + nodes.Count + " recognized, " + eligible
                + " eligible, " + skillExcluded + " profession/skill/blacklist excluded, "
                + blackspotted + " profile-blackspot excluded, " + failedHarvest + " session-blacklisted, " + coolingDown + " on retry cooldown; "
                + "known GUID-entry candidates in pool=" + stats.KnownGuidEntries + ", header rejected=" + stats.HeaderRejected
                + ", outside radius=" + stats.OutsideRadius + ", changed=" + stats.Changed + "; "
                + "an empty scan is not proof no nodes exist");
        }
        return scanOk && eligible > 0;
    }

    // Called by RouteRunner between mesh points. Never clears a stop.
    private static bool Interrupt(bool checkNodes)
    {
        if (_stop || ClickAimAbort.Requested) return true;
        if (unchecked(Environment.TickCount - _lastCombatCheck) >= 500)
        {
            _lastCombatCheck = Environment.TickCount;
            if (StopIfDeadOrGhost() || !MapStillCurrent()) return true;
            WowBReader? barReader = WowBGrindFactions.ExperimentalBoundReader;
            if (barReader?.Uses70862Features == true && !WowBGrindFactions.Experimental70707BarSafe(barReader)) return true;
            if (!GrindRunner.TryReadGatherCombat(out bool fighting) || (fighting && !_ignoreMonsters)) return true;
        }
        if (!checkNodes || unchecked(Environment.TickCount - _lastScan) < 1500) return false;
        _lastScan = Environment.TickCount;
        if (!FindNode(out GatherNodeScanner.Node node)) return false;
        _pending = node;
        return true;
    }

    private static bool MapStillCurrent()
    {
        WowBReader? reader = ObjectManager.WowB;
        if (reader != null && reader.TryReadMapId(out uint map) && ObjectManager.WowB == reader && map == _sessionMap)
            return true;
        Logging.Write("[Gather] client map changed or became unreadable; stopping rather than walking on a guessed map");
        _stop = true;
        return false;
    }

    private static bool MovementPlayerSafe(GreenMagic.Memory memory, WowBReader reader, IntPtr hwnd,
                                           out WowBObservedUnit player, bool requireMana = true)
    {
        player = default;
        try
        {
            if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || ObjectManager.Wow != memory ||
                ObjectManager.WowB != reader || memory.WindowHandle != hwnd || !reader.Uses70862Features ||
                !WowBGrindFactions.Experimental70707BarSafe(reader) || StopIfDeadOrGhost()) return false;
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out uint map) || map != _sessionMap ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null ||
                !snap.TryGetLocalPlayer(out player) || player.Guid.IsEmpty ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.MaxHealth == 0 || fields.Health == 0 || fields.Health > fields.MaxHealth ||
                (requireMana && (fields.PlayerPowerTypeCandidate != 0 || fields.Power == 0)) ||
                !WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost) || ghost ||
                !GrindRunner.TryReadGatherCombat(out bool fighting) || (fighting && !_ignoreMonsters) ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map) return false;
            return !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && ObjectManager.Wow == memory &&
                   ObjectManager.WowB == reader && memory.WindowHandle == hwnd && WowBGrindFactions.Experimental70707BarSafe(reader);
        }
        catch { return false; }
    }

    private static ClickAimAction? FindMovementAction(WowBReader reader, int slot)
    {
        foreach (ClickAimAction candidate in ClickAimCombat.ReadBar(reader))
            if (GatherMovementSkill.IsSupported(candidate, slot, reader.IsExperimental70938)) return candidate;
        return null;
    }

    // Returns true after a key attempt, so the caller rechecks the whole loop
    // before travelling. We cannot prove whether the form is active or cast.
    private static bool TryMovementSkill(IntPtr hwnd, ClickAimMover mover, double distance)
    {
        if (!_movementEnabled || !double.IsFinite(distance) || distance <= InteractYd) return false;
        GreenMagic.Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || hwnd == IntPtr.Zero) return false;
        if (reader.IsExperimental70938)
        {
            ClickAimAction? slotAction;
            try { slotAction = FindMovementAction(reader, _movementSlot); }
            catch { return false; }
            if (slotAction == null)
            {
                if (!_movementAttempted) { _movementAttempted = true; Logging.Write("[Gather] configured movement slot is unavailable or unsupported; no form key sent"); }
                return false;
            }
            if (WowBGhostWolfTravel.IsGhostWolfSpell(slotAction.Id))
                return _ghostWolfTravel?.TryEnsure(hwnd, mover, TravelFormSafe) == true;
        }
        if (_movementAttempted || distance < 15) return false;
        if (!reader.Uses70862Features || _movementSlot < 1 || _movementSlot > 12)
        {
            _movementAttempted = true;
            Logging.Write("[Gather] movement skill requires 70862/70888/70938 and a configured first-bar slot 1–12; no key sent");
            return false;
        }
        try
        {
            ClickAimAction? action = FindMovementAction(reader, _movementSlot);
            if (action == null)
            {
                _movementAttempted = true;
                Logging.Write("[Gather] selected slot does not contain a supported movement spell; no key sent");
                return false;
            }
            if (!MovementPlayerSafe(memory, reader, hwnd, out WowBObservedUnit before)) return false;
            ClickAimKeySteer.ReleaseHeld();
            ClickAimKeySteer.ReleaseArrows(hwnd);
            try { mover.MoveStop(); } catch { }
            if (ClickAimAbort.Wait(200)) return true;
            if (!MovementPlayerSafe(memory, reader, hwnd, out WowBObservedUnit now) || now.Guid != before.Guid ||
                now.UnitAddress != before.UnitAddress || now.RecordAddress != before.RecordAddress) return true;
            ClickAimAction? rechecked = FindMovementAction(reader, _movementSlot);
            if (rechecked == null || rechecked.Raw != action.Raw || _stop || ClickAimAbort.Requested ||
                RouteRunner.StopRequested || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                memory.WindowHandle != hwnd || !WowBGrindFactions.Experimental70707BarSafe(reader)) return true;
            _movementAttempted = true; // do not blindly toggle on every route tick
            ClickAimInput.TapKey(hwnd, action.KeyVk, 40);
            Logging.Write("[Gather] attempted movement spell " + action.Id + " in slot " + action.Slot +
                "; success and active form are unknown. A later node interaction can permit one more attempt.");
            // Ghost Wolf may have a cast time; remain stationary and interrupt
            // for STOP, death, attachment/map/bar changes or normal-mode combat.
            for (int elapsed = 0; elapsed < 3000; elapsed += 250)
            {
                if (ClickAimAbort.Wait(250) || !MovementPlayerSafe(memory, reader, hwnd, out now, requireMana: false) ||
                    now.Guid != before.Guid || now.UnitAddress != before.UnitAddress || now.RecordAddress != before.RecordAddress) break;
            }
            return true;
        }
        catch (Exception ex) { Logging.Write("[Gather] movement skill withheld: " + ex.Message); return false; }
    }

    private static bool TravelFormSafe()
        => !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && !StopIfDeadOrGhost() && MapStillCurrent();

    private static bool Walk(ClickAimMover mover, RoutePoint point, bool checkNodes)
    {
        if (_stop || ClickAimAbort.Requested) return false;
        if (!RouteRunner.MeshReady()) { Logging.Write("[Gather] mesh navigation lost; refusing the leg"); return false; }
        RouteRunner.ShouldInterrupt = () => Interrupt(checkNodes) ||
            (_ghostWolfTravel?.ShouldPauseTravel(ObjectManager.Wow?.WindowHandle ?? IntPtr.Zero, TravelFormSafe) == true);
        RouteRunner.PollInterruptDuringWalk = true;
        RouteRunner.RequireMeshDuringSession = true;
        try { return RouteRunner.RunWithinSession(mover, new List<RoutePoint> { point }, 1); }
        finally { RouteRunner.RequireMeshDuringSession = false; RouteRunner.PollInterruptDuringWalk = false; RouteRunner.ShouldInterrupt = null; }
    }

    /// <summary>
    /// A gather leg failed. Try up to <see cref="MeshRelocate.Attempts"/> other mesh-valid
    /// locations within <see cref="MeshRelocate.RadiusYd"/> of the initial stuck position:
    /// walk to each over the mesh and, once there, ask for the destination again.
    /// Returns true when reached. On interruption, the caller returns to the main loop
    /// for combat, death, map, node or user-stop handling.
    /// </summary>
    private static bool RelocateAndRetry(ClickAimMover mover, RoutePoint original,
                                         bool checkNodes = true, Func<bool>? targetStillValid = null)
    {
        if (!ClickAimPositions.TryRead(out double originX, out double originY))
        {
            Logging.Write("[Gather] relocation withheld: player position is unreadable");
            return false;
        }
        var tried = new List<RoutePoint>();
        // Sampling and path queries can take time; yield to combat/death/STOP
        // even before any alternate movement starts.
        bool Interrupted()
        {
            if (!Interrupt(false)) return false;
            if (!_stop && !ClickAimAbort.Requested) RouteRunner.LastRunInterrupted = true;
            return true;
        }
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Gather] could not reach ({0:F1}, {1:F1}); trying up to {2} other mesh locations within {3:F0} yd "
            + "of the character before giving up", original.X, original.Y, MeshRelocate.Attempts, MeshRelocate.RadiusYd));

        for (int attempt = 1; attempt <= MeshRelocate.Attempts; attempt++)
        {
            if (_stop || ClickAimAbort.Requested || !MapStillCurrent() || Interrupted()) return false;
            if (!MeshRelocate.TryPick(_sessionMap, originX, originY, tried, IsBlackspot, Interrupted, out RoutePoint alternate))
            {
                if (!_stop && !ClickAimAbort.Requested && !RouteRunner.LastRunInterrupted)
                    Logging.Write("[Gather] relocation " + attempt + "/" + MeshRelocate.Attempts
                        + ": no additional candidate could be validated in the bounded sample");
                return false;
            }
            if (Interrupted() || !MapStillCurrent()) return false;
            tried.Add(alternate);

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Gather] relocation {0}/{1}: walking to ({2:F1}, {3:F1}) over the mesh",
                attempt, MeshRelocate.Attempts, alternate.X, alternate.Y));
            bool there = Walk(mover, alternate, checkNodes);
            if (_stop || ClickAimAbort.Requested || RouteRunner.LastRunInterrupted) return false;
            if (!there)
            {
                Logging.Write("[Gather] relocation " + attempt + "/" + MeshRelocate.Attempts
                    + ": could not get there; trying another location");
                continue;
            }

            // A resource may disappear while we relocate. Do not walk back to
            // its stale coordinates unless it is still the same eligible node.
            if (targetStillValid != null && !targetStillValid())
            {
                Logging.Write("[Gather] relocation: resource is no longer a fresh eligible observation; not retrying its old position");
                return false;
            }
            if (_stop || ClickAimAbort.Requested || RouteRunner.LastRunInterrupted || !MapStillCurrent() || Interrupted()) return false;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Gather] relocation {0}/{1}: arrived; asking for ({2:F1}, {3:F1}) again from here",
                attempt, MeshRelocate.Attempts, original.X, original.Y));
            if (Walk(mover, original, checkNodes))
            {
                Logging.Write("[Gather] reached the destination after relocation " + attempt + "/" + MeshRelocate.Attempts);
                return true;
            }
            if (_stop || ClickAimAbort.Requested || RouteRunner.LastRunInterrupted) return false;
        }

        Logging.Write("[Gather] all " + MeshRelocate.Attempts + " alternate mesh locations were tried without reaching the destination");
        return false;
    }

    // After an actual bounded hover miss, approach from a different side. A
    // failed mesh query or interrupted walk is unknown, not a failed angle.
    private static bool TryHoverAngle(ClickAimMover mover, IntPtr hwnd, ref GatherNodeScanner.Node node,
                                      int angleNumber, List<double> triedBearings, Func<bool>? extraSafety, Func<bool>? standaloneSafety)
    {
        GreenMagic.Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (!WowBGatherCastResearch.TryBegin(memory, reader, out WowBObservedUnit player) || memory == null || reader == null) return false;
        bool Safe()
        {
            return !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && MapStillCurrent() &&
                ObjectManager.Wow == memory && ObjectManager.WowB == reader && memory.WindowHandle == hwnd &&
                (!reader.IsExperimental70707 || WowBGrindFactions.Experimental70707BarSafe(reader)) &&
                (extraSafety != null ? extraSafety() : !StopIfDeadOrGhost()) &&
                (standaloneSafety == null || standaloneSafety()) &&
                GrindRunner.TryReadGatherCombat(out bool fighting) && !fighting &&
                WowBGatherCastResearch.Observe(memory, reader, player) == WowBGatherCastResearch.Observation.Idle;
        }
        if (!Safe() || !ClickAimPositions.TryRead(out double x, out double y)) return false;
        double dx = x - node.Position.X, dy = y - node.Position.Y;
        double bearing = Math.Atan2(dy, dx);
        if (triedBearings.Count == 0) triedBearings.Add(bearing);
        // Alternate both sides and the opposite side; retrying more nearby mesh
        // points is bounded and does not mean that the node is unreachable.
        double[] rotations = angleNumber == 1 ? new[] { 2.1, -2.1, Math.PI }
                           : angleNumber == 2 ? new[] { -2.1, Math.PI, 2.1 }
                           : new[] { Math.PI, 2.1, -2.1 };
        foreach (double rotation in rotations)
        {
            if (!Safe()) return false;
            double a = triedBearings[0] + rotation;
            bool alreadyTried = false;
            foreach (double prior in triedBearings)
                if (Math.Abs(Math.Atan2(Math.Sin(a - prior), Math.Cos(a - prior))) < 0.5) { alreadyTried = true; break; }
            if (alreadyTried) continue;
            var destination = new RoutePoint { X = node.Position.X + 3.0 * Math.Cos(a),
                Y = node.Position.Y + 3.0 * Math.Sin(a), Z = node.Position.Z };
            if (IsBlackspot(destination.X, destination.Y) ||
                RouteRunner.TryCombatMeshPath(x, y, destination) == null) continue;
            Func<bool>? priorInterrupt = RouteRunner.ShouldInterrupt;
            bool priorPoll = RouteRunner.PollInterruptDuringWalk, priorMesh = RouteRunner.RequireMeshDuringSession;
            bool reached;
            try
            {
                RouteRunner.ShouldInterrupt = () => !Safe();
                RouteRunner.PollInterruptDuringWalk = true;
                RouteRunner.RequireMeshDuringSession = true;
                reached = RouteRunner.RunWithinSession(mover, new List<RoutePoint> { destination }, 1);
            }
            finally
            {
                RouteRunner.ShouldInterrupt = priorInterrupt;
                RouteRunner.PollInterruptDuringWalk = priorPoll;
                RouteRunner.RequireMeshDuringSession = priorMesh;
            }
            if (!Safe() || RouteRunner.LastRunInterrupted) return false;
            if (!reached || !ClickAimPositions.TryRead(out double nx, out double ny)) return false;
            // A route may claim arrival before it really changed the viewpoint.
            double ndx = nx - node.Position.X, ndy = ny - node.Position.Y;
            double actualBearing = Math.Atan2(ndy, ndx);
            foreach (double prior in triedBearings)
                if (Math.Abs(Math.Atan2(Math.Sin(actualBearing - prior), Math.Cos(actualBearing - prior))) < 0.5) return false;
            if (ClickAimMath.Len(nx - x, ny - y) < 1.0) return false;
            ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            if (ClickAimAbort.Wait(400) || !Safe() || !GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> nearby)) return false;
            bool found = false;
            foreach (var fresh in nearby)
                if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; found = true; break; }
            if (!found || !Safe() || !ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6)) return false;
            if (!Safe()) return false;
            triedBearings.Add(actualBearing);
            return true;
        }
        return false;
    }

    // No movement, turns, stop-key taps or extra clicks while a cast may be running.
    // A resource remaining in the pool can need another hit, or the attempt can have
    // failed; neither proves success. On 70938 ore, count five detected and
    // cleared candidates or three candidate-absent starts separately.
    private static void GatherAtNode(IntPtr hwnd, ClickAimMover mover, GatherNodeScanner.Node node, Func<bool>? extraSafety = null)
    {
        // Standalone ignore mode still enforces session, death and map checks.
        bool standaloneGuard = _ignoreMonsters && extraSafety == null;
        WowBReader? initialReader = ObjectManager.WowB;
        GreenMagic.Memory? initialMemory = ObjectManager.Wow;
        bool StandaloneSafety()
        {
            if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || initialReader == null || initialMemory == null ||
                ObjectManager.WowB != initialReader || ObjectManager.Wow != initialMemory || initialMemory.WindowHandle != hwnd ||
                (initialReader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(initialReader))) return false;
            return !StopIfDeadOrGhost() && MapStillCurrent() && ObjectManager.WowB == initialReader && ObjectManager.Wow == initialMemory;
        }
        // The node was confirmed in range before interaction. It can disappear
        // after a click, so use its recorded position to recognize combat during
        // the stationary interaction without requiring it to remain in the pool.
        void DeferForCombat(bool fighting)
        {
            _pending = node;
            if (!standaloneGuard || !fighting || !StandaloneSafety() ||
                !ClickAimPositions.TryRead(out double x, out double y)) return;
            double z = ClickAimPositions.Z();
            double dx = x - node.Position.X, dy = y - node.Position.Y, dz = z - node.Position.Z;
            if (GatherNodeDefence.InRange(dx, dy, dz, InteractYd))
            {
                _nodeDefence = true;
                Logging.Write("[Gather] combat at the node; defending before gathering resumes");
            }
        }
        int cursorRecoveries = 0, angleAttempts = 0;
        var triedBearings = new List<double>();
        int searchLimit = initialReader?.IsExperimental70938 == true ? ClassicGatherAttempts : MaxGatherAttempts;
        for (int attempt = 1; attempt <= searchLimit + (initialReader?.IsExperimental70938 == true ? 6 : 0) && !_stop && !ClickAimAbort.Requested; attempt++)
        {
            bool fighting;
            if ((extraSafety != null && !extraSafety()) || (standaloneGuard && !StandaloneSafety())) { _pending = node; return; }
            if (!GrindRunner.TryReadGatherCombat(out fighting)) { _pending = node; return; }
            if (fighting) { DeferForCombat(true); return; }
            Logging.Write("[Gather] hover search attempt " + attempt + (initialReader?.IsExperimental70938 == true ? "" : "/" + searchLimit)
                          + "; background mouse movement allowed, foreground interruptions retry automatically");
            if (!MapStillCurrent()) return;
            bool mining70938 = initialReader?.IsExperimental70938 == true && !node.IsHerb;
            WowBObservedUnit castPlayer = default;
            bool castReady = mining70938 && WowBGatherCastResearch.TryBegin(initialMemory, initialReader, out castPlayer) &&
                initialMemory != null && initialReader != null &&
                WowBGatherCastResearch.Observe(initialMemory, initialReader, castPlayer) == WowBGatherCastResearch.Observation.Idle;
            bool clicked = GatherInteraction.TryInteract(hwnd, node, candidate => MapStillCurrent() && Ready(candidate) &&
                (extraSafety == null || initialReader?.IsExperimental70938 != true || candidate.Guid == node.Guid), extraSafety, out GatherNodeScanner.Node clickedNode,
                                                          out string reason, out bool hoverMiss, out bool cursorMoved,
                                                          standaloneGuard ? StandaloneSafety : null);
            // A node interaction may have broken a movement form; one new
            // attempt is allowed before later travel, not on every route tick.
            if (clicked && extraSafety == null && _movementEnabled) _movementAttempted = false;
            if (clicked && clickedNode.Guid != node.Guid)
            {
                Logging.Write("[Gather] hovered eligible nearby " + clickedNode.Name + " " + clickedNode.Guid
                              + " instead of " + node.Guid + "; deferring the original GUID for 30 seconds");
                Retry[node.Guid] = unchecked(Environment.TickCount + 30000);
                node = clickedNode;
                angleAttempts = 0; triedBearings.Clear();
                // A substituted herb/ore is not the same pre-click observation.
                castReady = false;
                mining70938 = initialReader?.IsExperimental70938 == true && !node.IsHerb;
            }
            Logging.Write(clicked ? "[Gather] node interaction: " + reason
                                  : "[Gather] no right-button down sent: " + reason);
            if (ClickAimAbort.Requested || _stop || (extraSafety != null && !extraSafety())) return;
            if (GatherBackgroundFocus.RetryCursorInterruption(clicked, cursorMoved))
            {
                // No button-down was posted. A displaced hover is not a failed
                // gather or proof of depletion: re-sample promptly instead of
                // the six-second post-click wait and long node cooldown.
                if (ClickAimAbort.Wait(150) || _stop || (extraSafety != null && !extraSafety())) return;
                if (!MapStillCurrent()) return;
                if (!GrindRunner.TryReadGatherCombat(out fighting)) { _pending = node; return; }
                if (fighting) { DeferForCombat(true); return; }
                if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> refreshed)) return;
                bool found = false;
                foreach (var fresh in refreshed)
                    if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; found = true; break; }
                if (!found) return;
                if (node.Distance > InteractYd) { _pending = node; return; }
                if (++cursorRecoveries <= 4)
                {
                    Logging.Write("[Gather] retrying interrupted hover with a fresh cursor and node check");
                    attempt--;
                    continue;
                }
                // Yield to the outer combat/STOP loop, then reconsider this node
                // without a 20/120-second failure cooldown.
                _pending = node;
                Logging.Write("[Gather] repeated mouse interruptions; yielding briefly before another search");
                return;
            }
            if (!clicked && hoverMiss && initialReader?.IsExperimental70938 == true)
            {
                if (angleAttempts >= 3)
                {
                    if ((extraSafety != null && !extraSafety()) || (standaloneGuard && !StandaloneSafety()) ||
                        !MapStillCurrent() || !GrindRunner.TryReadGatherCombat(out fighting) || fighting ||
                        !GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> check)) return;
                    bool stillPresent = false;
                    foreach (var fresh in check) if (fresh.Guid == node.Guid && Ready(fresh)) { stillPresent = true; break; }
                    if (!stillPresent || _stop || ClickAimAbort.Requested || RouteRunner.StopRequested ||
                        ObjectManager.Wow != initialMemory || ObjectManager.WowB != initialReader) return;
                    FailedHarvestNodes.Add(node.Guid);
                    Logging.Write("[Gather] node " + node.Guid + " not hovered after three alternate approaches; session-blacklisted this GUID. Visibility/occlusion is not verified.");
                    return;
                }
                Logging.Write("[Gather] hover miss; trying alternate approach " + (angleAttempts + 1) + "/3");
                if (!TryHoverAngle(mover, hwnd, ref node, angleAttempts + 1, triedBearings, extraSafety, standaloneGuard ? StandaloneSafety : null))
                {
                    if (!_stop && !ClickAimAbort.Requested && MapStillCurrent())
                    { Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs); Logging.Write("[Gather] alternate approach unavailable or interrupted; deferring, not blacklisting"); }
                    return;
                }
                angleAttempts++;
                attempt--;
                continue;
            }
            // Keep the old fallback for herbs, other builds, uncertain reads,
            // and any interaction that was not posted. Never treat unknown as idle.
            bool seen = false, missing = false, uncertain = !castReady, cleared = false;
            int idleAfter = 0, started = Environment.TickCount, clearedAt = 0;
            bool unknownLogged = false;
            Logging.Write(mining70938 && clicked && castReady
                ? "[Gather] waiting for the 70938 ID-3564 cast-like candidate and its clear; loot is not verified"
                : "[Gather] holding still for 6 seconds for a possible cast and auto-loot; success is not yet known");
            while (true)
            {
                if (ClickAimAbort.Wait(250) || _stop) return;
                if (extraSafety != null && !extraSafety()) { Logging.Write("[Gather] Grind safety check interrupted the cast wait"); return; }
                if (standaloneGuard && !StandaloneSafety()) return;
                if (!GrindRunner.TryReadGatherCombat(out fighting))
                {
                    if (!unknownLogged) Logging.Write("[Gather] combat state unreadable; continuing to hold still");
                    unknownLogged = true; uncertain = true;
                    continue;
                }
                if (fighting)
                {
                    Logging.Write("[Gather] combat detected during cast wait; interrupting wait for defence");
                    DeferForCombat(true);
                    return;
                }
                int elapsed = unchecked(Environment.TickCount - started);
                if (clicked && castReady && initialReader != null && initialMemory != null)
                {
                    var state = WowBGatherCastResearch.Observe(initialMemory, initialReader, castPlayer);
                    if (state == WowBGatherCastResearch.Observation.Mining)
                    { seen = true; idleAfter = 0; cleared = false; }
                    else if (state == WowBGatherCastResearch.Observation.Idle)
                    {
                        if (seen && ++idleAfter >= 2 && !cleared) { cleared = true; clearedAt = Environment.TickCount; }
                    }
                    else { uncertain = true; idleAfter = 0; cleared = false; }
                    if (seen && cleared && unchecked(Environment.TickCount - clearedAt) >= MiningLootWaitMs) break;
                    if (!seen && !uncertain && elapsed >= MiningStartWaitMs) { missing = true; break; }
                    // A stuck active signal must not hang forever; result remains
                    // unknown and will be deferred rather than immediately retried.
                    if (seen && elapsed >= MiningCastLimitMs) { uncertain = true; cleared = false; break; }
                }
                if ((!clicked || !castReady || (!seen && uncertain)) && elapsed >= GatherHoldMs) break;
            }
            if (mining70938 && clicked)
                Logging.Write(seen ? "[Gather] ID-3564 cast-like candidate observed" + (cleared ? " then cleared" : " but did not clear reliably") + "; cast completion and loot are unverified"
                    : missing ? "[Gather] no ID-3564 candidate observed during the start window; this can miss a cast or another rank"
                    : "[Gather] cast observation unknown; no missing-cast failure counted");
            if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> after))
            {
                Logging.Write("[Gather] post-attempt scan failed; result unknown, will retry later");
                Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                return;
            }
            bool observed = false;
            foreach (var fresh in after)
                if (fresh.Guid == node.Guid) { node = fresh; observed = true; break; }
            if (!observed)
            {
                // An absent node may be depleted or missing from the snapshot.
                // Do not carry old progress into a later re-observation.
                ObservedHarvestAttempts.Remove(node.Guid);
                MissingMiningStarts.Remove(node.Guid);
                int delay = clicked ? UnavailableRetryMs : InteractionRetryMs;
                Logging.Write("[Gather] " + node.Guid + " no longer observed after the wait; loot was not independently verified. "
                              + "The same GUID is deferred for " + (delay / 1000) + " seconds.");
                Retry[node.Guid] = unchecked(Environment.TickCount + delay);
                return;
            }
            // A posted click is not proof that a cast or loot succeeded. Only
            // count it after an uninterrupted wait and a successful scan still
            // observing the same GUID. Cursor/hover misses do not count as casts.
            if (clicked && initialReader?.IsExperimental70938 == true)
            {
                if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || ObjectManager.WowB != initialReader ||
                    ObjectManager.Wow != initialMemory || !MapStillCurrent() || (extraSafety != null && !extraSafety()) ||
                    (standaloneGuard && !StandaloneSafety())) return;
                if (!GrindRunner.TryReadGatherCombat(out fighting)) { _pending = node; return; }
                if (fighting) { DeferForCombat(true); return; }
                if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested ||
                    ObjectManager.WowB != initialReader || ObjectManager.Wow != initialMemory) return;
                if (mining70938 && missing)
                {
                    MissingMiningStarts.TryGetValue(node.Guid, out int absentCasts);
                    absentCasts++;
                    MissingMiningStarts[node.Guid] = absentCasts;
                    if (absentCasts >= 3)
                    {
                        FailedHarvestNodes.Add(node.Guid);
                        Logging.Write("[Gather] node " + node.Guid + " session-blacklisted after three posted interactions with no observed ID-3564 start; a missed cast or different rank remains possible");
                        return;
                    }
                    Logging.Write("[Gather] no cast-like start " + absentCasts + "/3; retrying if safe");
                    if (!Ready(node)) return;
                    if (node.Distance > InteractYd) { _pending = node; return; }
                    continue;
                }
                if (mining70938 && (!seen || !cleared))
                {
                    MissingMiningStarts.Remove(node.Guid);
                    Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                    Logging.Write("[Gather] cast state uncertain; deferring without counting a failure");
                    return;
                }
                MissingMiningStarts.Remove(node.Guid);
                ObservedHarvestAttempts.TryGetValue(node.Guid, out int previousAttempts);
                int observedAttempts = previousAttempts + 1;
                if (observedAttempts >= ClassicGatherAttempts)
                {
                    ObservedHarvestAttempts.Remove(node.Guid);
                    FailedHarvestNodes.Add(node.Guid);
                    Logging.Write("[Gather] node " + node.Guid + " still observed after " + observedAttempts +
                                  " posted interactions with completed waits; blacklisted this GUID for the current gathering session. "
                                  + "Harvest failure is unverified; a multi-hit vein may still contain ore.");
                    return;
                }
                ObservedHarvestAttempts[node.Guid] = observedAttempts;
                Logging.Write("[Gather] node still observed after posted interaction " + observedAttempts + "/" +
                              ClassicGatherAttempts + "; attempting another hit if safe. Cast and loot are unverified.");
            }
            if (!Ready(node)) return;
            // An unposted click for a reason other than a confirmed hover miss
            // is unknown, not permission to repeatedly interact from this state.
            if (!clicked && initialReader?.IsExperimental70938 == true && !hoverMiss)
            {
                Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                Logging.Write("[Gather] interaction was not posted; deferring without a cast or hover failure count");
                return;
            }
            if (hoverMiss)
            {
                Retry[node.Guid] = unchecked(Environment.TickCount + UnavailableRetryMs);
                Logging.Write("[Gather] could not hover " + node.Guid + "; deferring this GUID for 120 seconds. "
                              + "It may be depleted, occluded or missed by the search; a different GUID is not deferred.");
                return;
            }
            if (node.Distance > InteractYd)
            {
                Logging.Write("[Gather] node still observed but out of reach; will approach again");
                _pending = node;
                return;
            }
            Logging.Write("[Gather] node still observed after attempt " + attempt
                          + "; may need another hit or previous interaction may have failed");
        }
        if (!_stop && !ClickAimAbort.Requested)
        {
            Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
            Logging.Write("[Gather] attempt limit reached; will reconsider this node after 20 seconds");
        }
    }

    // Grind owns the thread, mover and stop state. This does not start a second bot.
    internal static bool PrepareForGrind(string profile)
    {
        if (_running || ClickAimAbort.Requested) return false;
        try
        {
            GatherSettings.Load();
            if (!GatherSettings.GatherWhileGrinding) return false;
            if ((!GatherSettings.Mining || GatherSettings.MiningSkill == 0)
                && (!GatherSettings.Herbalism || GatherSettings.HerbalismSkill == 0))
            { Logging.Write("[Grind gather] no trained profession selected; gathering disabled"); return false; }
            if (string.IsNullOrWhiteSpace(profile)) Blackspots.Clear();
            else LoadBlackspots(profile);
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null || !reader.TryReadMapId(out _sessionMap) || ObjectManager.WowB != reader)
            { Logging.Write("[Grind gather] client map ID unreadable; gathering disabled"); return false; }
            if (!RouteRunner.MeshReady())
            { Logging.Write("[Grind gather] navmesh unavailable; gathering disabled"); return false; }
            if (ClickAimAbort.Requested) return false;
            Retry.Clear();
            FailedHarvestNodes.Clear();
            ObservedHarvestAttempts.Clear();
            MissingMiningStarts.Clear();
            GatherInteraction.ResetHoverHint();
            _pending = null;
            ClearGrindCommitment();
            _stop = false;
            _lastScan = unchecked(Environment.TickCount - 1500);
            _lastSummary = unchecked(Environment.TickCount - 15000);
            _lastSafetySummary = unchecked(Environment.TickCount - 15000);
            Logging.Write("[Grind gather] enabled: Mining=" + GatherSettings.Mining + " skill=" + GatherSettings.MiningSkill +
                ", Herbalism=" + GatherSettings.Herbalism + " skill=" + GatherSettings.HerbalismSkill +
                "; resources within " + GrindSearchYd.ToString("F0", CultureInfo.InvariantCulture) +
                " yd in the client-loaded pool; 70938 starts only without living non-player NPCs within 100 yd, then prioritizes the node; actual combat and selling preempt. Hostility and node layout remain unverified.");
            return true;
        }
        catch (Exception ex) { Logging.Write("[Grind gather] disabled: " + ex.Message); return false; }
    }

    internal static bool HasGrindResource(Func<bool> safe)
    {
        if (_stop || ClickAimAbort.Requested || unchecked(Environment.TickCount - _lastScan) < 1500) return false;
        _lastScan = Environment.TickCount;
        return MapStillCurrent() && safe() && FindNode(out _, GrindSearchYd);
    }

    // A committed node owns the Grind detour even between attempts. Unreadable
    // observations hold the bot, never count as a failure or proof of depletion.
    private static void ReportGrindCommitment(string message)
    {
        if (unchecked(Environment.TickCount - _grindReportAt) < 0) return;
        _grindReportAt = unchecked(Environment.TickCount + 15000);
        Logging.Write("[Grind gather] " + message);
    }

    private static void RecordGrindApproachFailure(GatherNodeScanner.Node node, Func<bool> safe, string reason)
    {
        if (!safe() || !MapStillCurrent() || !GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> observed)) return;
        bool stillObserved = false;
        foreach (var fresh in observed) if (fresh.Guid == node.Guid && Ready(fresh)) { stillObserved = true; break; }
        if (!stillObserved || !safe() || _stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return;
        _grindApproachFailures++;
        if (_grindApproachFailures >= 3)
        {
            FailedHarvestNodes.Add(node.Guid);
            Logging.Write("[Grind gather] committed node " + node.Guid + " session-blacklisted after three failed approaches (" + reason + "); reachability remains unverified");
        }
        else
        {
            Retry[node.Guid] = unchecked(Environment.TickCount + 3000);
            Logging.Write("[Grind gather] committed node approach failed " + _grindApproachFailures + "/3 (" + reason + "); retrying this node");
        }
    }

    // Returns true while gathering/holding priority; false permits combat or
    // ordinary Grind decisions. Presence, successful cast and loot are candidates.
    internal static bool GatherForGrind(IntPtr hwnd, ClickAimMover mover, Func<bool> startSafe, Func<bool> continueSafe)
    {
        bool committed = _grindCommitted.HasValue;
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || !MapStillCurrent()) return false;
        if (!GrindRunner.TryReadGatherCombat(out bool fighting))
        {
            if (committed) { ReportGrindCommitment("combat state unreadable; holding the committed node"); ClickAimAbort.Wait(250); }
            return committed;
        }
        if (fighting) return false; // allow Grind's existing defensive fight
        GatherNodeScanner.Node node;
        if (committed)
        {
            node = _grindCommitted!.Value;
            if (!continueSafe()) { ReportGrindCommitment("safety check unavailable; holding the committed node"); ClickAimAbort.Wait(250); return true; }
            if (FailedHarvestNodes.Contains(node.Guid))
            { Logging.Write("[Grind gather] committed node session-blacklisted; resuming Grind"); ClearGrindCommitment(); return true; }
            if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> observed, out GatherNodeScanner.ScanStats scanStats))
            { ReportGrindCommitment("node scan unavailable; holding the committed node"); ClickAimAbort.Wait(250); return true; }
            bool found = false;
            foreach (var fresh in observed)
                if (fresh.Guid == node.Guid) { node = fresh; found = true; break; }
            if (!found)
            {
                if (scanStats.HeaderRejected != 0 || scanStats.Changed != 0)
                { ReportGrindCommitment("node candidates unreadable or changing; holding the committed node"); ClickAimAbort.Wait(250); return true; }
                // A node can disappear because it was depleted, despawned or
                // not streamed/read. Three fresh scans end this commitment, not
                // a claim of successful harvest or permission to click blindly.
                if (++_grindMissingScans >= 3)
                {
                    Logging.Write("[Grind gather] committed node no longer observed in three scans; resuming Grind (loot/depletion unverified)");
                    ClearGrindCommitment();
                }
                else ClickAimAbort.Wait(500);
                return true;
            }
            _grindMissingScans = 0; _grindCommitted = node;
            if (IsBlackspot(node.Position.X, node.Position.Y) ||
                !GatherNodeScanner.IsEligible(node, GatherSettings.Mining, GatherSettings.MiningSkill, GatherSettings.Herbalism, GatherSettings.HerbalismSkill))
            { Logging.Write("[Grind gather] committed node no longer eligible; resuming Grind"); ClearGrindCommitment(); return true; }
            if (!Ready(node))
            { ReportGrindCommitment("waiting for the committed node's retry cooldown"); ClickAimAbort.Wait(250); return true; }
        }
        else
        {
            if (!FindNode(out node, GrindSearchYd)) return false;
            if (!startSafe())
            {
                if (unchecked(Environment.TickCount - _lastSafetySummary) >= 15000)
                {
                    _lastSafetySummary = Environment.TickCount;
                    Logging.Write("[Grind gather] resource in range, but combat, a living non-player NPC within 100 yd or unreadable safety state takes priority");
                }
                return false;
            }
            if (ObjectManager.WowB?.IsExperimental70938 == true)
            {
                _grindCommitted = node; _grindMissingScans = _grindApproachFailures = 0; _grindReportAt = 0;
                committed = true;
                Logging.Write("[Grind gather] committed to " + node.Guid + "; nearby monsters alone will not interrupt this node");
            }
        }
        Func<bool> safe = committed ? continueSafe : startSafe;
        bool oldQuiet = mover.QuietKeyStop;
        mover.QuietKeyStop = true;
        try
        {
            Logging.Write("[Grind gather] approaching " + node.Name + " " + node.Guid + " at "
                          + node.Distance.ToString("F1", CultureInfo.InvariantCulture) + " yd");
            if (node.Distance > InteractYd)
            {
                var destination = new RoutePoint { X = node.Position.X, Y = node.Position.Y, Z = node.Position.Z };
                Func<bool>? previousInterrupt = RouteRunner.ShouldInterrupt;
                bool previousPoll = RouteRunner.PollInterruptDuringWalk;
                bool previousRequireMesh = RouteRunner.RequireMeshDuringSession;
                bool reached;
                int lastSafetyCheck = unchecked(Environment.TickCount - 500);
                try
                {
                    RouteRunner.ShouldInterrupt = () =>
                    {
                        if (_stop || ClickAimAbort.Requested) return true;
                        if (unchecked(Environment.TickCount - lastSafetyCheck) < 500) return false;
                        lastSafetyCheck = Environment.TickCount;
                        return !MapStillCurrent() || !safe();
                    };
                    RouteRunner.PollInterruptDuringWalk = true;
                    RouteRunner.RequireMeshDuringSession = true;
                    reached = RouteRunner.RunWithinSession(mover, new List<RoutePoint> { destination }, 1);
                }
                finally
                {
                    RouteRunner.ShouldInterrupt = previousInterrupt;
                    RouteRunner.PollInterruptDuringWalk = previousPoll;
                    RouteRunner.RequireMeshDuringSession = previousRequireMesh;
                }
                if (_stop || ClickAimAbort.Requested || !safe()) return true;
                if (!reached)
                {
                    if (!RouteRunner.LastRunInterrupted)
                    {
                        if (!committed) Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs);
                        else RecordGrindApproachFailure(node, safe, "mesh route did not arrive");
                    }
                    return true;
                }
            }
            ClickAimKeySteer.ReleaseHeld();
            mover.MoveStop();
            if (ClickAimAbort.Wait(400) || _stop || !safe() || !MapStillCurrent()) return true;
            if (!GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> nearby)) return true;
            bool present = false;
            foreach (var fresh in nearby)
                if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; present = true; break; }
            if (!present)
            {
                if (committed) RecordGrindApproachFailure(node, safe, "node not in interaction range");
                else Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                return true;
            }
            if (!safe()) return true;
            if (!ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6))
            {
                if (committed) RecordGrindApproachFailure(node, safe, "could not face node");
                else Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                return true;
            }
            if (!safe() || _stop || ClickAimAbort.Requested) return true;
            if (committed) _grindApproachFailures = 0;
            GatherAtNode(hwnd, mover, node, safe);
            return true;
        }
        finally { mover.QuietKeyStop = oldQuiet; }
    }

    private static void RunGuarded(List<RoutePoint> points, MeshRoam? roam, WowBReader? experimentalReader)
    {
        try
        {
            if (experimentalReader != null)
            {
                WowBGrindFactions.ClaimExperimentalGrindThread();
                if (ObjectManager.WowB != experimentalReader || !experimentalReader.ExperimentalGrindActive) return;
            }
            Run(points, roam);
        }
        catch (Exception ex) { Logging.Write("[Gather] worker stopped on error: " + ex.Message); }
        finally
        {
            _deathCheckpoint = null;
            if (experimentalReader != null) WowBGrindFactions.EndExperimental70707();
            _running = false;
        }
    }

    private static void Run(List<RoutePoint> points, MeshRoam? roam)
    {
        _deathCheckpoint = new WowBDeathCheckpoint();
        _recoveryReady = false;
        var mover = new ClickAimMover { QuietKeyStop = true };
        int index = 0, failures = 0, fastHotspots = 0, sampleFailures = 0;
        int stationaryNodeFailures = 0;
        double lastFailureX = double.NaN, lastFailureY = double.NaN;
        try
        {
            IntPtr hwnd = ObjectManager.Wow?.WindowHandle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) { Logging.Write("[Gather] no game window"); return; }
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null || !reader.TryReadMapId(out _sessionMap) || ObjectManager.WowB != reader)
            { Logging.Write("[Gather] client map ID unreadable; refusing to guess a map"); return; }
            Logging.Write("[Gather] client map root reports map " + _sessionMap);
            Logging.Write("[Gather] node scan radius " + SearchYd.ToString("F0", CultureInfo.InvariantCulture) +
                " yd; only client-loaded pool objects can be seen, and distant routes require a validated mesh");
            if (roam != null && !roam.MapCurrent()) { Logging.Write("[Roam] map changed before start; stopping"); return; }
            if (_ignoreMonsters) Logging.Write("[Gather] ignore-monsters enabled: ignore combat while travelling, but defend at a node before gathering. Death and STOP still apply.");
            Logging.Write("[Gather] experimental ground gatherer: " + (roam == null ? points.Count + " hotspots" : "profile-free local roaming") + "; Mining="
                + GatherSettings.Mining + " skill=" + GatherSettings.MiningSkill + "; Herbalism="
                + GatherSettings.Herbalism + " skill=" + GatherSettings.HerbalismSkill
                + "; these skills are manually configured, not read from the client");
            foreach (var spot in Blackspots)
            {
                if (_stop || ClickAimAbort.Requested) break;
                uint map = ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, spot.X, spot.Y);
                if (map == uint.MaxValue || map != _sessionMap) { Logging.Write("[Gather] client map changed or became unreadable; stopping"); return; }
                MeshBlackspots.Mark(Navigator.TripperNavigator, map, spot.X, spot.Y, spot.Z, (float)spot.Radius);
            }
            bool recovered;
            do
            {
            while (!_stop && !ClickAimAbort.Requested)
            {
                if (StopIfDeadOrGhost()) break;
                if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))
                {
                    ClickAimKeySteer.ReleaseHeld();
                    ClickAimKeySteer.ReleaseArrows(hwnd);
                    Logging.Write("[Gather] action bar changed or was unreadable; holding movement while checking the original bar" +
                        (reader.Uses70862Features ? " (up to 60 seconds)" : ""));
                    int nextBarDeathCheck = Environment.TickCount;
                    bool BarRecoveryStopped()
                    {
                        if (_stop || RouteRunner.StopRequested || ClickAimAbort.Requested) return true;
                        if (reader.Uses70862Features && unchecked(Environment.TickCount - nextBarDeathCheck) >= 0)
                        { nextBarDeathCheck = unchecked(Environment.TickCount + 1000); return StopIfDeadOrGhost(); }
                        return false;
                    }
                    if (!WowBGrindFactions.WaitForExperimentalBar(reader, BarRecoveryStopped, out string barReason))
                    { Logging.Write("[Gather] " + reader.ExperimentalBuildNumber + " action bar not safely restored: " + barReason + "; stopping"); break; }
                    Logging.Write("[Gather] action bar matched the original snapshot again; rechecking combat before continuing");
                    continue;
                }
                if (!MapStillCurrent() || (roam != null && !roam.MapCurrent())) break;
                if (!GrindRunner.TryReadGatherCombat(out bool fighting))
                {
                    Logging.Write("[Gather] combat state unreadable; holding position");
                    ClickAimAbort.Wait(1500); continue;
                }
                if (!fighting) GrindRunner.ClearGatherDefenceSelectionOnCombatEnd();
                if (!fighting && _nodeDefence)
                {
                    _nodeDefence = false;
                    Logging.Write("[Gather] node defence combat ended; rechecking the resource before gathering");
                }
                if (GatherNodeDefence.ShouldDefend(fighting, _ignoreMonsters, _nodeDefence))
                {
                    fastHotspots = 0;
                    ClickAimKeySteer.ReleaseHeld();
                    try { mover.MoveStop(); } catch { }
                    string defence = GrindRunner.DefendGatherer(hwnd);
                    if (defence.Length > 0) Logging.Write("[Gather] defence: " + defence);
                    ClickAimAbort.Wait(1000); continue;
                }
                if (!fighting)
                {
                    string rest = GrindRunner.RestForGather(hwnd);
                    if (rest.Length > 0) { fastHotspots = 0; Logging.Write("[Gather] " + rest); }
                    if (_stop || ClickAimAbort.Requested) break;
                    // Buff only between gathering interactions and outside combat.
                    if (GrindRunner.BuffForGather(hwnd, mover, () => _stop)) fastHotspots = 0;
                    if (_stop || ClickAimAbort.Requested) break;
                }
                // Unknown combat remains unsafe; the opt-in only bypasses a known combat flag.
                if (!GrindRunner.TryReadGatherCombat(out fighting) || (fighting && !_ignoreMonsters)) continue;
                GatherNodeScanner.Node node;
                if (_pending.HasValue) { node = _pending.Value; _pending = null; }
                else if (!FindNode(out node))
                {
                    RoutePoint next;
                    if (roam != null)
                    {
                        if (!roam.TryNext(out next))
                        {
                            if (!roam.MapCurrent()) { Logging.Write("[Roam] map changed or became unreadable; stopping"); break; }
                            if (++sampleFailures >= 5) { Logging.Write("[Roam] could not find a local mesh destination after five tries; stopping"); break; }
                            Logging.Write("[Roam] no local mesh destination found; waiting before retry");
                            ClickAimAbort.Wait(2000);
                            continue;
                        }
                        sampleFailures = 0;
                    }
                    else next = points[index % points.Count];
                    if (IsBlackspot(next.X, next.Y))
                    {
                        Logging.Write("[Gather] hotspot lies inside a profile blackspot; skipping it");
                        if (roam == null) index = (index + 1) % points.Count;
                        fastHotspots = 0;
                        ClickAimAbort.Wait(1000);
                        continue;
                    }
                    if (ClickAimPositions.TryRead(out double moveX, out double moveY) &&
                        TryMovementSkill(hwnd, mover, ClickAimMath.Len(next.X - moveX, next.Y - moveY))) continue;
                    int startedHotspot = Environment.TickCount;
                    bool walked = Walk(mover, next, true);
                    if (_stop || ClickAimAbort.Requested) break;
                    if (RouteRunner.LastRunInterrupted) { fastHotspots = 0; continue; }
                    if (!walked && roam == null)
                    {
                        // A profile hotspot is fixed; retrying it from the same
                        // stuck position repeats the failure. Roaming already
                        // chooses a different destination after a failed leg.
                        walked = RelocateAndRetry(mover, next);
                        if (_stop || ClickAimAbort.Requested) break;
                        if (RouteRunner.LastRunInterrupted) { fastHotspots = 0; continue; }
                        if (!walked)
                        {
                            Logging.Write("[Gather] relocation could not recover the hotspot; stopping");
                            break;
                        }
                    }
                    if (walked)
                    {
                        stationaryNodeFailures = 0;
                        if (roam == null) index = (index + 1) % points.Count;
                        failures = 0;
                        if (roam == null && unchecked(Environment.TickCount - startedHotspot) < FastHotspotMs)
                        {
                            if (ShouldPauseHotspotLoop(++fastHotspots, points.Count))
                            { fastHotspots = 0; ClickAimAbort.Wait(HotspotLoopPauseMs); }
                        }
                        else fastHotspots = 0;
                    }
                    else
                    {
                        fastHotspots = 0;
                        if (++failures >= 3) { Logging.Write("[Gather] three mesh leg failures; stopping"); break; }
                    }
                    continue;
                }
                fastHotspots = 0;
                if (!Ready(node)) continue;
                // An interrupted leg may have left a stale observation; refresh position and range.
                if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> current)) continue;
                bool observed = false;
                foreach (var fresh in current) if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; observed = true; break; }
                if (!observed) continue;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Gather] {0} ({1:F1} yd, id {2}, guid {3}, position {4:F1},{5:F1},{6:F1}) - routing toward the resource",
                    node.Name, node.Distance, node.Entry, node.Guid, node.Position.X, node.Position.Y, node.Position.Z));
                if (node.Distance > InteractYd)
                {
                    if (TryMovementSkill(hwnd, mover, node.Distance)) { _pending = node; continue; }
                    var destination = new RoutePoint { X = node.Position.X, Y = node.Position.Y, Z = node.Position.Z };
                    bool reached = Walk(mover, destination, false);
                    if (_stop || ClickAimAbort.Requested) break;
                    if (RouteRunner.LastRunInterrupted) { _pending = node; continue; }
                    if (!reached)
                    {
                        Logging.Write("[Gather] resource approach did not arrive; trying bounded mesh relocation before another resource");
                        WowBGuid requestedGuid = node.Guid;
                        WoWPoint requestedPosition = node.Position;
                        bool nodeLost = false;
                        bool NodeStillValid()
                        {
                            if (GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> snapshot))
                                foreach (var fresh in snapshot)
                                    if (fresh.Guid == requestedGuid && Ready(fresh) &&
                                        GatherNodeScanner.IsEligible(fresh, GatherSettings.Mining, GatherSettings.MiningSkill,
                                                                     GatherSettings.Herbalism, GatherSettings.HerbalismSkill) &&
                                        !IsBlackspot(fresh.Position.X, fresh.Position.Y) &&
                                        ClickAimMath.Len(fresh.Position.X - requestedPosition.X, fresh.Position.Y - requestedPosition.Y) <= 3.0 &&
                                        Math.Abs(fresh.Position.Z - requestedPosition.Z) <= 3.0) return true;
                            nodeLost = true; // includes unreadable scans: do not infer a movement failure
                            return false;
                        }
                        // The current resource would otherwise immediately interrupt
                        // relocation as a newly discovered node. Safety polling stays on.
                        reached = RelocateAndRetry(mover, destination, false, NodeStillValid);
                        if (_stop || ClickAimAbort.Requested) break;
                        if (RouteRunner.LastRunInterrupted) { _pending = node; continue; }
                        if (!reached)
                        {
                            if (nodeLost) { Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs); continue; }
                            Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs);
                            if (!ClickAimPositions.TryRead(out double failedX, out double failedY))
                            { Logging.Write("[Gather] player position unreadable after recovery; stopping"); break; }
                            if (!double.IsFinite(lastFailureX) || ClickAimMath.Len(failedX - lastFailureX, failedY - lastFailureY) >= 3.0)
                                stationaryNodeFailures = 1;
                            else stationaryNodeFailures++;
                            lastFailureX = failedX; lastFailureY = failedY;
                            if (stationaryNodeFailures >= 3)
                            {
                                Logging.Write("[Gather] three resource approaches and relocation attempts failed near the same position; stopping instead of repeatedly cycling nodes");
                                break;
                            }
                            continue;
                        }
                    }
                    stationaryNodeFailures = 0;
                }
                ClickAimKeySteer.ReleaseHeld();
                try { mover.MoveStop(); } catch { }
                if (ClickAimAbort.Wait(400)) break;
                if (!GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> nearby)) continue;
                bool stillHere = false;
                foreach (var fresh in nearby) if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; stillHere = true; break; }
                if (!stillHere) { Logging.Write("[Gather] node not observed in reach; no click sent"); Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs); continue; }
                stationaryNodeFailures = 0;
                if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) break;
                if (_ignoreMonsters && (StopIfDeadOrGhost() || !MapStillCurrent() || ObjectManager.WowB != reader ||
                    (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader)))) continue;
                if (!GrindRunner.TryReadGatherCombat(out bool combatBeforeFace)) { _pending = node; continue; }
                if (combatBeforeFace)
                {
                    _pending = node;
                    if (_ignoreMonsters)
                    {
                        _nodeDefence = true;
                        Logging.Write("[Gather] combat at the confirmed in-range node; defending before gathering resumes");
                    }
                    continue;
                }
                if (!ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6))
                {
                    Logging.Write("[Gather] could not face node; will retry it later");
                    Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                    continue;
                }
                if (_stop || ClickAimAbort.Requested) break;
                GatherAtNode(hwnd, mover, node);

            }
            recovered = _recoveryReady && !ClickAimAbort.Requested && RecoverIfPending(mover);
            } while (recovered && !_stop && !ClickAimAbort.Requested);
        }
        catch (Exception ex) { Logging.Write("[Gather] stopped on error: " + ex.Message); }
        finally
        {
            RouteRunner.RequireMeshDuringSession = false;
            RouteRunner.PollInterruptDuringWalk = false;
            RouteRunner.ShouldInterrupt = null;
            ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            Logging.Write("[Gather] stopped");
        }
    }
}

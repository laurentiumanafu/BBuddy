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
    private const double SearchYd = 100.0;
    // Optional Grind detours stay local; standalone GatherBuddy keeps 100 yd.
    private const double GrindSearchYd = 25.0;
    private const double InteractYd = 5.0;
    private const int RetryMs = 90000;
    private const int InteractionRetryMs = 20000;
    // A missed hover is not evidence of depletion. Back off for the exact GUID
    // without suppressing new spawns at the same position.
    private const int UnavailableRetryMs = 120000;
    private const int MaxGatherAttempts = 4;
    // Covers a 2.5 s mining cast plus time for the loot window / auto-loot.
    private const int GatherHoldMs = 6000;
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
    private static readonly List<(double X, double Y, double Z, double Radius)> Blackspots = new List<(double, double, double, double)>();
    private static int _lastScan;
    private static int _lastCombatCheck;
    private static int _lastSummary;
    private static int _lastSafetySummary;
    private static GatherNodeScanner.Node? _pending;
    private static uint _sessionMap;
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
                Logging.Write("[Gather] EXPERIMENTAL " + experimentalReader.ExperimentalBuildNumber + ": static node vtable candidate, faction-filtered defence only; monitor closely and use STOP.");
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
            Retry.Clear();
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
        if (IsBlackspot(node.Position.X, node.Position.Y)) return false;
        if (!GatherNodeScanner.IsEligible(node, GatherSettings.Mining, GatherSettings.MiningSkill,
                                          GatherSettings.Herbalism, GatherSettings.HerbalismSkill)) return false;
        return !Retry.TryGetValue(node.Guid, out int until) || unchecked(Environment.TickCount - until) >= 0;
    }

    private static bool FindNode(out GatherNodeScanner.Node node, double radius = SearchYd)
    {
        node = default;
        bool scanOk = GatherNodeScanner.TryCapture(radius, out List<GatherNodeScanner.Node> nodes, out GatherNodeScanner.ScanStats stats);
        int eligible = 0, blackspotted = 0, coolingDown = 0, skillExcluded = 0;
        foreach (GatherNodeScanner.Node candidate in nodes)
        {
            if (!GatherNodeScanner.IsEligible(candidate, GatherSettings.Mining, GatherSettings.MiningSkill,
                                               GatherSettings.Herbalism, GatherSettings.HerbalismSkill)) { skillExcluded++; continue; }
            if (IsBlackspot(candidate.Position.X, candidate.Position.Y)) { blackspotted++; continue; }
            if (!Ready(candidate)) { coolingDown++; continue; }
            if (eligible++ == 0) node = candidate;
        }
        if (unchecked(Environment.TickCount - _lastSummary) >= 15000)
        {
            _lastSummary = Environment.TickCount;
            if (!scanOk) Logging.Write("[Gather] node scan failed; no conclusion about nearby resources");
            else Logging.Write("[Gather] scan within " + radius.ToString("F0", CultureInfo.InvariantCulture) + " yd: " + nodes.Count + " recognized, " + eligible
                + " eligible, " + skillExcluded + " profession/skill/blacklist excluded, "
                + blackspotted + " profile-blackspot excluded, " + coolingDown + " on retry cooldown; "
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
            if (!GrindRunner.TryReadGatherCombat(out bool fighting) || fighting) return true;
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

    private static bool Walk(ClickAimMover mover, RoutePoint point, bool checkNodes)
    {
        if (_stop || ClickAimAbort.Requested) return false;
        if (!RouteRunner.MeshReady()) { Logging.Write("[Gather] mesh navigation lost; refusing the leg"); return false; }
        RouteRunner.ShouldInterrupt = () => Interrupt(checkNodes);
        RouteRunner.PollInterruptDuringWalk = true;
        RouteRunner.RequireMeshDuringSession = true;
        try { return RouteRunner.RunWithinSession(mover, new List<RoutePoint> { point }, 1); }
        finally { RouteRunner.RequireMeshDuringSession = false; RouteRunner.PollInterruptDuringWalk = false; RouteRunner.ShouldInterrupt = null; }
    }

    /// <summary>
    /// A hotspot leg failed. Try up to <see cref="MeshRelocate.Attempts"/> other mesh-valid
    /// locations within <see cref="MeshRelocate.RadiusYd"/> of the initial stuck position:
    /// walk to each over the mesh and, once there, ask for the hotspot again.
    /// Returns true when reached. On interruption, the caller returns to the main loop
    /// for combat, death, map, node or user-stop handling.
    /// </summary>
    private static bool RelocateAndRetry(ClickAimMover mover, RoutePoint original)
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
            bool there = Walk(mover, alternate, true);
            if (_stop || ClickAimAbort.Requested || RouteRunner.LastRunInterrupted) return false;
            if (!there)
            {
                Logging.Write("[Gather] relocation " + attempt + "/" + MeshRelocate.Attempts
                    + ": could not get there; trying another location");
                continue;
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Gather] relocation {0}/{1}: arrived; asking for ({2:F1}, {3:F1}) again from here",
                attempt, MeshRelocate.Attempts, original.X, original.Y));
            if (Walk(mover, original, true))
            {
                Logging.Write("[Gather] reached the hotspot after relocation " + attempt + "/" + MeshRelocate.Attempts);
                return true;
            }
            if (_stop || ClickAimAbort.Requested || RouteRunner.LastRunInterrupted) return false;
        }

        Logging.Write("[Gather] all " + MeshRelocate.Attempts + " alternate mesh locations were tried without reaching the hotspot");
        return false;
    }

    // No movement, turns, stop-key taps or extra clicks while a cast may be running.
    // A resource remaining in the pool can need another hit, or the attempt can have
    // failed; neither proves success. Bound attempts and revisit rather than spin forever.
    private static void GatherAtNode(IntPtr hwnd, GatherNodeScanner.Node node, Func<bool>? extraSafety = null)
    {
        int cursorRecoveries = 0;
        for (int attempt = 1; attempt <= MaxGatherAttempts && !_stop && !ClickAimAbort.Requested; attempt++)
        {
            bool fighting;
            if ((extraSafety != null && !extraSafety()) || !GrindRunner.TryReadGatherCombat(out fighting) || fighting)
            {
                _pending = node;
                return;
            }
            Logging.Write("[Gather] hover search attempt " + attempt + "/" + MaxGatherAttempts
                          + "; background mouse movement allowed, foreground interruptions retry automatically");
            if (!MapStillCurrent()) return;
            bool clicked = GatherInteraction.TryInteract(hwnd, node, candidate => MapStillCurrent() && Ready(candidate), extraSafety, out GatherNodeScanner.Node clickedNode,
                                                          out string reason, out bool hoverMiss, out bool cursorMoved);
            if (clicked && clickedNode.Guid != node.Guid)
            {
                Logging.Write("[Gather] hovered eligible nearby " + clickedNode.Name + " " + clickedNode.Guid
                              + " instead of " + node.Guid + "; deferring the original GUID for 30 seconds");
                Retry[node.Guid] = unchecked(Environment.TickCount + 30000);
                node = clickedNode;
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
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) { _pending = node; return; }
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
            // Even an unconfirmed attempt may coincide with a manual or unexpected cast.
            // Stay still for the entire window instead of scanning once and walking off.
            Logging.Write("[Gather] holding still for 6 seconds for a possible cast and auto-loot; success is not yet known");
            int started = Environment.TickCount;
            bool unknownLogged = false;
            while (true)
            {
                if (ClickAimAbort.Wait(250) || _stop) return;
                if (extraSafety != null && !extraSafety()) { Logging.Write("[Gather] Grind safety check interrupted the cast wait"); return; }
                if (!GrindRunner.TryReadGatherCombat(out fighting))
                {
                    if (!unknownLogged) Logging.Write("[Gather] combat state unreadable; continuing to hold still");
                    unknownLogged = true;
                    continue;
                }
                if (fighting)
                {
                    Logging.Write("[Gather] combat detected during cast wait; interrupting wait for defence");
                    _pending = node;
                    return;
                }
                if (unchecked(Environment.TickCount - started) >= GatherHoldMs) break;
            }
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
                int delay = clicked ? UnavailableRetryMs : InteractionRetryMs;
                Logging.Write("[Gather] " + node.Guid + " no longer observed after the wait; loot was not independently verified. "
                              + "The same GUID is deferred for " + (delay / 1000) + " seconds.");
                Retry[node.Guid] = unchecked(Environment.TickCount + delay);
                return;
            }
            if (!Ready(node)) return;
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
            GatherInteraction.ResetHoverHint();
            _pending = null;
            _stop = false;
            _lastScan = unchecked(Environment.TickCount - 1500);
            _lastSummary = unchecked(Environment.TickCount - 15000);
            _lastSafetySummary = unchecked(Environment.TickCount - 15000);
            Logging.Write("[Grind gather] enabled: Mining=" + GatherSettings.Mining + " skill=" + GatherSettings.MiningSkill +
                ", Herbalism=" + GatherSettings.Herbalism + " skill=" + GatherSettings.HerbalismSkill +
                "; resources within 25 yd, combat and nearby NPCs take priority. Hostility and 70707 node layout remain unverified.");
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

    // Returns true if a node was attempted, not whether it was successfully looted.
    internal static bool GatherForGrind(IntPtr hwnd, ClickAimMover mover, Func<bool> safe)
    {
        if (_stop || ClickAimAbort.Requested || !MapStillCurrent() ||
            !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting ||
            !FindNode(out GatherNodeScanner.Node node, GrindSearchYd)) return false;
        if (!safe())
        {
            if (unchecked(Environment.TickCount - _lastSafetySummary) >= 15000)
            {
                _lastSafetySummary = Environment.TickCount;
                Logging.Write("[Grind gather] a resource is in range, but combat, a nearby NPC, an eligible pull or unreadable safety state takes priority");
            }
            return false;
        }
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
                    if (!RouteRunner.LastRunInterrupted) Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs);
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
            if (!present) { Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs); return true; }
            if (!safe() || !ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6))
            { Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs); return true; }
            if (!safe() || _stop || ClickAimAbort.Requested) return true;
            GatherAtNode(hwnd, node, safe);
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
        try
        {
            IntPtr hwnd = ObjectManager.Wow?.WindowHandle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) { Logging.Write("[Gather] no game window"); return; }
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null || !reader.TryReadMapId(out _sessionMap) || ObjectManager.WowB != reader)
            { Logging.Write("[Gather] client map ID unreadable; refusing to guess a map"); return; }
            Logging.Write("[Gather] client map root reports map " + _sessionMap);
            if (roam != null && !roam.MapCurrent()) { Logging.Write("[Roam] map changed before start; stopping"); return; }
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
                { Logging.Write("[Gather] " + reader.ExperimentalBuildNumber + " action bar changed or became unreadable; stopping"); break; }
                if (!MapStillCurrent() || (roam != null && !roam.MapCurrent())) break;
                if (!GrindRunner.TryReadGatherCombat(out bool fighting))
                {
                    Logging.Write("[Gather] combat state unreadable; holding position");
                    ClickAimAbort.Wait(1500); continue;
                }
                if (fighting)
                {
                    fastHotspots = 0;
                    ClickAimKeySteer.ReleaseHeld();
                    try { mover.MoveStop(); } catch { }
                    string defence = GrindRunner.DefendGatherer(hwnd);
                    if (defence.Length > 0) Logging.Write("[Gather] defence: " + defence);
                    ClickAimAbort.Wait(1000); continue;
                }
                string rest = GrindRunner.RestForGather(hwnd);
                if (rest.Length > 0) { fastHotspots = 0; Logging.Write("[Gather] " + rest); }
                if (_stop || ClickAimAbort.Requested) break;
                // Buff only between gathering interactions and outside combat.
                if (GrindRunner.BuffForGather(hwnd, mover, () => _stop)) fastHotspots = 0;
                if (_stop || ClickAimAbort.Requested) break;
                // Re-check combat after a meal, buff or slow state read, before moving.
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) continue;
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
                    var destination = new RoutePoint { X = node.Position.X, Y = node.Position.Y, Z = node.Position.Z };
                    bool reached = Walk(mover, destination, false);
                    if (_stop || ClickAimAbort.Requested) break;
                    if (RouteRunner.LastRunInterrupted) { _pending = node; continue; }
                    if (!reached) { Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs); continue; }
                }
                ClickAimKeySteer.ReleaseHeld();
                try { mover.MoveStop(); } catch { }
                if (ClickAimAbort.Wait(400)) break;
                if (!GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> nearby)) continue;
                bool stillHere = false;
                foreach (var fresh in nearby) if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; stillHere = true; break; }
                if (!stillHere) { Logging.Write("[Gather] node not observed in reach; no click sent"); Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs); continue; }
                if (!GrindRunner.TryReadGatherCombat(out bool combatBeforeFace) || combatBeforeFace)
                {
                    _pending = node;
                    continue;
                }
                if (!ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6))
                {
                    Logging.Write("[Gather] could not face node; will retry it later");
                    Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                    continue;
                }
                if (_stop || ClickAimAbort.Requested) break;
                GatherAtNode(hwnd, node);

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

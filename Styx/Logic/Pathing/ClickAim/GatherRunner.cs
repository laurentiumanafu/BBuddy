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
    private static GatherNodeScanner.Node? _pending;
    private static uint _sessionMap;

    public static bool Running => _running;

    public static void Stop()
    {
        _stop = true;
        ClickAimAbort.Request();
        RouteRunner.StopRequested = true;
        GrindRunner.Stop();
    }

    public static bool Start(string profile)
    {
        if (_running) return false;
        List<RoutePoint> points;
        try
        {
            points = ProfileRoute.Load(profile, 0, false, false);
            LoadBlackspots(profile);
        }
        catch (Exception ex) { Logging.Write("[Gather] profile could not be read: " + ex.Message); return false; }
        if (points.Count == 0) { Logging.Write("[Gather] profile has no hotspots"); return false; }
        GatherSettings.Load();
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
            var thread = new Thread(() => Run(points)) { IsBackground = true, Name = "GatherRunner" };
            thread.Start();
            return true;
        }
        catch { _running = false; throw; }
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

    private static bool FindNode(out GatherNodeScanner.Node node)
    {
        node = default;
        bool scanOk = GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> nodes);
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
            else Logging.Write("[Gather] scan within 100 yd: " + nodes.Count + " recognized, " + eligible
                + " eligible, " + skillExcluded + " profession/skill/blacklist excluded, "
                + blackspotted + " profile-blackspot excluded, " + coolingDown + " on retry cooldown; "
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
            if (!MapStillCurrent()) return true;
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
                          + "; keep the real mouse still");
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
            if (cursorMoved)
            {
                // A moved desktop cursor is not a hover miss or a failed gather. Preserve
                // the six-second cast wait above, then require a quiet cursor before a
                // bounded retry with a fresh away transition and all usual validations.
                if (++cursorRecoveries <= 3 && WaitForQuietCursor(hwnd, extraSafety))
                {
                    Logging.Write("[Gather] cursor quiet; restarting the search without consuming a gather attempt");
                    attempt--;
                    continue;
                }
                if (!_stop && !ClickAimAbort.Requested)
                {
                    Retry[node.Guid] = unchecked(Environment.TickCount + InteractionRetryMs);
                    Logging.Write("[Gather] cursor recovery unavailable or limit reached; deferring this GUID for 20 seconds");
                }
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

    private static bool WaitForQuietCursor(IntPtr hwnd, Func<bool>? extraSafety)
    {
        Logging.Write("[Gather] waiting for the desktop cursor to remain still before retrying; source of movement is unknown");
        int started = Environment.TickCount;
        int quietSince = started;
        if (!ClickAimInput.TryCursorScreen(out int anchorX, out int anchorY)) return false;
        while (unchecked(Environment.TickCount - started) < 15000)
        {
            if (ClickAimAbort.Wait(250) || _stop || (extraSafety != null && !extraSafety())) return false;
            if (ObjectManager.Wow == null || ObjectManager.Wow.WindowHandle != hwnd || !MapStillCurrent()) return false;
            if (!GrindRunner.TryReadGatherCombat(out bool fighting) || fighting) return false;
            if (!ClickAimInput.TryCursorScreen(out int x, out int y)) return false;
            if (Math.Abs((long)x - anchorX) > 3 || Math.Abs((long)y - anchorY) > 3)
            {
                anchorX = x; anchorY = y; quietSince = Environment.TickCount;
            }
            if (unchecked(Environment.TickCount - quietSince) >= 1500) return true;
        }
        return false;
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
            LoadBlackspots(profile);
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
            Logging.Write("[Grind gather] enabled; configured professions only. Living NPCs within 100 yd block gathering because hostility is unknown.");
            return true;
        }
        catch (Exception ex) { Logging.Write("[Grind gather] disabled: " + ex.Message); return false; }
    }

    internal static bool HasGrindResource(Func<bool> safe)
    {
        if (_stop || ClickAimAbort.Requested || unchecked(Environment.TickCount - _lastScan) < 1500) return false;
        _lastScan = Environment.TickCount;
        return MapStillCurrent() && safe() && FindNode(out _);
    }

    // Returns true if a node was attempted, not whether it was successfully looted.
    internal static bool GatherForGrind(IntPtr hwnd, ClickAimMover mover, Func<bool> safe)
    {
        if (_stop || ClickAimAbort.Requested || !MapStillCurrent() || !safe() || !FindNode(out GatherNodeScanner.Node node)) return false;
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

    private static void Run(List<RoutePoint> points)
    {
        var mover = new ClickAimMover { QuietKeyStop = true };
        int index = 0, failures = 0, fastHotspots = 0;
        try
        {
            IntPtr hwnd = ObjectManager.Wow?.WindowHandle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) { Logging.Write("[Gather] no game window"); return; }
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null || !reader.TryReadMapId(out _sessionMap) || ObjectManager.WowB != reader)
            { Logging.Write("[Gather] client map ID unreadable; refusing to guess a map"); return; }
            Logging.Write("[Gather] client map root reports map " + _sessionMap);
            Logging.Write("[Gather] experimental ground gatherer: " + points.Count + " hotspots; Mining="
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
            while (!_stop && !ClickAimAbort.Requested)
            {
                if (!MapStillCurrent()) break;
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
                // Re-check combat after a meal or a slow state read, before moving.
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) continue;
                GatherNodeScanner.Node node;
                if (_pending.HasValue) { node = _pending.Value; _pending = null; }
                else if (!FindNode(out node))
                {
                    RoutePoint next = points[index % points.Count];
                    if (IsBlackspot(next.X, next.Y))
                    {
                        Logging.Write("[Gather] hotspot lies inside a profile blackspot; skipping it");
                        index = (index + 1) % points.Count;
                        fastHotspots = 0;
                        ClickAimAbort.Wait(1000);
                        continue;
                    }
                    int startedHotspot = Environment.TickCount;
                    bool walked = Walk(mover, next, true);
                    if (_stop || ClickAimAbort.Requested) break;
                    if (RouteRunner.LastRunInterrupted) { fastHotspots = 0; continue; }
                    if (walked)
                    {
                        index = (index + 1) % points.Count;
                        failures = 0;
                        if (unchecked(Environment.TickCount - startedHotspot) < FastHotspotMs)
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
        }
        catch (Exception ex) { Logging.Write("[Gather] stopped on error: " + ex.Message); }
        finally
        {
            RouteRunner.RequireMeshDuringSession = false;
            RouteRunner.PollInterruptDuringWalk = false;
            RouteRunner.ShouldInterrupt = null;
            ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            _running = false;
            Logging.Write("[Gather] stopped");
        }
    }
}

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
    private static volatile bool _running;
    private static volatile bool _stop;
    private static readonly Dictionary<WowBGuid, int> Retry = new Dictionary<WowBGuid, int>();
    private static readonly List<(double X, double Y, double Z, double Radius)> Blackspots = new List<(double, double, double, double)>();
    private static int _lastScan;
    private static GatherNodeScanner.Node? _pending;

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
        _pending = null;
        _lastScan = unchecked(Environment.TickCount - 1500);
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
        if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> nodes)) return false;
        foreach (GatherNodeScanner.Node candidate in nodes)
            if (Ready(candidate)) { node = candidate; return true; }
        return false;
    }

    // Called by RouteRunner between mesh points. Never clears a stop.
    private static bool Interrupt(bool checkNodes)
    {
        if (_stop || ClickAimAbort.Requested) return true;
        if (!GrindRunner.TryReadGatherCombat(out bool fighting) || fighting) return true;
        if (!checkNodes || unchecked(Environment.TickCount - _lastScan) < 1500) return false;
        _lastScan = Environment.TickCount;
        if (!FindNode(out GatherNodeScanner.Node node)) return false;
        _pending = node;
        return true;
    }

    private static bool Walk(ClickAimMover mover, RoutePoint point, bool checkNodes)
    {
        if (_stop || ClickAimAbort.Requested) return false;
        if (!RouteRunner.MeshReady()) { Logging.Write("[Gather] mesh navigation lost; refusing the leg"); return false; }
        RouteRunner.ShouldInterrupt = () => Interrupt(checkNodes);
        try { return RouteRunner.RunWithinSession(mover, new List<RoutePoint> { point }, 1); }
        finally { RouteRunner.ShouldInterrupt = null; }
    }

    private static void Run(List<RoutePoint> points)
    {
        var mover = new ClickAimMover();
        int index = 0, failures = 0;
        try
        {
            IntPtr hwnd = ObjectManager.Wow?.WindowHandle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) { Logging.Write("[Gather] no game window"); return; }
            Logging.Write("[Gather] experimental ground gatherer: " + points.Count + " hotspots; Mining="
                + GatherSettings.Mining + " skill=" + GatherSettings.MiningSkill + "; Herbalism="
                + GatherSettings.Herbalism + " skill=" + GatherSettings.HerbalismSkill
                + "; these skills are manually configured, not read from the client");
            foreach (var spot in Blackspots)
            {
                if (_stop || ClickAimAbort.Requested) break;
                MeshBlackspots.Mark(Navigator.TripperNavigator, ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, spot.X, spot.Y),
                                    spot.X, spot.Y, spot.Z, (float)spot.Radius);
            }
            while (!_stop && !ClickAimAbort.Requested)
            {
                if (!GrindRunner.TryReadGatherCombat(out bool fighting))
                {
                    Logging.Write("[Gather] combat state unreadable; holding position");
                    ClickAimAbort.Wait(1500); continue;
                }
                if (fighting)
                {
                    ClickAimKeySteer.ReleaseHeld();
                    try { mover.MoveStop(); } catch { }
                    Logging.Write("[Gather] combat flag set: holding position. Attacker identity is not known; defend manually.");
                    ClickAimAbort.Wait(1500); continue;
                }
                GatherNodeScanner.Node node;
                if (_pending.HasValue) { node = _pending.Value; _pending = null; }
                else if (!FindNode(out node))
                {
                    RoutePoint next = points[index % points.Count];
                    if (IsBlackspot(next.X, next.Y))
                    {
                        Logging.Write("[Gather] hotspot lies inside a profile blackspot; skipping it");
                        index++; ClickAimAbort.Wait(1000); continue;
                    }
                    bool walked = Walk(mover, next, true);
                    if (_stop || ClickAimAbort.Requested) break;
                    if (RouteRunner.LastRunInterrupted) continue;
                    if (walked) { index++; failures = 0; ClickAimAbort.Wait(500); }
                    else if (++failures >= 3) { Logging.Write("[Gather] three mesh leg failures; stopping"); break; }
                    continue;
                }
                if (!Ready(node)) continue;
                // An interrupted leg may have left a stale observation; refresh position and range.
                if (!GatherNodeScanner.TryCapture(SearchYd, out List<GatherNodeScanner.Node> current)) continue;
                bool observed = false;
                foreach (var fresh in current) if (fresh.Guid == node.Guid && Ready(fresh)) { node = fresh; observed = true; break; }
                if (!observed) continue;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Gather] {0} ({1:F1} yd, id {2}) - routing toward the resource", node.Name, node.Distance, node.Entry));
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
                if (!ClickAimKeySteer.Face(node.Position.X, node.Position.Y, hwnd, 20.0, 6)) { Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs); continue; }
                if (_stop || ClickAimAbort.Requested) break;
                bool clicked = GatherInteraction.TryInteract(hwnd, node);
                if (!clicked) Logging.Write("[Gather] could not confirm the node under the cursor; no right-click was sent");
                else Logging.Write("[Gather] one confirmed-node right-click sent; watching for disappearance (not proof of loot)");
                if (clicked)
                {
                    for (int i = 0; i < 20 && !_stop && !ClickAimAbort.Requested; i++)
                    {
                        if (ClickAimAbort.Wait(600)) break;
                        if (!GrindRunner.TryReadGatherCombat(out bool attacked) || attacked) break;
                        if (GatherNodeScanner.TryCapture(InteractYd, out List<GatherNodeScanner.Node> after))
                        {
                            bool present = false;
                            foreach (var seen in after) if (seen.Guid == node.Guid) { present = true; break; }
                            if (!present) { Logging.Write("[Gather] node no longer observed; loot was not independently verified"); break; }
                        }
                    }
                }
                // Failed/unknown or still-present nodes must not pin the route. Revisit later.
                Retry[node.Guid] = unchecked(Environment.TickCount + RetryMs);
            }
        }
        catch (Exception ex) { Logging.Write("[Gather] stopped on error: " + ex.Message); }
        finally
        {
            RouteRunner.ShouldInterrupt = null;
            ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            _running = false;
            Logging.Write("[Gather] stopped");
        }
    }
}

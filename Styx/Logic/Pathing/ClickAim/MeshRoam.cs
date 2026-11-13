using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Profile-free local destinations. Map ID is not zone ID; the client zone/area
/// offsets are not verified. This bounds destination selection, not all combat or
/// resource detours, and makes no claim about crossing a zone boundary.
/// </summary>
internal sealed class MeshRoam
{
    private const float StepRadiusYd = 65f;
    private const int CandidateCount = 20;
    private const int RecentLimit = 6;
    private readonly WowBReader _reader;
    private readonly uint _map;
    private readonly Vector3 _anchor;
    private readonly double _radius;
    private readonly List<Vector3> _recent = new List<Vector3>();

    private MeshRoam(WowBReader reader, uint map, Vector3 anchor, double radius)
    { _reader = reader; _map = map; _anchor = anchor; _radius = radius; }

    internal static bool WithinAnchor(double x, double y, double anchorX, double anchorY, double radius)
        => double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(anchorX) && double.IsFinite(anchorY)
           && double.IsFinite(radius) && radius >= 100 && radius <= 3000
           && ClickAimMath.Len(x - anchorX, y - anchorY) <= radius;

    // Area 11 is ground in the verified Trinity v16 single-tile format, but
    // an Alliance portal in the legacy HB enum. Never classify by byte alone.
    internal static bool SafeDestinationArea(uint map, Vector3 position, byte area, Func<int, int, bool> tileLoaded)
        => MmapGroundPolicy.IsGround(map, position, area, tileLoaded);

    internal static string SelfTest()
    {
        bool ok = WithinAnchor(0, 0, 0, 0, 250) && WithinAnchor(250, 0, 0, 0, 250)
               && !WithinAnchor(251, 0, 0, 0, 250) && WithinAnchor(251, 0, 0, 0, 1000)
               && !WithinAnchor(double.NaN, 0, 0, 0, 250) && !WithinAnchor(0, 0, 0, 0, 0)
               && MmapGroundPolicy.LegacyGround(0) && MmapGroundPolicy.LegacyGround(1) && MmapGroundPolicy.LegacyGround(4)
               && !MmapGroundPolicy.LegacyGround(2) && !MmapGroundPolicy.LegacyGround(3) && !MmapGroundPolicy.LegacyGround(17);
        return "[Roam] destination-bound fixtures: " + (ok ? "pass" : "FAIL") + " (not a live mesh test)";
    }

    internal static bool TryCreate(out MeshRoam? roam)
    {
        roam = null;
        try
        {
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null || !reader.TryReadMapId(out uint map) || ObjectManager.WowB != reader
                || !ClickAimPositions.TryRead(out double x, out double y))
            { Logging.Write("[Roam] current map or player position unavailable; cannot start"); return false; }
            double z = ClickAimPositions.Z();
            if (!double.IsFinite(z) || !RouteRunner.MeshReady())
            { Logging.Write("[Roam] player height or navigation mesh unavailable; cannot start"); return false; }
            var nav = Navigator.TripperNavigator;
            var start = new Vector3((float)x, (float)y, (float)z);
            nav.EnsureTilesAroundPosition(map, start, Navigator.LoadTilesAroundRadius);
            bool found = nav.FindNearestPolyRef(map, start, 2.5f, 5f, out Tripper.Navigation.PolygonReference poly, out Vector3 snap);
            bool sameMap = reader.TryReadMapId(out uint again) && again == map && ObjectManager.WowB == reader;
            if (!found || !poly.IsValid || !float.IsFinite(snap.X) || !float.IsFinite(snap.Y) || !float.IsFinite(snap.Z)
                || ClickAimMath.Len(snap.X - x, snap.Y - y) > 2.5 || Math.Abs(snap.Z - z) > 5 || !sameMap)
            {
                Logging.Write("[Roam] player is not on a verified mesh layer, or map changed; cannot start."
                    + " nearest polygon found=" + found + ", valid=" + poly.IsValid + ", map stable=" + sameMap);
                if (sameMap) ClickAimDiagnostics.ReportMeshTileDiagnostics(map, start, nav, "roam start refused; no movement authorized");
                return false;
            }
            int radius = GatherSettings.RoamRadiusYd;
            if (radius < 100 || radius > 3000) { Logging.Write("[Roam] invalid roaming radius"); return false; }
            roam = new MeshRoam(reader, map, snap, radius);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Roam] map {0}, starting at ({1:F1}, {2:F1}, {3:F1}); random destinations within {4:F0} yd of start. "
                + "Zone borders are unknown; combat, resource and route detours can go beyond this radius.",
                map, snap.X, snap.Y, snap.Z, radius));
            return true;
        }
        catch (Exception ex) { Logging.Write("[Roam] cannot initialize mesh: " + ex.Message); return false; }
    }

    internal bool MapCurrent()
        => ObjectManager.WowB == _reader && _reader.TryReadMapId(out uint map) && map == _map;

    internal bool TryNext(out RoutePoint point)
    {
        point = new RoutePoint();
        if (ClickAimAbort.Requested || !MapCurrent() || !ClickAimPositions.TryRead(out double x, out double y)) return false;
        double z = ClickAimPositions.Z();
        if (!double.IsFinite(z)) return false;
        try
        {
            var nav = Navigator.TripperNavigator;
            if (!nav.IsLoaded) { Logging.Write("[Roam] sampling unavailable: navigation DLL not loaded"); return false; }
            var at = new Vector3((float)x, (float)y, (float)z);
            nav.EnsureTilesAroundPosition(_map, at, Navigator.LoadTilesAroundRadius);
            if (!nav.FindNearestPolyRef(_map, at, 2.5f, 5f, out Tripper.Navigation.PolygonReference current, out Vector3 from)
                || !current.IsValid || !float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(from.Z)
                || ClickAimMath.Len(from.X - x, from.Y - y) > 2.5 || Math.Abs(from.Z - z) > 5)
            { Logging.Write("[Roam] sampling unavailable: player polygon, snap or layer not verified"); return false; }
            int random = 0, bounds = 0, distanceRejected = 0, recent = 0, snapRejected = 0, areaQuery = 0, areaRejected = 0;
            var rejectedAreas = new SortedDictionary<byte, int>();
            for (int i = 0; i < CandidateCount && !ClickAimAbort.Requested; i++)
            {
                if (!MapCurrent()) return false;
                if (!nav.FindRandomPoint(_map, from, StepRadiusYd, out Vector3 next)) { random++; continue; }
                if (!float.IsFinite(next.X) || !float.IsFinite(next.Y) || !float.IsFinite(next.Z)
                    || !WithinAnchor(next.X, next.Y, _anchor.X, _anchor.Y, _radius)) { bounds++; continue; }
                double distance = ClickAimMath.Len(next.X - x, next.Y - y);
                if (distance < (i < 15 ? 15.0 : 6.0) || distance > StepRadiusYd + 2.0) { distanceRejected++; continue; }
                if (i < 15)
                {
                    bool seen = false;
                    foreach (var old in _recent)
                        if (ClickAimMath.Len(next.X - old.X, next.Y - old.Y) < 15.0) { seen = true; break; }
                    if (seen) { recent++; continue; }
                }
                if (!nav.FindNearestPolyRef(_map, next, 0.5f, 2f, out Tripper.Navigation.PolygonReference end, out Vector3 endSnap)
                    || !end.IsValid || !float.IsFinite(endSnap.X) || !float.IsFinite(endSnap.Y) || !float.IsFinite(endSnap.Z)
                    || !WithinAnchor(endSnap.X, endSnap.Y, _anchor.X, _anchor.Y, _radius)
                    || ClickAimMath.Len(endSnap.X - next.X, endSnap.Y - next.Y) > 0.5
                    || Math.Abs(endSnap.Z - next.Z) > 2.0) { snapRejected++; continue; }
                if ((nav.GetPolyArea(_map, end, out byte area) & 0x80000000U) != 0) { areaQuery++; continue; }
                if (!SafeDestinationArea(_map, endSnap, area, (tx, ty) => nav.IsTileLoaded(_map, tx, ty)))
                {
                    areaRejected++;
                    rejectedAreas.TryGetValue(area, out int count);
                    rejectedAreas[area] = count + 1;
                    continue;
                }
                if (ClickAimAbort.Requested || !MapCurrent()) return false;
                point = new RoutePoint { X = endSnap.X, Y = endSnap.Y, Z = endSnap.Z, Kind = "Roam" };
                _recent.Add(endSnap);
                if (_recent.Count > RecentLimit) _recent.RemoveAt(0);
                return true;
            }
            if (!ClickAimAbort.Requested && MapCurrent())
            {
                var areas = new List<string>();
                foreach (var pair in rejectedAreas) areas.Add(pair.Key.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture));
                Logging.Write("[Roam] sampling rejections (up to " + CandidateCount + "): random=" + random + ", invalid/outside anchor=" + bounds +
                    ", distance=" + distanceRejected + ", recent=" + recent + ", snap/layer=" + snapRejected +
                    ", area query=" + areaQuery + ", ground/format=" + areaRejected +
                    ", rejected area IDs=" + (areas.Count == 0 ? "none" : string.Join(",", areas)) +
                    "; no movement authorized without a verified destination");
            }
        }
        catch (Exception ex) { Logging.Write("[Roam] mesh sampling failed: " + ex.Message); }
        return false;
    }

}

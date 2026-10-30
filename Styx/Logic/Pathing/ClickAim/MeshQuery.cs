using System;
using System.Globalization;
using System.Numerics;
using Styx.Helpers;
using Tripper.Navigation;

namespace Styx.Logic.Pathing;

/// <summary>
/// Small queries against the navmesh that answer "is the mesh where I think it is?".
///
/// The one that matters here is <see cref="Snap"/>. A path request carries a Z, and
/// that Z comes from the reader on the 64-bit client - a value that can be a jump, a
/// slope, a swimming state or a frame late. Detour's own start search only looks
/// ±20 yd vertically, so a Z that is merely wrong is indistinguishable from a map
/// with no mesh: `findNearestPoly` returns nothing and the path comes back
/// `Failed`. Snapping with a deliberately tall box first tells the two apart, and
/// the amount it had to move is the number that answers the question.
/// </summary>
internal static class MeshQuery
{
    // The two extents are passed as separate numbers to Navigator.FindNearestPolyRef:
    // the DLL swizzles the XYZ it receives {Y, Z, X} before handing it to Detour, so
    // the vertical extent has to travel in the Z slot. Sending Vector3(5, 400, 5)
    // meant "400 yd wide and 5 yd tall", which is why a goal 50 yd above the height
    // in the request found nothing on 2026-10-29.

    /// <summary>Horizontal half-extent of the snap box: wants the ground under the feet.</summary>
    private const float SnapHorizontal = 5.0f;

    /// <summary>
    /// Vertical half-extent of the snap box. Taller than Detour's own 20 yd so the
    /// correction is measured rather than being limited by the search.
    /// </summary>
    private const float SnapVertical = 40.0f;

    /// <summary>A correction larger than this means the Z handed to Detour was wrong.</summary>
    private const float SuspiciousCorrection = 8.0f;

    /// <summary>
    /// Vertical half-extent of the second, deliberately absurd search. Used only
    /// when the tight box found nothing, which is what a goal written as bare
    /// <c>x,y</c> produces: the request carries Z = 0, and in most of the world the
    /// ground is nowhere near zero. A 6173 yd walk was refused on 2026-10-29 for
    /// exactly this reason - the goal was fine, its Z was a placeholder.
    /// </summary>
    private const float WideVertical = 400.0f;

    /// <summary>
    /// How far below or above the point a widened search may land before its answer is refused.
    ///
    /// The 22:47 run is the reason: the player stood at Z 386 and the 400 yd search "found the
    /// mesh at 3.5" - a polygon 383 yd below, i.e. a different layer of the world (or a tile that
    /// belongs to another map). Detour was then asked to route between two points that are not
    /// connected, returned its 2-point failure, and the leg fell back to a straight click. A
    /// column that only has ground 383 yd away has nothing usable in it, and saying so is more
    /// honest than handing a wrong-layer point to the router.
    /// </summary>
    private const float MaxWidenedGap = 100.0f;

    /// <summary>
    /// Moves the point onto the nearest walkable polygon and reports how far it had
    /// to move. Returns false when there is no polygon in either box, which is the
    /// "the mesh really is missing here" case.
    ///
    /// <paramref name="widened"/> says the point was only found after the vertical
    /// search was opened up - i.e. whoever asked did not know the height.
    /// </summary>
    public static bool Snap(Tripper.Navigation.Navigator nav, uint mapId, Vector3 point,
                            out Vector3 snapped, out float moved, out bool widened)
    {
        snapped = point;
        moved = 0f;
        widened = false;
        if (nav == null) return false;

        PolygonReference poly;
        Vector3 nearest;
        bool found = TryFind(nav, mapId, point, SnapHorizontal, SnapVertical, out poly, out nearest);
        if (!found)
        {
            Vector3 wideNearest;
            PolygonReference widePoly;
            found = TryFind(nav, mapId, point, SnapHorizontal, WideVertical, out widePoly, out wideNearest);
            if (!found) return false;

            float gap = Math.Abs(wideNearest.Z - point.Z);
            if (gap > MaxWidenedGap)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[MeshQuery] the only polygon within {0:F0} yd of ({1:F1}, {2:F1}, {3:F1}) is "
                    + "{4:F0} yd away in height (at Z {5:F1}) - that is a different layer, so it is "
                    + "refused and the caller falls back to aim-and-click",
                    SnapHorizontal, point.X, point.Y, point.Z, gap, wideNearest.Z));
                return false;
            }

            poly = widePoly;
            nearest = wideNearest;
            widened = true;
        }

        snapped = nearest;
        float dx = nearest.X - point.X;
        float dy = nearest.Y - point.Y;
        float dz = nearest.Z - point.Z;
        moved = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return true;
    }

    private static bool TryFind(Tripper.Navigation.Navigator nav, uint mapId, Vector3 point,
                                float horizontal, float vertical,
                                out PolygonReference poly, out Vector3 nearest)
    {
        poly = PolygonReference.Invalid;
        nearest = point;
        try { return nav.FindNearestPolyRef(mapId, point, horizontal, vertical, out poly, out nearest); }
        catch (Exception ex)
        {
            Logging.Write("[MeshQuery] snap failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Snap with a log line, for the callers that want the numbers in the log.
    /// <paramref name="what"/> names the point ("player", "goal") so the line reads.
    /// </summary>
    public static bool SnapLogged(Tripper.Navigation.Navigator nav, uint mapId, Vector3 point,
                                  string what, out Vector3 snapped)
    {
        float moved;
        bool widened;
        if (!Snap(nav, mapId, point, out snapped, out moved, out widened))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[MeshQuery] no polygon within {0:F0} yd of the {1} at ({2:F1}, {3:F1}, {4:F1}), "
                + "even after searching {5:F0} yd up and down - the mesh has nothing in that column",
                SnapHorizontal, what, point.X, point.Y, point.Z, WideVertical));
            return false;
        }

        if (widened)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[MeshQuery] the {0} had no usable height (given {1:F1}); searched {2:F0} yd and "
                + "found the mesh at {3:F1} - using ({4:F1}, {5:F1}, {6:F1})",
                what, point.Z, WideVertical, snapped.Z, snapped.X, snapped.Y, snapped.Z));
        }

        if (!widened && moved >= SuspiciousCorrection)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[MeshQuery] the {0} at ({1:F1}, {2:F1}, {3:F1}) is {4:F1} yd off the mesh; using "
                + "({5:F1}, {6:F1}, {7:F1}) - a correction this large usually means the Z the reader "
                + "gave is wrong, not that the mesh is", what,
                point.X, point.Y, point.Z, moved, snapped.X, snapped.Y, snapped.Z));
        }

        return true;
    }
}

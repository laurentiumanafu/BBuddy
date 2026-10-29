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
    /// Moves the point onto the nearest walkable polygon and reports how far it had
    /// to move. Returns false when there is no polygon in the box at all, which is
    /// the "the mesh really is missing here" case.
    /// </summary>
    public static bool Snap(Tripper.Navigation.Navigator nav, uint mapId, Vector3 point,
                            out Vector3 snapped, out float moved)
    {
        snapped = point;
        moved = 0f;
        if (nav == null) return false;

        PolygonReference poly;
        Vector3 nearest;
        bool found;
        try
        {
            found = nav.FindNearestPolyRef(mapId, point,
                new Vector3(SnapHorizontal, SnapVertical, SnapHorizontal), out poly, out nearest);
        }
        catch (Exception ex)
        {
            Logging.Write("[MeshQuery] snap failed: " + ex.Message);
            return false;
        }

        if (!found) return false;

        snapped = nearest;
        float dx = nearest.X - point.X;
        float dy = nearest.Y - point.Y;
        float dz = nearest.Z - point.Z;
        moved = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return true;
    }

    /// <summary>
    /// Snap with a log line, for the callers that want the numbers in the log.
    /// <paramref name="what"/> names the point ("player", "goal") so the line reads.
    /// </summary>
    public static bool SnapLogged(Tripper.Navigation.Navigator nav, uint mapId, Vector3 point,
                                  string what, out Vector3 snapped)
    {
        float moved;
        if (!Snap(nav, mapId, point, out snapped, out moved))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[MeshQuery] no polygon within {0:F0} yd of the {1} at ({2:F1}, {3:F1}, {4:F1}) - "
                + "the mesh has nothing there", SnapHorizontal, what, point.X, point.Y, point.Z));
            return false;
        }

        if (moved >= SuspiciousCorrection)
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

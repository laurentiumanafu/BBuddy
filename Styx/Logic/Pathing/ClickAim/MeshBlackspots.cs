using System;
using System.Globalization;
using System.Numerics;
using Styx.Helpers;
using Tripper.Navigation;

namespace Styx.Logic.Pathing;

/// <summary>
/// Marks pieces of the navmesh as expensive so the next path goes around them.
///
/// The mmaps describe terrain and building interiors. They do not describe a tree
/// trunk, a fence post, a cart or a player standing in a doorway - and those are
/// exactly what a click-walk runs into. When the mover reports that it cannot get
/// past something, the honest response is not to click harder: it is to tell the
/// pathfinder that this piece of ground is bad and ask for another route.
///
/// The DLL already has the vocabulary for this. <c>NavTerrain::NAV_BLACKSPOT</c> is
/// area 17 and <c>PathFinder::createFilter</c> gives it a cost of 60, so a polygon
/// re-typed to 17 costs sixty times more to cross than ground. Detour then prefers
/// any other route, but will still use it if it is the only way - which is the
/// behaviour wanted: a character trapped in a corner must be able to escape the
/// blackspot it is standing in.
///
/// Marks are per process and per tile: they live in the loaded <c>dtNavMesh</c>, so
/// they disappear when the tile is unloaded (one idle minute) and come back clean.
/// </summary>
internal static class MeshBlackspots
{
    /// <summary>Small enough to hug a tree, large enough that a route can pass.</summary>
    private const float DefaultRadius = 6.0f;

    /// <summary>Vertical half-extent of the search box: a blackspot is a column.</summary>
    private const float VerticalExtent = 25.0f;

    private const int MaxPolys = 512;
    private const uint FailureBit = 0x80000000u;

    /// <summary>How many places have been marked since the process started.</summary>
    public static int Marks { get; private set; }

    /// <summary>
    /// Marks every polygon within <paramref name="radius"/> of a world position.
    /// Returns how many polygons changed area; 0 means the position is not on the
    /// mesh (nothing to mark) or the engine refused.
    /// </summary>
    public static int Mark(Tripper.Navigation.Navigator nav, uint mapId, double x, double y, double z, float radius)
    {
        if (nav == null) return 0;
        if (radius <= 0f) radius = DefaultRadius;

        var center = new Vector3((float)x, (float)y, (float)z);
        var extents = new Vector3(radius, VerticalExtent, radius);

        PolygonReference[] polys;
        try { polys = nav.QueryPolygons(mapId, center, extents, MaxPolys); }
        catch (Exception ex)
        {
            Logging.Write("[Blackspot] query failed: " + ex.Message);
            return 0;
        }

        int marked = 0;
        foreach (PolygonReference poly in polys)
        {
            if (!poly.IsValid) continue;
            try
            {
                byte current;
                if ((nav.GetPolyArea(mapId, poly, out current) & FailureBit) == 0 &&
                    current == (byte)Tripper.Navigation.AreaType.Blackspot)
                    continue;                       // already marked, not a new fact

                if ((nav.SetPolyArea(mapId, poly, (byte)Tripper.Navigation.AreaType.Blackspot) & FailureBit) == 0)
                    marked++;
            }
            catch (Exception ex)
            {
                Logging.Write("[Blackspot] SetPolyArea failed: " + ex.Message);
                break;
            }
        }

        if (marked > 0)
        {
            Marks++;
            string line = string.Format(CultureInfo.InvariantCulture,
                "[Blackspot] marked {0} polygons within {1:F0} yd of ({2:F1}, {3:F1}, {4:F1}) - "
                + "the next path will go around it", marked, radius, x, y, z);
            Logging.Write(line);
        }
        else
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Blackspot] nothing to mark near ({0:F1}, {1:F1}): the stuck point is not on "
                + "the mesh, so the obstacle is either under an overhang or the mesh there is "
                + "missing", x, y, z));
        }

        return marked;
    }
}

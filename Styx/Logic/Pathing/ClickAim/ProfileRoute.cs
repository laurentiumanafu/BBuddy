using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace Styx.Logic.Pathing;

/// <summary>One waypoint taken from a profile.</summary>
public sealed class RoutePoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    /// <summary>Element it came from: Hotspot, RunTo, Vendor, Mailbox.</summary>
    public string Kind { get; set; } = "Hotspot";

    /// <summary>Quest or container it belongs to, for the log.</summary>
    public string Label { get; set; } = "";

    public WoWPoint ToWoWPoint()
    {
        return new WoWPoint((float)X, (float)Y, (float)Z);
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} ({1:F1}, {2:F1}, {3:F1}){4}",
            Kind, X, Y, Z, string.IsNullOrEmpty(Label) ? "" : " [" + Label + "]");
    }
}

/// <summary>
/// Reads the movement part of a CopilotBuddy / HonorBuddy profile.
///
/// A profile is not a path. What it contains is a set of point lists: each quest
/// objective has its own <c>&lt;Hotspots&gt;</c> cluster, and long-distance moves are
/// <c>&lt;RunTo X Y Z&gt;</c> elements. Those points are exactly the waypoints a
/// mesh-driven run would route between, which is why this is the useful half of
/// "load a profile and walk it" - the routing between them is the mover's job.
///
/// Nothing here needs the object model, a bot, or a session: it is XML.
/// </summary>
public static class ProfileRoute
{
    /// <summary>
    /// Longest leg a synthesised right-click can be trusted with. Beyond this one
    /// click would have to be aimed perfectly across terrain it cannot see, and the
    /// field tool measured its useful range at ~100 yd.
    /// </summary>
    public const double MaxClickWalkYd = 100.0;

    /// <summary>
    /// Parses a profile into an ordered waypoint list (document order).
    /// <paramref name="questId"/> 0 means "every quest in the file".
    /// </summary>
    public static List<RoutePoint> Load(string path, int questId = 0, bool includeRunTo = true,
                                        bool includeServices = false)
    {
        var points = new List<RoutePoint>();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("No profile path given", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Profile not found", path);

        XDocument doc = XDocument.Load(path);
        string wanted = questId.ToString(CultureInfo.InvariantCulture);

        foreach (XElement element in doc.Descendants())
        {
            string name = element.Name.LocalName;
            bool isPoint = name == "Hotspot" || name == "RunTo"
                        || (includeServices && (name == "Vendor" || name == "Mailbox"));
            if (!isPoint) continue;
            if (name == "RunTo" && !includeRunTo) continue;

            XElement? quest = FindQuestAncestor(element);
            if (questId != 0)
            {
                if (quest == null) continue;
                string? id = (string?)quest.Attribute("Id");
                if (id != wanted) continue;
            }

            double x, y, z;
            if (!TryReadXyz(element, out x, out y, out z)) continue;

            var point = new RoutePoint
            {
                X = x,
                Y = y,
                Z = z,
                Kind = name,
                Label = quest == null
                    ? ((string?)doc.Root?.Element("Name") ?? "")
                    : (((string?)quest.Attribute("Id") ?? "") + " " + ((string?)quest.Attribute("Name") ?? "")).Trim()
            };

            // A profile often repeats the same point in adjacent objectives.
            if (points.Count > 0)
            {
                RoutePoint last = points[points.Count - 1];
                if (ClickAimMath.Len(point.X - last.X, point.Y - last.Y) < 0.5) continue;
            }
            points.Add(point);
        }

        return points;
    }

    private static XElement? FindQuestAncestor(XElement element)
    {
        foreach (XElement ancestor in element.Ancestors())
        {
            if (ancestor.Name.LocalName == "Quest") return ancestor;
        }
        return null;
    }

    private static bool TryReadXyz(XElement element, out double x, out double y, out double z)
    {
        x = y = z = 0.0;
        if (!TryReadAttribute(element, "X", out x)) return false;
        if (!TryReadAttribute(element, "Y", out y)) return false;
        // Z is optional: a point without it is still walkable in X/Y.
        TryReadAttribute(element, "Z", out z);
        return Math.Abs(x) < 100000.0 && Math.Abs(y) < 100000.0;
    }

    private static bool TryReadAttribute(XElement element, string name, out double value)
    {
        value = 0.0;
        XAttribute? attribute = element.Attribute(name);
        if (attribute == null)
        {
            foreach (XAttribute candidate in element.Attributes())
            {
                if (string.Equals(candidate.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    attribute = candidate;
                    break;
                }
            }
        }
        if (attribute == null) return false;
        return double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Index of the waypoint closest to a position, or -1 for an empty route.</summary>
    public static int NearestIndex(IList<RoutePoint> route, double x, double y)
    {
        if (route == null || route.Count == 0) return -1;
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < route.Count; i++)
        {
            double d = ClickAimMath.Len(route[i].X - x, route[i].Y - y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Splits a flat route into walkable clusters: a run of waypoints where every
    /// consecutive gap is short enough to be a normal grind area. A profile's
    /// &lt;RunTo&gt; elements jump across continents (one measured hop here is 17,705 yd),
    /// so treating the whole file as one chain would be nonsense.
    /// </summary>
    public static List<List<RoutePoint>> Clusters(IList<RoutePoint> route, double maxGapYd = 150.0)
    {
        var clusters = new List<List<RoutePoint>>();
        if (route == null || route.Count == 0) return clusters;

        var current = new List<RoutePoint> { route[0] };
        for (int i = 1; i < route.Count; i++)
        {
            RoutePoint previous = current[current.Count - 1];
            double gap = ClickAimMath.Len(route[i].X - previous.X, route[i].Y - previous.Y);
            if (gap > maxGapYd)
            {
                clusters.Add(current);
                current = new List<RoutePoint>();
            }
            current.Add(route[i]);
        }
        clusters.Add(current);
        return clusters;
    }

    /// <summary>
    /// The cluster closest to a position - the stand-in for "where in this profile am
    /// I", which needs a real map lookup that this client does not have yet.
    /// </summary>
    public static List<RoutePoint> NearestCluster(IList<RoutePoint> route, double x, double y,
                                                  out int clusterIndex, out int clusterCount,
                                                  double maxGapYd = 150.0)
    {
        List<List<RoutePoint>> clusters = Clusters(route, maxGapYd);
        clusterCount = clusters.Count;
        clusterIndex = -1;
        List<RoutePoint>? best = null;
        double bestDistance = double.MaxValue;

        for (int c = 0; c < clusters.Count; c++)
        {
            List<RoutePoint> cluster = clusters[c];
            for (int i = 0; i < cluster.Count; i++)
            {
                double d = ClickAimMath.Len(cluster[i].X - x, cluster[i].Y - y);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = cluster;
                    clusterIndex = c;
                }
            }
        }

        return best ?? new List<RoutePoint>();
    }

    /// <summary>One-line summary for the log: how much walking this profile actually implies.</summary>
    public static string Describe(IList<RoutePoint> route)
    {
        if (route == null || route.Count == 0) return "no waypoints";

        int hotspots = 0, runTo = 0, other = 0;
        double length = 0.0, longest = 0.0, minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < route.Count; i++)
        {
            RoutePoint p = route[i];
            if (p.Kind == "Hotspot") hotspots++;
            else if (p.Kind == "RunTo") runTo++;
            else other++;

            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;

            if (i == 0) continue;
            double d = ClickAimMath.Len(p.X - route[i - 1].X, p.Y - route[i - 1].Y);
            length += d;
            if (d > longest) longest = d;
        }

        var text = new StringBuilder();
        text.AppendFormat(CultureInfo.InvariantCulture,
            "{0} waypoints ({1} hotspot, {2} RunTo{3}), {4:F0} yd total, longest leg {5:F0} yd, Z {6:F0}..{7:F0}",
            route.Count, hotspots, runTo, other == 0 ? "" : ", " + other + " other", length, longest, minZ, maxZ);
        return text.ToString();
    }
}

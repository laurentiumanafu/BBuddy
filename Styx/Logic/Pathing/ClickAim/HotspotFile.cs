using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// The hotspot list you build in the field: <c>Settings\clickaim-hotspots.xml</c>.
///
/// "Walk 40 yards" was a mechanism test. What a bot actually needs is a list of points
/// to visit, which is why every HonorBuddy profile has <c>&lt;Hotspots&gt;</c> and why
/// <see cref="ProfileRoute"/> already knows how to read them. This is the other half:
/// writing one, from where the character is standing right now, without a text editor.
///
/// The file is an ordinary profile, so it can be
///
///   * walked - <see cref="ClickAimDiagnostics"/> takes "profile:&lt;path&gt;" in the
///     request file, and <c>Tools\Profile.cmd</c> writes exactly that;
///   * replaced - drop in any HonorBuddy profile and the same route runs;
///   * hand-edited - it is XML with one element per point.
///
/// Nothing here touches the client: a capture reads the position the reader already
/// gives and appends a line.
/// </summary>
internal static class HotspotFile
{
    private const string FileName = "clickaim-hotspots.xml";
    private const string RootName = "HBProfile";

    /// <summary>Same folder as <c>clickaim-walk.txt</c>, next to the executable.</summary>
    public static string DefaultPath
    {
        get
        {
            string root;
            try { root = Logging.ApplicationPath; }
            catch { root = AppContext.BaseDirectory; }
            return Path.Combine(root, "Settings", FileName);
        }
    }

    /// <summary>How many points the file holds now (0 if it does not exist yet).</summary>
    public static int Count(string? path = null)
    {
        try
        {
            return Read(path ?? DefaultPath).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The points in the file, in the order they were captured.
    ///
    /// <see cref="ProfileRoute.Load"/> is reused rather than re-parsed here: a hotspot
    /// list and a profile are the same thing, and having two readers is how they drift
    /// apart.
    /// </summary>
    public static List<RoutePoint> Read(string path)
    {
        if (!File.Exists(path)) return new List<RoutePoint>();
        return ProfileRoute.Load(path);
    }

    /// <summary>
    /// Appends a point. Returns the number of points in the file afterwards, or -1 with
    /// <paramref name="error"/> set if the file could not be written.
    ///
    /// The document is rebuilt rather than string-concatenated: a half-written XML file
    /// is a hotspot list that silently stops loading, and that failure would look exactly
    /// like "the bot ignored the profile".
    /// </summary>
    public static int Add(double x, double y, double z, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            XDocument doc;
            if (File.Exists(file))
            {
                doc = XDocument.Load(file);
            }
            else
            {
                string dir = Path.GetDirectoryName(file) ?? ".";
                if (dir.Length > 0) Directory.CreateDirectory(dir);
                doc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement(RootName,
                        new XElement("Name", "ClickAim hotspots"),
                        new XElement("Hotspots")));
            }

            XElement? hotspots = doc.Root?.Element("Hotspots");
            if (hotspots == null)
            {
                hotspots = new XElement("Hotspots");
                doc.Root?.Add(hotspots);
            }

            hotspots.Add(new XElement("Hotspot",
                new XAttribute("X", x.ToString("0.000", CultureInfo.InvariantCulture)),
                new XAttribute("Y", y.ToString("0.000", CultureInfo.InvariantCulture)),
                new XAttribute("Z", z.ToString("0.000", CultureInfo.InvariantCulture))));

            // Writing through a temporary file and moving it into place: if the process
            // dies mid-write the old list is still there, whole.
            /* Temp then move, with overwrite: deleting the old file first leaves a
             * window where the list does not exist at all, and a crash there loses every
             * point that was ever captured. */
            string temp = file + ".tmp";
            doc.Save(temp);
            File.Move(temp, file, true);

            return Count(file);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    /// <summary>Empties the list but keeps the file (and its name) in place.</summary>
    public static bool Clear(out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file)) return true;
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(RootName,
                    new XElement("Name", "ClickAim hotspots"),
                    new XElement("Hotspots")));
            string temp = file + ".tmp";
            doc.Save(temp);
            File.Move(temp, file, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>One line for the log: where the file is and what is in it.</summary>
    public static string Describe(string? path = null)
    {
        string file = path ?? DefaultPath;
        string name = Path.GetFileName(file);
        var points = Read(file);
        if (points.Count == 0) return name + " has no hotspots yet";

        var sb = new StringBuilder();
        sb.Append(points.Count).Append(points.Count == 1 ? " hotspot in " : " hotspots in ").Append(name);
        int show = Math.Min(points.Count, 5);
        for (int i = 0; i < show; i++)
        {
            sb.Append(i == 0 ? ": " : ", ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "({0:F0}, {1:F0})",
                points[i].X, points[i].Y);
        }
        if (points.Count > show) sb.Append(", ...");
        return sb.ToString();
    }
}

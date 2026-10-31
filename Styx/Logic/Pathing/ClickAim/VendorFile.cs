using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// Where the character sells its loot, and what to click when it gets there:
/// <c>Settings\clickaim-vendors.xml</c>.
///
/// Original CopilotBuddy read this out of the profile - <c>BotPoi.Current = new
/// BotPoi(vendor, PoiType.Sell)</c>, with the vendor a <c>&lt;Vendor Name ID Type X Y Z&gt;</c>
/// element that <see cref="VendorManager"/> had already loaded. Two of those three things are
/// missing on build 70291:
///
///   * there are no profile files in this repository, and the profiles a user has on disk are
///     for a different client, so <c>&lt;Vendors&gt;</c> cannot be relied on;
///   * the merchant frame is a Lua object, and the Lua route is retired, so "is the merchant
///     window open" cannot be read and "sell all" cannot be called.
///
/// What is left is the part a player does: stand at the vendor, click it, click the goods, and
/// click the junk in the bags. So this file holds a point to walk to (captured where the
/// character is standing - the same trick as <see cref="HotspotFile"/>) and the list of clicks
/// that a human made once, in order, which the bot replays.
///
/// A step is a fraction of the client rectangle, not a pixel: the client is resized, the DPI
/// changes, and a stored pixel would then sell the wrong thing. Fractions survive all of it.
///
/// The file is an ordinary profile shape, so a profile that DOES have <c>&lt;Vendor&gt;</c>
/// elements can be read by <see cref="ProfileRoute.Load"/> unchanged. It is written through a
/// temporary file: a half-written plan is a bot that clicks at random.
/// </summary>
internal static class VendorFile
{
    private const string FileName = "clickaim-vendors.xml";
    private const string RootName = "HBProfile";

    /// <summary>Same folder as the calibration and the grind settings.</summary>
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

    /// <summary>One replayable action in a sell plan.</summary>
    public sealed class Step
    {
        /// <summary>"LeftClick", "RightClick", "Key" or "Wait".</summary>
        public string Kind { get; set; } = "LeftClick";

        /// <summary>Horizontal position as a fraction of the client width (0..1).</summary>
        public double Fx { get; set; }

        /// <summary>Vertical position as a fraction of the client height (0..1).</summary>
        public double Fy { get; set; }

        /// <summary>Virtual-key code, for a Key step.</summary>
        public int Vk { get; set; }

        /// <summary>How long to leave the client alone after the action.</summary>
        public int WaitMs { get; set; } = 300;

        public bool IsClick
        {
            get
            {
                return string.Equals(Kind, "LeftClick", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Kind, "RightClick", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Kind, "Click", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsRightClick
        {
            get { return string.Equals(Kind, "RightClick", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsKey
        {
            get { return string.Equals(Kind, "Key", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsWait
        {
            get { return string.Equals(Kind, "Wait", StringComparison.OrdinalIgnoreCase); }
        }

        public Step Clone()
        {
            return new Step { Kind = Kind, Fx = Fx, Fy = Fy, Vk = Vk, WaitMs = WaitMs };
        }

        /// <summary>
        /// The one-line form for the list in the window: short enough for a column, but never so
        /// short that two steps look alike.
        /// </summary>
        public string Short
        {
            get
            {
                if (IsWait) return "wait " + WaitMs.ToString(CultureInfo.InvariantCulture) + " ms";
                if (IsKey) return "press " + Vk.ToString(CultureInfo.InvariantCulture)
                                  + (Vk > 32 && Vk < 127 ? " (" + (char)Vk + ")" : "");
                return (IsRightClick ? "right click " : "left click ")
                       + Fx.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                       + Fy.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }

        public override string ToString()
        {
            if (IsWait)
                return "wait " + WaitMs.ToString(CultureInfo.InvariantCulture) + " ms";
            if (IsKey)
                return "press key " + Vk.ToString(CultureInfo.InvariantCulture)
                       + (Vk > 32 && Vk < 127 ? " (" + (char)Vk + ")" : "")
                       + " and wait " + WaitMs.ToString(CultureInfo.InvariantCulture) + " ms";
            return (IsRightClick ? "right-click at " : "left-click at ")
                   + Fx.ToString("0.000", CultureInfo.InvariantCulture) + ", "
                   + Fy.ToString("0.000", CultureInfo.InvariantCulture)
                   + " and wait " + WaitMs.ToString(CultureInfo.InvariantCulture) + " ms";
        }
    }

    /// <summary>A place to sell: a point to walk to, and the clicks to make there.</summary>
    public sealed class VendorPoint
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public List<Step> Steps { get; } = new List<Step>();

        public override string ToString()
        {
            return (Name.Length == 0 ? "vendor" : Name)
                   + string.Format(CultureInfo.InvariantCulture, " at ({0:F0}, {1:F0}, {2:F0})", X, Y, Z);
        }
    }

    /// <summary>One lock for the two threads that touch the file: the grind loop and the window.</summary>
    private static readonly object Gate = new object();

    /// <summary>How many vendor points the file holds (0 if it does not exist yet).</summary>
    public static int Count(string? path = null)
    {
        try { return Read(path ?? DefaultPath).Count; }
        catch { return 0; }
    }

    /// <summary>
    /// The points in the file, in the order they were captured.
    ///
    /// A <c>&lt;Vendor&gt;</c> element directly under the root is accepted as well as one inside
    /// <c>&lt;Vendors&gt;</c>, because that is how the profiles this was adapted from write them
    /// and reading both costs nothing.
    /// </summary>
    public static List<VendorPoint> Read(string? path = null)
    {
        string file = path ?? DefaultPath;
        var points = new List<VendorPoint>();
        if (!File.Exists(file)) return points;

        lock (Gate)
        {
            XDocument doc = XDocument.Load(file);
            if (doc.Root == null) return points;

            foreach (XElement element in doc.Root.Descendants())
            {
                if (!string.Equals(element.Name.LocalName, "Vendor", StringComparison.OrdinalIgnoreCase))
                    continue;

                var point = new VendorPoint
                {
                    Name = (string?)element.Attribute("Name") ?? "",
                    X = ReadDouble(element, "X"),
                    Y = ReadDouble(element, "Y"),
                    Z = ReadDouble(element, "Z")
                };
                if (Math.Abs(point.X) > 100000.0 || Math.Abs(point.Y) > 100000.0) continue;
                if (point.Name.Length == 0) point.Name = "vendor " + (points.Count + 1).ToString(CultureInfo.InvariantCulture);

                foreach (XElement stepElement in element.Elements())
                {
                    if (!string.Equals(stepElement.Name.LocalName, "Step", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Step? step = ReadStep(stepElement);
                    if (step != null) point.Steps.Add(step);
                }

                points.Add(point);
            }
        }

        return points;
    }

    private static Step? ReadStep(XElement element)
    {
        string kind = (string?)element.Attribute("Kind") ?? "";
        if (kind.Length == 0) return null;

        var step = new Step
        {
            Kind = kind,
            Fx = ReadDouble(element, "FX"),
            Fy = ReadDouble(element, "FY"),
            Vk = (int)ReadDouble(element, "Vk"),
            WaitMs = (int)ReadDouble(element, "WaitMs")
        };
        if (step.WaitMs <= 0) step.WaitMs = 300;

        if (step.IsClick && (step.Fx <= 0.0 || step.Fx >= 1.0 || step.Fy <= 0.0 || step.Fy >= 1.0))
            return null;                       // a click at the edge of the window is a typo
        if (!step.IsClick && !step.IsKey && !step.IsWait) return null;
        return step;
    }

    private static double ReadDouble(XElement element, string name)
    {
        string? raw = (string?)element.Attribute(name);
        if (raw == null) return 0.0;
        double value;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? value
            : 0.0;
    }

    /// <summary>
    /// Appends a point where the character is standing right now. Returns the number of points
    /// after the write, or -1 with <paramref name="error"/> set.
    /// </summary>
    public static int Add(double x, double y, double z, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                XDocument doc = LoadOrCreate(file);
                XElement vendors = EnsureVendors(doc);

                int index = 0;
                foreach (XElement existing in vendors.Elements("Vendor")) index++;

                vendors.Add(new XElement("Vendor",
                    new XAttribute("Name", "vendor " + (index + 1).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("X", x.ToString("0.000", CultureInfo.InvariantCulture)),
                    new XAttribute("Y", y.ToString("0.000", CultureInfo.InvariantCulture)),
                    new XAttribute("Z", z.ToString("0.000", CultureInfo.InvariantCulture))));

                Save(doc, file);
                return index + 1;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    /// <summary>Appends one step to the point at <paramref name="index"/> (0-based).</summary>
    public static bool AddStep(int index, Step step, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(file)) { error = "there is no vendor point to add a step to"; return false; }
                XDocument doc = XDocument.Load(file);
                XElement? target = VendorAt(doc, index);
                if (target == null) { error = "there is no vendor point to add a step to"; return false; }

                var element = new XElement("Step",
                    new XAttribute("Kind", step.Kind),
                    new XAttribute("WaitMs", step.WaitMs.ToString(CultureInfo.InvariantCulture)));
                if (step.IsClick)
                {
                    element.SetAttributeValue("FX", step.Fx.ToString("0.0000", CultureInfo.InvariantCulture));
                    element.SetAttributeValue("FY", step.Fy.ToString("0.0000", CultureInfo.InvariantCulture));
                }
                if (step.IsKey)
                    element.SetAttributeValue("Vk", step.Vk.ToString(CultureInfo.InvariantCulture));

                target.Add(element);
                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Empties the sell plan of one point, keeping the point itself.</summary>
    public static bool ClearSteps(int index, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(file)) { error = "there is no vendor file"; return false; }
                XDocument doc = XDocument.Load(file);
                XElement? target = VendorAt(doc, index);
                if (target == null) { error = "there is no vendor point to clear"; return false; }

                var steps = new List<XElement>();
                foreach (XElement element in target.Elements()) steps.Add(element);
                for (int i = 0; i < steps.Count; i++) steps[i].Remove();

                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes one point and everything recorded under it, so a vendor captured by mistake can be
    /// taken back out. The grind loop picks the nearest point, so a stray point in a town the
    /// character walks through is worse than no point at all.
    /// </summary>
    public static bool Remove(int index, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(file)) { error = "there is no vendor file"; return false; }
                XDocument doc = XDocument.Load(file);
                XElement? target = VendorAt(doc, index);
                if (target == null) { error = "there is no vendor point to remove"; return false; }

                target.Remove();
                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes one step from a plan. This is the fix for a mis-recorded click: the wrong step is
    /// deleted and the rest of the plan stays as it was, instead of clearing everything and
    /// recording it again.
    /// </summary>
    public static bool RemoveStep(int index, int stepIndex, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(file)) { error = "there is no vendor file"; return false; }
                XDocument doc = XDocument.Load(file);
                XElement? target = VendorAt(doc, index);
                if (target == null) { error = "there is no vendor point"; return false; }

                XElement? step = StepAt(target, stepIndex);
                if (step == null) { error = "there is no step at that position"; return false; }

                step.Remove();
                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Moves one step up or down the plan. Order is the whole meaning of a sale: the bag key has to
    /// open the bags before an item is clicked, and the merchant window has to be open first.
    /// </summary>
    public static bool MoveStep(int index, int stepIndex, int by, out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(file)) { error = "there is no vendor file"; return false; }
                XDocument doc = XDocument.Load(file);
                XElement? target = VendorAt(doc, index);
                if (target == null) { error = "there is no vendor point"; return false; }

                var steps = new List<XElement>();
                foreach (XElement element in target.Elements())
                {
                    if (string.Equals(element.Name.LocalName, "Step", StringComparison.OrdinalIgnoreCase))
                        steps.Add(element);
                }

                if (steps.Count == 0) { error = "there are no steps to move"; return false; }
                if (stepIndex < 0 || stepIndex >= steps.Count) { error = "there is no step at that position"; return false; }

                int to = stepIndex + by;
                if (to < 0) to = 0;
                if (to >= steps.Count) to = steps.Count - 1;
                if (to == stepIndex) return true;

                XElement moving = steps[stepIndex];
                XElement neighbour = steps[to];

                /* Rebuild the position by cloning the moving element next to its neighbour: the
                 * original is removed afterwards, so a half-moved step never exists on disk - and
                 * a half-moved step is a plan that sells in the wrong order. */
                var clone = new XElement(moving);
                if (by < 0) neighbour.AddBeforeSelf(clone);
                else neighbour.AddAfterSelf(clone);
                moving.Remove();

                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static XElement? StepAt(XElement vendor, int stepIndex)
    {
        int at = 0;
        foreach (XElement element in vendor.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "Step", StringComparison.OrdinalIgnoreCase))
                continue;
            if (at == stepIndex) return element;
            at++;
        }
        return null;
    }

    /// <summary>Removes every point (the file stays, with an empty list).</summary>
    public static bool Clear(out string error, string? path = null)
    {
        error = "";
        string file = path ?? DefaultPath;
        try
        {
            lock (Gate)
            {
                XDocument doc = LoadOrCreate(file);
                EnsureVendors(doc).RemoveNodes();
                Save(doc, file);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The point closest to a position, or -1 when there are none.</summary>
    public static int Nearest(List<VendorPoint> points, double x, double y)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            double d = ClickAimMath.Len(points[i].X - x, points[i].Y - y);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>One line per point for the log and for the window.</summary>
    public static string Describe(List<VendorPoint> points, double x = double.NaN, double y = double.NaN)
    {
        if (points.Count == 0) return "no vendor point saved yet";
        var text = new StringBuilder();
        text.Append(points.Count.ToString(CultureInfo.InvariantCulture))
            .Append(points.Count == 1 ? " vendor point, " : " vendor points, ");
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) text.Append("; ");
            text.Append(points[i].Name);
            if (!double.IsNaN(x) && !double.IsNaN(y))
                text.AppendFormat(CultureInfo.InvariantCulture, " ({0:F0} yd away)",
                    ClickAimMath.Len(points[i].X - x, points[i].Y - y));
            text.Append(points[i].Steps.Count == 0
                ? ", no sell steps"
                : ", " + points[i].Steps.Count.ToString(CultureInfo.InvariantCulture) + " sell step(s)");
        }
        return text.ToString();
    }

    /// <summary>Describe the active point plus its plan, one step per line.</summary>
    public static string DescribePlan(VendorPoint point)
    {
        if (point == null) return "no vendor point";
        var text = new StringBuilder();
        text.AppendLine(point.ToString());
        if (point.Steps.Count == 0)
        {
            text.AppendLine("  (no steps: stand at the vendor with the merchant window open and record them)");
            return text.ToString();
        }
        for (int i = 0; i < point.Steps.Count; i++)
        {
            text.Append("  ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .AppendLine(point.Steps[i].ToString());
        }
        return text.ToString();
    }

    private static XElement? VendorAt(XDocument doc, int index)
    {
        XElement? vendors = doc.Root?.Element("Vendors");
        if (vendors == null) return null;
        int at = 0;
        foreach (XElement element in vendors.Elements("Vendor"))
        {
            if (at == index) return element;
            at++;
        }
        return null;
    }

    private static XDocument LoadOrCreate(string file)
    {
        if (File.Exists(file))
        {
            XDocument existing = XDocument.Load(file);
            if (existing.Root != null) return existing;
        }

        string dir = Path.GetDirectoryName(file) ?? ".";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(RootName,
                new XElement("Name", "ClickAim vendors"),
                new XElement("Vendors")));
    }

    private static XElement EnsureVendors(XDocument doc)
    {
        XElement? vendors = doc.Root?.Element("Vendors");
        if (vendors != null) return vendors;
        vendors = new XElement("Vendors");
        doc.Root?.Add(vendors);
        return vendors;
    }

    private static void Save(XDocument doc, string file)
    {
        string temp = file + ".tmp";
        doc.Save(temp);
        File.Move(temp, file, true);
    }
}

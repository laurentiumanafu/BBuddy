using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Styx.Logic.Pathing;

namespace Styx.Helpers { internal static class Logging { public static string ApplicationPath => AppContext.BaseDirectory; } }

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mailbox-record-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "vendors.xml");
        try
        {
            Check(VendorFile.Add(1, 2, 3, out _, path) == 1, "add");
            Check(!VendorFile.SetMailbox(0, 4, 5, 6, 1, out _, path), "town required");
            Check(VendorFile.SetTown(0, 1, 2, 3, 1, out _, path), "town");
            Check(VendorFile.AddMerchant(0, "0x00000000000000010000000000000002", 1, out _, path), "merchant");
            Check(VendorFile.AddStep(0, new VendorFile.Step { Kind = "Wait" }, out _, path), "step");
            Check(!VendorFile.SetMailbox(0, 4, 5, 6, 2, out _, path), "wrong map");
            Check(!VendorFile.SetMailbox(0, double.NaN, 5, 6, 1, out _, path), "invalid coordinate");
            Check(VendorFile.SetMailbox(0, 4, 5, 6, 1, out _, path), "record");
            var point = VendorFile.Read(path)[0];
            Check(point.Mailbox != null && point.Mailbox.Map == 1 && point.Mailbox.X == 4 && point.Mailbox.Z == 6, "roundtrip");
            Check(point.MerchantGuids.Count == 1 && point.Steps.Count == 1, "preserve plan");
            Check(VendorFile.SetMailbox(0, 7, 8, 9, 1, out _, path), "replace");
            Check(XDocument.Load(path).Descendants("Mailbox").Count() == 1 && VendorFile.Read(path)[0].Mailbox?.X == 7, "one mailbox");
            XDocument broken = XDocument.Load(path);
            broken.Descendants("Mailbox").Single().SetAttributeValue("X", "NaN");
            broken.Save(path);
            point = VendorFile.Read(path)[0];
            Check(point.Mailbox == null && point.Steps.Count == 1, "bad metadata does not break existing plan");
            Check(VendorFile.SetMailbox(0, 4, 5, 6, 1, out _, path), "replace bad metadata");
            Check(VendorFile.SetTown(0, 20, 30, 40, 1, out _, path), "new town");
            point = VendorFile.Read(path)[0];
            Check(point.Mailbox == null && point.MerchantGuids.Count == 0 && point.Steps.Count == 1, "town resets metadata but keeps steps");
            Console.WriteLine("Mailbox recording persistence tests passed.");
        }
        finally { Directory.Delete(dir, true); }
    }
}

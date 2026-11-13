#if WOWB_READER_TESTS
using System;
using System.IO;
using System.Numerics;
using Styx.Logic.Pathing;

internal static class MmapGroundPolicyTests
{
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    internal static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mmap-ground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pos = new Vector3(-9179.1f, -4532.8f, 16.2f); // tile 49,40
            string map = Path.Combine(dir, "0000.mmap"), tile = Path.Combine(dir, "0000_49_40.mmtile");
            bool Query(byte area, bool loaded = true) => MmapGroundPolicy.IsGroundInDirectory(dir, 0, pos, area, (x, y) => loaded && x == 49 && y == 40);
            File.WriteAllBytes(map, new byte[28]); // bare legacy params; policy only classifies header
            Check(Query(0) && Query(1) && Query(4) && !Query(2) && !Query(3) && !Query(11) && !Query(17), "legacy area semantics");
            using (var w = new BinaryWriter(File.Create(map)))
            { w.Write(0x4d4d4150u); w.Write(16u); w.Write(new byte[32]); }
            using (var w = new BinaryWriter(File.Create(tile)))
            { w.Write(0x4d4d4150u); w.Write(7u); w.Write(16u); w.Write(8u); w.Write(1u); w.Write(0x444e4156u); w.Write(7u); }
            Check(Query(11) && !Query(11, false), "v16 ground requires loaded tile");
            foreach (byte area in new byte[] { 0, 1, 2, 3, 4, 8, 9, 10, 12, 17 })
                Check(!Query(area), "v16 non-ground or ambiguous area " + area);
            Check(!MmapGroundPolicy.IsGroundInDirectory(dir, 0, new Vector3(float.NaN, 0, 0), 11, (_, _) => true), "invalid coordinate");
            var border = new Vector3(32f * 533.3333f - 49f * 533.3333f, pos.Y, pos.Z);
            Check(!MmapGroundPolicy.IsGroundInDirectory(dir, 0, border, 11, (_, _) => true), "tile boundary ambiguous");
            // A classic filename wins, as it does in the native loader; a mixed
            // or truncated tile must not be interpreted using the modern one.
            File.WriteAllBytes(Path.Combine(dir, "0004940.mmtile"), new byte[28]);
            Check(!Query(11), "classic mixed-format tile takes priority");
            File.Delete(Path.Combine(dir, "0004940.mmtile"));
            using (var w = new BinaryWriter(File.OpenWrite(tile))) { w.Seek(8, SeekOrigin.Begin); w.Write(15u); }
            Check(!Query(11), "unsupported tile version");
            File.WriteAllBytes(tile, new byte[4]);
            Check(!Query(11), "truncated tile");
            File.WriteAllBytes(map, new byte[4]);
            Check(!Query(11), "truncated parameters");
            File.Delete(map);
            Check(!Query(11), "missing parameters");
            Console.WriteLine("Mesh ground format fixtures passed (no native DLL or live game).");
        }
        finally { Directory.Delete(dir, true); }
    }
}
#endif

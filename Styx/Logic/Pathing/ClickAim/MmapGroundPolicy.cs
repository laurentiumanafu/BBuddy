using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Styx.Logic.Pathing;

// Area IDs are extractor-specific. In the captured Trinity v16 mesh, 11 means
// ground; in the legacy HB encoding 11 means Alliance portal. Never reinterpret
// 11 unless BOTH the selected map and the selected single tile carry the v16
// headers. Refuse ambiguous, mixed, inaccessible or border cases.
internal static class MmapGroundPolicy
{
    private const uint Magic = 0x4d4d4150, Version = 16, DetourMagic = 0x444e4156, DetourVersion = 7;
    private const float TileSize = 533.3333f, Origin = 32f * TileSize;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int capacity);

    private static string? NativeMmapsDirectory()
    {
        IntPtr module = GetModuleHandleW("Navigation.dll");
        if (module == IntPtr.Zero) return null;
        var path = new StringBuilder(32768);
        uint length = GetModuleFileNameW(module, path, path.Capacity);
        if (length == 0 || length >= path.Capacity) return null;
        string? directory = Path.GetDirectoryName(path.ToString());
        return directory == null ? null : Path.Combine(directory, "mmaps");
    }

    // Native loader prefers the classic name, then the modern name. A file that
    // exists but is unreadable is ambiguous: do not guess which source it used.
    private static FileStream OpenPreferred(string directory, string classic, string modern)
    {
        try { return File.OpenRead(Path.Combine(directory, classic)); }
        catch (FileNotFoundException) { return File.OpenRead(Path.Combine(directory, modern)); }
        catch (DirectoryNotFoundException) { return File.OpenRead(Path.Combine(directory, modern)); }
    }

    internal static bool LegacyGround(byte area) => area == 0 || area == 1 || area == 4;

    internal static bool IsGround(uint map, Vector3 position, byte area, Func<int, int, bool> tileLoaded)
    {
        try
        {
            string? dir = NativeMmapsDirectory();
            return dir != null && IsGroundInDirectory(dir, map, position, area, tileLoaded);
        }
        catch { return false; }
    }

    // Inject directory and loaded-status predicate so this policy can be tested
    // without loading the Windows DLL or issuing game input.
    internal static bool IsGroundInDirectory(string directory, uint map, Vector3 position, byte area, Func<int, int, bool> tileLoaded)
    {
        try
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return false;
            string id = map.ToString("000", CultureInfo.InvariantCulture);
            string modernId = map.ToString("0000", CultureInfo.InvariantCulture);
            using var mapFile = OpenPreferred(directory, id + ".mmap", modernId + ".mmap");
            if (mapFile.Length < 28) return false;
            using var mapReader = new BinaryReader(mapFile);
            uint first = mapReader.ReadUInt32();
            if (first != Magic) return LegacyGround(area); // old bare params
            if (mapFile.Length != 40 || mapReader.ReadUInt32() != Version || area != 11) return false;

            double tx = (Origin - (double)position.X) / TileSize, ty = (Origin - (double)position.Y) / TileSize;
            if (tx < 0 || tx >= 64 || ty < 0 || ty >= 64) return false;
            int x = (int)tx, y = (int)ty;
            // A point at a file boundary may resolve to a neighboring tile with
            // a different extractor. Do not infer its encoding from one filename.
            double fx = (tx - x) * TileSize, fy = (ty - y) * TileSize;
            if (fx < 1 || fy < 1 || fx > TileSize - 1 || fy > TileSize - 1 || !tileLoaded(x, y)) return false;
            string classic = string.Format(CultureInfo.InvariantCulture, "{0}{1:00}{2:00}.mmtile", id, x, y);
            string modern = string.Format(CultureInfo.InvariantCulture, "{0}_{1:00}_{2:00}.mmtile", modernId, x, y);
            using var tile = OpenPreferred(directory, classic, modern);
            if (tile.Length < 28) return false;
            using var r = new BinaryReader(tile);
            if (r.ReadUInt32() != Magic || r.ReadUInt32() != DetourVersion || r.ReadUInt32() != Version) return false;
            uint size = r.ReadUInt32();
            if (size < 8 || (long)size + 20 != tile.Length) return false;
            r.ReadUInt32(); // usesLiquids plus padding
            return r.ReadUInt32() == DetourMagic && r.ReadUInt32() == DetourVersion;
        }
        catch { return false; }
    }
}

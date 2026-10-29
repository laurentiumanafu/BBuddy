using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Styx.Helpers;

namespace Styx.Logic.Pathing;

/// <summary>
/// One baked ADT tile: a 267x267 grid of "can I stand here" plus a height.
/// Read-only at runtime; the bake happens offline in Tools/NavGrid.
/// </summary>
public sealed class NavGridTile
{
    public const int Cells = 268;   // 533.3333/268 = 1.99005 yd; /4 = 67 cells per Detour sub-tile
    public const int CellCount = Cells * Cells;
    public const float AdtYd = 533.3333f;
    public const float CellYd = AdtYd / Cells;      // 1.99005 yd
    public const int HeightQ = 4;                   // quarter yards

    public const byte Walk = 0x01;
    public const byte Hole = 0x02;
    public const byte Liquid = 0x04;
    public const byte Steep = 0x08;

    private const ushort Version = 1;
    private const int HeaderBytes = 44;

    public int MapId { get; private set; }
    public int TileX { get; private set; }
    public int TileY { get; private set; }
    public float OriginX { get; private set; }
    public float OriginY { get; private set; }

    private short[] _heights = new short[CellCount];
    private byte[] _flags = new byte[CellCount];

    public static int Index(int col, int row) { return row * Cells + col; }

    public bool Walkable(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Cells || row >= Cells) return false;
        return (_flags[Index(col, row)] & Walk) != 0;
    }

    public byte Flags(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Cells || row >= Cells) return 0;
        return _flags[Index(col, row)];
    }

    public float Height(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Cells || row >= Cells) return 0f;
        return _heights[Index(col, row)] / (float)HeightQ;
    }

    public bool WorldToCell(double x, double y, out int col, out int row)
    {
        col = 0;
        row = 0;
        double fx = (OriginX - x) / CellYd;
        double fy = (OriginY - y) / CellYd;
        if (fx < -0.5 || fy < -0.5 || fx > Cells - 0.5 || fy > Cells - 0.5) return false;
        int c = (int)Math.Floor(fx + 0.5);
        int r = (int)Math.Floor(fy + 0.5);
        if (c < 0) c = 0;
        if (r < 0) r = 0;
        if (c >= Cells) c = Cells - 1;
        if (r >= Cells) r = Cells - 1;
        col = c;
        row = r;
        return true;
    }

    public void CellToWorld(int col, int row, out double x, out double y)
    {
        x = OriginX - col * CellYd;
        y = OriginY - row * CellYd;
    }

    /// <summary>Creates a tile in memory - used by the self-test, not by the reader.</summary>
    internal static NavGridTile CreateForTest(int mapId, int tx, int ty, float height)
    {
        var tile = new NavGridTile();
        tile.MapId = mapId;
        tile.TileX = tx;
        tile.TileY = ty;
        tile.OriginX = (32 - ty) * AdtYd - CellYd * 0.5f;
        tile.OriginY = (32 - tx) * AdtYd - CellYd * 0.5f;
        for (int i = 0; i < CellCount; i++)
        {
            tile._heights[i] = (short)Math.Round(height * HeightQ);
            tile._flags[i] = Walk;
        }
        return tile;
    }

    internal void TestSet(int col, int row, float height, bool walkable)
    {
        int index = Index(col, row);
        _heights[index] = (short)Math.Round(height * HeightQ);
        _flags[index] = walkable ? Walk : (byte)0;
    }

    public static NavGridTile Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < HeaderBytes + CellCount * 3)
            throw new InvalidDataException("navtile is too short");

        if (data[0] != (byte)'N' || data[1] != (byte)'A' || data[2] != (byte)'V' || data[3] != (byte)'G')
            throw new InvalidDataException("navtile magic is not NAVG");

        ushort version = BitConverter.ToUInt16(data, 4);
        if (version != Version)
            throw new InvalidDataException("navtile version " + version + " is not supported");

        int mapId = BitConverter.ToInt32(data, 8);
        int tileX = BitConverter.ToInt32(data, 12);
        int tileY = BitConverter.ToInt32(data, 16);
        short cells = BitConverter.ToInt16(data, 20);
        short heightQ = BitConverter.ToInt16(data, 22);
        float originX = BitConverter.ToSingle(data, 24);
        float originY = BitConverter.ToSingle(data, 28);
        int heightBytes = BitConverter.ToInt32(data, 32);
        int flagBytes = BitConverter.ToInt32(data, 36);
        int storedCrc = BitConverter.ToInt32(data, 40);

        if (cells != Cells || heightQ != HeightQ)
            throw new InvalidDataException("navtile geometry mismatch");
        if (heightBytes != CellCount * 2 || flagBytes != CellCount)
            throw new InvalidDataException("navtile payload sizes are wrong");

        int heightsAt = HeaderBytes;
        int flagsAt = heightsAt + heightBytes;
        if (data.Length < flagsAt + flagBytes) throw new InvalidDataException("navtile is truncated");

        // CRC covers the header with the CRC field zeroed, then heights, then flags.
        var header = new byte[HeaderBytes];
        Array.Copy(data, header, HeaderBytes);
        for (int i = 40; i < 44; i++) header[i] = 0;
        uint crc = 0xFFFFFFFFu;
        crc = Crc(crc, header, 0, header.Length);
        crc = Crc(crc, data, heightsAt, heightBytes);
        crc = Crc(crc, data, flagsAt, flagBytes);
        crc = ~crc;
        if ((int)crc != storedCrc) throw new InvalidDataException("navtile CRC mismatch");

        var tile = new NavGridTile();
        tile.MapId = mapId;
        tile.TileX = tileX;
        tile.TileY = tileY;
        tile.OriginX = originX;
        tile.OriginY = originY;
        for (int i = 0; i < CellCount; i++)
            tile._heights[i] = BitConverter.ToInt16(data, heightsAt + i * 2);
        Array.Copy(data, flagsAt, tile._flags, 0, CellCount);
        return tile;
    }

    private static uint Crc(uint crc, byte[] data, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1u)));
        }
        return crc;
    }
}

/// <summary>
/// A* over a set of contiguous tiles, with line-of-sight smoothing. Mirrors
/// Tools/NavGrid/navgrid.c; the C version is the one with the unit tests, so a
/// disagreement means this port is wrong.
/// </summary>
public sealed class NavGridFinder
{
    private readonly int _baseTx, _baseTy;
    private readonly int _rows, _cols;
    private readonly double _originX, _originY;
    private readonly NavGridTile[] _tiles;
    private readonly int _globalCols, _globalRows;

    public NavGridFinder(int baseTx, int baseTy, int rows, int cols,
                         double originX, double originY, NavGridTile[] tiles)
    {
        _baseTx = baseTx;
        _baseTy = baseTy;
        _rows = rows;
        _cols = cols;
        _originX = originX;
        _originY = originY;
        _tiles = tiles;
        _globalCols = cols * NavGridTile.Cells;
        _globalRows = rows * NavGridTile.Cells;
    }

    public int CellTotal { get { return _globalCols * _globalRows; } }

    public static void TileOf(double x, double y, out int tileX, out int tileY)
    {
        // The truncation applies to the difference, which is how both server cores do
        // it: (int)(32 - coord / 533.3333). Subtracting after truncating is off by one
        // for every positive coordinate, so tile 25 would be looked up for a position
        // that is in tile 24.
        tileX = (int)(32.0 - y / NavGridTile.AdtYd);
        tileY = (int)(32.0 - x / NavGridTile.AdtYd);
    }

    public bool WorldToGlobal(double x, double y, out int gcol, out int grow)
    {
        gcol = 0;
        grow = 0;
        double fx = (_originX - x) / NavGridTile.CellYd - 0.5;
        double fy = (_originY - y) / NavGridTile.CellYd - 0.5;
        if (fx < -1 || fy < -1 || fx > _globalCols || fy > _globalRows) return false;
        int c = (int)Math.Floor(fx + 0.5);
        int r = (int)Math.Floor(fy + 0.5);
        if (c < 0) c = 0;
        if (r < 0) r = 0;
        if (c >= _globalCols) c = _globalCols - 1;
        if (r >= _globalRows) r = _globalRows - 1;
        gcol = c;
        grow = r;
        return true;
    }

    public void GlobalToWorld(int gcol, int grow, out double x, out double y)
    {
        x = _originX - gcol * NavGridTile.CellYd - NavGridTile.CellYd * 0.5;
        y = _originY - grow * NavGridTile.CellYd - NavGridTile.CellYd * 0.5;
    }

    private bool Cell(int gcol, int grow, out float height)
    {
        height = 0f;
        if (gcol < 0 || grow < 0 || gcol >= _globalCols || grow >= _globalRows) return false;
        int tileCol = gcol / NavGridTile.Cells;
        int tileRow = grow / NavGridTile.Cells;
        NavGridTile tile = _tiles[tileRow * _cols + tileCol];
        if (tile == null) return false;
        int localCol = gcol - tileCol * NavGridTile.Cells;
        int localRow = grow - tileRow * NavGridTile.Cells;
        if (!tile.Walkable(localCol, localRow)) return false;
        height = tile.Height(localCol, localRow);
        return true;
    }

    public bool LineWalkable(double x0, double y0, double x1, double y1)
    {
        int c0, r0, c1, r1;
        if (!WorldToGlobal(x0, y0, out c0, out r0)) return false;
        if (!WorldToGlobal(x1, y1, out c1, out r1)) return false;

        int dx = Math.Abs(c1 - c0), dy = Math.Abs(r1 - r0);
        int sx = c0 < c1 ? 1 : -1, sy = r0 < r1 ? 1 : -1;
        int err = dx - dy;
        float height;
        if (!Cell(c0, r0, out height)) return false;

        int limit = (Math.Abs(c1 - c0) + Math.Abs(r1 - r0)) * 4 + 16;
        int c = c0, r = r0;
        for (int guard = 0; guard < limit; guard++)
        {
            if (c == c1 && r == r1) return true;

            int e2 = err * 2;
            int nc = c, nr = r;
            if (e2 > -dy) { err -= dy; nc += sx; }
            if (e2 < dx) { err += dx; nr += sy; }

            float nextHeight;
            // A diagonal step may not slip between two blocked cells.
            if (nc != c && nr != r)
            {
                float ignored;
                if (!Cell(nc, r, out ignored)) return false;
                if (!Cell(c, nr, out ignored)) return false;
            }
            if (!Cell(nc, nr, out nextHeight)) return false;
            if (Math.Abs(nextHeight - height) > 1.5f) return false;
            c = nc;
            r = nr;
            height = nextHeight;
        }
        return false;
    }

    /// <summary>
    /// Returns waypoints from the player to the goal, or an empty list. Points are
    /// string-pulled, so the result is a few long segments rather than a staircase.
    /// </summary>
    public List<WoWPoint> FindPath(double sx, double sy, double sz,
                                   double gx, double gy, double gz,
                                   int maxPoints = 128)
    {
        var result = new List<WoWPoint>();
        int total = CellTotal;
        if (total <= 0) return result;

        int startCol, startRow, goalCol, goalRow;
        if (!WorldToGlobal(sx, sy, out startCol, out startRow)) return result;
        if (!WorldToGlobal(gx, gy, out goalCol, out goalRow)) return result;

        float startHeight;
        if (!Cell(startCol, startRow, out startHeight))
        {
            if (!Nudge(ref startCol, ref startRow, 4)) return result;
        }
        float goalHeight;
        if (!Cell(goalCol, goalRow, out goalHeight))
        {
            if (!Nudge(ref goalCol, ref goalRow, 6)) return result;
        }

        int start = startRow * _globalCols + startCol;
        int goal = goalRow * _globalCols + goalCol;

        var g = new float[total];
        var f = new float[total];
        var parent = new int[total];
        var state = new byte[total];
        for (int i = 0; i < total; i++) { g[i] = float.MaxValue; parent[i] = -1; }

        var heap = new MinHeap(total);
        g[start] = 0f;
        f[start] = 0f;
        state[start] = 1;
        heap.Push(start, 0f);

        bool found = false;
        const float diag = 1.41421356f;

        while (heap.Count > 0)
        {
            float score;
            int current = heap.Pop(out score);
            if (current < 0) break;
            if (state[current] == 2) continue;
            state[current] = 2;
            if (current == goal) { found = true; break; }

            int ccol = current % _globalCols;
            int crow = current / _globalCols;
            float cheight;
            Cell(ccol, crow, out cheight);

            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dc == 0 && dr == 0) continue;
                    int ncol = ccol + dc;
                    int nrow = crow + dr;
                    float nheight;
                    if (!Cell(ncol, nrow, out nheight)) continue;
                    if (Math.Abs(nheight - cheight) > 1.5f) continue;

                    if (dc != 0 && dr != 0)
                    {
                        float ignored;
                        if (!Cell(ccol + dc, crow, out ignored)) continue;
                        if (!Cell(ccol, crow + dr, out ignored)) continue;
                    }

                    int neighbour = nrow * _globalCols + ncol;
                    if (state[neighbour] == 2) continue;

                    float step = (dc != 0 && dr != 0) ? diag : 1f;
                    float rise = Math.Abs(nheight - cheight);
                    if (rise > 0.5f) step += (rise - 0.5f) * 2f;

                    float candidate = g[current] + step;
                    if (state[neighbour] == 1 && candidate >= g[neighbour]) continue;

                    parent[neighbour] = current;
                    g[neighbour] = candidate;
                    int dxg = Math.Abs(goalCol - ncol), dyg = Math.Abs(goalRow - nrow);
                    int mind = Math.Min(dxg, dyg), maxd = Math.Max(dxg, dyg);
                    f[neighbour] = candidate + (maxd - mind) + diag * mind;
                    state[neighbour] = 1;
                    heap.Push(neighbour, f[neighbour]);
                }
            }
        }

        if (!found) return result;

        var chain = new List<int>();
        for (int node = goal; node >= 0; node = parent[node]) chain.Add(node);
        chain.Reverse();

        result.Add(new WoWPoint((float)sx, (float)sy, (float)sz));
        int anchor = 0;
        while (anchor < chain.Count - 1)
        {
            int best = anchor + 1;
            for (int probe = chain.Count - 1; probe > anchor; probe--)
            {
                double ax, ay, px, py;
                GlobalToWorld(chain[anchor] % _globalCols, chain[anchor] / _globalCols, out ax, out ay);
                GlobalToWorld(chain[probe] % _globalCols, chain[probe] / _globalCols, out px, out py);
                if (LineWalkable(ax, ay, px, py)) { best = probe; break; }
            }

            if (best == chain.Count - 1)
            {
                result.Add(new WoWPoint((float)gx, (float)gy, (float)gz));
                anchor = best;
            }
            else
            {
                int bcol = chain[best] % _globalCols;
                int brow = chain[best] / _globalCols;
                double bx, by;
                float bh;
                GlobalToWorld(bcol, brow, out bx, out by);
                Cell(bcol, brow, out bh);
                result.Add(new WoWPoint((float)bx, (float)by, bh));
                anchor = best;
            }

            if (result.Count >= maxPoints)
            {
                result.Add(new WoWPoint((float)gx, (float)gy, (float)gz));
                break;
            }
        }

        return result;
    }

    private bool Nudge(ref int col, ref int row, int radius)
    {
        for (int r = 1; r <= radius; r++)
        {
            for (int dr = -r; dr <= r; dr++)
            {
                for (int dc = -r; dc <= r; dc++)
                {
                    float height;
                    if (Cell(col + dc, row + dr, out height))
                    {
                        col += dc;
                        row += dr;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private sealed class MinHeap
    {
        private readonly int[] _items;
        private readonly float[] _scores;
        private int _count;

        public MinHeap(int capacity)
        {
            _items = new int[capacity];
            _scores = new float[capacity];
        }

        public int Count { get { return _count; } }

        public void Push(int item, float score)
        {
            int i = _count++;
            _items[i] = item;
            _scores[i] = score;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_scores[parent] <= _scores[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public int Pop(out float score)
        {
            if (_count <= 0) { score = 0f; return -1; }
            int top = _items[0];
            score = _scores[0];
            _count--;
            if (_count > 0)
            {
                _items[0] = _items[_count];
                _scores[0] = _scores[_count];
                int i = 0;
                for (;;)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < _count && _scores[left] < _scores[smallest]) smallest = left;
                    if (right < _count && _scores[right] < _scores[smallest]) smallest = right;
                    if (smallest == i) break;
                    Swap(smallest, i);
                    i = smallest;
                }
            }
            return top;
        }

        private void Swap(int a, int b)
        {
            int ti = _items[a]; _items[a] = _items[b]; _items[b] = ti;
            float ts = _scores[a]; _scores[a] = _scores[b]; _scores[b] = ts;
        }
    }
}

/// <summary>
/// Tile directory + path lookup for the mover. Tiles are baked offline by
/// Tools/NavGrid/navbake and read lazily; a missing tile just means "no path
/// known here", never an exception on the movement path.
/// </summary>
public static class NavGridStore
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, NavGridTile> Cache = new Dictionary<string, NavGridTile>();
    private static string _root;

    public static string DefaultRoot
    {
        get
        {
            string app;
            try { app = Logging.ApplicationPath; }
            catch { app = AppContext.BaseDirectory; }
            return Path.Combine(app, "NavTiles");
        }
    }

    public static string Root
    {
        get { return _root ?? DefaultRoot; }
        set { _root = value; lock (Gate) { Cache.Clear(); } }
    }

    public static int CachedTiles { get { lock (Gate) { return Cache.Count; } } }

    /// <summary>
    /// Map id used to name tiles. Kalimdor is 1 and Eastern Kingdoms is 0 on both
    /// the 3.3.5a client and the modern one; set it explicitly with --navmap when
    /// the current map is something else.
    /// </summary>
    public static int MapId { get; set; } = 1;

    public static string TileFile(string root, int mapId, int tileX, int tileY)
    {
        return Path.Combine(root, string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2}.navtile",
                                                 mapId, tileX, tileY));
    }

    public static NavGridTile TryLoad(int mapId, int tileX, int tileY)
    {
        string root = Root;
        string key = root + "|" + mapId + "|" + tileX + "|" + tileY;
        lock (Gate)
        {
            NavGridTile cached;
            if (Cache.TryGetValue(key, out cached)) return cached;
        }

        string path = TileFile(root, mapId, tileX, tileY);
        if (!File.Exists(path)) return null;

        NavGridTile tile;
        try { tile = NavGridTile.Read(path); }
        catch (Exception ex)
        {
            Logging.Write("[NavGrid] " + Path.GetFileName(path) + " is unreadable: " + ex.Message);
            return null;
        }

        lock (Gate) { Cache[key] = tile; }
        return tile;
    }

    /// <summary>
    /// Path between two world points using whatever tiles are present. Returns an
    /// empty list when there is no geometry or no route - callers fall back to a
    /// straight walk, which is what the mover did before any of this existed.
    /// </summary>
    public static List<WoWPoint> FindPath(int mapId, double sx, double sy, double sz,
                                          double gx, double gy, double gz)
    {
        var empty = new List<WoWPoint>();

        int stx, sty, gtx, gty;
        NavGridFinder.TileOf(sx, sy, out stx, out sty);
        NavGridFinder.TileOf(gx, gy, out gtx, out gty);

        int tx0 = Math.Min(stx, gtx), tx1 = Math.Max(stx, gtx);
        int ty0 = Math.Min(sty, gty), ty1 = Math.Max(sty, gty);
        if ((long)(tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 36) return empty;   // > 6x6 tiles: refuse

        var tiles = new NavGridTile[(tx1 - tx0 + 1) * (ty1 - ty0 + 1)];
        int loaded = 0;
        for (int tx = tx0; tx <= tx1; tx++)
        {
            for (int ty = ty0; ty <= ty1; ty++)
            {
                NavGridTile tile = TryLoad(mapId, tx, ty);
                if (tile == null) continue;
                tiles[(tx - tx0) * (ty1 - ty0 + 1) + (ty - ty0)] = tile;
                loaded++;
            }
        }
        if (loaded == 0) return empty;

        var finder = new NavGridFinder(tx0, ty0, tx1 - tx0 + 1, ty1 - ty0 + 1,
                                       (32 - ty0) * NavGridTile.AdtYd,
                                       (32 - tx0) * NavGridTile.AdtYd, tiles);
        return finder.FindPath(sx, sy, sz, gx, gy, gz);
    }

    /// <summary>
    /// The same assertions the C suite runs, on tiles built in memory. This is the
    /// cheapest way to prove the C# port still matches the tested reference.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0;
        int total = 0;
        var failures = new StringBuilder();

        Action<string, bool> check = (name, ok) =>
        {
            total++;
            if (ok) passed++;
            else failures.Append(" [").Append(name).Append(']');
        };

        // 1. Open ground: two points, straight line, heights carried.
        NavGridTile open = NavGridTile.CreateForTest(1, 32, 32, 10f);
        NavGridFinder openFinder = new NavGridFinder(32, 32, 1, 1, 0, 0, new[] { open });
        double sx, sy, gx, gy;
        open.CellToWorld(20, 20, out sx, out sy);
        open.CellToWorld(200, 200, out gx, out gy);
        List<WoWPoint> path = openFinder.FindPath(sx, sy, 10, gx, gy, 10);
        check("openPath", path.Count == 2);
        check("openStart", path.Count > 0 && Math.Abs(path[0].X - sx) < 0.01);
        check("openEnd", path.Count > 1 && Math.Abs(path[path.Count - 1].X - gx) < 0.01);
        check("openLos", openFinder.LineWalkable(sx, sy, gx, gy));

        // 2. Wall with a gap: the path must detour and every segment must be walkable.
        NavGridTile walled = NavGridTile.CreateForTest(1, 32, 32, 4f);
        for (int row = 0; row < NavGridTile.Cells; row++)
        {
            if (row >= 120 && row <= 124) continue;
            walled.TestSet(100, row, 4f, false);
        }
        var wallFinder = new NavGridFinder(32, 32, 1, 1, 0, 0, new[] { walled });
        walled.CellToWorld(50, 60, out sx, out sy);
        walled.CellToWorld(150, 60, out gx, out gy);
        check("wallBlocksStraight", !wallFinder.LineWalkable(sx, sy, gx, gy));
        List<WoWPoint> detour = wallFinder.FindPath(sx, sy, 4, gx, gy, 4);
        check("detourFound", detour.Count >= 2);
        bool segmentsOk = true;
        double length = 0;
        for (int i = 1; i < detour.Count; i++)
        {
            if (!wallFinder.LineWalkable(detour[i - 1].X, detour[i - 1].Y, detour[i].X, detour[i].Y))
                segmentsOk = false;
            double dx = detour[i].X - detour[i - 1].X;
            double dy = detour[i].Y - detour[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        check("segmentsWalkable", segmentsOk);
        check("detourLonger", length > 220.0);

        // 3. Sealed box: no path at all.
        NavGridTile boxed = NavGridTile.CreateForTest(1, 32, 32, 0f);
        for (int row = 196; row <= 204; row++)
        {
            for (int col = 196; col <= 204; col++)
            {
                if (row == 196 || row == 204 || col == 196 || col == 204)
                    boxed.TestSet(col, row, 0f, false);
            }
        }
        var boxFinder = new NavGridFinder(32, 32, 1, 1, 0, 0, new[] { boxed });
        boxed.CellToWorld(40, 40, out sx, out sy);
        boxed.CellToWorld(200, 200, out gx, out gy);
        check("sealedBox", boxFinder.FindPath(sx, sy, 0, gx, gy, 0).Count == 0);

        // 4. Diagonal pinch: refused, so the route goes around.
        NavGridTile pinch = NavGridTile.CreateForTest(1, 32, 32, 0f);
        pinch.TestSet(151, 150, 0f, false);
        pinch.TestSet(150, 151, 0f, false);
        var pinchFinder = new NavGridFinder(32, 32, 1, 1, 0, 0, new[] { pinch });
        pinch.CellToWorld(150, 150, out sx, out sy);
        pinch.CellToWorld(151, 151, out gx, out gy);
        List<WoWPoint> around = pinchFinder.FindPath(sx, sy, 0, gx, gy, 0);
        bool aroundOk = around.Count >= 2;
        double aroundLength = 0;
        for (int i = 1; i < around.Count; i++)
        {
            double dx = around[i].X - around[i - 1].X;
            double dy = around[i].Y - around[i - 1].Y;
            aroundLength += Math.Sqrt(dx * dx + dy * dy);
        }
        check("pinchNoCut", aroundOk && aroundLength > 3.0);

        // 5. Tile mapping: the client's own convention, including negatives.
        int tx, ty;
        NavGridFinder.TileOf(-100, -100, out tx, out ty);
        check("tileOfNegative", tx == 32 && ty == 32);
        NavGridFinder.TileOf(100, -600, out tx, out ty);
        check("tileOfMixed", tx == 33 && ty == 32);
        NavGridFinder.TileOf(-600, -100, out tx, out ty);
        check("tileOfWest", tx == 32 && ty == 33);

        if (failures.Length == 0)
            return string.Format(CultureInfo.InvariantCulture, "NavGrid self-test {0}/{1} passed", passed, total);
        return string.Format(CultureInfo.InvariantCulture, "NavGrid self-test {0}/{1} FAILED:{2}",
                             passed, total, failures);
    }
}

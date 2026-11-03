using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Read-only, English-label image research. A match is NOT authorization to click:
// overlays, UI scale, locale, window geometry, and lookalike dialogs can fool it.
internal static class WowBCorpseDialogPattern
{
    private static readonly string[] ReleaseText = {
        "....................................................##...........................",
        "#####..........#..................................##.##..........#........#......",
        "#...#..........#..................................#..............................",
        "#...#....###...#....###....###...####...###.......##.....#####...#...###..#...###",
        "####....#...#..#...#...#.....#...#.....#...#.......###...#...##..#...##...#...#..",
        "#.##...######..#..######....##...###..######.........##..#....#..#...#....#...#..",
        "#..##...#......#...#......#..#.....##..#..............#..#....#..#...#....#...#..",
        "#...#...#......#...#.....##..#......#..#..............#..#...##..#...#....#...#..",
        "##...#...####..#....####..#####..####...####......####...#.###...#...#....#....##",
        ".........................................................#.......................",
        ".........................................................#.......................",
        ".........................................................#.......................",
    };

    private static readonly string[] ResurrectText = {
        "##.##......................................................................................##",
        "#...#.......................................................................................#",
        "#...#....###....##...#...#...###..###...###....###...###......#.##.....####..##...#...#.....#",
        "#..#....#...#..#.....#...#...#....#....#...#..#......#........#...#...#...##..#..##.........#",
        "#.##....#####..##....#...#...#....#....#####..#......#........#...#...#....#..#..##..#.....#.",
        "#..#....#........##..#...#...#....#....#......#......#........#...#...#....#...#...#.#....#..",
        "#...#...#.........#..#...#...#....#....#......#......#........#...#...#...##...##..##........",
        "##...#...####..####...####...#....#.....####...####...#.......#...#....####....##...#.....#..",
    };

    private static readonly string[] AcceptText = {
        "..###.....................................",
        "..#.#.....................................",
        "..#.#......###....###....###...#####...###",
        ".#..##....#......#......#...#..#...##..#..",
        ".#####....#......#.....######..#....#..#..",
        ".#...##..##.....##......#......#....#..#..",
        "#.....#...#......#......#......#...##..#..",
        "#.....##...####...####...####..#.###....##",
        "...............................#..........",
        "...............................#..........",
        "...............................#..........",
    };

    internal readonly struct Result
    {
        internal readonly bool ReleaseText, ResurrectText, AcceptText;
        internal readonly int ReleaseX, ReleaseY, ResurrectX, ResurrectY, AcceptX, AcceptY;
        internal readonly bool ReleaseUnique, ResurrectUnique, AcceptUnique, ReleaseButtonRed, AcceptButtonRed;
        internal Result(bool release, bool resurrect, bool accept,
                        int rx, int ry, int tx, int ty, int ax, int ay,
                        bool ru, bool tu, bool au, bool releaseRed, bool acceptRed)
        { ReleaseText = release; ResurrectText = resurrect; AcceptText = accept;
          ReleaseX = rx; ReleaseY = ry; ResurrectX = tx; ResurrectY = ty; AcceptX = ax; AcceptY = ay;
          ReleaseUnique = ru; ResurrectUnique = tu; AcceptUnique = au;
          ReleaseButtonRed = releaseRed; AcceptButtonRed = acceptRed; }
    }

    private sealed class Pattern
    {
        internal readonly int Width, Height;
        internal readonly bool[] Pixels;
        internal readonly int[] Anchors, BlankAnchors;
        internal readonly int Ink;
        internal Pattern(string[] rows, double scale)
        {
            Width = Math.Max(1, (int)Math.Round(rows[0].Length * scale));
            Height = Math.Max(1, (int)Math.Round(rows.Length * scale));
            Pixels = new bool[Width * Height];
            var positive = new List<int>();
            var blank = new List<int>();
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    bool marked = rows[Math.Min(rows.Length - 1, (int)(y / scale))][Math.Min(rows[0].Length - 1, (int)(x / scale))] == '#';
                    Pixels[y * Width + x] = marked;
                    if (marked) positive.Add(y * Width + x);
                    else blank.Add(y * Width + x);
                }
            Ink = positive.Count;
            Anchors = new int[Math.Min(16, Ink)];
            for (int i = 0; i < Anchors.Length; i++) Anchors[i] = positive[i * Ink / Anchors.Length];
            BlankAnchors = new int[Math.Min(16, blank.Count)];
            for (int i = 0; i < BlankAnchors.Length; i++) BlankAnchors[i] = blank[i * blank.Count / BlankAnchors.Length];
        }
    }

    // Only the screenshot's native glyph size is supported. Offline rescaling
    // of the supplied screenshots caused misses, so do not imply scale support.
    private static readonly double[] Scales = { 1.0 };
    private static readonly Pattern[] ReleasePatterns = Build(ReleaseText);
    private static readonly Pattern[] ResurrectPatterns = Build(ResurrectText);
    private static readonly Pattern[] AcceptPatterns = Build(AcceptText);
    private static Pattern[] Build(string[] rows)
    {
        var result = new Pattern[Scales.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new Pattern(rows, Scales[i]);
        return result;
    }

    // Input is top-down 32-bit BGRA. No window read, input, or filesystem access.
    internal static Result Inspect(byte[] bgra, int width, int height, int stride)
    {
        if (bgra == null || width < 320 || height < 240 || width > 8192 || height > 8192 ||
            stride < width * 4 || (long)stride * height > bgra.Length)
            return default;
        bool release = FindButton(bgra, width, height, stride, ReleasePatterns, out int rx, out int ry, out bool ru);
        bool resurrect = Find(bgra, width, height, stride, ResurrectPatterns, 0, out int tx, out int ty, out bool tu);
        bool accept = FindButton(bgra, width, height, stride, AcceptPatterns, out int ax, out int ay, out bool au);
        return new Result(release, resurrect, accept, rx, ry, tx, ty, ax, ay, ru, tu, au,
                          release && RedButton(bgra, width, height, stride, rx, ry),
                          accept && RedButton(bgra, width, height, stride, ax, ay));
    }

    // A text match alone never authorizes input: require one uniquely located
    // label on a red button, and the resurrection heading above Accept.
    internal static bool SuitableForClick(Result result, bool release, out int x, out int y)
    {
        x = y = 0;
        if (release)
        {
            if (!result.ReleaseText || !result.ReleaseUnique || !result.ReleaseButtonRed || result.ResurrectText) return false;
            x = result.ReleaseX; y = result.ReleaseY; return true;
        }
        if (result.ReleaseText || !result.ResurrectText || !result.ResurrectUnique || !result.AcceptText ||
            !result.AcceptUnique || !result.AcceptButtonRed || Math.Abs(result.ResurrectX - result.AcceptX) > 15 ||
            result.AcceptY - result.ResurrectY < 15 || result.AcceptY - result.ResurrectY > 45) return false;
        x = result.AcceptX; y = result.AcceptY; return true;
    }

    // Background capture is accepted for input only when our posted hover
    // causes a visible change on the already-matched button. This rejects
    // black/unchanging or stale PrintWindow frames; it is not proof of identity.
    internal static bool ButtonChanged(byte[] before, byte[] after, int width, int height, int x, int y)
    {
        if (before == null || after == null || width < 100 || height < 20 ||
            (long)width * height * 4 > before.Length || (long)width * height * 4 > after.Length ||
            x < 50 || x >= width - 50 || y < 8 || y >= height - 8) return false;
        int changed = 0;
        for (int row = y - 8; row <= y + 7; row++)
        for (int col = x - 50; col <= x + 49; col++)
        {
            int i = (row * width + col) * 4;
            if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) +
                Math.Abs(before[i + 2] - after[i + 2]) >= 30 && ++changed >= 20) return true;
        }
        return false;
    }

    private static bool RedButton(byte[] pixels, int width, int height, int stride, int x, int y)
    {
        int[] dx = { -45, -35, 35, 45 };
        int hits = 0;
        foreach (int offset in dx)
        {
            int px = x + offset, py = y - 7;
            if (px < 0 || px >= width || py < 0 || py >= height) return false;
            int pos = py * stride + px * 4;
            int b = pixels[pos], g = pixels[pos + 1], r = pixels[pos + 2];
            if (r >= 60 && r >= 2 * g + 30 && r >= 2 * b + 30) hits++;
        }
        return hits == dx.Length;
    }

    private static bool InkAt(byte[] pixels, int pos, int palette)
    {
        int b = pixels[pos], g = pixels[pos + 1], r = pixels[pos + 2];
        bool white = r > 155 && g > 155 && b > 155 && Math.Abs(r - g) < 45 && Math.Abs(g - b) < 45;
        bool yellow = r > 140 && g > 90 && b < 110 && r > g && 10 * g > 13 * b;
        // The supplied 70707 hovered button renders the light glyphs with a
        // red tint from the button glow; strict neutral-white misses them.
        bool hovered = r >= 140 && g >= 100 && b >= 90 && 20 * g > 11 * r && 20 * b > 11 * r;
        return palette == 0 ? white : palette == 1 ? yellow : palette == 3 ? hovered : white || yellow;
    }

    private static bool FindButton(byte[] pixels, int width, int height, int stride, Pattern[] patterns,
                                   out int x, out int y, out bool unique)
    {
        // Keep the original yellow search independent of the brighter hover
        // search so white neighboring text cannot invalidate an existing match.
        bool yellow = Find(pixels, width, height, stride, patterns, 1, out int yx, out int yy, out bool yu);
        bool white = Find(pixels, width, height, stride, patterns, 0, out int wx, out int wy, out bool wu);
        bool hovered = Find(pixels, width, height, stride, patterns, 3, out int hx, out int hy, out bool hu);
        if (!yellow && !white && !hovered)
            return Find(pixels, width, height, stride, patterns, 2, out x, out y, out unique);
        x = yellow ? yx : white ? wx : hx; y = yellow ? yy : white ? wy : hy;
        unique = (!yellow || yu) && (!white || wu) && (!hovered || hu) &&
                 (!yellow || (Math.Abs(yx - x) <= 3 && Math.Abs(yy - y) <= 3)) &&
                 (!white || (Math.Abs(wx - x) <= 3 && Math.Abs(wy - y) <= 3)) &&
                 (!hovered || (Math.Abs(hx - x) <= 3 && Math.Abs(hy - y) <= 3));
        return true;
    }

    private static bool Find(byte[] pixels, int width, int height, int stride, Pattern[] patterns, int palette,
                             out int foundX, out int foundY, out bool unique)
    {
        foundX = foundY = 0;
        unique = true;
        bool found = false;
        // Only look in the central upper UI area. This is intentionally a
        // bounded diagnostic based on two screenshots, not general OCR.
        int xStart = width / 5, xEnd = width * 4 / 5;
        int yStart = height / 20, yEnd = height * 3 / 10;
        foreach (Pattern pattern in patterns)
        {
            for (int y = yStart; y <= yEnd - pattern.Height; y++)
            for (int x = xStart; x <= xEnd - pattern.Width; x++)
            {
                int matchedAnchors = 0;
                foreach (int anchor in pattern.Anchors)
                {
                    int row = anchor / pattern.Width, col = anchor % pattern.Width;
                    if (InkAt(pixels, (y + row) * stride + (x + col) * 4, palette)) matchedAnchors++;
                }
                if (matchedAnchors * 4 < pattern.Anchors.Length * 3) continue;
                int blankHits = 0;
                foreach (int anchor in pattern.BlankAnchors)
                {
                    int row = anchor / pattern.Width, col = anchor % pattern.Width;
                    if (InkAt(pixels, (y + row) * stride + (x + col) * 4, palette)) blankHits++;
                }
                if (blankHits * 4 > pattern.BlankAnchors.Length) continue;
                int hits = 0, extras = 0;
                for (int row = 0; row < pattern.Height; row++)
                for (int col = 0; col < pattern.Width; col++)
                {
                    bool actual = InkAt(pixels, (y + row) * stride + (x + col) * 4, palette);
                    if (pattern.Pixels[row * pattern.Width + col]) { if (actual) hits++; }
                    else if (actual) extras++;
                }
                int blank = pattern.Pixels.Length - pattern.Ink;
                if (hits * 100 < pattern.Ink * 85 || extras * 100 > blank * 8) continue;
                int centreX = x + pattern.Width / 2, centreY = y + pattern.Height / 2;
                if (!found) { found = true; foundX = centreX; foundY = centreY; }
                else if (Math.Abs(foundX - centreX) > 3 || Math.Abs(foundY - centreY) > 3)
                { unique = false; return true; }
            }
        }
        return found;
    }
}

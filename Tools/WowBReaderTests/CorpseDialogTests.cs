#if WOWB_READER_TESTS
using System;
using System.Reflection;
using Styx.Logic.Pathing;

internal static class CorpseDialogTests
{
    private static string[] Pattern(string name) => (string[])typeof(WowBCorpseDialogPattern)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static byte[] Draw(string[] pattern, bool yellow, int x, int y, int width, int height)
    {
        byte[] image = new byte[width * height * 4];
        for (int row = 0; row < pattern.Length; row++)
        for (int col = 0; col < pattern[row].Length; col++)
        {
            if (pattern[row][col] != '#') continue;
            int i = ((y + row) * width + x + col) * 4;
            image[i] = yellow ? (byte)0 : (byte)220;
            image[i + 1] = yellow ? (byte)170 : (byte)220;
            image[i + 2] = yellow ? (byte)220 : (byte)220;
        }
        return image;
    }

    private static void Overlay(byte[] image, byte[] source)
    {
        for (int i = 0; i < image.Length; i += 4)
            if (source[i] != 0 || source[i + 1] != 0 || source[i + 2] != 0)
                for (int j = 0; j < 4; j++) image[i + j] = source[i + j];
    }

    private static void ButtonBackground(byte[] image, int width, int x, int y)
    {
        foreach (int offset in new[] { -45, -35, 35, 45 })
            image[((y - 7) * width + x + offset) * 4 + 2] = 100;
    }

    internal static void Run()
    {
        const int width = 640, height = 480, stride = width * 4;
        var release = Draw(Pattern("ReleaseText"), true, 250, 80, width, height);
        var rezz = Draw(Pattern("ResurrectText"), false, 250, 80, width, height);
        var a = WowBCorpseDialogPattern.Inspect(release, width, height, stride);
        var b = WowBCorpseDialogPattern.Inspect(rezz, width, height, stride);
        if (!a.ReleaseText || a.ResurrectText || b.ReleaseText || !b.ResurrectText)
            throw new Exception("Dialog pattern positive/cross-label fixture failed");
        var empty = WowBCorpseDialogPattern.Inspect(new byte[stride * height], width, height, stride);
        var invalid = WowBCorpseDialogPattern.Inspect(new byte[1], width, height, stride);
        var outside = WowBCorpseDialogPattern.Inspect(Draw(Pattern("ReleaseText"), true, 250, 350, width, height), width, height, stride);
        if (empty.ReleaseText || empty.ResurrectText || invalid.ReleaseText || invalid.ResurrectText || outside.ReleaseText)
            throw new Exception("Dialog pattern blank/invalid/outside-area fixture failed");
        Array.Fill<byte>(release, 255);
        var solid = WowBCorpseDialogPattern.Inspect(release, width, height, stride);
        if (solid.ReleaseText || solid.ResurrectText) throw new Exception("Solid overlay mistaken for prompt");
        // A label by itself may classify the diagnostic but must never authorize a click.
        if (WowBCorpseDialogPattern.SuitableForClick(a, true, out _, out _) ||
            WowBCorpseDialogPattern.SuitableForClick(b, false, out _, out _))
            throw new Exception("Text alone authorized a dialog click");
        string[] releasePattern = Pattern("ReleaseText"), heading = Pattern("ResurrectText"), accept = Pattern("AcceptText");
        const int cx = 320;
        int releaseX = cx - releasePattern[0].Length / 2, releaseY = 100;
        var releaseButton = Draw(releasePattern, true, releaseX, releaseY, width, height);
        ButtonBackground(releaseButton, width, cx, releaseY + releasePattern.Length / 2);
        var releaseResult = WowBCorpseDialogPattern.Inspect(releaseButton, width, height, stride);
        if (!WowBCorpseDialogPattern.SuitableForClick(releaseResult, true, out int rx, out _) || Math.Abs(rx - cx) > 1)
            throw new Exception("Unique release button was not located");
        // Raster derived from the user's hovered-button screenshot, not a
        // recording of the game screen. Its red-tinted glyphs differ from the
        // original yellow mask and from neutral white.
        string[] hoveredRaster = {
            "...............#....................................##...........................",
            "#####..........#..................................##.##..........#........#......",
            "##..#..........#..................................#..............................",
            "##..#....###...#....###....###...###....###.......##.....#.###...#...###..#...###",
            "##.#....#...#..#...#...#.....#...#.....#...#.......###...##..##..#...##...#...#..",
            "#.##....#####..#...#####....##...##....#####.........##..#....#..#...#....#...#..",
            "#..#....#......#...#......#..#.....##..#..............#..#....#..#...#....#...#..",
            "#...#...##.....#...##.....#..#......#..#..............#..#...##..#...#....#...#..",
            "##...#...####..#....####..##..#..####...####......####...#.###...#...#....#....#.",
            ".........................................................#.......................",
            ".........................................................#.......................",
            ".........................................................#.......................",
        };
        var tinted = Draw(hoveredRaster, false, releaseX, releaseY, width, height);
        for (int i = 0; i < tinted.Length; i += 4)
            if (tinted[i] != 0) { tinted[i] = 125; tinted[i + 1] = 140; tinted[i + 2] = 220; }
        ButtonBackground(tinted, width, cx, releaseY + releasePattern.Length / 2);
        if (!WowBCorpseDialogPattern.SuitableForClick(WowBCorpseDialogPattern.Inspect(tinted, width, height, stride), true, out int tintedX, out _) ||
            tintedX != rx) throw new Exception("Screenshot-derived tinted hover raster did not match the release button");
        var highlighted = Draw(releasePattern, false, releaseX, releaseY, width, height);
        ButtonBackground(highlighted, width, cx, releaseY + releasePattern.Length / 2);
        var highlightedResult = WowBCorpseDialogPattern.Inspect(highlighted, width, height, stride);
        if (!WowBCorpseDialogPattern.SuitableForClick(highlightedResult, true, out int hx, out int hy) ||
            hx != rx || !WowBCorpseDialogPattern.ButtonChanged(releaseButton, highlighted, width, height, hx, hy) ||
            WowBCorpseDialogPattern.ButtonChanged(releaseButton, releaseButton, width, height, hx, hy) ||
            WowBCorpseDialogPattern.ButtonChanged(releaseButton, new byte[1], width, height, hx, hy))
            throw new Exception("Highlight matching or background freshness policy failed");
        Overlay(releaseButton, Draw(releasePattern, true, 420, releaseY, width, height));
        if (WowBCorpseDialogPattern.SuitableForClick(WowBCorpseDialogPattern.Inspect(releaseButton, width, height, stride), true, out _, out _))
            throw new Exception("Duplicate release labels authorized a click");
        var resurrection = Draw(heading, false, cx - heading[0].Length / 2, 80, width, height);
        int acceptY = 106;
        Overlay(resurrection, Draw(accept, true, cx - accept[0].Length / 2, acceptY, width, height));
        ButtonBackground(resurrection, width, cx, acceptY + accept.Length / 2);
        var result = WowBCorpseDialogPattern.Inspect(resurrection, width, height, stride);
        if (!WowBCorpseDialogPattern.SuitableForClick(result, false, out int ax, out _) || Math.Abs(ax - cx) > 1)
            throw new Exception("Heading and unique Accept button were not located");
        var highlightedAccept = Draw(heading, false, cx - heading[0].Length / 2, 80, width, height);
        Overlay(highlightedAccept, Draw(accept, false, cx - accept[0].Length / 2, acceptY, width, height));
        ButtonBackground(highlightedAccept, width, cx, acceptY + accept.Length / 2);
        if (!WowBCorpseDialogPattern.SuitableForClick(WowBCorpseDialogPattern.Inspect(highlightedAccept, width, height, stride), false, out _, out _))
            throw new Exception("Bright Accept button was not matched");
        var changedOutside = (byte[])resurrection.Clone();
        for (int i = 0; i < 80; i++) changedOutside[i * 4] = 200;
        if (WowBCorpseDialogPattern.ButtonChanged(resurrection, changedOutside, width, height, ax, acceptY + accept.Length / 2))
            throw new Exception("An unrelated frame change was mistaken for a button response");
        var wrongGeometry = Draw(heading, false, 240, 80, width, height);
        Overlay(wrongGeometry, Draw(accept, true, cx - accept[0].Length / 2, acceptY, width, height));
        ButtonBackground(wrongGeometry, width, cx, acceptY + accept.Length / 2);
        if (WowBCorpseDialogPattern.SuitableForClick(WowBCorpseDialogPattern.Inspect(wrongGeometry, width, height, stride), false, out _, out _))
            throw new Exception("Unrelated heading and button authorized a click");
        Console.WriteLine("Corpse dialog synthetic pattern and click-eligibility fixtures passed (no live capture or input).");
    }
}
#endif

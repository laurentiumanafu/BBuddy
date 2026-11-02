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
        Console.WriteLine("Corpse dialog synthetic pattern fixtures passed (no live capture or input).");
    }
}
#endif

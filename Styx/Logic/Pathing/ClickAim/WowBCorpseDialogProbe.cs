using System;
using System.Runtime.InteropServices;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Explicit, stopped-bot diagnostic. Reads visible desktop pixels only while the
// game is foreground; no screenshot is saved and no mouse/key input is sent.
internal static class WowBCorpseDialogProbe
{
    private static int _active;
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint lines,
        [Out] byte[] pixels, ref BitmapInfo info, uint usage);

    internal static bool Start()
    {
        if (ClickAimUi.Running || ClickAimUi.Paused || Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        int generation = ClickAimAbort.Generation;
        try
        {
            var worker = new Thread(() => Run(generation)) { IsBackground = true, Name = "CorpseDialogProbe" };
            worker.Start();
            return true;
        }
        catch { Interlocked.Exchange(ref _active, 0); throw; }
    }

    private static bool SessionStable(Memory memory, WowBReader reader, IntPtr hwnd, int generation)
        => !ClickAimUi.Running && !ClickAimUi.Paused && ClickAimAbort.Generation == generation &&
           ObjectManager.Wow == memory && ObjectManager.WowB == reader && memory.WindowHandle == hwnd;

    private static bool WindowGeometry(IntPtr hwnd, out Point point, out int width, out int height)
    {
        point = new Point(); width = height = 0;
        if (GetForegroundWindow() != hwnd || IsIconic(hwnd) || !ClientToScreen(hwnd, ref point) ||
            !ClickAimInput.ClientSize(hwnd, out width, out height) || width < 320 || height < 240 || width > 4096 || height > 2160)
            return false;
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), virtualWidth = GetSystemMetrics(78), virtualHeight = GetSystemMetrics(79);
        return virtualWidth > 0 && virtualHeight > 0 && point.X >= left && point.Y >= top &&
               (long)point.X + width <= (long)left + virtualWidth && (long)point.Y + height <= (long)top + virtualHeight;
    }

    private static byte[]? CopyVisibleClient(Point point, int width, int height)
    {
        IntPtr screen = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            memoryDc = CreateCompatibleDC(screen);
            if (memoryDc == IntPtr.Zero) return null;
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == IntPtr.Zero) return null;
            previous = SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) { previous = IntPtr.Zero; return null; }
            // Include layered windows: an overlay can make the result unreliable,
            // but no result from this diagnostic ever permits a click.
            if (!BitBlt(memoryDc, 0, 0, width, height, screen, point.X, point.Y, 0x40CC0020U)) return null;
            IntPtr removed = SelectObject(memoryDc, previous);
            if (removed == IntPtr.Zero || removed == new IntPtr(-1)) return null;
            previous = IntPtr.Zero;
            var info = new BitmapInfo { Header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width,
                Height = -height, Planes = 1, BitCount = 32, Compression = 0 } };
            var pixels = new byte[checked(width * height * 4)];
            return GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref info, 0) == height ? pixels : null;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            // Delete the DC before the bitmap so a failed restore cannot leave
            // the bitmap selected and prevent its deletion.
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Recovery calls this only with a live-session guard. The image remains
    // transient, and a match alone must never authorize input.
    internal static bool TryInspectForRecovery(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> safety,
                                               out WowBCorpseDialogPattern.Result result, out int width, out int height)
    {
        result = default; width = height = 0;
        try
        {
            if (!safety() || ObjectManager.Wow != memory || ObjectManager.WowB != reader || memory.WindowHandle != hwnd ||
                !WindowGeometry(hwnd, out Point origin, out width, out height)) return false;
            byte[]? pixels = CopyVisibleClient(origin, width, height);
            if (pixels == null || !safety() || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !WindowGeometry(hwnd, out Point after, out int finalWidth, out int finalHeight) ||
                after.X != origin.X || after.Y != origin.Y || finalWidth != width || finalHeight != height) return false;
            result = WowBCorpseDialogPattern.Inspect(pixels, width, height, width * 4);
            return safety() && ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
                   WindowGeometry(hwnd, out Point end, out int endWidth, out int endHeight) &&
                   end.X == origin.X && end.Y == origin.Y && endWidth == width && endHeight == height;
        }
        catch { return false; }
    }

    private static void Run(int generation)
    {
        try
        {
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            IntPtr hwnd = memory?.WindowHandle ?? IntPtr.Zero;
            if (memory == null || reader == null || hwnd == IntPtr.Zero) { Logging.Write("[Corpse dialog test] attach to WowB first"); return; }
            Logging.Write("[Corpse dialog test] stopped-bot visual research only: focus the game within 10 seconds. No input or screenshot file will be produced.");
            Point origin = default; int width = 0, height = 0;
            bool focused = false;
            for (int i = 0; i < 100; i++)
            {
                if (!SessionStable(memory, reader, hwnd, generation)) { Logging.Write("[Corpse dialog test] bot, attachment or STOP changed; discarded"); return; }
                if (WindowGeometry(hwnd, out origin, out width, out height)) { focused = true; break; }
                Thread.Sleep(100);
            }
            if (!focused) { Logging.Write("[Corpse dialog test] game was not foreground with a supported client size; no result"); return; }
            byte[]? pixels = CopyVisibleClient(origin, width, height);
            if (pixels == null || !SessionStable(memory, reader, hwnd, generation) ||
                !WindowGeometry(hwnd, out Point after, out int afterWidth, out int afterHeight) ||
                after.X != origin.X || after.Y != origin.Y || afterWidth != width || afterHeight != height)
            { Logging.Write("[Corpse dialog test] capture, foreground or window geometry changed; discarded"); return; }
            var result = WowBCorpseDialogPattern.Inspect(pixels, width, height, width * 4);
            if (!SessionStable(memory, reader, hwnd, generation) || GetForegroundWindow() != hwnd)
            { Logging.Write("[Corpse dialog test] state or foreground changed during analysis; discarded"); return; }
            Logging.Write("[Corpse dialog test] English-label candidate: Release Spirit=" + result.ReleaseText +
                          ", Resurrect now?=" + result.ResurrectText + ". Visual research only: overlays, scale, locale and similar dialogs can fool it; no click was sent.");
        }
        catch (Exception ex) { Logging.Write("[Corpse dialog test] failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}

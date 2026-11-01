using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Opt-in observation of the desktop cursor while the user hovers a selected NPC.
/// Never sends mouse/keyboard input or alters targeting. A Windows cursor may not
/// be the cursor drawn by the game and the mouseover address is a candidate only.
/// </summary>
internal static class WowBCursorProbe
{
    private const ulong HealthHandlerRva = 0x1D63190;
    private const string HealthSignature = "488BC4554157488D68A14881ECB80000";
    private const int WaitMilliseconds = 8000;
    private static int _active;
    public static string PathName => Path.Combine(Logging.ApplicationPath, "Settings", "wowb-cursor-samples.jsonl");

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public uint Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant, Color;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetBitmapObject(IntPtr handle, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    public static bool Start(string label, Action<string> done)
    {
        if ((label != "friendly" && label != "neutral_attackable" && label != "aggressive") || done == null ||
            ClickAimUi.Running || ClickAimUi.Paused || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return false;
        try
        {
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget || memory.WindowHandle == IntPtr.Zero)
            {
                Interlocked.Exchange(ref _active, 0);
                return false;
            }
            IntPtr hwnd = memory.WindowHandle;
            var worker = new Thread(() => Run(label, done, memory, reader, hwnd)) { IsBackground = true, Name = "WowBCursorProbe" };
            worker.Start();
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    private static bool Attached(Memory memory, WowBReader reader, IntPtr hwnd)
        => !ClickAimUi.Running && !ClickAimUi.Paused && ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
           hwnd != IntPtr.Zero && memory.WindowHandle == hwnd;

    private static bool ReadHover(Memory memory, out WowBGuid guid)
    {
        guid = default;
        byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(WowBOffsets.MouseoverGuidCandidate), 16);
        if (bytes == null || bytes.Length != 16) return false;
        guid = new WowBGuid(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
        return true;
    }

    private static bool TryCursor(IntPtr hwnd, out CursorInfo info)
    {
        info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetForegroundWindow() != hwnd || !GetCursorInfo(ref info) ||
            !ClickAimInput.TryCursorFraction(hwnd, out _, out _)) return false;
        IntPtr atPoint = WindowFromPoint(info.Position);
        return atPoint == hwnd || (atPoint != IntPtr.Zero && IsChild(hwnd, atPoint));
    }

    internal static bool SameCursor(uint flagsA, long handleA, int xA, int yA, uint flagsB, long handleB, int xB, int yB)
        => flagsA == flagsB && handleA == handleB && Math.Abs((long)xA - xB) <= 2 && Math.Abs((long)yA - yB) <= 2;

    private static void Run(string label, Action<string> done, Memory memory, WowBReader reader, IntPtr hwnd)
    {
        string result;
        try
        {
            Thread.Sleep(WaitMilliseconds); // no input is sent; the user switches to the game and hovers
            if (!Attached(memory, reader, hwnd)) { result = "Bot started or client changed; no cursor sample saved."; }
            else result = Capture(label, memory, reader, hwnd);
        }
        catch (Exception ex) { result = "Cursor sample failed; no targeting decision made: " + ex.Message; }
        finally { Interlocked.Exchange(ref _active, 0); }
        Logging.Write("[NPC cursor probe] " + result);
        try { done(result); } catch { /* The window may have closed while waiting. */ }
    }

    private static string Capture(string label, Memory memory, WowBReader reader, IntPtr hwnd)
    {
        using var uncached = memory.TemporaryCacheState(false);
        byte[]? sig = memory.ReadBytes64(memory.GetAbsolute64(HealthHandlerRva), 16);
        if (sig == null || Convert.ToHexString(sig) != HealthSignature)
            return "The 70291 signature did not match; no cursor sample saved.";
        if (!Attached(memory, reader, hwnd) || !TryCursor(hwnd, out CursorInfo first))
            return "Game must be foreground and the pointer must be over its window; no cursor sample saved.";
        if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
            !before.TryGetLocalPlayer(out WowBObservedUnit player) ||
            !before.TryGetSelectedTarget(out WowBObservedUnit target) || target.IsLocalPlayer || target.HasPlayerVtable)
            return "Select a living NPC, then hover that same NPC; no cursor sample saved.";
        bool hoverRead = ReadHover(memory, out WowBGuid hover);
        CursorImage? image = ReadCursorImage(first.Cursor);
        Thread.Sleep(250);
        if (!Attached(memory, reader, hwnd) || !TryCursor(hwnd, out CursorInfo last) ||
            !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                        last.Flags, last.Cursor.ToInt64(), last.Position.X, last.Position.Y))
            return "Cursor, foreground window or client changed; no cursor sample saved. Keep the cursor still.";
        CursorImage? imageAfter = ReadCursorImage(last.Cursor);
        if (image == null || imageAfter == null || image.fingerprint != imageAfter.fingerprint)
            image = null; // animated/changing/unreadable cursor images are not a stable fingerprint
        if (!Attached(memory, reader, hwnd) || !TryCursor(hwnd, out CursorInfo finalCursor) ||
            !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                        finalCursor.Flags, finalCursor.Cursor.ToInt64(), finalCursor.Position.X, finalCursor.Position.Y))
            return "Cursor, foreground window or client changed; no cursor sample saved. Keep the cursor still.";
        if (!ReadHover(memory, out WowBGuid hoverAfter)) hoverRead = false;
        if (hoverRead && hoverAfter != hover) return "Mouseover candidate changed during capture; no cursor sample saved.";
        if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
            after.LocalGuid != before.LocalGuid || after.TargetGuid != before.TargetGuid ||
            !after.TryGetLocalPlayer(out WowBObservedUnit playerAfter) || !after.TryGetSelectedTarget(out WowBObservedUnit targetAfter) ||
            playerAfter.RecordAddress != player.RecordAddress || playerAfter.UnitAddress != player.UnitAddress ||
            targetAfter.RecordAddress != target.RecordAddress || targetAfter.UnitAddress != target.UnitAddress || !Attached(memory, reader, hwnd))
            return "Player, target or client changed during capture; no cursor sample saved.";
        bool matchesSelected = hoverRead && !hover.IsEmpty && hover == target.Guid;
        var sample = new
        {
            schema = 1, utc = DateTime.UtcNow.ToString("O"), label,
            playerGuid = player.Guid.ToString(), targetGuid = target.Guid.ToString(),
            targetEntryHint = (target.Guid.High >> 6) & 0x7FFFFFUL,
            hoverGuid = hoverRead ? hover.ToString() : null, matchesSelected,
            cursorFlags = first.Flags, cursorShowing = (first.Flags & 1) != 0 && (first.Flags & 2) == 0,
            cursorHandle = first.Cursor.ToInt64().ToString("X"), cursorImage = image,
            // A label is supplied by the user; no game-computed attackability is recorded.
            warning = "desktop cursor and mouseover candidate are not proven attackability"
        };
        string path = PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.AppendAllText(path, JsonSerializer.Serialize(sample) + Environment.NewLine);
        return "Saved " + label + "; hover " + (matchesSelected ? "matches selected GUID (candidate only)" : "did NOT match selected GUID (inconclusive)") +
               "; Windows cursor " + (((first.Flags & 1) != 0 && (first.Flags & 2) == 0) ? "shown" : "hidden or suppressed") +
               "; image fingerprint " + (image == null ? "unavailable" : image.fingerprint) + ". Saved to " + path;
    }

    private sealed class CursorImage
    {
        public bool isIcon { get; set; }
        public uint hotspotX { get; set; }
        public uint hotspotY { get; set; }
        public int maskWidth { get; set; }
        public int maskHeight { get; set; }
        public int colorWidth { get; set; }
        public int colorHeight { get; set; }
        public string fingerprint { get; set; } = "";
    }

    private static CursorImage? ReadCursorImage(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetIconInfo(handle, out IconInfo icon)) return null;
        IntPtr dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero || !ReadBitmap(dc, icon.Mask, out int mw, out int mh, out byte[]? mask)) return null;
            int cw = 0, ch = 0;
            byte[]? color = null;
            if (icon.Color != IntPtr.Zero && !ReadBitmap(dc, icon.Color, out cw, out ch, out color)) return null;
            if (mask == null) return null;
            return new CursorImage { isIcon = icon.IsIcon, hotspotX = icon.HotspotX, hotspotY = icon.HotspotY,
                maskWidth = mw, maskHeight = mh, colorWidth = cw, colorHeight = ch,
                fingerprint = Fingerprint(icon.HotspotX, icon.HotspotY, mw, mh, mask, cw, ch, color) };
        }
        finally
        {
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }

    private static bool ReadBitmap(IntPtr dc, IntPtr bitmap, out int width, out int height, out byte[]? pixels)
    {
        width = height = 0; pixels = null;
        if (bitmap == IntPtr.Zero || GetBitmapObject(bitmap, Marshal.SizeOf<Bitmap>(), out Bitmap b) == 0 ||
            b.Width <= 0 || b.Width > 512 || b.Height <= 0 || b.Height > 512) return false;
        width = b.Width; height = b.Height;
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        byte[] buffer = new byte[width * height * 4];
        if (GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref info, 0) != height) return false;
        pixels = buffer;
        return true;
    }

    internal static string Fingerprint(uint hx, uint hy, int mw, int mh, byte[] mask, int cw, int ch, byte[]? color)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
        {
            writer.Write(hx); writer.Write(hy); writer.Write(mw); writer.Write(mh); writer.Write(mask.Length); writer.Write(mask);
            writer.Write(cw); writer.Write(ch); writer.Write(color?.Length ?? -1);
            if (color != null) writer.Write(color);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}

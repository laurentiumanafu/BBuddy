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
/// Passive capture never sends input. A separate opt-in background experiment posts
/// only mouse moves, never clicks or keys. Neither mode alters targeting. A Windows
/// cursor may not be game-drawn and the mouseover address is a candidate only.
/// The separate opt-in Grind pool gate uses guarded posted moves but never bypasses the pool failsafe.
/// </summary>
internal static class WowBCursorProbe
{
    private const ulong HealthHandlerRva = 0x1D63190;
    private const string HealthSignature = "488BC4554157488D68A14881ECB80000";
    private const int WaitMilliseconds = 8000;
    // Observed on one user's 70291 installation. These are *positive diagnostic
    // hints only*, never proof of attackability, hostility, range or input authority.
    private const string ObservedHighlightedDagger = "A3672FD4057306DCC48DADA960AE5F4BA98C0D111FC1D5A733B67F36F9FA1587";
    private const string ObservedDarkDagger = "90D89DC029CDCC1BD699CA22E46676485C43AEEF015EBC476FB539E5613039E3";
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
        if ((label != "friendly" && label != "neutral_attackable" && label != "aggressive" && label != "off_target_control") || done == null ||
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

    internal static string DaggerHint(string label, bool visible, bool candidateMatches, string? fingerprint)
    {
        // An off-target control or unmatched/hidden/unreadable cursor is never
        // evidence for a dagger over the selection. Unknown is NOT friendly.
        if (label == "off_target_control" || !visible || !candidateMatches || fingerprint == null) return "unknown";
        if (fingerprint == ObservedHighlightedDagger) return "observed_highlighted_dagger";
        if (fingerprint == ObservedDarkDagger) return "observed_dark_dagger";
        return "unknown";
    }

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
        bool cursorShowing = (first.Flags & 1) != 0 && (first.Flags & 2) == 0;
        string daggerHint = DaggerHint(label, cursorShowing, matchesSelected, image?.fingerprint);
        var sample = new
        {
            schema = 1, utc = DateTime.UtcNow.ToString("O"), label,
            playerGuid = player.Guid.ToString(), targetGuid = target.Guid.ToString(),
            targetEntryHint = (target.Guid.High >> 6) & 0x7FFFFFUL,
            hoverGuid = hoverRead ? hover.ToString() : null, matchesSelected,
            cursorFlags = first.Flags, cursorShowing,
            cursorHandle = first.Cursor.ToInt64().ToString("X"), cursorImage = image,
            daggerHint,
            // A label is supplied by the user; no game-computed attackability is recorded.
            warning = "desktop cursor and mouseover candidate are not proven attackability"
        };
        string path = PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.AppendAllText(path, JsonSerializer.Serialize(sample) + Environment.NewLine);
        return "Saved " + label + "; hover " + (matchesSelected ? "matches selected GUID (candidate only; may be stale)" : (label == "off_target_control" ? "did NOT match selected GUID" : "did NOT match selected GUID (inconclusive)")) +
               "; Windows cursor " + (((first.Flags & 1) != 0 && (first.Flags & 2) == 0) ? "shown" : "hidden or suppressed") +
               "; image fingerprint " + (image == null ? "unavailable" : image.fingerprint) +
               "; experimental dagger hint " + daggerHint + " (not an attackability verdict). Saved to " + path;
    }

    // A prepared point is private to this process and expires quickly. It is never
    // a click coordinate or an authorization to attack.
    private sealed class PostedSetup
    {
        public Memory Memory = null!;
        public WowBReader Reader = null!;
        public IntPtr Hwnd;
        public int Width, Height, OriginX, OriginY, X, Y, PreparedAt, StopGeneration;
        public WowBObservedUnit Player, Target;
        public string ForegroundFingerprint = "";
    }
    private static PostedSetup? _postedSetup;
    public static string PostedPathName => Path.Combine(Logging.ApplicationPath, "Settings", "wowb-posted-hover-samples.jsonl");

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private static bool MouseButtonsUp()
        => (GetAsyncKeyState(0x01) & 0x8000) == 0 && (GetAsyncKeyState(0x02) & 0x8000) == 0 &&
           (GetAsyncKeyState(0x04) & 0x8000) == 0 && (GetAsyncKeyState(0x05) & 0x8000) == 0 &&
           (GetAsyncKeyState(0x06) & 0x8000) == 0;

    private static bool SameLocation(WowBObservedUnit a, WowBObservedUnit b)
    {
        double dx = a.Location.X - b.Location.X, dy = a.Location.Y - b.Location.Y, dz = a.Location.Z - b.Location.Z;
        return double.IsFinite(dx) && double.IsFinite(dy) && double.IsFinite(dz) && dx * dx + dy * dy + dz * dz <= 0.25;
    }

    internal static string ActorChangeReason(WowBObservedUnit expected, WowBObservedUnit actual, bool isTarget)
    {
        string name = isTarget ? "selected NPC" : "player";
        if (actual.Guid != expected.Guid || actual.RecordAddress != expected.RecordAddress || actual.UnitAddress != expected.UnitAddress)
            return name + " identity changed";
        if (!SameLocation(expected, actual)) return name + " moved more than 0.5 yards, or position is unreadable";
        return "";
    }

    internal static bool SameActor(WowBObservedUnit a, WowBObservedUnit b) => ActorChangeReason(a, b, false).Length == 0;

    private static bool Layout(PostedSetup setup)
    {
        var origin = new Point();
        return ClickAimInput.ClientSize(setup.Hwnd, out int w, out int h) && w == setup.Width && h == setup.Height &&
            ClientToScreen(setup.Hwnd, ref origin) && origin.X == setup.OriginX && origin.Y == setup.OriginY;
    }

    private static bool SameWorld(PostedSetup setup, out string reason)
    {
        reason = "Stop was pressed after preparation";
        if (ClickAimAbort.Generation != setup.StopGeneration) return false;
        reason = "client attachment changed, or the bot started or paused";
        if (!Attached(setup.Memory, setup.Reader, setup.Hwnd)) return false;
        reason = "game window position or size changed";
        if (!Layout(setup)) return false;
        reason = "could not read the current player and selected NPC";
        if (!setup.Reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null ||
            !snap.TryGetLocalPlayer(out WowBObservedUnit player) || !snap.TryGetSelectedTarget(out WowBObservedUnit target))
            return false;
        reason = "selected target is no longer an NPC";
        if (target.IsLocalPlayer || target.HasPlayerVtable) return false;
        reason = ActorChangeReason(setup.Target, target, true);
        if (reason.Length != 0) return false;
        reason = ActorChangeReason(setup.Player, player, false);
        if (reason.Length != 0) return false;
        reason = "player facing changed or is unreadable";
        if (!float.IsFinite(player.Facing) || !float.IsFinite(setup.Player.Facing) ||
            Math.Abs(player.Facing - setup.Player.Facing) > 0.02) return false;
        reason = "selected NPC health is zero or could not be read";
        if (!setup.Reader.TryGetUnitFields(target.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health == 0) return false;
        reason = "player health/combat state could not be read, is zero, or the provisional combat bit is set";
        if (!setup.Reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields self) ||
            self.Health == 0 || (self.UnitFlags & 0x00080000U) != 0) return false;
        reason = "Stop was pressed, client changed, or the bot started during the check";
        if (ClickAimAbort.Generation != setup.StopGeneration || !Attached(setup.Memory, setup.Reader, setup.Hwnd)) return false;
        reason = "";
        return true;
    }

    // These two operations deliberately share _active with the passive probe.
    // Only the second operation posts WM_MOUSEMOVE, and only after explicit consent.
    public static bool StartPosted(bool prepare, Action<string> done)
    {
        if (done == null || ClickAimUi.Running || ClickAimUi.Paused || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
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
            if (prepare) _postedSetup = null;
            IntPtr hwnd = memory.WindowHandle;
            int stopGeneration = ClickAimAbort.Generation;
            var worker = new Thread(() => RunPosted(prepare, done, memory, reader, hwnd, stopGeneration))
                { IsBackground = true, Name = "WowBPostedHoverProbe" };
            worker.Start();
            return true;
        }
        catch { Interlocked.Exchange(ref _active, 0); throw; }
    }

    private static void RunPosted(bool prepare, Action<string> done, Memory memory, WowBReader reader, IntPtr hwnd, int stopGeneration)
    {
        string result;
        try
        {
            Thread.Sleep(prepare ? WaitMilliseconds : 3000);
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? sig = memory.ReadBytes64(memory.GetAbsolute64(HealthHandlerRva), 16);
            if (ClickAimAbort.Generation != stopGeneration)
                result = "Stop was pressed during the countdown; test cancelled.";
            else if (!Attached(memory, reader, hwnd))
                result = "Client changed, or the bot started or paused during the countdown; test cancelled.";
            else if (sig == null || Convert.ToHexString(sig) != HealthSignature)
                result = "70291 signature could not be read or did not match; test cancelled.";
            else result = prepare ? PreparePosted(memory, reader, hwnd, stopGeneration) : RunPostedBackground(memory, reader, hwnd);
        }
        catch (Exception ex) { result = "Posted-hover test stopped: " + ex.Message; }
        finally { Interlocked.Exchange(ref _active, 0); }
        Logging.Write("[NPC posted-hover probe] " + result);
        try { done(result); } catch { }
    }

    private static string PreparePosted(Memory memory, WowBReader reader, IntPtr hwnd, int stopGeneration)
    {
        if (!TryCursor(hwnd, out CursorInfo first) || !MouseButtonsUp() ||
            !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null ||
            !snap.TryGetLocalPlayer(out WowBObservedUnit player) || !snap.TryGetSelectedTarget(out WowBObservedUnit target) ||
            target.IsLocalPlayer || target.HasPlayerVtable ||
            !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields self) || self.Health == 0 ||
            (self.UnitFlags & 0x00080000U) != 0 ||
            !reader.TryGetUnitFields(target.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health == 0 ||
            !ReadHover(memory, out WowBGuid hover) || hover != target.Guid || hover.IsEmpty)
            return "Preparation failed: stop the bot, avoid combat, select a living NPC, foreground the game and hover that same NPC with buttons released.";
        if (!ClickAimInput.ClientSize(hwnd, out int w, out int h) || w < 10 || h < 10) return "Game client size unavailable.";
        var origin = new Point();
        var point = first.Position;
        if (!ClientToScreen(hwnd, ref origin) || !ScreenToClient(hwnd, ref point) ||
            point.X < 2 || point.Y < 2 || point.X >= w - 2 || point.Y >= h - 2) return "Hover point is outside the game client.";
        CursorImage? image = ReadCursorImage(first.Cursor);
        Thread.Sleep(250);
        if (!Attached(memory, reader, hwnd) || !TryCursor(hwnd, out CursorInfo last) || !MouseButtonsUp() ||
            !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                        last.Flags, last.Cursor.ToInt64(), last.Position.X, last.Position.Y) ||
            !ReadHover(memory, out WowBGuid lastHover) || lastHover != target.Guid)
            return "Foreground hover changed; prepare again and hold still.";
        CursorImage? imageAfter = ReadCursorImage(last.Cursor);
        if ((first.Flags & 1) == 0 || (first.Flags & 2) != 0 || image == null || imageAfter == null || image.fingerprint != imageAfter.fingerprint)
            return "Foreground Windows cursor is hidden, unavailable or changing; prepare again.";
        var setup = new PostedSetup { Memory = memory, Reader = reader, Hwnd = hwnd, Width = w, Height = h,
            OriginX = origin.X, OriginY = origin.Y, X = point.X, Y = point.Y, Player = player, Target = target,
            PreparedAt = Environment.TickCount, StopGeneration = stopGeneration, ForegroundFingerprint = image.fingerprint };
        if (!SameWorld(setup, out string reason)) return "Preparation cancelled: " + reason + ". Prepare again.";
        if (!TryCursor(hwnd, out CursorInfo final) ||
            !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                        final.Flags, final.Cursor.ToInt64(), final.Position.X, final.Position.Y))
            return "Target, player, window or cursor changed; prepare again.";
        _postedSetup = setup;
        return "Foreground hover prepared. Without moving the character, camera, NPC or game window, return here and press step 2 within two minutes.";
    }

    private sealed class PostedObservation
    {
        public string? hoverGuid { get; set; }
        public bool? matchesTarget { get; set; }
        public uint cursorFlags { get; set; }
        public bool cursorShowing { get; set; }
        public string cursorHandle { get; set; } = "";
        public string? cursorFingerprint { get; set; }
    }

    private static bool DesktopGuard(PostedSetup setup, IntPtr foreground, Point start, out string reason)
    {
        reason = "game must be in the background and the same other window must stay foreground";
        if (foreground == IntPtr.Zero || foreground == setup.Hwnd || GetForegroundWindow() != foreground) return false;
        reason = "a physical mouse button is held";
        if (!MouseButtonsUp()) return false;
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        reason = "Windows cursor position could not be read";
        if (!GetCursorInfo(ref cursor)) return false;
        reason = "desktop cursor moved after the 3-second countdown";
        if (Math.Abs((long)cursor.Position.X - start.X) > 2 || Math.Abs((long)cursor.Position.Y - start.Y) > 2) return false;
        reason = "foreground preparation expired (two-minute limit)";
        int age = unchecked(Environment.TickCount - setup.PreparedAt);
        if (age < 0 || age > 120000) return false;
        reason = "Stop was pressed after preparation";
        if (ClickAimAbort.Generation != setup.StopGeneration) return false;
        reason = "";
        return true;
    }

    private static bool BackgroundGuard(PostedSetup setup, IntPtr foreground, Point start, out string reason)
        => DesktopGuard(setup, foreground, start, out reason) && SameWorld(setup, out reason) &&
           DesktopGuard(setup, foreground, start, out reason);

    private static bool ObservePosted(PostedSetup setup, IntPtr foreground, Point start, out PostedObservation observation, out string reason)
    {
        observation = new PostedObservation();
        if (!BackgroundGuard(setup, foreground, start, out reason)) return false;
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        reason = "Windows cursor could not be read";
        if (!GetCursorInfo(ref cursor)) return false;
        bool read = ReadHover(setup.Memory, out WowBGuid hover);
        CursorImage? image = ReadCursorImage(cursor.Cursor);
        Thread.Sleep(100);
        var last = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!BackgroundGuard(setup, foreground, start, out reason)) return false;
        reason = "Windows cursor state could not be read or changed during sampling (not necessarily physical movement)";
        if (!GetCursorInfo(ref last) ||
            !SameCursor(cursor.Flags, cursor.Cursor.ToInt64(), cursor.Position.X, cursor.Position.Y,
                        last.Flags, last.Cursor.ToInt64(), last.Position.X, last.Position.Y)) return false;
        CursorImage? lastImage = ReadCursorImage(last.Cursor);
        if (!ReadHover(setup.Memory, out WowBGuid lastHover) || !read || lastHover != hover) read = false;
        var final = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!BackgroundGuard(setup, foreground, start, out reason)) return false;
        reason = "Windows cursor state could not be read or changed during sampling (not necessarily physical movement)";
        if (!GetCursorInfo(ref final) ||
            !SameCursor(cursor.Flags, cursor.Cursor.ToInt64(), cursor.Position.X, cursor.Position.Y,
                        final.Flags, final.Cursor.ToInt64(), final.Position.X, final.Position.Y)) return false;
        observation = new PostedObservation { hoverGuid = read ? hover.ToString() : null,
            matchesTarget = read ? hover == setup.Target.Guid : (bool?)null,
            cursorFlags = cursor.Flags, cursorShowing = (cursor.Flags & 1) != 0 && (cursor.Flags & 2) == 0,
            cursorHandle = cursor.Cursor.ToInt64().ToString("X"),
            cursorFingerprint = image != null && lastImage != null && image.fingerprint == lastImage.fingerprint ? image.fingerprint : null };
        reason = "";
        return true;
    }

    internal static bool CandidateTransition(bool? awayMatches, bool? returnedMatches)
        => awayMatches == false && returnedMatches == true;

    private static string RunPostedBackground(Memory memory, WowBReader reader, IntPtr hwnd)
    {
        PostedSetup? setup = _postedSetup;
        _postedSetup = null; // one explicit click per attempt, even on failure
        if (setup == null || setup.Memory != memory || setup.Reader != reader || setup.Hwnd != hwnd)
            return "No prepared foreground hover for this client. Run step 1 first.";
        IntPtr foreground = GetForegroundWindow();
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor)) return "Before posting: Windows cursor could not be read. Prepare again.";
        Point realPosition = cursor.Position; // baseline is AFTER the user has clicked step 2 and the countdown
        if (!BackgroundGuard(setup, foreground, realPosition, out string reason))
            return "Before posting: " + reason + ". Prepare again.";
        if (!ObservePosted(setup, foreground, realPosition, out PostedObservation before, out reason))
            return "Before posting: " + reason + ". No sample saved; prepare again.";
        // Pick a point far from the recorded NPC point; it might still be UI or
        // another object. The observed GUID transition, not geometry, is the control.
        double awayX = setup.X < setup.Width / 2 ? 0.80 : 0.20;
        double awayY = setup.Y < setup.Height / 2 ? 0.80 : 0.20;
        if (!BackgroundGuard(setup, foreground, realPosition, out reason))
            return "Before move-away: " + reason + ". No sample saved; prepare again.";
        if (!ClickAimInput.PostMouseMoveFraction(hwnd, awayX, awayY))
            return "Posting the move-away failed. No sample saved; prepare again.";
        Thread.Sleep(1250); // allow a throttled background client time to process the move
        if (!ObservePosted(setup, foreground, realPosition, out PostedObservation away, out reason))
            return "After move-away: " + reason + ". No return move or sample; prepare again.";
        if (!BackgroundGuard(setup, foreground, realPosition, out reason))
            return "Before return move: " + reason + ". No return move or sample; prepare again.";
        if (!ClickAimInput.PostMouseMoveFraction(hwnd,
                setup.X / (double)(setup.Width - 1), setup.Y / (double)(setup.Height - 1)))
            return "Posting the return move failed. No sample saved; prepare again.";
        Thread.Sleep(1250); // allow a throttled background client time to process the move
        if (!ObservePosted(setup, foreground, realPosition, out PostedObservation returned, out reason))
            return "After return move: " + reason + ". No sample saved; prepare again.";
        bool transition = CandidateTransition(away.matchesTarget, returned.matchesTarget);
        var sample = new { schema = 1, utc = DateTime.UtcNow.ToString("O"),
            playerGuid = setup.Player.Guid.ToString(), targetGuid = setup.Target.Guid.ToString(),
            foregroundFingerprint = setup.ForegroundFingerprint, before, away, returned,
            candidateAwayThenTarget = transition,
            warning = "desktop cursor belongs to the foreground desktop; GUID and cursor observations are not attackability or authorization" };
        Directory.CreateDirectory(Path.GetDirectoryName(PostedPathName) ?? ".");
        File.AppendAllText(PostedPathName, JsonSerializer.Serialize(sample) + Environment.NewLine);
        return "Saved background hover test; candidate away→target transition: " + (transition ? "observed" : "NOT observed (inconclusive)") +
            "; desktop fingerprints before/away/return: " + (before.cursorFingerprint ?? "unavailable") + "/" +
            (away.cursorFingerprint ?? "unavailable") + "/" + (returned.cursorFingerprint ?? "unavailable") +
            ". Not an attackability verdict. Saved to " + PostedPathName;
    }

    // Optional Grind pool gate. A positive is only a tentative cursor hint; it
    // does not remove the existing pool target's TAB/damage failsafe.
    internal static bool PoolNeedsCursorGate(bool enabled, bool poolPick, bool freshDagger)
        => enabled && poolPick && !freshDagger;

    internal static bool ExpectedPoolTargetMatches(WowBGuid expected, WowBGuid actual)
        => expected.IsEmpty || expected == actual;

    internal static bool DaggerTransition(WowBGuid target, WowBGuid away, WowBGuid on,
                                           string? awayImage, string? onImage)
        => !target.IsEmpty && away != target && on == target && awayImage != null && onImage != null &&
           awayImage != onImage && DaggerHint("neutral_attackable", true, true, onImage) != "unknown";

    private sealed class GrindHoverContext
    {
        public Memory Memory = null!;
        public WowBReader Reader = null!;
        public IntPtr Hwnd, Foreground;
        public int Width, Height, OriginX, OriginY;
        public Point DesktopPosition;
        public WowBObservedUnit Target;
        public Func<bool> Cancel = null!;
    }

    private static bool GrindHoverGuard(GrindHoverContext c)
    {
        if (ClickAimAbort.Requested || c.Cancel() || ObjectManager.Wow != c.Memory || ObjectManager.WowB != c.Reader ||
            c.Memory.WindowHandle != c.Hwnd || c.Foreground == IntPtr.Zero || GetForegroundWindow() != c.Foreground || !MouseButtonsUp())
            return false;
        var point = new Point();
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!ClickAimInput.ClientSize(c.Hwnd, out int w, out int h) || w != c.Width || h != c.Height ||
            !ClientToScreen(c.Hwnd, ref point) || point.X != c.OriginX || point.Y != c.OriginY ||
            !GetCursorInfo(ref cursor) || Math.Abs((long)cursor.Position.X - c.DesktopPosition.X) > 2 ||
            Math.Abs((long)cursor.Position.Y - c.DesktopPosition.Y) > 2 || c.Reader.ReadTargetGuid() != c.Target.Guid)
            return false;
        return !ClickAimAbort.Requested && !c.Cancel() && ObjectManager.Wow == c.Memory && ObjectManager.WowB == c.Reader &&
            GetForegroundWindow() == c.Foreground;
    }

    private static bool WaitGrindHover(GrindHoverContext c, int milliseconds)
    {
        for (int elapsed = 0; elapsed < milliseconds; elapsed += 50)
        {
            if (!GrindHoverGuard(c) || ClickAimAbort.Wait(Math.Min(50, milliseconds - elapsed))) return false;
        }
        return GrindHoverGuard(c);
    }

    private static bool ObserveGrindHover(GrindHoverContext c, out WowBGuid hover, out string? fingerprint)
    {
        hover = default; fingerprint = null;
        if (!GrindHoverGuard(c)) return false;
        var first = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref first) || (first.Flags & 1) == 0 || (first.Flags & 2) != 0 ||
            !ReadHover(c.Memory, out WowBGuid a)) return false;
        CursorImage? image = ReadCursorImage(first.Cursor);
        if (!WaitGrindHover(c, 100)) return false;
        var last = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref last) || !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                last.Flags, last.Cursor.ToInt64(), last.Position.X, last.Position.Y)) return false;
        CursorImage? imageAfter = ReadCursorImage(last.Cursor);
        var final = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (image == null || imageAfter == null || image.fingerprint != imageAfter.fingerprint ||
            !ReadHover(c.Memory, out WowBGuid b) || a != b || !GrindHoverGuard(c) || !GetCursorInfo(ref final) ||
            !SameCursor(first.Flags, first.Cursor.ToInt64(), first.Position.X, first.Position.Y,
                        final.Flags, final.Cursor.ToInt64(), final.Position.X, final.Position.Y)) return false;
        hover = a; fingerprint = image.fingerprint;
        return true;
    }

    internal static bool TryGrindDaggerHover(IntPtr hwnd, WowBObservedUnit target, int dx, int dy,
                                              Func<bool> cancel, out string reason)
    {
        reason = "cursor check unavailable or interrupted";
        try
        {
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            if (cancel == null || ClickAimAbort.Requested || cancel() || memory == null || reader == null ||
                !memory.Is64BitTarget || memory.WindowHandle != hwnd || hwnd == IntPtr.Zero ||
                target.Guid.IsEmpty || target.IsLocalPlayer || target.HasPlayerVtable)
                return false;
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? sig = memory.ReadBytes64(memory.GetAbsolute64(HealthHandlerRva), 16);
            if (sig == null || Convert.ToHexString(sig) != HealthSignature) { reason = "70291 signature unavailable"; return false; }
            if (!ClickAimInput.ClientSize(hwnd, out int width, out int height) || width < 10 || height < 10)
            { reason = "game client size unavailable"; return false; }
            int x = width / 2 + dx, y = height / 2 + dy;
            // LeftClick clamps the input coordinates; reproduce that exact pixel.
            x = Math.Max(1, Math.Min(width - 2, x)); y = Math.Max(1, Math.Min(height - 2, y));
            var origin = new Point();
            var desktop = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!ClientToScreen(hwnd, ref origin) || !GetCursorInfo(ref desktop)) return false;
            var c = new GrindHoverContext { Memory = memory, Reader = reader, Hwnd = hwnd, Foreground = GetForegroundWindow(),
                Width = width, Height = height, OriginX = origin.X, OriginY = origin.Y,
                DesktopPosition = desktop.Position, Target = target, Cancel = cancel };
            if (!GrindHoverGuard(c)) { reason = "focus, pointer, selected target, STOP or combat changed"; return false; }
            double awayX = x < width / 2 ? 0.80 : 0.20;
            double awayY = y < height / 2 ? 0.80 : 0.20;
            if (!GrindHoverGuard(c) || ClickAimAbort.Requested || !ClickAimInput.PostMouseMoveFraction(hwnd, awayX, awayY) || !WaitGrindHover(c, 1250) ||
                !ObserveGrindHover(c, out WowBGuid away, out string? awayImage))
            { reason = "move-away or cursor observation unavailable"; return false; }
            if (away == target.Guid) { reason = "mouseover did not leave selected NPC"; return false; }
            if (!GrindHoverGuard(c) || ClickAimAbort.Requested || !ClickAimInput.PostMouseMoveFraction(hwnd, x / (double)(width - 1), y / (double)(height - 1)) ||
                !WaitGrindHover(c, 1250) || !ObserveGrindHover(c, out WowBGuid on, out string? onImage))
            { reason = "return hover or cursor observation unavailable"; return false; }
            if (!DaggerTransition(target.Guid, away, on, awayImage, onImage))
            { reason = "not one of the two observed dagger images, or selected NPC hover was not confirmed"; return false; }
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snap) || snap == null ||
                !snap.TryGetSelectedTarget(out WowBObservedUnit current) || current.Guid != target.Guid ||
                current.UnitAddress != target.UnitAddress || current.RecordAddress != target.RecordAddress ||
                !reader.TryGetUnitFields(current.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health == 0 ||
                !ReadHover(memory, out WowBGuid final) || final != target.Guid || !GrindHoverGuard(c))
            { reason = "selected NPC, mouseover, health, focus or combat changed after the cursor check"; return false; }
            reason = "known dagger fingerprint with a fresh away-to-target candidate transition (not an attackability proof)";
            return true;
        }
        catch (Exception ex) { reason = "cursor check failed: " + ex.Message; return false; }
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

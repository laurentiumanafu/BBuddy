using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Tests whether a posted, window-only mouse-move updates the candidate mouseover GUID.
/// Sends no mouse buttons, keys, movement commands, Lua or process writes.
/// </summary>
internal static class GatherHoverTest
{
    private static int _active;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    public static bool Start() => StartCore(Run, "GatherHoverTest");
    public static bool StartBackground()
    {
        int generation = ClickAimAbort.Generation;
        return StartCore(() => RunBackground(generation), "GatherBackgroundHoverTest");
    }

    private static bool StartCore(ThreadStart action, string name)
    {
        if (ClickAimUi.Running || ClickAimUi.Paused || Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        try
        {
            var thread = new Thread(action) { IsBackground = true, Name = name };
            thread.Start();
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    private static bool ReadHover(Memory memory, out WowBGuid guid)
    {
        guid = default(WowBGuid);
        WowBReader? reader = ObjectManager.WowB;
        if (reader == null || ObjectManager.Wow != memory) return false;
        byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(reader.MouseoverGuidCandidateRva), 16);
        if (bytes == null || bytes.Length != 16) return false;
        guid = new WowBGuid(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
        return true;
    }

    // This experiment is diagnostic only. It posts mouse moves to the game but
    // never buttons, keys, process writes or gameplay interactions.
    private static bool BackgroundStable(Memory memory, WowBReader reader, IntPtr hwnd, int generation,
                                         int width, int height, Point origin, IntPtr foreground)
    {
        if (ClickAimUi.Running || ClickAimUi.Paused || ClickAimAbort.Generation != generation ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader || memory.WindowHandle != hwnd ||
            GetForegroundWindow() != foreground || !ClickAimInput.ClientSize(hwnd, out int w, out int h) ||
            w != width || h != height) return false;
        Point current = default;
        return ClientToScreen(hwnd, ref current) && current.X == origin.X && current.Y == origin.Y;
    }

    private static bool DiagnosticWait(int milliseconds, int generation)
    {
        for (int elapsed = 0; elapsed < milliseconds; elapsed += 50)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused || ClickAimAbort.Generation != generation) return false;
            Thread.Sleep(Math.Min(50, milliseconds - elapsed));
        }
        return !ClickAimUi.Running && !ClickAimUi.Paused && ClickAimAbort.Generation == generation;
    }

    private static void RunBackground(int generation)
    {
        try
        {
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget || memory.WindowHandle == IntPtr.Zero)
            { Logging.Write("[Gather background test] attach to wowb.exe first"); return; }
            using var uncached = memory.TemporaryCacheState(false);
            IntPtr hwnd = memory.WindowHandle;
            if (reader.IsExperimental70707 && !WowBGrindFactions.TraceBuild70707(memory, reader))
            { Logging.Write("[Gather background test] 70707 partial signature check failed"); return; }
            Logging.Write("[Gather background test] bot must stay stopped. Within 8 seconds focus the game and hover a visible nearby herb or ore; keep the cursor still. Only posted mouse moves will be sent, never clicks.");
            if (!DiagnosticWait(8000, generation) || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                memory.WindowHandle != hwnd || GetForegroundWindow() != hwnd ||
                !ClickAimInput.ClientSize(hwnd, out int width, out int height) ||
                !ClickAimInput.TryCursorFraction(hwnd, out double fx, out double fy) ||
                !ClickAimInput.TryCursorScreen(out int startX, out int startY) ||
                !ReadHover(memory, out WowBGuid expected) || expected.IsEmpty ||
                GatherNodeScanner.Classify((uint)((expected.High >> 6) & 0x7FFFFFUL), out _) == null)
            { Logging.Write("[Gather background test] preparation failed or stopped; no result"); return; }
            Point origin = default;
            if (!ClientToScreen(hwnd, ref origin) || !DiagnosticWait(250, generation) ||
                GetForegroundWindow() != hwnd || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !ClickAimInput.TryCursorScreen(out int checkX, out int checkY) ||
                Math.Abs((long)checkX - startX) > 3 || Math.Abs((long)checkY - startY) > 3 ||
                !ReadHover(memory, out WowBGuid verify) || verify != expected)
            { Logging.Write("[Gather background test] preparation changed; no result"); return; }
            Logging.Write("[Gather background test] candidate captured. Switch to another application within 12 seconds, then move the desktop mouse there for 6 seconds; do not move the game, camera or character.");
            IntPtr other = IntPtr.Zero;
            uint gameProcess;
            if (GetWindowThreadProcessId(hwnd, out gameProcess) == 0 || gameProcess == 0)
            { Logging.Write("[Gather background test] game window process unavailable; no result"); return; }
            for (int elapsed = 0; elapsed < 12000; elapsed += 100)
            {
                if (!DiagnosticWait(100, generation) || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return;
                IntPtr foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero && foreground != hwnd &&
                    GetWindowThreadProcessId(foreground, out uint process) != 0 && process != 0 && process != gameProcess)
                { other = foreground; break; }
            }
            if (other == IntPtr.Zero || !BackgroundStable(memory, reader, hwnd, generation, width, height, origin, other))
            { Logging.Write("[Gather background test] no stable different foreground application; no result"); return; }
            double awayX = fx < 0.5 ? 0.95 : 0.05;
            double awayY = fy < 0.5 ? 0.95 : 0.05;
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, awayX, awayY) || !DiagnosticWait(400, generation) ||
                !BackgroundStable(memory, reader, hwnd, generation, width, height, origin, other) ||
                !ReadHover(memory, out WowBGuid away) ||
                !ClickAimInput.PostMouseMoveFraction(hwnd, fx, fy) || !DiagnosticWait(400, generation) ||
                !BackgroundStable(memory, reader, hwnd, generation, width, height, origin, other) ||
                !ReadHover(memory, out WowBGuid returned))
            { Logging.Write("[Gather background test] window, attachment, stop state or hover read changed; no result"); return; }
            bool transition = away != expected && returned == expected;
            if (!ClickAimInput.TryCursorScreen(out int lastX, out int lastY))
            { Logging.Write("[Gather background test] desktop cursor unavailable; no result"); return; }
            int moves = 0, matching = 0, movedWithMismatch = 0, samples = 0;
            for (int i = 0; i < 60; i++)
            {
                if (!DiagnosticWait(100, generation) || !BackgroundStable(memory, reader, hwnd, generation, width, height, origin, other) ||
                    !ClickAimInput.TryCursorScreen(out int x, out int y) || !ReadHover(memory, out WowBGuid hover))
                { Logging.Write("[Gather background test] foreground, geometry, attachment or reads changed; result discarded"); return; }
                bool moved = x != lastX || y != lastY;
                if (moved) moves++;
                if (moved && hover != expected) movedWithMismatch++;
                lastX = x; lastY = y; samples++;
                if (hover == expected) matching++;
            }
            Logging.Write("[Gather background test] away/return transition=" + (transition ? "observed" : "not observed") +
                "; samples=" + samples + "; samples with desktop movement=" + moves + "; matching hover samples=" + matching +
                "; movement samples with mismatching hover=" + movedWithMismatch +
                ". Diagnostic only: this does not prove safe clicks or authorize removing gather checks.");
            if (moves == 0) Logging.Write("[Gather background test] no desktop movement observed; isolation test inconclusive");
        }
        catch (Exception ex) { Logging.Write("[Gather background test] failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _active, 0); }
    }

    private static void Run()
    {
        try
        {
            Memory? memory = ObjectManager.Wow;
            if (memory == null || !memory.Is64BitTarget || memory.WindowHandle == IntPtr.Zero)
            {
                Logging.Write("[Gather hover test] attach to wowb.exe first");
                return;
            }
            using var uncached = memory.TemporaryCacheState(false);
            IntPtr hwnd = memory.WindowHandle;
            Logging.Write("[Gather hover test] bot must remain stopped. Within 8 seconds hover a nearby "
                          + "visible node in the game, then keep the mouse still. No buttons or keys will be sent.");
            Thread.Sleep(8000);
            if (ClickAimUi.Running || ObjectManager.Wow != memory || memory.WindowHandle != hwnd)
            {
                Logging.Write("[Gather hover test] bot started or client changed; test cancelled");
                return;
            }
            if (!ClickAimInput.TryCursorFraction(hwnd, out double fx, out double fy)
                || !ReadHover(memory, out WowBGuid expected) || expected.IsEmpty)
            {
                Logging.Write("[Gather hover test] cursor is not over the game or the mouseover candidate is empty; no test sent");
                return;
            }
            if (!GatherNodeScanner.TryCapture(8.0, out List<GatherNodeScanner.Node> nodes))
            {
                Logging.Write("[Gather hover test] resource scan failed; no test sent");
                return;
            }
            bool matched = false;
            foreach (GatherNodeScanner.Node node in nodes)
            {
                if (node.Guid != expected || node.DefaultBlacklisted) continue;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Gather hover test] candidate {0} entry={1} {2:F1} yd guid={3} at client ({4:F3},{5:F3})",
                    node.Name, node.Entry, node.Distance, node.Guid, fx, fy));
                matched = true;
                break;
            }
            if (!matched || !ReadHover(memory, out WowBGuid before) || before != expected)
            {
                Logging.Write("[Gather hover test] hover did not match a nearby resource; no test sent");
                return;
            }
            if (!ClickAimInput.TryCursorFraction(hwnd, out double checkX, out double checkY)
                || Math.Abs(checkX - fx) > 0.003 || Math.Abs(checkY - fy) > 0.003)
            {
                Logging.Write("[Gather hover test] real cursor moved during capture; test cancelled");
                return;
            }
            // Move to a different, fixed client-space point, then back to the user's
            // recorded point. Posting WM_MOUSEMOVE does not move the real desktop cursor.
            double awayX = fx < 0.5 ? 0.95 : 0.05;
            double awayY = fy < 0.5 ? 0.95 : 0.05;
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, awayX, awayY))
            {
                Logging.Write("[Gather hover test] could not post the away mouse move");
                return;
            }
            Thread.Sleep(400);
            if (ClickAimUi.Running || ObjectManager.Wow != memory || memory.WindowHandle != hwnd
                || !ClickAimInput.TryCursorFraction(hwnd, out checkX, out checkY)
                || Math.Abs(checkX - fx) > 0.003 || Math.Abs(checkY - fy) > 0.003
                || !ReadHover(memory, out WowBGuid away))
            {
                Logging.Write("[Gather hover test] client or cursor changed, or away reading failed; test inconclusive");
                return;
            }
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, fx, fy))
            {
                Logging.Write("[Gather hover test] could not post the return mouse move");
                return;
            }
            Thread.Sleep(400);
            if (ClickAimUi.Running || ObjectManager.Wow != memory || memory.WindowHandle != hwnd
                || !ClickAimInput.TryCursorFraction(hwnd, out checkX, out checkY)
                || Math.Abs(checkX - fx) > 0.003 || Math.Abs(checkY - fy) > 0.003
                || !ReadHover(memory, out WowBGuid after))
            {
                Logging.Write("[Gather hover test] client or cursor changed, or return reading failed; test inconclusive");
                return;
            }
            Logging.Write("[Gather hover test] before=" + before + " away=" + away + " returned=" + after);
            if (away != expected && after == expected)
                Logging.Write("[Gather hover test] posted mouse moves changed away and back to the resource GUID. "
                              + "Promising, but not a gathering or depletion test.");
            else
                Logging.Write("[Gather hover test] posted mouse moves did not show a clear away/back transition. "
                              + "Do not use this to authorize a right-click; real cursor movement or another method may be needed.");
        }
        catch (Exception ex) { Logging.Write("[Gather hover test] failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}

using System;
using System.Runtime.InteropServices;

namespace Styx.Logic.Pathing;

// Focus is not proof of mouse isolation or authorization to click. Gathering
// can ignore desktop cursor motion while another application is focused; all
// identity, geometry and final hover checks still apply.
internal static class GatherBackgroundFocus
{
    internal enum State { OtherApplication, Unavailable, Game }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    internal static State Classify(IntPtr foreground, IntPtr game, uint process, uint gameProcess)
    {
        if (foreground == IntPtr.Zero) return State.Unavailable;
        if (foreground == game) return State.Game;
        if (process == 0 || gameProcess == 0) return State.Unavailable;
        return process == gameProcess ? State.Game : State.OtherApplication;
    }

    internal static bool MayRelax(State state, bool geometryAvailable)
        => state == State.OtherApplication && geometryAvailable;

    // A cursor interruption is retryable only if no button-down was posted.
    internal static bool RetryCursorInterruption(bool buttonDownSent, bool cursorInterrupted)
        => !buttonDownSent && cursorInterrupted;

    internal static State Current(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint gameProcess) == 0 || gameProcess == 0)
            return State.Unavailable;
        IntPtr foreground = GetForegroundWindow();
        uint process = 0;
        if (foreground != IntPtr.Zero) GetWindowThreadProcessId(foreground, out process);
        return Classify(foreground, hwnd, process, gameProcess);
    }

    internal static bool TryClientOrigin(IntPtr hwnd, out int x, out int y)
    {
        x = y = 0;
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
        Point origin = default;
        if (!ClientToScreen(hwnd, ref origin)) return false;
        x = origin.X; y = origin.Y;
        return true;
    }
}

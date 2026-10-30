using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Styx.Logic.Pathing;

/// <summary>
/// Synthesised mouse and key input for a client where the click-to-move function
/// is unknown. Every value here was established in the field on build 70291
/// (Tools/WowBClickAim logs, 2026-10-29):
///
///  * PostMessage works while the game window is in the BACKGROUND; SendInput
///    does not, because it drives the real cursor of whatever window is focused.
///  * a click is a move, a right-button down, then up - and the up is posted
///    TWICE. A dropped button-up leaves the client running toward the cursor,
///    which is what walked a character 171.52 yd in 25.00 s on the v9 build.
///  * a drag is a right-button down plus successive moves: the client rotates
///    from the deltas, not from one jump.
/// </summary>
internal static class ClickAimInput
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int MK_RBUTTON = 0x0002;
    private const int MK_LBUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageInternal(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    private static IntPtr Lp(int x, int y)
    {
        return new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    }

    public static bool ClientSize(IntPtr hWnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        RECT r;
        if (hWnd == IntPtr.Zero || !GetClientRect(hWnd, out r)) return false;
        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        return width > 0 && height > 0;
    }

    /// <summary>Right-click at a client-space pixel offset from the centre.</summary>
    public static void Click(IntPtr hWnd, int dx, int dy)
    {
        int w, h;
        if (!ClientSize(hWnd, out w, out h)) return;
        int x = w / 2 + dx;
        int y = h / 2 + dy;
        PostMessageInternal(hWnd, WM_MOUSEMOVE, IntPtr.Zero, Lp(x, y));
        Thread.Sleep(10);
        PostMessageInternal(hWnd, WM_RBUTTONDOWN, new IntPtr(MK_RBUTTON), Lp(x, y));
        Thread.Sleep(45);
        PostMessageInternal(hWnd, WM_RBUTTONUP, IntPtr.Zero, Lp(x, y));
        Thread.Sleep(5);
        PostMessageInternal(hWnd, WM_RBUTTONUP, IntPtr.Zero, Lp(x, y));
    }

    /// <summary>
    /// Horizontal right-drag of <paramref name="dragPx"/> pixels: positive drags
    /// right. The right button stays down for the whole drag, which is why the
    /// character runs during it - the caller must treat the leg as a turn AND a
    /// walk, never as a pure rotation.
    /// </summary>
    public static void Drag(IntPtr hWnd, int dragPx)
    {
        int w, h;
        if (!ClientSize(hWnd, out w, out h)) return;
        int cy = h / 2;
        int x0 = w / 2 - dragPx / 2;
        int x1 = w / 2 + dragPx / 2;
        PostMessageInternal(hWnd, WM_MOUSEMOVE, IntPtr.Zero, Lp(x0, cy));
        Thread.Sleep(15);
        PostMessageInternal(hWnd, WM_RBUTTONDOWN, new IntPtr(MK_RBUTTON), Lp(x0, cy));
        Thread.Sleep(20);
        const int steps = 8;
        for (int i = 1; i <= steps; i++)
        {
            int x = x0 + (x1 - x0) * i / steps;
            PostMessageInternal(hWnd, WM_MOUSEMOVE, new IntPtr(MK_RBUTTON), Lp(x, cy));
            Thread.Sleep(12);
        }
        PostMessageInternal(hWnd, WM_RBUTTONUP, IntPtr.Zero, Lp(x1, cy));
        Thread.Sleep(5);
        PostMessageInternal(hWnd, WM_RBUTTONUP, IntPtr.Zero, Lp(x1, cy));
    }

    /// <summary>
    /// Holds a key down, or lets it up - no timeout, no repeat. This is what a movement
    /// key needs: the client walks for as long as the key is down, so the hold lasts
    /// until the caller says otherwise.
    ///
    /// The lParam carries the scan code in bits 16-23, which is what the client reads to
    /// decide which physical key this is; <see cref="TapKey"/> sends a bare 1 there and
    /// gets away with it because a tap is also accepted on the virtual-key code alone.
    /// </summary>
    public static void HoldKey(IntPtr hWnd, int vk, bool down)
    {
        if (vk <= 0 || hWnd == IntPtr.Zero) return;
        int scan = (int)MapVirtualKey((uint)vk, 0);
        int lp = 1 | (scan << 16);
        // The cast goes INSIDE unchecked(): unchecked() suppresses overflow checking, it
        // does NOT change a literal's type. 0xC0000000 is typed uint, so
        // `lp |= unchecked(0xC0000000)` is int |= uint and does not compile.
        if (!down) lp |= unchecked((int)0xC0000000);   // previous state + transition
        PostMessageInternal(hWnd, down ? WM_KEYDOWN : WM_KEYUP, new IntPtr(vk), new IntPtr(lp));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Holds a key for <paramref name="ms"/> (movement keys stop a walk).</summary>
    public static void TapKey(IntPtr hWnd, int vk, int ms)
    {
        if (vk <= 0) return;
        if (ms < 20) ms = 20;
        PostMessageInternal(hWnd, WM_KEYDOWN, new IntPtr(vk), new IntPtr(0x00010001));
        Thread.Sleep(ms);
        PostMessageInternal(hWnd, WM_KEYUP, new IntPtr(vk), new IntPtr(unchecked((int)0xC0010001)));
    }

    /// <summary>
    /// The panic: release both buttons twice, click once at the centre and hold
    /// the stop key. Field use only - a stuck right button makes every later
    /// click read as "run this way", so nothing else recovers from it.
    /// </summary>
    public static void ReleaseAll(IntPtr hWnd, int stopKey, int settleMs)
    {
        int w, h;
        if (!ClientSize(hWnd, out w, out h)) return;
        int cx = w / 2, cy = h / 2;
        for (int i = 0; i < 2; i++)
        {
            PostMessageInternal(hWnd, WM_RBUTTONUP, IntPtr.Zero, Lp(cx, cy));
            PostMessageInternal(hWnd, WM_LBUTTONUP, IntPtr.Zero, Lp(cx, cy));
            Thread.Sleep(10);
        }
        PostMessageInternal(hWnd, WM_MOUSEMOVE, IntPtr.Zero, Lp(cx, cy));
        Thread.Sleep(10);
        PostMessageInternal(hWnd, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), Lp(cx, cy));
        Thread.Sleep(30);
        PostMessageInternal(hWnd, WM_LBUTTONUP, IntPtr.Zero, Lp(cx, cy));
        Thread.Sleep(20);
        TapKey(hWnd, stopKey, 70);
        if (settleMs > 0) Thread.Sleep(settleMs);
    }
}

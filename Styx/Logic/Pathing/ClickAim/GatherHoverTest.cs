using System;
using System.Collections.Generic;
using System.Globalization;
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

    public static bool Start()
    {
        if (ClickAimUi.Running || Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        try
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "GatherHoverTest" };
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
        byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(WowBOffsets.MouseoverGuidCandidate), 16);
        if (bytes == null || bytes.Length != 16) return false;
        guid = new WowBGuid(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
        return true;
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

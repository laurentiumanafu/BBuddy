using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>Exploratory posted-hover search. A matched GUID is not proof of a successful gather.</summary>
internal static class GatherInteraction
{
    private static bool ReadHover(Memory memory, out WowBGuid guid)
    {
        guid = default;
        byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(WowBOffsets.MouseoverGuidCandidate), 16);
        if (bytes == null || bytes.Length != 16) return false;
        guid = new WowBGuid(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
        return true;
    }

    public static bool TryInteract(IntPtr hwnd, GatherNodeScanner.Node node)
    {
        Memory? memory = ObjectManager.Wow;
        if (memory == null || memory.WindowHandle != hwnd || !memory.Is64BitTarget
            || !ClickAimInput.ClientSize(hwnd, out int width, out int height)
            || !ClickAimInput.TryCursorFraction(hwnd, out double realX, out double realY)) return false;
        using var uncached = memory.TemporaryCacheState(false);
        // First demand an away transition, so an old/stuck candidate cannot authorize a click.
        if (!ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05) || ClickAimAbort.Wait(400)
            || !ReadHover(memory, out WowBGuid away) || away == node.Guid) return false;
        // Start in front of the character, spreading over the central viewport. This is a
        // bounded search, not projection; a small or obscured node may never be found.
        double[] xs = { 0.50, 0.46, 0.54, 0.42, 0.58, 0.38, 0.62, 0.34, 0.66 };
        double[] ys = { 0.45, 0.50, 0.40, 0.55, 0.35, 0.60, 0.30, 0.65, 0.25 };
        foreach (double y in ys)
        foreach (double x in xs)
        {
            if (ClickAimAbort.Requested || ObjectManager.Wow != memory || memory.WindowHandle != hwnd
                || !ClickAimInput.TryCursorFraction(hwnd, out double rx, out double ry)
                || Math.Abs(rx - realX) > 0.003 || Math.Abs(ry - realY) > 0.003
                || !GrindRunner.TryReadGatherCombat(out bool combat) || combat) return false;
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, x, y) || ClickAimAbort.Wait(350)) return false;
            if (!ReadHover(memory, out WowBGuid hover)) return false;
            if (hover != node.Guid) continue;
            int dx = (int)Math.Round(x * (width - 1)) - width / 2;
            int dy = (int)Math.Round(y * (height - 1)) - height / 2;
            return ClickAimInput.ConfirmedRightClick(hwnd, dx, dy, () =>
            {
                if (ObjectManager.Wow != memory || memory.WindowHandle != hwnd
                    || !ClickAimInput.TryCursorFraction(hwnd, out double cx, out double cy)
                    || Math.Abs(cx - realX) > 0.003 || Math.Abs(cy - realY) > 0.003
                    || !GrindRunner.TryReadGatherCombat(out bool fighting) || fighting
                    || !GatherNodeScanner.TryCapture(5.0, out List<GatherNodeScanner.Node> fresh)) return false;
                bool present = false;
                foreach (var item in fresh) if (item.Guid == node.Guid) { present = true; break; }
                // Last read before button-down, after the potentially long pool scan.
                return present && ReadHover(memory, out WowBGuid final) && final == node.Guid;
            });
        }
        return false;
    }
}

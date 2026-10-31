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

    private static bool CursorStill(int x, int y)
    {
        return ClickAimInput.TryCursorScreen(out int nowX, out int nowY)
            && Math.Abs(nowX - x) <= 3 && Math.Abs(nowY - y) <= 3;
    }

    public static bool TryInteract(IntPtr hwnd, GatherNodeScanner.Node node, out string reason)
    {
        reason = "game window or desktop cursor unavailable";
        Memory? memory = ObjectManager.Wow;
        if (memory == null || memory.WindowHandle != hwnd || !memory.Is64BitTarget
            || !ClickAimInput.ClientSize(hwnd, out int width, out int height)
            || !ClickAimInput.TryCursorScreen(out int realX, out int realY)) return false;
        using var uncached = memory.TemporaryCacheState(false);
        // First demand an away transition, so an old/stuck candidate cannot authorize a click.
        reason = "hover-away post/read failed or the hover GUID stayed on the node";
        if (!ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05) || ClickAimAbort.Wait(400)
            || !ReadHover(memory, out WowBGuid away) || away == node.Guid) return false;
        // Two interleaved grids cover gaps in the first pass. They still cannot guarantee
        // a hit on a small/occluded node, so a miss is reported rather than clicked blindly.
        double[] xs = { 0.50, 0.46, 0.54, 0.42, 0.58, 0.38, 0.62, 0.34, 0.66 };
        double[] ys = { 0.45, 0.50, 0.40, 0.55, 0.35, 0.60, 0.30, 0.65, 0.25 };
        for (int pass = 0; pass < 2; pass++)
        foreach (double baseY in ys)
        foreach (double baseX in xs)
        {
            double x = baseX + (pass == 0 ? 0 : 0.02);
            double y = baseY + (pass == 0 ? 0 : 0.025);
            reason = "stopped, client changed, or real cursor moved during hover search";
            if (ClickAimAbort.Requested || ObjectManager.Wow != memory || memory.WindowHandle != hwnd
                || !CursorStill(realX, realY)) return false;
            reason = "combat detected or combat state unreadable during hover search";
            if (!GrindRunner.TryReadGatherCombat(out bool combat) || combat) return false;
            reason = "hover post/read failed";
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, x, y) || ClickAimAbort.Wait(350)) return false;
            if (!ReadHover(memory, out WowBGuid hover)) return false;
            if (hover != node.Guid) continue;
            int pixelX = (int)Math.Round(x * (width - 1));
            int pixelY = (int)Math.Round(y * (height - 1));
            // The first GUID match may be transient. Let it settle, without posting
            // another mouse move, then revalidate after all slower reads.
            reason = "stopped while confirming hover";
            if (ClickAimAbort.Wait(200)) return false;
            string validation = "";
            bool sent = ClickAimInput.ConfirmedRightClick(hwnd, pixelX, pixelY, width, height, () =>
            {
                if (ObjectManager.Wow != memory || memory.WindowHandle != hwnd) { validation = "client changed"; return false; }
                if (!CursorStill(realX, realY)) { validation = "real cursor moved"; return false; }
                if (!GrindRunner.TryReadGatherCombat(out bool fighting)) { validation = "combat state unreadable"; return false; }
                if (fighting) { validation = "combat detected"; return false; }
                if (!GatherNodeScanner.TryCapture(5.0, out List<GatherNodeScanner.Node> fresh))
                { validation = "final resource scan failed"; return false; }
                bool present = false;
                foreach (var item in fresh) if (item.Guid == node.Guid) { present = true; break; }
                if (!present) { validation = "node not observed within 5 yd"; return false; }
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) { validation = "combat detected or unreadable after node scan"; return false; }
                if (!CursorStill(realX, realY)) { validation = "real cursor moved during validation"; return false; }
                if (!ReadHover(memory, out WowBGuid final)) { validation = "final hover read failed"; return false; }
                if (final != node.Guid) { validation = "final hover changed to " + final; return false; }
                return true;
            }, out string clickReason);
            reason = validation.Length > 0 ? validation : clickReason;
            return sent;
        }
        reason = "node GUID was not found in the sampled central viewport";
        return false;
    }
}

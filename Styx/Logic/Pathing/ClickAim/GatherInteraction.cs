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

    private const int HoverSearchBudgetMs = 45000;

    private static bool CursorStill(int x, int y, out string reason)
    {
        if (!ClickAimInput.TryCursorScreen(out int nowX, out int nowY))
        {
            reason = "desktop cursor position could not be read";
            return false;
        }
        if (Math.Abs((long)nowX - x) > 3 || Math.Abs((long)nowY - y) > 3)
        {
            // GetCursorPos cannot tell whether a person, the game or Windows moved it.
            reason = "desktop cursor coordinates changed from (" + x + "," + y + ") to ("
                   + nowX + "," + nowY + "); source unknown";
            return false;
        }
        reason = "";
        return true;
    }

    // hoverMiss is only set after the bounded grid search really failed to find the
    // node. Cursor/client/stop/read failures are NOT evidence of a depleted node.
    public static bool TryInteract(IntPtr hwnd, GatherNodeScanner.Node node, out string reason, out bool hoverMiss)
    {
        hoverMiss = false;
        reason = "game window or desktop cursor unavailable";
        Memory? memory = ObjectManager.Wow;
        if (memory == null || memory.WindowHandle != hwnd || !memory.Is64BitTarget
            || !ClickAimInput.ClientSize(hwnd, out int width, out int height)
            || !ClickAimInput.TryCursorScreen(out int realX, out int realY)) return false;
        using var uncached = memory.TemporaryCacheState(false);
        // First demand an away transition, so an old/stuck candidate cannot authorize a click.
        reason = "failed to post hover-away mouse move";
        if (!ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05)) return false;
        reason = "Stop requested while waiting for hover-away";
        if (ClickAimAbort.Wait(400)) return false;
        reason = "hover-away GUID could not be read";
        if (!ReadHover(memory, out WowBGuid away)) return false;
        reason = "hover-away GUID remained on the node";
        if (away == node.Guid) return false;
        // Two interleaved grids cover gaps in the first pass. They still cannot guarantee
        // a hit on a small/occluded node, so a miss is reported rather than clicked blindly.
        double[] xs = { 0.50, 0.46, 0.54, 0.42, 0.58, 0.38, 0.62, 0.34, 0.66 };
        double[] ys = { 0.45, 0.50, 0.40, 0.55, 0.35, 0.60, 0.30, 0.65, 0.25 };
        int started = Environment.TickCount;
        int sampled = 0;
        for (int pass = 0; pass < 2; pass++)
        foreach (double baseY in ys)
        foreach (double baseX in xs)
        {
            if (ClickAimAbort.Requested) { reason = "Stop requested during hover search"; return false; }
            if (ObjectManager.Wow != memory) { reason = "attached client changed during hover search"; return false; }
            if (memory.WindowHandle != hwnd) { reason = "game window handle changed during hover search"; return false; }
            if (!CursorStill(realX, realY, out reason)) return false;
            if (unchecked(Environment.TickCount - started) >= HoverSearchBudgetMs)
            {
                hoverMiss = true;
                reason = "node GUID not found within 45 seconds (" + sampled + " pixels sampled); may be occluded or unavailable";
                return false;
            }
            double x = baseX + (pass == 0 ? 0 : 0.02);
            double y = baseY + (pass == 0 ? 0 : 0.025);
            reason = "combat detected or combat state unreadable during hover search";
            if (!GrindRunner.TryReadGatherCombat(out bool combat) || combat) return false;
            reason = "failed to post hover-search mouse move";
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, x, y)) return false;
            reason = "Stop requested while waiting for hover update";
            if (ClickAimAbort.Wait(350)) return false;
            sampled++;
            reason = "hover GUID could not be read during search";
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
                if (!CursorStill(realX, realY, out string cursorReason)) { validation = cursorReason; return false; }
                if (!GrindRunner.TryReadGatherCombat(out bool fighting)) { validation = "combat state unreadable"; return false; }
                if (fighting) { validation = "combat detected"; return false; }
                if (!GatherNodeScanner.TryCapture(5.0, out List<GatherNodeScanner.Node> fresh))
                { validation = "final resource scan failed"; return false; }
                bool present = false;
                foreach (var item in fresh) if (item.Guid == node.Guid) { present = true; break; }
                if (!present) { validation = "node not observed within 5 yd"; return false; }
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) { validation = "combat detected or unreadable after node scan"; return false; }
                if (!CursorStill(realX, realY, out cursorReason)) { validation = cursorReason; return false; }
                if (!ReadHover(memory, out WowBGuid final)) { validation = "final hover read failed"; return false; }
                if (final != node.Guid) { validation = "final hover changed to " + final; return false; }
                return true;
            }, out string clickReason);
            reason = validation.Length > 0 ? validation : clickReason;
            return sent;
        }
        hoverMiss = true;
        reason = "node GUID was not found in " + sampled + " sampled viewport pixels; may be occluded or unavailable";
        return false;
    }
}

using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Helpers;
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

    private static bool Near(GatherNodeScanner.Node a, GatherNodeScanner.Node b)
    {
        double dx = a.Position.X - b.Position.X, dy = a.Position.Y - b.Position.Y, dz = a.Position.Z - b.Position.Z;
        return dx * dx + dy * dy + dz * dz <= 16.0;
    }

    private static bool MaySubstitute(GatherNodeScanner.Node requested, GatherNodeScanner.Node candidate,
                                      WowBGuid hover, WowBGuid away, Func<GatherNodeScanner.Node, bool> allowed)
        => !hover.IsEmpty && hover != away && hover != requested.Guid && candidate.Guid == hover
           && candidate.Distance <= 5.0 && Near(requested, candidate) && allowed(candidate);

    public static string SelfTest()
    {
        var kind = new Bots.Gatherbuddy.GatherableNode(656160U, "fixture", 1U);
        var a = new WowBGuid(1, 2); var b = new WowBGuid(3, 4);
        var node = new GatherNodeScanner.Node(a, kind, true, new WoWPoint(0, 0, 0), 2);
        var near = new GatherNodeScanner.Node(b, kind, true, new WoWPoint(0, 0, 0), 2);
        var far = new GatherNodeScanner.Node(b, kind, true, new WoWPoint(0, 0, 4.1f), 2);
        var outOfReach = new GatherNodeScanner.Node(b, kind, true, new WoWPoint(0, 0, 0), 5.1);
        bool ok = MaySubstitute(node, near, b, default, _ => true)
               && !MaySubstitute(node, near, b, b, _ => true)
               && !MaySubstitute(node, near, a, default, _ => true)
               && !MaySubstitute(node, near, b, default, _ => false)
               && !MaySubstitute(node, far, b, default, _ => true)
               && !MaySubstitute(node, outOfReach, b, default, _ => true);
        return "[Gather] hover substitute fixtures: " + (ok ? "pass" : "FAIL") + " (not a live interaction test)";
    }

    // hoverMiss is only set after the bounded grid search really failed to find the
    // node. Cursor/client/stop/read failures are NOT evidence of a depleted node.
    public static bool TryInteract(IntPtr hwnd, GatherNodeScanner.Node node,
                                   Func<GatherNodeScanner.Node, bool> allowed,
                                   out GatherNodeScanner.Node clickedNode, out string reason, out bool hoverMiss)
    {
        clickedNode = default;
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
        bool loggedOther = false;
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
            GatherNodeScanner.Node hoveredNode = node;
            if (hover != node.Guid)
            {
                if (!hover.IsEmpty && !loggedOther)
                {
                    loggedOther = true;
                    Logging.Write("[Gather] hover observed another GUID " + hover + " while searching for " + node.Guid
                                  + "; a GUID alone does not prove it is an eligible resource");
                }
                // Distinct objects can occupy the same position. The GUID under the
                // cursor may be a different eligible node, never a generic object.
                // Require an actual away transition for the substitute as well.
                if (hover.IsEmpty || hover == away
                    || GatherNodeScanner.Classify((uint)((hover.High >> 6) & 0x7FFFFFUL), out _) == null
                    || !GatherNodeScanner.TryCapture(5.0, out List<GatherNodeScanner.Node> others)) continue;
                bool match = false;
                foreach (var candidate in others)
                {
                    if (!MaySubstitute(node, candidate, hover, away, allowed)) continue;
                    hoveredNode = candidate;
                    match = true;
                    break;
                }
                if (!match) continue;
            }
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
                foreach (var item in fresh)
                {
                    if (item.Guid != hoveredNode.Guid || !allowed(item)) continue;
                    if (item.Distance <= 5.0 && Near(node, item)) { present = true; break; }
                }
                if (!present) { validation = "eligible node not observed in reach near the intended resource"; return false; }
                if (!GrindRunner.TryReadGatherCombat(out fighting) || fighting) { validation = "combat detected or unreadable after node scan"; return false; }
                if (!CursorStill(realX, realY, out cursorReason)) { validation = cursorReason; return false; }
                if (!ReadHover(memory, out WowBGuid final)) { validation = "final hover read failed"; return false; }
                if (final != hoveredNode.Guid) { validation = "final hover changed to " + final; return false; }
                return true;
            }, out string clickReason);
            reason = validation.Length > 0 ? validation : clickReason;
            if (sent) clickedNode = hoveredNode;
            return sent;
        }
        hoverMiss = true;
        reason = "node GUID was not found in " + sampled + " sampled viewport pixels; may be occluded or unavailable";
        return false;
    }
}

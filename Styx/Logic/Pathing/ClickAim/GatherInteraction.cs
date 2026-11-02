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
    private static bool ReadHover(Memory memory, WowBReader reader, out WowBGuid guid)
    {
        guid = default;
        if (ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        return reader.TryReadMouseoverGuidCandidate(out guid) && ObjectManager.Wow == memory && ObjectManager.WowB == reader;
    }

    private const int HoverSearchBudgetMs = 45000;

    // Only hints: every eventual button-down still requires the current full GUID,
    // fresh node scan, combat/cursor checks and final confirmation.
    private static bool _herbHintKnown, _oreHintKnown;
    private static double _herbHintX, _herbHintY, _oreHintX, _oreHintY;

    internal static void ResetHoverHint() { _herbHintKnown = false; _oreHintKnown = false; }

    private static void AddHint(List<(double X, double Y)> points, double x, double y)
    {
        if (double.IsFinite(x) && double.IsFinite(y) && x > 0.05 && x < 0.95 && y > 0.05 && y < 0.95)
            points.Add((x, y));
    }

    private static List<(double X, double Y)> SearchPoints(GatherNodeScanner.Node node, int width, int height,
                                                            bool hintKnown, double hintX, double hintY)
    {
        var points = new List<(double X, double Y)>();
        if (hintKnown) AddHint(points, hintX, hintY);
        if (node.IsHerb)
        {
            // Retain the grid's successful first point before trying speculative projection.
            AddHint(points, 0.50, 0.45);
            // Approximate projection is only a search hint, never a click authorization.
            // The camera may not look exactly at the player and plant models have height.
            if (ClickAimPositions.TryReadCamera(out double cx, out double cy, out double cz)
                && ClickAimPositions.TryRead(out double px, out double py))
            {
                double pz = ClickAimPositions.Z();
                if (double.IsFinite(pz)
                    && ClickAimMath.ProjectToScreen(cx, cy, cz, px, py, pz,
                        node.Position.X, node.Position.Y, node.Position.Z + 0.3,
                        width / 2.0, height / 2.0, 90.0, out int dx, out int dy))
                {
                    double x = (width / 2.0 + dx) / (width - 1.0);
                    double y = (height / 2.0 + dy) / (height - 1.0);
                    AddHint(points, x, y);
                    AddHint(points, x, y - 0.025);
                    AddHint(points, x, y + 0.025);
                    AddHint(points, x - 0.025, y);
                    AddHint(points, x + 0.025, y);
                }
            }
        }
        double[] xs = { 0.50, 0.46, 0.54, 0.42, 0.58, 0.38, 0.62, 0.34, 0.66 };
        double[] ys = { 0.45, 0.50, 0.40, 0.55, 0.35, 0.60, 0.30, 0.65, 0.25 };
        for (int pass = 0; pass < 2; pass++)
        foreach (double baseY in ys)
        foreach (double baseX in xs)
        {
            if (node.IsHerb && pass == 0 && baseX == 0.50 && baseY == 0.45) continue;
            points.Add((baseX + (pass == 0 ? 0 : 0.02), baseY + (pass == 0 ? 0 : 0.025)));
        }
        return points;
    }

    private static bool CursorStill(int x, int y, out string reason, out bool moved)
    {
        moved = false;
        if (!ClickAimInput.TryCursorScreen(out int nowX, out int nowY))
        {
            reason = "desktop cursor position could not be read";
            return false;
        }
        if (Math.Abs((long)nowX - x) > 3 || Math.Abs((long)nowY - y) > 3)
        {
            // GetCursorPos cannot tell whether a person, the game or Windows moved it.
            moved = true;
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
        var oreNode = new GatherNodeScanner.Node(a, kind, false, new WoWPoint(0, 0, 0), 2);
        var orePoints = SearchPoints(oreNode, 1920, 1080, false, 0, 0);
        var oreHintPoints = SearchPoints(oreNode, 1920, 1080, true, 0.38, 0.60);
        var invalidHintPoints = SearchPoints(oreNode, 1920, 1080, true, double.NaN, 0.60);
        var hints = new List<(double X, double Y)>();
        AddHint(hints, 0.5, 0.45);
        AddHint(hints, double.NaN, 0.45);
        AddHint(hints, -0.1, 0.45);
        bool ok = orePoints.Count == 162 && orePoints[0].X == 0.50 && orePoints[0].Y == 0.45
               && oreHintPoints.Count == 163 && oreHintPoints[0].X == 0.38 && oreHintPoints[0].Y == 0.60
               && invalidHintPoints.Count == 162 && invalidHintPoints[0].X == 0.50
               && hints.Count == 1 && MaySubstitute(node, near, b, default, _ => true)
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
                                   Func<GatherNodeScanner.Node, bool> allowed, Func<bool>? extraSafety,
                                   out GatherNodeScanner.Node clickedNode, out string reason, out bool hoverMiss,
                                   out bool cursorMoved)
    {
        clickedNode = default;
        hoverMiss = false;
        cursorMoved = false;
        reason = "game window or desktop cursor unavailable";
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || memory.WindowHandle != hwnd || !memory.Is64BitTarget
            || !ClickAimInput.ClientSize(hwnd, out int width, out int height)
            || !ClickAimInput.TryCursorScreen(out int realX, out int realY)) return false;
        using var uncached = memory.TemporaryCacheState(false);
        if (reader.IsExperimental70707)
            Logging.Write("[Gather] 70707 hover GUID candidate RVA 0x" + reader.MouseoverGuidCandidateRva.ToString("X") +
                          "; click still requires a changed hover matching a fresh eligible node");
        // First demand an away transition, so an old/stuck candidate cannot authorize a click.
        reason = "failed to post hover-away mouse move";
        if (!ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05)) return false;
        reason = "Stop requested while waiting for hover-away";
        if (ClickAimAbort.Wait(400)) return false;
        reason = "hover-away GUID could not be read";
        if (!ReadHover(memory, reader, out WowBGuid away)) return false;
        reason = "hover-away GUID remained on the node";
        if (away == node.Guid) return false;
        // Two interleaved grids cover gaps in the first pass. They still cannot guarantee
        // a hit on a small/occluded node, so a miss is reported rather than clicked blindly.
        List<(double X, double Y)> points = SearchPoints(node, width, height,
            node.IsHerb ? _herbHintKnown : _oreHintKnown,
            node.IsHerb ? _herbHintX : _oreHintX, node.IsHerb ? _herbHintY : _oreHintY);
        int started = Environment.TickCount;
        int sampled = 0;
        bool loggedOther = false;
        foreach (var point in points)
        {
            if (ClickAimAbort.Requested) { reason = "Stop requested during hover search"; return false; }
            if (extraSafety != null && !extraSafety()) { reason = "Grind safety check interrupted hover search"; return false; }
            if (ObjectManager.Wow != memory) { reason = "attached client changed during hover search"; return false; }
            if (memory.WindowHandle != hwnd) { reason = "game window handle changed during hover search"; return false; }
            if (!CursorStill(realX, realY, out reason, out bool sampleCursorMoved)) { cursorMoved = sampleCursorMoved; return false; }
            if (unchecked(Environment.TickCount - started) >= HoverSearchBudgetMs)
            {
                hoverMiss = true;
                reason = "node GUID not found within 45 seconds (" + sampled + " pixels sampled); may be occluded or unavailable";
                return false;
            }
            double x = point.X;
            double y = point.Y;
            reason = "combat detected or combat state unreadable during hover search";
            if (!GrindRunner.TryReadGatherCombat(out bool combat) || combat) return false;
            reason = "failed to post hover-search mouse move";
            if (!ClickAimInput.PostMouseMoveFraction(hwnd, x, y)) return false;
            reason = "Stop requested while waiting for hover update";
            if (ClickAimAbort.Wait(350)) return false;
            sampled++;
            reason = "hover GUID could not be read during search";
            if (!ReadHover(memory, reader, out WowBGuid hover)) return false;
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
            bool movedDuringValidation = false;
            bool sent = ClickAimInput.ConfirmedRightClick(hwnd, pixelX, pixelY, width, height, () =>
            {
                if (ObjectManager.Wow != memory || memory.WindowHandle != hwnd) { validation = "client changed"; return false; }
                if (!CursorStill(realX, realY, out string cursorReason, out bool moved))
                { validation = cursorReason; movedDuringValidation = moved; return false; }
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
                if (extraSafety != null && !extraSafety()) { validation = "Grind safety check failed before interaction"; return false; }
                if (!CursorStill(realX, realY, out cursorReason, out moved))
                { validation = cursorReason; movedDuringValidation = moved; return false; }
                if (!ReadHover(memory, reader, out WowBGuid final)) { validation = "final hover read failed"; return false; }
                if (final != hoveredNode.Guid) { validation = "final hover changed to " + final; return false; }
                return true;
            }, out string clickReason);
            reason = validation.Length > 0 ? validation : clickReason;
            cursorMoved = movedDuringValidation;
            if (sent)
            {
                clickedNode = hoveredNode;
                if (hoveredNode.IsHerb) { _herbHintKnown = true; _herbHintX = x; _herbHintY = y; }
                else { _oreHintKnown = true; _oreHintX = x; _oreHintY = y; }
                Logging.Write("[Gather] hover match after " + sampled + " sampled point(s)");
            }
            return sent;
        }
        hoverMiss = true;
        reason = "node GUID was not found in " + sampled + " sampled viewport pixels; may be occluded or unavailable";
        return false;
    }
}

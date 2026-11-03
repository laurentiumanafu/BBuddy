using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// 70707 experiment. The ghost flag, HP, UI templates and saved position are
// candidates, not guarantees. Never uses Lua or client function injection.
internal static class WowBCorpseRecovery
{
    private enum State { Unknown, Alive, Dead, Ghost }
    private const int PromptWaitMs = 45000, TransitionWaitMs = 30000, RouteLimitMs = 15 * 60 * 1000;
    private const double MaxGhostDistance = 3000.0;

    private static bool Allowed(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> running)
        => running() && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
           ObjectManager.Wow == memory && ObjectManager.WowB == reader && memory.WindowHandle == hwnd &&
           reader.IsExperimental70707 && reader.ExperimentalGrindActive;

    private static State ReadState(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, out WoWPoint position)
    {
        position = default;
        try
        {
            if (!Allowed(memory, reader, hwnd, running)) return State.Unknown;
            using var uncached = memory.TemporaryCacheState(false);
            if (!reader.TryReadMapId(out uint map) || map != checkpoint.Map ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit player) || player.Guid != checkpoint.Player ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                fields.MaxHealth == 0 || fields.Health > fields.MaxHealth ||
                !WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost) ||
                !reader.TryReadMapId(out uint mapAfter) || mapAfter != map || !Allowed(memory, reader, hwnd, running)) return State.Unknown;
            position = player.Location;
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) return State.Unknown;
            return ghost ? State.Ghost : fields.Health == 0 ? State.Dead : State.Alive;
        }
        catch { return State.Unknown; }
    }

    // All observed non-player units are treated as potential threats, including
    // unreadable/dead candidates: faction and hostility are not established.
    // A successful scan is not proof the pool contains every nearby NPC.
    private static bool TryNpcSnapshot(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, out WoWPoint position, out List<WoWPoint> npcs)
    {
        position = default; npcs = new List<WoWPoint>();
        try
        {
            if (ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint before) != State.Ghost ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit player) || player.Guid != checkpoint.Player ||
                !reader.TryReadMapId(out uint map) || map != checkpoint.Map || !Allowed(memory, reader, hwnd, running)) return false;
            foreach (WowBObservedUnit unit in snapshot.Units)
            {
                if (unit.IsLocalPlayer || unit.HasPlayerVtable || unit.Guid.IsEmpty) continue;
                if (!float.IsFinite(unit.Location.X) || !float.IsFinite(unit.Location.Y) || !float.IsFinite(unit.Location.Z)) return false;
                npcs.Add(unit.Location);
            }
            if (ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint after) != State.Ghost ||
                WowBCorpseSpotPolicy.Distance(before, after) > 3 || WowBCorpseSpotPolicy.Distance(player.Location, after) > 3 ||
                Math.Abs((double)player.Location.Z - after.Z) > 3) return false;
            position = after;
            return true;
        }
        catch { return false; }
    }

    private static bool SpotSafe(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running)
    {
        if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint p, out var npcs) ||
            !WowBCorpseSpotPolicy.InRange(p, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) ||
            !WowBCorpseSpotPolicy.ClearEnough(p, npcs, WowBCorpseSpotPolicy.MinNpcClearance) ||
            !TrySnapSafeSpot(checkpoint.Map, p, checkpoint.Position, out WoWPoint snapped) ||
            WowBCorpseSpotPolicy.Distance(p, snapped) > 1.5 || Math.Abs((double)p.Z - snapped.Z) > 3) return false;
        return Allowed(memory, reader, hwnd, running) && ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Ghost;
    }

    // True means a guarded image was obtained; visible distinguishes a missing
    // prompt from unavailable focus/capture, which must not trigger blind roaming.
    private static bool TryPromptAtSafeSpot(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, out bool visible)
    {
        visible = false;
        bool Safety() => SpotSafe(memory, reader, hwnd, checkpoint, running);
        if (!Safety() || !WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
            out var result, out _, out _, out _, out _, out _) || !Safety()) return false;
        visible = WowBCorpseDialogPattern.SuitableForClick(result, false, out _, out _);
        return true;
    }

    private static bool TrySnapSafeSpot(uint map, WoWPoint candidate, WoWPoint corpse, out WoWPoint result)
    {
        result = default;
        try
        {
            var nav = Navigator.TripperNavigator;
            var point = new Vector3(candidate.X, candidate.Y, candidate.Z);
            nav.EnsureTilesAroundPosition(map, point, Navigator.LoadTilesAroundRadius);
            if (!nav.IsLoaded || !nav.FindNearestPolyRef(map, point, 2.5f, 5f, out Tripper.Navigation.PolygonReference poly, out Vector3 snapped) ||
                !poly.IsValid || !float.IsFinite(snapped.X) || !float.IsFinite(snapped.Y) || !float.IsFinite(snapped.Z) ||
                (nav.GetPolyArea(map, poly, out byte area) & 0x80000000U) != 0 || !WowBCorpseSpotPolicy.GroundArea(area)) return false;
            result = new WoWPoint(snapped.X, snapped.Y, snapped.Z);
            return WowBCorpseSpotPolicy.Distance(candidate, result) <= 2.5 && Math.Abs((double)candidate.Z - result.Z) <= 5 &&
                   WowBCorpseSpotPolicy.InRange(result, corpse, WowBCorpseSpotPolicy.MaxClickRadius);
        }
        catch { return false; }
    }

    private static bool WalkGhostTo(Memory memory, WowBReader reader, IntPtr hwnd, WowBDeathCheckpoint.Candidate checkpoint,
        Func<bool> running, ClickAimMover mover, RoutePoint goal, int started, bool stopNearCorpse)
    {
        var oldInterrupt = RouteRunner.ShouldInterrupt;
        bool oldPoll = RouteRunner.PollInterruptDuringWalk, oldMesh = RouteRunner.RequireMeshDuringSession, oldQuiet = mover.QuietKeyStop;
        bool reachedRange = false, arrived;
        try
        {
            RouteRunner.ShouldInterrupt = () =>
            {
                if (!Allowed(memory, reader, hwnd, running) || unchecked(Environment.TickCount - started) >= RouteLimitMs ||
                    ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint p) != State.Ghost) return true;
                if (stopNearCorpse && WowBCorpseSpotPolicy.InRange(p, checkpoint.Position, WowBCorpseSpotPolicy.ArrivalRadius))
                { reachedRange = true; return true; }
                return false;
            };
            RouteRunner.PollInterruptDuringWalk = true; RouteRunner.RequireMeshDuringSession = true; mover.QuietKeyStop = true;
            arrived = RouteRunner.RunWithinSession(mover, new List<RoutePoint> { goal }, 1);
        }
        finally
        {
            RouteRunner.ShouldInterrupt = oldInterrupt; RouteRunner.PollInterruptDuringWalk = oldPoll;
            RouteRunner.RequireMeshDuringSession = oldMesh; ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            mover.QuietKeyStop = oldQuiet;
        }
        return Allowed(memory, reader, hwnd, running) && unchecked(Environment.TickCount - started) < RouteLimitMs &&
               (arrived || (stopNearCorpse && reachedRange)) &&
               ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Ghost;
    }

    private static bool FindSafeSpot(Memory memory, WowBReader reader, IntPtr hwnd, WowBDeathCheckpoint.Candidate checkpoint,
        Func<bool> running, ClickAimMover mover, int started)
    {
        if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint current, out var npcs) ||
            !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) return false;
        double nearest = WowBCorpseSpotPolicy.Clearance(current, npcs);
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Corpse] scouting {0} observed non-player units; nearest={1}; absence or faction does not prove safety",
            npcs.Count, double.IsPositiveInfinity(nearest) ? "none observed" : nearest.ToString("F1", CultureInfo.InvariantCulture) + " yd"));
        if (WowBCorpseSpotPolicy.ClearEnough(current, npcs, WowBCorpseSpotPolicy.PreferCurrentClearance) &&
            TrySnapSafeSpot(checkpoint.Map, current, checkpoint.Position, out WoWPoint currentGround) &&
            WowBCorpseSpotPolicy.Distance(current, currentGround) <= 1.5 && Math.Abs((double)current.Z - currentGround.Z) <= 3)
        {
            if (!TryPromptAtSafeSpot(memory, reader, hwnd, checkpoint, running, out bool visible))
            { Logging.Write("[Corpse] cannot confirm current ground, NPC state or prompt capture; stopping"); return false; }
            if (visible) { Logging.Write("[Corpse] current position has the prompt and preferred observed NPC clearance"); return true; }
        }

        int routeAttempts = 0;
        foreach (double radius in WowBCorpseSpotPolicy.Rings)
        {
            if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs) ||
                !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) return false;
            List<WoWPoint> candidates = WowBCorpseSpotPolicy.RankRing(checkpoint.Position, current, npcs, radius);
            int ringAttempts = 0;
            foreach (WoWPoint candidate in candidates)
            {
                if (!Allowed(memory, reader, hwnd, running) || unchecked(Environment.TickCount - started) >= RouteLimitMs) return false;
                if (routeAttempts >= 12 || ringAttempts >= 3) break;
                if (!TrySnapSafeSpot(checkpoint.Map, candidate, checkpoint.Position, out WoWPoint spot) ||
                    !WowBCorpseSpotPolicy.ClearEnough(spot, npcs, WowBCorpseSpotPolicy.MinNpcClearance) ||
                    WowBCorpseSpotPolicy.Distance(spot, current) < 3) continue;
                routeAttempts++; ringAttempts++;
                var goal = new RoutePoint { X = spot.X, Y = spot.Y, Z = spot.Z, Kind = "Resurrection spot" };
                WoWPoint[]? path = RouteRunner.TryCombatMeshPath(current.X, current.Y, goal);
                if (path == null || !Allowed(memory, reader, hwnd, running)) continue;
                double pathLength = 0;
                for (int i = 1; i < path.Length; i++) pathLength += WowBCorpseSpotPolicy.Distance(path[i - 1], path[i]);
                if (!double.IsFinite(pathLength) || pathLength > 120) continue;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Corpse] trying mesh spot {0:F1} yd from checkpoint, {1:F1} yd from nearest observed NPC",
                    WowBCorpseSpotPolicy.Distance(spot, checkpoint.Position), WowBCorpseSpotPolicy.Clearance(spot, npcs)));
                if (!WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, false))
                {
                    if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs)) return false;
                    continue;
                }
                if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs)) return false;
                if (!WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) ||
                    !WowBCorpseSpotPolicy.ClearEnough(current, npcs, WowBCorpseSpotPolicy.MinNpcClearance)) continue;
                if (ClickAimAbort.Wait(500)) return false;
                if (!TryPromptAtSafeSpot(memory, reader, hwnd, checkpoint, running, out bool visible))
                { Logging.Write("[Corpse] cannot confirm ground, NPC state or prompt capture at candidate; stopping"); return false; }
                if (visible)
                { Logging.Write("[Corpse] resurrection prompt visible at a mesh spot with NPC clearance; rechecking before click"); return true; }
                Logging.Write("[Corpse] prompt not confirmed at this radius; trying closer safe candidates");
                break;
            }
        }
        if (TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs) &&
            WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) &&
            WowBCorpseSpotPolicy.ClearEnough(current, npcs, WowBCorpseSpotPolicy.MinNpcClearance) &&
            TryPromptAtSafeSpot(memory, reader, hwnd, checkpoint, running, out bool finalVisible) && finalVisible) return true;
        Logging.Write("[Corpse] no reachable prompt position with the required observed NPC clearance; stopping rather than resurrecting beside an NPC");
        return false;
    }

    private static bool TryDialogClick(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, bool release, out string problem)
    {
        problem = "player state or recovery session changed";
        State expected = release ? State.Dead : State.Ghost;
        bool Safety()
        {
            if (!Allowed(memory, reader, hwnd, running) || ReadState(memory, reader, hwnd, checkpoint, running, out _) != expected) return false;
            return release || SpotSafe(memory, reader, hwnd, checkpoint, running);
        }
        if (!Safety()) return false;
        if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out var dialog, out _, out bool background, out int width, out int height, out problem)) return false;
        if (!WowBCorpseDialogPattern.SuitableForClick(dialog, release, out int x, out int y))
        {
            problem = (background ? "background" : "foreground") + " image did not meet button checks (release=" +
                dialog.ReleaseText + ", releaseUnique=" + dialog.ReleaseUnique + ", releaseRed=" + dialog.ReleaseButtonRed +
                ", heading=" + dialog.ResurrectText + ", accept=" + dialog.AcceptText + ", acceptRed=" + dialog.AcceptButtonRed + ")";
            // A prior unsuccessful hover can leave the button highlighted and
            // unrecognizable. Move away without clicking so a later frame can recover.
            if (Safety()) ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05);
            return false;
        }
        // First clear a previous hover and obtain a fresh pre-hover image. The
        // background mode additionally requires a visible response to our hover.
        problem = "state or hover-away move failed";
        if (!Safety() || !ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05) || ClickAimAbort.Wait(200)) return false;
        if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out var away, out byte[]? before, out bool awayBackground, out int awayWidth, out int awayHeight, out problem)) return false;
        problem = "button, focus or client geometry changed after hover-away";
        if (before == null || awayBackground != background || awayWidth != width || awayHeight != height ||
            !WowBCorpseDialogPattern.SuitableForClick(away, release, out int awayX, out int awayY) ||
            Math.Abs(awayX - x) > 2 || Math.Abs(awayY - y) > 2) return false;
        string? FinalCheck()
        {
            string lastFailure = "hover response not observed";
            // Background GPU rendering may lag behind the posted move. Give the
            // frame a short bounded chance to update; never click on a stale frame.
            for (int frame = 0; frame < 4; frame++)
            {
                if (!Safety()) return "player state or recovery session changed";
                if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                        out var again, out byte[]? after, out bool afterBackground, out int w, out int h, out string captureReason))
                    return captureReason;
                if (after == null || afterBackground != background || w != width || h != height) return "focus mode or client size changed";
                if (!WowBCorpseDialogPattern.SuitableForClick(again, release, out int x2, out int y2))
                    lastFailure = "after hover: release=" + again.ReleaseText + ", releaseUnique=" + again.ReleaseUnique +
                        ", releaseRed=" + again.ReleaseButtonRed + ", heading=" + again.ResurrectText +
                        ", accept=" + again.AcceptText + ", acceptUnique=" + again.AcceptUnique + ", acceptRed=" + again.AcceptButtonRed;
                else if (Math.Abs(x2 - x) > 2 || Math.Abs(y2 - y) > 2)
                    return "button position changed after hover";
                else if (background && !WowBCorpseDialogPattern.ButtonChanged(before, after, width, height, x, y))
                    lastFailure = "background capture showed no fresh button response to hover; no click";
                else return Safety() ? null : "player state or recovery session changed";
                if (frame < 3 && ClickAimAbort.Wait(180)) return "stopped while waiting for hover frame";
            }
            return lastFailure;
        }
        bool posted = ClickAimInput.ConfirmedLeftClick(hwnd, x, y, width, height, background, FinalCheck, out string reason);
        problem = reason;
        if (posted) Logging.Write("[Corpse] " + (release ? "release" : "resurrection") + " dialog click (" +
                                  (background ? "background window capture" : "foreground") + "): " + reason);
        return posted;
    }

    internal static bool TryRecover(WowBDeathCheckpoint.Candidate checkpoint, ClickAimMover mover, Func<bool> running)
    {
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        IntPtr hwnd = memory?.WindowHandle ?? IntPtr.Zero;
        if (memory == null || reader == null || hwnd == IntPtr.Zero || !Allowed(memory, reader, hwnd, running)) return false;
        // First-use steering calibration walks before path planning and does not
        // poll the ghost identity guard. Do not release without this prerequisite.
        if (!RouteRunner.MeshReady() || !mover.KeyMode)
        {
            Logging.Write("[Corpse] mesh or previously calibrated route steering unavailable; stopping before release rather than stranding the ghost");
            return false;
        }
        Logging.Write("[Corpse] EXPERIMENTAL 70707 recovery: foreground capture or experimental background window capture; STOP remains active. No Lua.");
        bool initialQuiet = mover.QuietKeyStop;
        try { mover.QuietKeyStop = true; mover.MoveStop(); }
        finally { mover.QuietKeyStop = initialQuiet; }
        int started = Environment.TickCount, lastProblem = unchecked(Environment.TickCount - 6000);
        bool released = false, sentRelease = false;
        while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
        {
            State state = ReadState(memory, reader, hwnd, checkpoint, running, out _);
            if (state == State.Ghost) { released = true; break; }
            if (state == State.Alive) { Logging.Write("[Corpse] player is already alive; stopping rather than guessing a recovery transition"); return false; }
            string problem = "player state unavailable or changing";
            if (state == State.Dead && TryDialogClick(memory, reader, hwnd, checkpoint, running, true, out problem)) { sentRelease = true; break; }
            if (unchecked(Environment.TickCount - lastProblem) >= 5000)
            { Logging.Write("[Corpse] release not clicked: " + problem); lastProblem = Environment.TickCount; }
            if (ClickAimAbort.Wait(500)) return false;
        }
        if (sentRelease)
        {
            started = Environment.TickCount;
            while (unchecked(Environment.TickCount - started) < TransitionWaitMs && Allowed(memory, reader, hwnd, running))
            {
                if (ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Ghost) { released = true; break; }
                if (ClickAimAbort.Wait(500)) return false;
            }
        }
        if (!released) { Logging.Write("[Corpse] release prompt/ghost transition not confirmed; stopped without retrying the click"); return false; }
        if (ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint ghostPos) != State.Ghost ||
            !RouteRunner.MeshReady()) { Logging.Write("[Corpse] ghost, map or mesh unavailable; stopping"); return false; }
        double distance = ClickAimMath.Len(ghostPos.X - checkpoint.Position.X, ghostPos.Y - checkpoint.Position.Y);
        if (!double.IsFinite(distance) || distance > MaxGhostDistance)
        { Logging.Write("[Corpse] checkpoint too far away or invalid; refusing route"); return false; }
        Logging.Write("[Corpse] ghost candidate confirmed; following the mesh toward the checkpoint, stopping short to scout");
        var goal = new RoutePoint { X = checkpoint.Position.X, Y = checkpoint.Position.Y, Z = checkpoint.Position.Z, Kind = "Corpse" };
        int routeStarted = Environment.TickCount;
        if (!WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, routeStarted, true) ||
            ReadState(memory, reader, hwnd, checkpoint, running, out ghostPos) != State.Ghost ||
            !WowBCorpseSpotPolicy.InRange(ghostPos, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius))
        { Logging.Write("[Corpse] mesh route or proximity was not confirmed; stopping"); return false; }
        if (ClickAimAbort.Wait(800) || !FindSafeSpot(memory, reader, hwnd, checkpoint, running, mover, routeStarted)) return false;
        started = Environment.TickCount; lastProblem = unchecked(started - 6000);
        bool sentResurrect = false;
        int replans = 0;
        while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
        {
            if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out ghostPos, out var nearby) ||
                !WowBCorpseSpotPolicy.InRange(ghostPos, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) break;
            if (!WowBCorpseSpotPolicy.ClearEnough(ghostPos, nearby, WowBCorpseSpotPolicy.MinNpcClearance))
            {
                if (++replans > 3) { Logging.Write("[Corpse] NPCs repeatedly approached; stopping without resurrection"); break; }
                Logging.Write("[Corpse] an observed NPC approached; scouting another resurrection spot");
                if (!FindSafeSpot(memory, reader, hwnd, checkpoint, running, mover, routeStarted)) break;
                continue;
            }
            if (TryDialogClick(memory, reader, hwnd, checkpoint, running, false, out string problem)) { sentResurrect = true; break; }
            if (unchecked(Environment.TickCount - lastProblem) >= 5000)
            { Logging.Write("[Corpse] resurrection not clicked: " + problem); lastProblem = Environment.TickCount; }
            if (ClickAimAbort.Wait(500)) return false;
        }
        if (!sentResurrect) { Logging.Write("[Corpse] resurrection dialog not confirmed; stopping"); return false; }
        started = Environment.TickCount;
        int aliveReads = 0;
        while (unchecked(Environment.TickCount - started) < TransitionWaitMs && Allowed(memory, reader, hwnd, running))
        {
            if (ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Alive) aliveReads++;
            else aliveReads = 0;
            if (aliveReads >= 2 && WowBGrindFactions.Experimental70707BarSafe(reader))
            { Logging.Write("[Corpse] alive candidate and action bar confirmed; resuming the bot"); return true; }
            if (ClickAimAbort.Wait(500)) return false;
        }
        Logging.Write("[Corpse] resurrection or action-bar transition not confirmed; stopping without retrying");
        return false;
    }
}

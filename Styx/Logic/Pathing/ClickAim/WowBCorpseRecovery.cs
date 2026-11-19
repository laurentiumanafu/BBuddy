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
    /* A graveyard surrounded by fences swallows a ghost: one way in is blocked, and the
     * next try from two yards to the left is a different try. The user asked for twenty,
     * not five, before the run is given up. Every location is still mesh-validated and the
     * route limit, an unreadable ghost and STOP still end it earlier. */
    private const int CorpseRelocationAttempts = 20;
    /* Space, the client's default jump key. A fence is not in the mesh, so no mesh route
     * can go around one; the hop is unverified, like every other press this build sends. */
    private const int JumpVk = 0x20;
    private const int JumpSteerMs = 250, JumpSettleMs = 350, JumpTickMs = 50;

    private static bool Allowed(Memory memory, WowBReader reader, IntPtr hwnd, Func<bool> running)
        => running() && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
           ObjectManager.Wow == memory && ObjectManager.WowB == reader && memory.WindowHandle == hwnd &&
           reader.IsExperimental70707 && reader.ExperimentalGrindActive;

    private static State ReadState(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, out WoWPoint position)
        => ReadState(memory, reader, hwnd, checkpoint, running, out position, out _);

    private static State ReadState(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, out WoWPoint position, out string reason)
    {
        position = default;
        reason = "recovery session, attachment or STOP guard";
        try
        {
            if (!Allowed(memory, reader, hwnd, running)) return State.Unknown;
            using var uncached = memory.TemporaryCacheState(false);
            reason = "map unavailable";
            if (!reader.TryReadMapId(out uint map)) return State.Unknown;
            reason = "map differs from saved checkpoint";
            if (map != checkpoint.Map) return State.Unknown;
            if (!WowBDeathCheckpoint.ReadRecoveryPlayer(reader, out WowBObservedUnit player, out uint health, out uint maxHealth, out string playerReason))
            { reason = "local player: " + playerReason; return State.Unknown; }
            reason = "player GUID changed or health candidate invalid";
            if (player.Guid != checkpoint.Player || maxHealth == 0 || health > maxHealth) return State.Unknown;
            reason = "ghost candidate unavailable or unstable";
            if (!WowBGrindFactions.TryReadGhostFlagCandidate(memory, reader, player, out bool ghost)) return State.Unknown;
            reason = "map or session changed during observation";
            if (!reader.TryReadMapId(out uint mapAfter) || mapAfter != map || !Allowed(memory, reader, hwnd, running)) return State.Unknown;
            position = player.Location;
            reason = "player position invalid";
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) return State.Unknown;
            reason = ghost ? "ghost candidate set" : health == 0 ? "zero health, ghost candidate clear" : "positive health, ghost candidate clear";
            return ghost ? State.Ghost : health == 0 ? State.Dead : State.Alive;
        }
        catch { reason = "state read failed unexpectedly"; return State.Unknown; }
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
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, bool requireNpcClearance)
    {
        if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint p, out var npcs) ||
            !WowBCorpseSpotPolicy.InRange(p, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) ||
            (requireNpcClearance && !WowBCorpseSpotPolicy.ClearEnough(p, npcs, WowBCorpseSpotPolicy.MinNpcClearance)) ||
            !TrySnapSafeSpot(checkpoint.Map, p, checkpoint.Position, out WoWPoint snapped) ||
            WowBCorpseSpotPolicy.Distance(p, snapped) > 1.5 || Math.Abs((double)p.Z - snapped.Z) > 3) return false;
        return Allowed(memory, reader, hwnd, running) && ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Ghost;
    }

    // True means a guarded image was obtained; visible distinguishes a missing
    // prompt from unavailable focus/capture, which must not trigger blind roaming.
    private static bool TryPromptAtSpot(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, bool requireNpcClearance, out bool visible)
    {
        visible = false;
        bool Safety() => SpotSafe(memory, reader, hwnd, checkpoint, running, requireNpcClearance);
        if (!Safety() || !WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
            out var result, out _, out _, out _, out _, out _) || !Safety()) return false;
        visible = WowBCorpseDialogPattern.SuitableForClick(result, false, out _, out _);
        return true;
    }

    private static int _lastGroundDiagnostic;
    private static bool _groundDiagnosticWritten;
    private static void GroundDiagnostic(uint map, string reason)
    {
        int now = Environment.TickCount;
        if (_groundDiagnosticWritten && unchecked(now - _lastGroundDiagnostic) < 5000) return;
        _lastGroundDiagnostic = now; _groundDiagnosticWritten = true;
        Logging.Write("[Corpse] mesh ground check refused on map " + map + ": " + reason +
                      "; no resurrection click or unverified walk authorized");
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
                !poly.IsValid || !float.IsFinite(snapped.X) || !float.IsFinite(snapped.Y) || !float.IsFinite(snapped.Z))
            { GroundDiagnostic(map, "nearest polygon unavailable"); return false; }
            uint status = nav.GetPolyArea(map, poly, out byte area);
            if ((status & 0x80000000U) != 0)
            { GroundDiagnostic(map, "polygon area query failed"); return false; }
            if (!MmapGroundPolicy.IsGround(map, snapped, area, (x, y) => nav.IsTileLoaded(map, x, y)))
            { GroundDiagnostic(map, "area " + area + " or its loaded tile/format could not be verified as ground"); return false; }
            result = new WoWPoint(snapped.X, snapped.Y, snapped.Z);
            if (WowBCorpseSpotPolicy.Distance(candidate, result) <= 2.5 && Math.Abs((double)candidate.Z - result.Z) <= 5 &&
                WowBCorpseSpotPolicy.InRange(result, corpse, WowBCorpseSpotPolicy.MaxClickRadius)) return true;
            GroundDiagnostic(map, "snapped point exceeds corpse range or layer tolerance");
            return false;
        }
        catch { GroundDiagnostic(map, "mesh or format check threw an exception"); return false; }
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

    /* A fence, post or lip is not in the mesh, so no mesh route can go around it: the
     * router can only see polygons. Steer toward the next location and jump once, which
     * is what a player does at a fence. The press and its effect are unverified, the
     * steering stops as soon as the ghost state, attachment or STOP changes, and the
     * keys are always released. */
    private static void JumpToward(Memory memory, WowBReader reader, IntPtr hwnd, ClickAimMover mover,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, RoutePoint goal)
    {
        if (hwnd == IntPtr.Zero || !Allowed(memory, reader, hwnd, running)) return;
        bool quiet = mover.QuietKeyStop;
        bool oldRequireFacing = mover.RequireFacingForKeyMode;
        try
        {
            mover.QuietKeyStop = true;
            mover.RequireFacingForKeyMode = true;
            bool jumped = false;
            for (int waited = 0; waited < JumpSteerMs + JumpSettleMs; waited += JumpTickMs)
            {
                if (!Allowed(memory, reader, hwnd, running) ||
                    ReadState(memory, reader, hwnd, checkpoint, running, out _) != State.Ghost) return;
                mover.MoveTowards(goal.ToWoWPoint());
                if (!jumped && waited >= JumpSteerMs)
                {
                    jumped = true;
                    ClickAimInput.TapKey(hwnd, JumpVk, 40);
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Corpse] jumped while steering toward ({0:F1}, {1:F1}); a fence is not in the mesh, so this hop is unverified",
                        goal.X, goal.Y));
                }
                if (ClickAimAbort.Wait(JumpTickMs)) return;
            }
        }
        catch (Exception ex) { Logging.Write("[Corpse] the jump could not be sent: " + ex.Message); }
        finally
        {
            try { mover.MoveStop(); } catch { }
            ClickAimKeySteer.ReleaseHeld();
            try { mover.ClearBlock(); } catch { }
            mover.RequireFacingForKeyMode = oldRequireFacing;
            mover.QuietKeyStop = quiet;
        }
    }

    // The five-location recovery was previously used only by Gather hotspots.
    // After a failed corpse leg, try bounded, mesh-validated positions around
    // the current ghost and re-plan toward the corpse from each one. A failed
    // route is not proof of a physical obstacle; no ground is blacklisted here.
    // The corpse run gets twenty of these, not five: a fenced graveyard needs
    // more than one way in tried before a body is written off.
    private static bool WalkGhostWithRelocation(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, ClickAimMover mover, RoutePoint goal, int started)
    {
        if (WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, true)) return true;

        bool Interrupted() => !Allowed(memory, reader, hwnd, running) ||
            unchecked(Environment.TickCount - started) >= RouteLimitMs ||
            ReadState(memory, reader, hwnd, checkpoint, running, out _) != State.Ghost;
        if (Interrupted() || ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint origin) != State.Ghost) return false;
        if (WowBCorpseSpotPolicy.InRange(origin, checkpoint.Position, WowBCorpseSpotPolicy.ArrivalRadius)) return true;

        var regions = new WowBCorpseRelocationRegions();
        int attempts = 0;
        bool stuck = true;             // the corpse leg above did not complete
        Logging.Write("[Corpse] corpse leg did not complete; trying up to " + CorpseRelocationAttempts +
            " alternate mesh locations before giving up, hopping once before each try after a failure (no obstacle is assumed)");
        while (true)
        {
            if (Interrupted() || ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint current) != State.Ghost) return false;
            if (WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.ArrivalRadius)) return true;
            if (attempts >= CorpseRelocationAttempts)
            {
                Logging.Write("[Corpse] " + CorpseRelocationAttempts + " alternate mesh locations were tried without reaching the corpse; stopping");
                return false;
            }
            if (!regions.TrySelect(current.X, current.Y, out WowBCorpseRelocationRegions.Region? region, out bool changed, out bool created) || region == null)
            {
                Logging.Write("[Corpse] stuck-area limit reached or ghost position invalid; stopping rather than retrying indefinitely");
                return false;
            }
            if (changed)
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Corpse] {0} stuck area {1}/{2} around ({3:F1}, {4:F1}); {5} relocation(s) already tried here, {6}/{7} in total",
                    created ? "new" : "revisited", region.Number, WowBCorpseRelocationRegions.MaxRegions,
                    region.X, region.Y, region.Attempts, attempts, CorpseRelocationAttempts));
            if (!MeshRelocate.TryPick(checkpoint.Map, region.X, region.Y, region.Tried, (_, _) => false, Interrupted, out RoutePoint alternate))
            {
                if (Interrupted()) return false;
                /* Nothing validated from here. That is a statement about this bounded
                 * sampling, not about the terrain, so it counts as one of the tries and
                 * the loop looks again: the ghost may have shifted, and the sampling is
                 * random within the radius. */
                attempts++;
                region.Attempts++;
                Logging.Write("[Corpse] no additional alternate location could be validated on the mesh (" +
                    attempts + "/" + CorpseRelocationAttempts + ")");
                continue;
            }
            if (Interrupted()) return false;
            region.Tried.Add(alternate);
            region.Attempts++;
            int attempt = ++attempts;
            // A fence is not in the mesh, so a mesh route cannot be planned around it.
            // Hop before the walk when the last attempt did not get through.
            if (stuck) JumpToward(memory, reader, hwnd, mover, checkpoint, running, alternate);
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Corpse] relocation {0}/{1}: walking to ({2:F1}, {3:F1}) on the mesh",
                attempt, CorpseRelocationAttempts, alternate.X, alternate.Y));
            bool arrived = WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, alternate, started, true);
            if (Interrupted() || ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint position) != State.Ghost) return false;
            // The alternate walk is interrupted as soon as the ghost enters the
            // corpse arrival range, just like the original leg.
            if (WowBCorpseSpotPolicy.InRange(position, checkpoint.Position, WowBCorpseSpotPolicy.ArrivalRadius)) return true;
            if (!arrived)
            {
                Logging.Write("[Corpse] relocation " + attempt + "/" + CorpseRelocationAttempts + ": did not arrive; trying another location");
                stuck = true;
                continue;
            }
            Logging.Write("[Corpse] relocation " + attempt + "/" + CorpseRelocationAttempts + ": arrived; retrying the corpse route");
            if (WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, true)) return true;
            stuck = true;
        }
    }

    // Mesh-only movement to a candidate; never use a partial/long path as a shortcut.
    private static bool TryWalkToSpot(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, ClickAimMover mover,
        int started, WoWPoint current, WoWPoint spot)
    {
        if (!Allowed(memory, reader, hwnd, running) || unchecked(Environment.TickCount - started) >= RouteLimitMs) return false;
        if (WowBCorpseSpotPolicy.Distance(spot, current) < 3) return true;
        var goal = new RoutePoint { X = spot.X, Y = spot.Y, Z = spot.Z, Kind = "Resurrection spot" };
        WoWPoint[]? path = RouteRunner.TryCombatMeshPath(current.X, current.Y, goal);
        if (path == null || !Allowed(memory, reader, hwnd, running)) return false;
        double length = 0;
        for (int i = 1; i < path.Length; i++) length += WowBCorpseSpotPolicy.Distance(path[i - 1], path[i]);
        if (!double.IsFinite(length) || length > 120) return false;
        return WalkGhostTo(memory, reader, hwnd, checkpoint, running, mover, goal, started, false);
    }

    private static bool NpcMovedTooClose(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running)
        => TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint p, out var npcs) &&
           WowBCorpseSpotPolicy.InRange(p, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) &&
           !WowBCorpseSpotPolicy.ClearEnough(p, npcs, WowBCorpseSpotPolicy.MinNpcClearance);

    // Four different directions are shared across rescans, so moving NPCs cannot
    // restart the budget indefinitely. NPC proximity is a preference, not a
    // reason to abandon a confirmed resurrection after all four are tried.
    private static bool FindSafeSpot(Memory memory, WowBReader reader, IntPtr hwnd, WowBDeathCheckpoint.Candidate checkpoint,
        Func<bool> running, ClickAimMover mover, int started, bool[] tried, out bool fallback)
    {
        fallback = false;
        if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint current, out var npcs) ||
            !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) return false;
        double nearest = WowBCorpseSpotPolicy.Clearance(current, npcs);
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Corpse] scouting {0} observed non-player units; nearest={1}; absence or faction does not prove safety",
            npcs.Count, double.IsPositiveInfinity(nearest) ? "none observed" : nearest.ToString("F1", CultureInfo.InvariantCulture) + " yd"));
        if (WowBCorpseSpotPolicy.ClearEnough(current, npcs, WowBCorpseSpotPolicy.PreferCurrentClearance) &&
            SpotSafe(memory, reader, hwnd, checkpoint, running, true))
        {
            if (!TryPromptAtSpot(memory, reader, hwnd, checkpoint, running, true, out bool visible))
            {
                if (!NpcMovedTooClose(memory, reader, hwnd, checkpoint, running))
                { Logging.Write("[Corpse] cannot confirm current prompt capture or recovery state; stopping"); return false; }
                Logging.Write("[Corpse] NPC moved close during prompt check; scouting other locations");
            }
            else if (visible) { Logging.Write("[Corpse] current position has the prompt and preferred observed NPC clearance"); return true; }
        }

        for (int attempt = 0; attempt < WowBCorpseSpotPolicy.DirectionCount; attempt++)
        {
            if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs) ||
                !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) ||
                !Allowed(memory, reader, hwnd, running) || unchecked(Environment.TickCount - started) >= RouteLimitMs) return false;
            var candidates = WowBCorpseSpotPolicy.ScoutLocations(checkpoint.Position, current, npcs);
            if (candidates.Count != WowBCorpseSpotPolicy.DirectionCount) return false;
            WowBCorpseSpotPolicy.ScoutLocation candidate = default;
            bool found = false;
            foreach (var item in candidates)
                if (!tried[item.Direction]) { candidate = item; found = true; break; }
            if (!found) break;
            tried[candidate.Direction] = true;
            int number = 0;
            foreach (bool wasTried in tried) if (wasTried) number++;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Corpse] scouting location {0}/4 (direction {1}); candidate nearest observed NPC {2:F1} yd",
                number, candidate.Direction + 1, WowBCorpseSpotPolicy.Clearance(candidate.Position, npcs)));
            if (!TrySnapSafeSpot(checkpoint.Map, candidate.Position, checkpoint.Position, out WoWPoint spot) ||
                !TryWalkToSpot(memory, reader, hwnd, checkpoint, running, mover, started, current, spot))
            {
                if (!Allowed(memory, reader, hwnd, running) ||
                    ReadState(memory, reader, hwnd, checkpoint, running, out _) != State.Ghost) return false;
                Logging.Write("[Corpse] that scout location was not reachable on the mesh; trying the next direction");
                continue;
            }
            if (ClickAimAbort.Wait(500)) return false;
            if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out npcs) ||
                !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) return false;
            if (!WowBCorpseSpotPolicy.ClearEnough(current, npcs, WowBCorpseSpotPolicy.MinNpcClearance) ||
                !SpotSafe(memory, reader, hwnd, checkpoint, running, true))
            { Logging.Write("[Corpse] scout location lacks the preferred observed NPC clearance or ground; trying the next direction"); continue; }
            if (!TryPromptAtSpot(memory, reader, hwnd, checkpoint, running, true, out bool prompt))
            {
                if (!NpcMovedTooClose(memory, reader, hwnd, checkpoint, running))
                { Logging.Write("[Corpse] cannot confirm prompt capture or recovery state at scout location; stopping"); return false; }
                Logging.Write("[Corpse] NPC moved close during prompt check; trying the next direction");
                continue;
            }
            if (prompt)
            { Logging.Write("[Corpse] resurrection prompt visible at a mesh spot with NPC clearance; rechecking before click"); return true; }
            Logging.Write("[Corpse] prompt not visible at this scout location; trying the next direction");
        }

        fallback = true;
        Logging.Write("[Corpse] no suitable scout location found after four directions; falling back to resurrection even near observed NPCs");
        return FindFallbackPrompt(memory, reader, hwnd, checkpoint, running, mover, started);
    }

    // Fallback relaxes NPC clearance only. A verified ghost/session, mesh ground,
    // the matching prompt, click/hover guards, and alive confirmation still apply.
    private static bool FindFallbackPrompt(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, ClickAimMover mover, int started)
    {
        if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out WoWPoint current, out _) ||
            !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) return false;
        if (SpotSafe(memory, reader, hwnd, checkpoint, running, false))
        {
            if (!TryPromptAtSpot(memory, reader, hwnd, checkpoint, running, false, out bool visible))
            { Logging.Write("[Corpse] fallback prompt capture or safety check unavailable; stopping without a click"); return false; }
            if (visible)
            { Logging.Write("[Corpse] resurrection prompt is visible here; using fallback even near observed NPCs"); return true; }
            Logging.Write("[Corpse] captured frame did not meet resurrection-button checks; checking reachable positions closer to the checkpoint");
        }
        else Logging.Write("[Corpse] current position failed ghost, range or ground verification; prompt was not checked here; checking reachable positions closer to the checkpoint");
        double reachedRadius = double.PositiveInfinity;
        foreach (WoWPoint candidate in WowBCorpseSpotPolicy.FallbackLocations(checkpoint.Position, current))
        {
            double radius = WowBCorpseSpotPolicy.Distance(candidate, checkpoint.Position);
            // Once a grounded point on this ring was visited, go inward instead
            // of walking around the entire ring just to find the same prompt.
            if (radius + 0.5 >= reachedRadius) continue;
            if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out current, out _) ||
                !WowBCorpseSpotPolicy.InRange(current, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius) ||
                !Allowed(memory, reader, hwnd, running) || unchecked(Environment.TickCount - started) >= RouteLimitMs) return false;
            if (!TrySnapSafeSpot(checkpoint.Map, candidate, checkpoint.Position, out WoWPoint spot)) continue;
            if (WowBCorpseSpotPolicy.Distance(current, spot) < 3)
            {
                if (SpotSafe(memory, reader, hwnd, checkpoint, running, false)) reachedRadius = radius;
                continue;
            }
            if (!TryWalkToSpot(memory, reader, hwnd, checkpoint, running, mover, started, current, spot)) continue;
            if (ClickAimAbort.Wait(500)) return false;
            if (!SpotSafe(memory, reader, hwnd, checkpoint, running, false)) continue;
            reachedRadius = radius;
            if (!TryPromptAtSpot(memory, reader, hwnd, checkpoint, running, false, out bool visible)) return false;
            if (visible)
            { Logging.Write("[Corpse] resurrection prompt found while moving closer; NPC clearance is waived for fallback"); return true; }
        }
        // The client may show its prompt after a short delay once movement stops.
        // Do not treat a single missing frame as proof that resurrection is impossible.
        int waitedFrom = Environment.TickCount;
        while (Allowed(memory, reader, hwnd, running) && unchecked(Environment.TickCount - waitedFrom) < PromptWaitMs)
        {
            if (!SpotSafe(memory, reader, hwnd, checkpoint, running, false) ||
                !TryPromptAtSpot(memory, reader, hwnd, checkpoint, running, false, out bool visible)) break;
            if (visible) return true;
            if (ClickAimAbort.Wait(500)) return false;
        }
        Logging.Write("[Corpse] fallback could not confirm both safe ground and a matching resurrection prompt; cannot safely click or resume as a ghost");
        return false;
    }

    // Where a label was matched, so a machine that puts the dialog somewhere
    // else says where instead of only saying that nothing was found.
    private static string LabelWhere(bool found, int x, int y) => found ? "(" + x + "," + y + ")" : "not found";

    private static bool TryDialogClick(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, bool release, bool requireNpcClearance, out string problem,
        bool recoverHoveredButton = false)
    {
        problem = "player state or recovery session changed";
        State expected = release ? State.Dead : State.Ghost;
        bool Safety()
        {
            if (!Allowed(memory, reader, hwnd, running) || ReadState(memory, reader, hwnd, checkpoint, running, out _) != expected) return false;
            return release || SpotSafe(memory, reader, hwnd, checkpoint, running, requireNpcClearance);
        }
        if (!Safety()) return false;
        if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out var dialog, out _, out bool background, out int width, out int height, out problem)) return false;
        // A recorded death-button calibration is a primary click source, not
        // just an override: on a PC where pattern matching cannot find the
        // button, the calibration still supplies the click point. The player
        // state (dead/ghost) is verified by Safety(); pattern matching is
        // only consulted when no calibration exists.
        bool calibrated = false;
        int x = 0, y = 0;
        if (WowBDeathButtonCalibration.TryLoad(checkpoint.Player.ToString(), width, height, out var calib) && calib != null)
        {
            calibrated = true;
            x = release ? calib.ReleaseX : calib.AcceptX;
            y = release ? calib.ReleaseY : calib.AcceptY;
            Logging.Write("[Corpse] using calibrated " + (release ? "Release Spirit" : "Accept Resurrection") +
                " button at (" + x + "," + y + ") of a " + width + "x" + height + " client");
        }
        if (!calibrated && !WowBCorpseDialogPattern.SuitableForClick(dialog, release, out x, out y))
        {
            problem = (background ? "background" : "foreground") + " image did not meet button checks (client " +
                width + "x" + height + "; release=" + dialog.ReleaseText + " at " + LabelWhere(dialog.ReleaseText, dialog.ReleaseX, dialog.ReleaseY) +
                ", releaseUnique=" + dialog.ReleaseUnique + ", releaseRed=" + dialog.ReleaseButtonRed +
                "; heading=" + dialog.ResurrectText + " at " + LabelWhere(dialog.ResurrectText, dialog.ResurrectX, dialog.ResurrectY) +
                "; accept=" + dialog.AcceptText + " at " + LabelWhere(dialog.AcceptText, dialog.AcceptX, dialog.AcceptY) +
                ", acceptUnique=" + dialog.AcceptUnique + ", acceptRed=" + dialog.AcceptButtonRed +
                "; widerSearchOnly=" + WowBCorpseDialogPattern.UsedExtendedSearch(dialog, release) + ")";
            // A previous posted hover can leave the button highlighted. For a
            // guarded retry only, clear it and inspect a fresh image; never
            // treat the highlighted or missing image as permission to click.
            if (!Safety() || !ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05)) return false;
            if (!recoverHoveredButton || ClickAimAbort.Wait(200)) return false;
            if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out dialog, out _, out bool freshBackground, out int freshWidth, out int freshHeight, out problem) ||
                freshBackground != background || freshWidth != width || freshHeight != height ||
                !WowBCorpseDialogPattern.SuitableForClick(dialog, release, out x, out y))
            { problem = "fresh dialog unavailable or unsuitable after clearing prior hover"; return false; }
        }
        /* Only the wider fallback sweep found this label, so the dialog sits outside
         * the band the supplied screenshots were matched in - what another resolution,
         * window or UI placement on another PC does. Nothing is relaxed: uniqueness,
         * the red button, the heading above Accept, the state guards and the hover
         * response still decide. Say where it was found so the band can be corrected. */
        if (WowBCorpseDialogPattern.UsedExtendedSearch(dialog, release))
            Logging.Write("[Corpse] " + (release ? "Release Spirit" : "Accept") + " label matched only in the wider search area at (" +
                x + "," + y + ") of a " + width + "x" + height + " client; uniqueness, red button, heading position, state and hover checks still apply");
        // First clear a previous hover and obtain a fresh pre-hover image. The
        // background mode additionally requires a visible response to our hover.
        problem = "state or hover-away move failed";
        if (!Safety() || !ClickAimInput.PostMouseMoveFraction(hwnd, 0.05, 0.05) || ClickAimAbort.Wait(200)) return false;
        if (!WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out var away, out byte[]? before, out bool awayBackground, out int awayWidth, out int awayHeight, out problem)) return false;
        problem = "button, focus or client geometry changed after hover-away";
        if (before == null || awayBackground != background || awayWidth != width || awayHeight != height ||
            (!calibrated && (!WowBCorpseDialogPattern.SuitableForClick(away, release, out int awayX, out int awayY) ||
            (Math.Abs(awayX - x) > 2 || Math.Abs(awayY - y) > 2)))) return false;
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
                int x2 = 0, y2 = 0;
                if (!calibrated && !WowBCorpseDialogPattern.SuitableForClick(again, release, out x2, out y2))
                    lastFailure = "after hover: release=" + again.ReleaseText + ", releaseUnique=" + again.ReleaseUnique +
                        ", releaseRed=" + again.ReleaseButtonRed + ", heading=" + again.ResurrectText +
                        ", accept=" + again.AcceptText + ", acceptUnique=" + again.AcceptUnique + ", acceptRed=" + again.AcceptButtonRed;
                else if (!calibrated && (Math.Abs(x2 - x) > 2 || Math.Abs(y2 - y) > 2))
                    return "button position changed after hover";
                else if (background && !WowBCorpseDialogPattern.ButtonChanged(before, after, width, height, x, y))
                    lastFailure = "background capture showed no fresh button response to hover; no click";
                else return Safety() ? null : "player state or recovery session changed";
                if (frame < 3 && ClickAimAbort.Wait(180)) return "stopped while waiting for hover frame";
            }
            return lastFailure;
        }
        // Give the 70938 resurrection dialog a longer posted button-down. This
        // is an unverified delivery mitigation, not a physical mouse click.
        int holdMs = !release && reader.Uses70938Features ? 100 : 35;
        bool posted = ClickAimInput.ConfirmedLeftClick(hwnd, x, y, width, height, background, FinalCheck, out string reason, holdMs);
        problem = reason;
        if (posted) Logging.Write("[Corpse] " + (release ? "release" : "resurrection") + " dialog click (" +
                                  (background ? "background window capture" : "foreground") + "): " + reason +
                                  "; hold=" + holdMs + " ms; post is not confirmation");
        return posted;
    }

    internal static bool TryRecover(WowBDeathCheckpoint.Candidate checkpoint, ClickAimMover mover, Func<bool> running)
    {
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        IntPtr hwnd = memory?.WindowHandle ?? IntPtr.Zero;
        if (memory == null || reader == null || hwnd == IntPtr.Zero || !Allowed(memory, reader, hwnd, running)) return false;
        // Mesh is mandatory before releasing. A Grind fight can use key-steered
        // mesh chasing without ever calibrating its route mover; after release we
        // may safely calibrate turns in place, never the normal blind probe walk.
        if (!RouteRunner.MeshReady())
        {
            Logging.Write("[Corpse] mesh unavailable; stopping before release rather than stranding the ghost");
            return false;
        }
        if (!mover.KeyMode)
            Logging.Write("[Corpse] route mover has no walking calibration; turn-only ghost steering will be checked after release");
        Logging.Write("[Corpse] EXPERIMENTAL 70707 recovery: foreground capture or experimental background window capture; STOP remains active. No Lua.");
        bool initialQuiet = mover.QuietKeyStop;
        try { mover.QuietKeyStop = true; mover.MoveStop(); }
        finally { mover.QuietKeyStop = initialQuiet; }
        int started = Environment.TickCount, lastProblem = unchecked(Environment.TickCount - 6000);
        bool released = false, sentRelease = false;
        string releasePostReason = "";
        while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
        {
            State state = ReadState(memory, reader, hwnd, checkpoint, running, out _);
            if (state == State.Ghost) { released = true; break; }
            if (state == State.Alive) { Logging.Write("[Corpse] player is already alive; stopping rather than guessing a recovery transition"); return false; }
            string problem = "player state unavailable or changing";
            if (state == State.Dead && TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true, out problem))
            { sentRelease = true; releasePostReason = problem; break; }
            if (unchecked(Environment.TickCount - lastProblem) >= 5000)
            { Logging.Write("[Corpse] release not clicked: " + problem); lastProblem = Environment.TickCount; }
            if (ClickAimAbort.Wait(500)) return false;
        }
        if (sentRelease)
        {
            (WowBReleaseTransition.State State, string Reason) ObserveRelease()
            {
                State state = ReadState(memory, reader, hwnd, checkpoint, running, out _, out string reason);
                return (state switch
                {
                    State.Dead => WowBReleaseTransition.State.Dead,
                    State.Ghost => WowBReleaseTransition.State.Ghost,
                    State.Alive => WowBReleaseTransition.State.Alive,
                    _ => WowBReleaseTransition.State.Unknown
                }, reason);
            }
            (bool Posted, string Reason) RetryRelease()
            {
                bool posted = TryDialogClick(memory, reader, hwnd, checkpoint, running, true, true, out string problem);
                return (posted, problem);
            }
            released = WowBReleaseTransition.Wait(ObserveRelease, RetryRelease,
                () => Allowed(memory, reader, hwnd, running), ClickAimAbort.Wait, () => Environment.TickCount,
                Logging.Write, releasePostReason, TransitionWaitMs, reader.Uses70938Features);
        }
        if (!released) { Logging.Write("[Corpse] ghost transition not confirmed; stopping without corpse movement"); return false; }
        if (ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint ghostPos) != State.Ghost ||
            !RouteRunner.MeshReady()) { Logging.Write("[Corpse] ghost, map or mesh unavailable; stopping"); return false; }
        double distance = ClickAimMath.Len(ghostPos.X - checkpoint.Position.X, ghostPos.Y - checkpoint.Position.Y);
        if (!double.IsFinite(distance) || distance > MaxGhostDistance)
        { Logging.Write("[Corpse] checkpoint too far away or invalid; refusing route"); return false; }
        bool temporaryKeyMode = !mover.KeyMode;
        bool oldRequireFacing = mover.RequireFacingForKeyMode;
        try
        {
            if (temporaryKeyMode)
            {
                bool SafeGhost() => ReadState(memory, reader, hwnd, checkpoint, running, out _) == State.Ghost;
                if (!mover.TryEnableRecoveryKeySteering(hwnd, SafeGhost, out string steeringReport))
                {
                    Logging.Write("[Corpse] turn-only ghost steering unavailable; stopping without movement: " + steeringReport);
                    return false;
                }
                Logging.Write("[Corpse] using turn-only calibrated steering for the ghost; the normal probe walk was skipped");
            }
            mover.RequireFacingForKeyMode = true;
            if (ReadState(memory, reader, hwnd, checkpoint, running, out _) != State.Ghost) return false;
            Logging.Write("[Corpse] ghost candidate confirmed; following the mesh toward the checkpoint, stopping short to scout");
            var goal = new RoutePoint { X = checkpoint.Position.X, Y = checkpoint.Position.Y, Z = checkpoint.Position.Z, Kind = "Corpse" };
            int routeStarted = Environment.TickCount;
            if (!WalkGhostWithRelocation(memory, reader, hwnd, checkpoint, running, mover, goal, routeStarted) ||
                ReadState(memory, reader, hwnd, checkpoint, running, out ghostPos) != State.Ghost ||
                !WowBCorpseSpotPolicy.InRange(ghostPos, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius))
            { Logging.Write("[Corpse] mesh route or proximity was not confirmed; stopping"); return false; }
            var tried = new bool[WowBCorpseSpotPolicy.DirectionCount];
            if (ClickAimAbort.Wait(800) || !FindSafeSpot(memory, reader, hwnd, checkpoint, running, mover, routeStarted, tried, out bool fallback)) return false;
            started = Environment.TickCount; lastProblem = unchecked(started - 6000);
            bool sentResurrect = false;
            string resurrectPostReason = "";
            while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
            {
                if (!TryNpcSnapshot(memory, reader, hwnd, checkpoint, running, out ghostPos, out var nearby) ||
                    !WowBCorpseSpotPolicy.InRange(ghostPos, checkpoint.Position, WowBCorpseSpotPolicy.MaxClickRadius)) break;
                if (!fallback && !WowBCorpseSpotPolicy.ClearEnough(ghostPos, nearby, WowBCorpseSpotPolicy.MinNpcClearance))
                {
                    Logging.Write("[Corpse] an observed NPC approached; trying remaining scout locations");
                    if (!FindSafeSpot(memory, reader, hwnd, checkpoint, running, mover, routeStarted, tried, out fallback)) break;
                    continue;
                }
                if (TryDialogClick(memory, reader, hwnd, checkpoint, running, false, !fallback, out string problem))
                { sentResurrect = true; resurrectPostReason = problem; break; }
                if (unchecked(Environment.TickCount - lastProblem) >= 5000)
                { Logging.Write("[Corpse] resurrection not clicked: " + problem); lastProblem = Environment.TickCount; }
                if (ClickAimAbort.Wait(500)) return false;
            }
            if (!sentResurrect) { Logging.Write("[Corpse] resurrection dialog not confirmed; stopping"); return false; }
            started = Environment.TickCount;
            int aliveReads = 0;
            var retry = new WowBResurrectionRetry(reader.Uses70938Features, resurrectPostReason, started);
            if (reader.Uses70938Features && resurrectPostReason != WowBReleaseTransition.CleanPost)
                Logging.Write("[Corpse] resurrection button-up was not confirmed posted; retries disabled");
            while (unchecked(Environment.TickCount - started) < TransitionWaitMs && Allowed(memory, reader, hwnd, running))
            {
                State state = ReadState(memory, reader, hwnd, checkpoint, running, out _);
                if (state == State.Alive) aliveReads++;
                else aliveReads = 0;
                if (aliveReads >= 2 && WowBGrindFactions.Experimental70707BarSafe(reader))
                { Logging.Write("[Corpse] alive candidate and action bar confirmed; resuming the bot"); return true; }
                if (retry.Observe(state == State.Ghost, Environment.TickCount) && Allowed(memory, reader, hwnd, running))
                {
                    // Recheck player/spot, corpse range, NPC clearance policy,
                    // fresh dialog, hover, focus and STOP; never repeat blindly.
                    bool posted = TryDialogClick(memory, reader, hwnd, checkpoint, running, false, !fallback, out string retryReason, recoverHoveredButton: true);
                    retry.Attempted(posted, retryReason, Environment.TickCount);
                    if (posted)
                        Logging.Write("[Corpse] resurrection retry " + retry.Posts + "/" + WowBResurrectionRetry.MaxPosts +
                            " posted after repeated ghost observations; transition still unconfirmed");
                    else
                        Logging.Write("[Corpse] resurrection retry withheld; further retries disabled: " + retryReason);
                }
                if (ClickAimAbort.Wait(500)) return false;
            }
            State finalState = ReadState(memory, reader, hwnd, checkpoint, running, out _, out string finalReason);
            Logging.Write("[Corpse] resurrection or action-bar transition not confirmed; stopping (state=" +
                finalState.ToString().ToLowerInvariant() + ", " + finalReason + "; posts=" + retry.Posts + ")");
            return false;
        }
        finally
        {
            ClickAimKeySteer.ReleaseHeld();
            mover.RequireFacingForKeyMode = oldRequireFacing;
            if (temporaryKeyMode) mover.EndRecoveryKeySteering();
        }
    }
}

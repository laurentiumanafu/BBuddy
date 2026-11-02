using System;
using System.Collections.Generic;
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
    private const double MaxGhostDistance = 3000.0, CorpseTolerance = 8.0, MaxVerticalDistance = 15.0;

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

    private static bool TryDialogClick(Memory memory, WowBReader reader, IntPtr hwnd,
        WowBDeathCheckpoint.Candidate checkpoint, Func<bool> running, bool release)
    {
        State expected = release ? State.Dead : State.Ghost;
        bool Safety()
        {
            if (!Allowed(memory, reader, hwnd, running) || ReadState(memory, reader, hwnd, checkpoint, running, out WoWPoint p) != expected) return false;
            return release || (ClickAimMath.Len(p.X - checkpoint.Position.X, p.Y - checkpoint.Position.Y) <= CorpseTolerance &&
                               Math.Abs(p.Z - checkpoint.Position.Z) <= MaxVerticalDistance);
        }
        if (!Safety() || !WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                out var dialog, out int width, out int height) || !WowBCorpseDialogPattern.SuitableForClick(dialog, release, out int x, out int y)) return false;
        bool FinalCheck()
        {
            if (!Safety() || !WowBCorpseDialogProbe.TryInspectForRecovery(memory, reader, hwnd, Safety,
                    out var again, out int w, out int h) || w != width || h != height ||
                !WowBCorpseDialogPattern.SuitableForClick(again, release, out int x2, out int y2)) return false;
            return Math.Abs(x2 - x) <= 2 && Math.Abs(y2 - y) <= 2 && Safety();
        }
        bool posted = ClickAimInput.ConfirmedLeftClick(hwnd, x, y, width, height, FinalCheck, out string reason);
        Logging.Write("[Corpse] " + (release ? "release" : "resurrection") + " dialog click: " + reason);
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
        Logging.Write("[Corpse] EXPERIMENTAL 70707 recovery: foreground WoW is required for each prompt; STOP remains active. No Lua.");
        bool initialQuiet = mover.QuietKeyStop;
        try { mover.QuietKeyStop = true; mover.MoveStop(); }
        finally { mover.QuietKeyStop = initialQuiet; }
        int started = Environment.TickCount;
        bool released = false, sentRelease = false;
        while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
        {
            State state = ReadState(memory, reader, hwnd, checkpoint, running, out _);
            if (state == State.Ghost) { released = true; break; }
            if (state == State.Alive) { Logging.Write("[Corpse] player is already alive; stopping rather than guessing a recovery transition"); return false; }
            if (state == State.Dead && TryDialogClick(memory, reader, hwnd, checkpoint, running, true)) { sentRelease = true; break; }
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
        Logging.Write("[Corpse] ghost candidate confirmed; planning a mesh-only route to the recorded location");
        var goal = new RoutePoint { X = checkpoint.Position.X, Y = checkpoint.Position.Y, Z = checkpoint.Position.Z, Kind = "Corpse" };
        var oldInterrupt = RouteRunner.ShouldInterrupt;
        bool oldPoll = RouteRunner.PollInterruptDuringWalk, oldMesh = RouteRunner.RequireMeshDuringSession, oldQuiet = mover.QuietKeyStop;
        started = Environment.TickCount;
        bool arrived;
        try
        {
            RouteRunner.ShouldInterrupt = () => !Allowed(memory, reader, hwnd, running) ||
                unchecked(Environment.TickCount - started) >= RouteLimitMs ||
                ReadState(memory, reader, hwnd, checkpoint, running, out _) != State.Ghost;
            RouteRunner.PollInterruptDuringWalk = true; RouteRunner.RequireMeshDuringSession = true; mover.QuietKeyStop = true;
            arrived = RouteRunner.RunWithinSession(mover, new List<RoutePoint> { goal }, 1);
        }
        finally
        {
            RouteRunner.ShouldInterrupt = oldInterrupt; RouteRunner.PollInterruptDuringWalk = oldPoll;
            RouteRunner.RequireMeshDuringSession = oldMesh;
            ClickAimKeySteer.ReleaseHeld();
            try { mover.MoveStop(); } catch { }
            mover.QuietKeyStop = oldQuiet;
        }
        if (!arrived || RouteRunner.LastRunInterrupted || ReadState(memory, reader, hwnd, checkpoint, running, out ghostPos) != State.Ghost ||
            ClickAimMath.Len(ghostPos.X - checkpoint.Position.X, ghostPos.Y - checkpoint.Position.Y) > CorpseTolerance ||
            Math.Abs(ghostPos.Z - checkpoint.Position.Z) > MaxVerticalDistance)
        { Logging.Write("[Corpse] mesh route or final corpse proximity was not confirmed; stopping"); return false; }
        started = Environment.TickCount;
        bool sentResurrect = false;
        while (unchecked(Environment.TickCount - started) < PromptWaitMs && Allowed(memory, reader, hwnd, running))
        {
            if (ReadState(memory, reader, hwnd, checkpoint, running, out ghostPos) != State.Ghost ||
                ClickAimMath.Len(ghostPos.X - checkpoint.Position.X, ghostPos.Y - checkpoint.Position.Y) > CorpseTolerance ||
                Math.Abs(ghostPos.Z - checkpoint.Position.Z) > MaxVerticalDistance) break;
            if (TryDialogClick(memory, reader, hwnd, checkpoint, running, false)) { sentResurrect = true; break; }
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

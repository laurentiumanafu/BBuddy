using System;

namespace Styx.Logic.Pathing;

// Diagnostic focus classification only. It is not evidence of mouse isolation
// and must never be used to authorize a gameplay interaction.
internal static class GatherBackgroundFocus
{
    internal enum State { OtherApplication, Unavailable, Game }

    internal static State Classify(IntPtr foreground, IntPtr game, uint process, uint gameProcess)
    {
        if (foreground == IntPtr.Zero) return State.Unavailable;
        if (foreground == game) return State.Game;
        if (process == 0 || gameProcess == 0) return State.Unavailable;
        return process == gameProcess ? State.Game : State.OtherApplication;
    }
}

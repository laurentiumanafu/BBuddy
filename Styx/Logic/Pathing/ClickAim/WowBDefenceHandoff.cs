using System;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Policy for continuing defence after a fight observes a changed selection.
// Selection and proximity do not prove hostility or identify the attacker.
internal static class WowBDefenceHandoff
{
    internal static bool MayQueue(WowBGuid previous, WowBGuid selected, bool stopped)
        => !stopped && !previous.IsEmpty && !selected.IsEmpty && selected != previous;

    internal static bool MayFight(WowBGuid queued, WowBGuid selected, bool stopped, bool fighting,
                                  bool isLocalPlayer, bool hasPlayerVtable, bool fieldsValid,
                                  bool factionAllowed, double distance, double maxDistance)
        => !stopped && fighting && !queued.IsEmpty && queued == selected &&
           !isLocalPlayer && !hasPlayerVtable && fieldsValid && factionAllowed &&
           double.IsFinite(distance) && distance >= 0 && distance <= maxDistance;
}

#if WOWB_READER_TESTS
using System;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

internal static class DefenceHandoffTests
{
    internal static void Run()
    {
        var first = new WowBGuid(1, 2);
        var second = new WowBGuid(1, 3); // same low half, distinct full GUID
        if (!WowBDefenceHandoff.MayQueue(first, second, false) ||
            WowBDefenceHandoff.MayQueue(first, first, false) ||
            WowBDefenceHandoff.MayQueue(first, default, false) ||
            WowBDefenceHandoff.MayQueue(first, second, true))
            throw new Exception("Defence target handoff queue policy failed");

        bool MayFight(WowBGuid? selected = null, bool stopped = false, bool fighting = true,
                      bool local = false, bool player = false, bool fields = true, bool faction = true,
                      double distance = 2.8)
            => WowBDefenceHandoff.MayFight(second, selected ?? second, stopped, fighting, local, player,
                                           fields, faction, distance, 12.0);
        if (!MayFight() || !MayFight(distance: 12.0) || MayFight(selected: first) || MayFight(stopped: true) ||
            MayFight(fighting: false) || MayFight(local: true) || MayFight(player: true) || MayFight(fields: false) ||
            MayFight(faction: false) || MayFight(distance: 12.1) || MayFight(distance: double.NaN))
            throw new Exception("Defence handoff must recheck combat, selection, unit, faction, range and STOP");
        Console.WriteLine("Defence target handoff policy fixtures passed (no live combat test).");
    }
}
#endif

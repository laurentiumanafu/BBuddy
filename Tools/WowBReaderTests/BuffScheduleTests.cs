#if WOWB_READER_TESTS
using System;
using Styx.Logic.Pathing;

internal static class BuffScheduleTests
{
    internal static void Run()
    {
        var intellect = new ClickAimAction { Id = 1459, Slot = 5, KeyVk = ClickAimCombat.KeyForSlot(5), Kind = ClickAimActionKind.Spell };
        var armor = new ClickAimAction { Id = 168, Slot = 6, KeyVk = ClickAimCombat.KeyForSlot(6), Kind = ClickAimActionKind.Spell };
        var spell = new ClickAimAction { Id = 133, Slot = 2, KeyVk = ClickAimCombat.KeyForSlot(2), Kind = ClickAimActionKind.Spell };
        ClickAimCombat.ClearRoleOverrides();
        if (!intellect.IsBuff || !armor.IsBuff || spell.IsBuff)
            throw new Exception("Mage buff roles were not recognized");
        var schedule = new ClickAimBuffSchedule();
        if (!schedule.Due(1459, 0) || !schedule.Due(168, 0) || schedule.Due(0, 0))
            throw new Exception("Fresh buff schedule failed");
        schedule.RecordAttempt(1459, 100);
        if (schedule.Due(1459, 100 + ClickAimBuffSchedule.RefreshMs - 1) ||
            !schedule.Due(1459, 100 + ClickAimBuffSchedule.RefreshMs) || !schedule.Due(168, 100))
            throw new Exception("Per-spell 30-minute timer failed");
        schedule.Reset();
        if (!schedule.Due(1459, 101)) throw new Exception("Post-resurrection/new-run reset failed");
        Console.WriteLine("Buff timer and Mage hotbar role fixtures passed (no live aura or cast test).");
    }
}
#endif

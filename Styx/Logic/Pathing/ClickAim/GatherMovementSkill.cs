namespace Styx.Logic.Pathing;

// This is slot identification, not a cast result or form-state reader.
internal static class GatherMovementSkill
{
    internal static bool IsSupported(ClickAimAction action, int slot)
        => slot >= 1 && slot <= 12 && action.Slot == slot && action.Kind == ClickAimActionKind.Spell &&
           (action.Id == 2645 || action.Id == 783 || action.Id == 5118) && action.Raw == (ulong)action.Id &&
           action.KeyVk == ClickAimCombat.KeyForSlot(slot);
}

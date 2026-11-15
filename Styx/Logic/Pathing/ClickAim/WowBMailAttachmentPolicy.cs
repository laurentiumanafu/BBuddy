namespace Styx.Logic.Pathing;

// A stable, unchanged raw candidate table is only evidence that an attempted
// attachment was not observed. It does not prove an item is a quest item,
// ineligible, or immune to a later update. Never use it to retry a click.
internal sealed class WowBMailAttachmentPolicy
{
    internal enum Decision { Pending, Attached, Skip, Hold }
    private int _samples;
    private bool _uncertain;
    private bool _attached;

    internal Decision Observe(bool readable, bool matchesBefore, bool matchesWithItem)
    {
        _samples++;
        if (readable && matchesWithItem) { _attached = true; return Decision.Attached; }
        if (!readable || !matchesBefore) _uncertain = true;
        return Decision.Pending;
    }

    // Called after the full bounded observation period, with fresh bag/table
    // checks bracketing each other. Unknown or changing state always holds.
    internal Decision Finish(bool completedWindow, bool bagStable, bool finalTableMatchesBefore)
    {
        if (_attached) return Decision.Attached;
        return completedWindow && _samples >= 3 && !_uncertain && bagStable && finalTableMatchesBefore
            ? Decision.Skip : Decision.Hold;
    }
}

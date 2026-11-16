using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Returns a count only for observed candidates. The separate backpack and
// unknown candidate 4 are not observed; disappearance is not proof of mail.
internal static class WowBMailSendObservation
{
    internal static int CountMissing(IReadOnlyList<WowBMailCandidateTable.Expected>? expected,
        IReadOnlyList<WowBMailGridLookup.Bag>? bags)
    {
        if (expected == null || expected.Count == 0 || expected.Count > 12 || bags == null || bags.Count != 5) return -1;
        var wanted = new HashSet<WowBGuid>();
        foreach (var item in expected)
            if (item.Item.IsEmpty || !wanted.Add(item.Item)) return -1;
        var seenBags = new HashSet<int>();
        var seenItems = new HashSet<WowBGuid>();
        foreach (var bag in bags)
        {
            if (bag == null || bag.Candidate < 0 || bag.Candidate > 4 || !seenBags.Add(bag.Candidate) || bag.Slots == null ||
                (bag.Unknown && (bag.Candidate < 4 || bag.Slots.Length != 0))) return -1;
            foreach (WowBGuid item in bag.Slots)
                if (!item.IsEmpty && !seenItems.Add(item)) return -1;
        }
        int missing = 0;
        foreach (var item in wanted) if (!seenItems.Contains(item)) missing++;
        return missing;
    }
}

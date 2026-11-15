using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Pure research policy: not connected to gameplay. The caller must prove the
// completeness and meaning of every inventory/UI observation before marking it
// trusted. A posted click or an item disappearing into an attachment is not a
// server acknowledgement. Candidate numbers are NOT verified on-screen bags.
internal static class WowBMailBatchPolicy
{
    internal const int BatchLimit = 12;
    internal const int FailureLimit = 3;
    internal enum Selection { Unknown, Empty, Batch }
    internal enum SendEvidence { Unknown, Accepted, Rejected }
    internal enum Decision { Hold, Progress, Retry, ReturnToProfile }

    internal readonly struct Slot
    {
        internal readonly int Candidate, Index;
        internal readonly WowBGuid Item;
        internal Slot(int candidate, int index, WowBGuid item)
        { Candidate = candidate; Index = index; Item = item; }
    }

    // Require complete, stable observations of the separately represented
    // backpack (candidate -1) and all five container candidates 0..4, so a move
    // into the excluded backpack cannot be misread as disappearance. The supplied
    // 70938 trace places the backpack in the player range, not container 0.
    // Every verified equipped container, including 0 and a future proven 4,
    // is eligible. Zero or unavailable identities remain unknown; they do not
    // establish that a bag is absent or the inventory is complete.
    // 'complete' is not a substitute for a verified live reader; no such
    // reader or UI mapper is wired yet.
    internal static Selection Plan(bool complete, IReadOnlyList<Slot> occupied, out List<Slot> batch)
    {
        batch = new List<Slot>();
        if (!complete || occupied == null) return Selection.Unknown;
        var positions = new HashSet<(int, int)>();
        var identities = new HashSet<WowBGuid>();
        var allowed = new List<Slot>();
        foreach (Slot slot in occupied)
        {
            if (slot.Candidate < -1 || slot.Candidate > 4 || slot.Index < 0 || slot.Index >= 64 || slot.Item.IsEmpty ||
                !positions.Add((slot.Candidate, slot.Index)) || !identities.Add(slot.Item)) return Selection.Unknown;
            if (slot.Candidate >= 0 && slot.Candidate <= 4) allowed.Add(slot);
        }
        allowed.Sort((a, b) => a.Candidate != b.Candidate ? a.Candidate.CompareTo(b.Candidate) : a.Index.CompareTo(b.Index));
        for (int i = 0; i < Math.Min(BatchLimit, allowed.Count); i++) batch.Add(allowed[i]);
        return batch.Count == 0 ? Selection.Empty : Selection.Batch;
    }

    // 'attachmentsClear' and acknowledgement must be observed, not inferred
    // from elapsed time, a key, an empty bag, or a screen coordinate. A rejected
    // send is retryable only when the full pre-send batch is back in the SAME
    // candidate slots, with no outstanding attachments. Unknown is never a
    // retry and never counts toward three confirmed failures.
    internal static Decision AfterSend(SendEvidence evidence, bool complete, bool attachmentsClear,
        IReadOnlyList<Slot> batch, IReadOnlyList<Slot> occupied, int confirmedFailures)
    {
        if (!complete || !attachmentsClear || batch == null || occupied == null || batch.Count < 1 || batch.Count > BatchLimit || confirmedFailures < 0)
            return Decision.Hold;
        if (Plan(true, occupied, out _) == Selection.Unknown) return Decision.Hold;
        var itemPositions = new Dictionary<WowBGuid, (int, int)>();
        foreach (Slot slot in occupied) itemPositions.Add(slot.Item, (slot.Candidate, slot.Index));
        var seen = new HashSet<WowBGuid>();
        var batchPositions = new HashSet<(int, int)>();
        foreach (Slot slot in batch)
        {
            if (slot.Candidate < 0 || slot.Candidate > 4 || slot.Index < 0 || slot.Index >= 64 || slot.Item.IsEmpty ||
                !seen.Add(slot.Item) || !batchPositions.Add((slot.Candidate, slot.Index)))
                return Decision.Hold;
            bool present = itemPositions.TryGetValue(slot.Item, out var position);
            if (evidence == SendEvidence.Accepted && present) return Decision.Hold;
            if (evidence == SendEvidence.Rejected && (!present || position != (slot.Candidate, slot.Index))) return Decision.Hold;
        }
        if (evidence == SendEvidence.Accepted) return Decision.Progress;
        if (evidence == SendEvidence.Rejected) return confirmedFailures >= FailureLimit - 1 ? Decision.ReturnToProfile : Decision.Retry;
        return Decision.Hold;
    }
}

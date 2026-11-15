using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Pure coordination model, not connected to gameplay and not an observer.
// Callers must independently verify each supplied UI/inventory observation;
// elapsed time or a posted input is never considered an acknowledgement.
internal sealed class WowBMailSequence
{
    internal enum Stage { Tab, AwaitTab, Delay, Recipient, AwaitRecipient, Batch, Attach, AwaitAttachment, Send, AwaitSend, Return, Hold }
    internal Stage Current { get; private set; } = Stage.Tab;
    internal int Failures { get; private set; }
    private long _tabPostedAt;
    private List<WowBMailBatchPolicy.Slot> _batch = new();
    private int _attached;

    // Caller checks STOP, alive, combat, map, attachment and focus immediately
    // before every input. A stopped or uncertain transaction cannot be resumed.
    internal void Interrupt() => Current = Stage.Hold;
    internal bool TabPosted(long now)
    {
        if (Current != Stage.Tab || now < 0) return false;
        _tabPostedAt = now; Current = Stage.AwaitTab; return true;
    }
    internal void TabObserved(bool open)
    {
        if (Current != Stage.AwaitTab) { Current = Stage.Hold; return; }
        Current = open ? Stage.Delay : Stage.Hold;
    }
    internal bool RecipientDue(long now)
    {
        if (Current != Stage.Delay || now < _tabPostedAt || now - _tabPostedAt < 1000) return false;
        Current = Stage.Recipient; return true;
    }
    internal bool RecipientPosted()
    {
        if (Current != Stage.Recipient) return false;
        Current = Stage.AwaitRecipient; return true;
    }
    internal void RecipientObserved(bool exact)
    {
        if (Current != Stage.AwaitRecipient) { Current = Stage.Hold; return; }
        Current = exact ? Stage.Batch : Stage.Hold;
    }
    internal void BeginBatch(bool complete, IReadOnlyList<WowBMailBatchPolicy.Slot> occupied)
    {
        if (Current != Stage.Batch) { Current = Stage.Hold; return; }
        var selection = WowBMailBatchPolicy.Plan(complete, occupied, out var batch);
        if (selection == WowBMailBatchPolicy.Selection.Unknown) { Current = Stage.Hold; return; }
        if (selection == WowBMailBatchPolicy.Selection.Empty) { Current = Stage.Return; return; }
        _batch = batch; _attached = 0; Current = Stage.Attach;
    }
    internal bool NextAttachment(out WowBMailBatchPolicy.Slot slot)
    {
        slot = default;
        if (Current != Stage.Attach || _attached >= _batch.Count) return false;
        slot = _batch[_attached]; return true;
    }
    internal bool AttachmentPosted(WowBMailBatchPolicy.Slot slot)
    {
        if (!NextAttachment(out var expected) || slot.Candidate != expected.Candidate || slot.Index != expected.Index || slot.Item != expected.Item) return false;
        Current = Stage.AwaitAttachment; return true;
    }
    // The independent observer must provide the full current attachment list,
    // in batch order, not simply a count or a timer-based guess.
    internal void AttachmentObserved(IReadOnlyList<WowBGuid>? attachments)
    {
        if (Current != Stage.AwaitAttachment || attachments == null || attachments.Count != _attached + 1)
        { Current = Stage.Hold; return; }
        for (int i = 0; i < attachments.Count; i++)
            if (attachments[i] != _batch[i].Item) { Current = Stage.Hold; return; }
        _attached++;
        Current = _attached == _batch.Count ? Stage.Send : Stage.Attach;
    }
    internal bool SendPosted()
    {
        if (Current != Stage.Send || _batch.Count == 0 || _batch.Count > WowBMailBatchPolicy.BatchLimit || _attached != _batch.Count) return false;
        Current = Stage.AwaitSend; return true;
    }
    internal void SendObserved(WowBMailBatchPolicy.SendEvidence evidence, bool complete, bool attachmentsClear,
        IReadOnlyList<WowBMailBatchPolicy.Slot> occupied)
    {
        if (Current != Stage.AwaitSend) { Current = Stage.Hold; return; }
        var result = WowBMailBatchPolicy.AfterSend(evidence, complete, attachmentsClear, _batch, occupied, Failures);
        if (result == WowBMailBatchPolicy.Decision.Progress)
        { Failures = 0; _batch.Clear(); Current = Stage.Batch; }
        else if (result == WowBMailBatchPolicy.Decision.Retry)
        { Failures++; _batch.Clear(); Current = Stage.Batch; }
        else if (result == WowBMailBatchPolicy.Decision.ReturnToProfile)
        { Failures++; _batch.Clear(); Current = Stage.Return; }
        else Current = Stage.Hold;
    }
}

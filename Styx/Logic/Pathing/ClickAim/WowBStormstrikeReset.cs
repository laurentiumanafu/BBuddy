using System;
using System.Collections.Generic;

namespace Styx.Logic.Pathing;

// Narrow experimental 70938 transition, not a general cooldown or proc API.
// A missing record, unknown sample, stale baseline or different node never
// grants an early attempt. The caller retains its existing 8-second estimate.
internal sealed class WowBStormstrikeReset
{
    internal readonly struct Node
    {
        internal readonly int State;
        internal readonly ulong Address;
        private readonly uint[] words; // +0x10..+0x40
        internal Node(int state, ulong address, byte[] bytes)
        {
            State = state; Address = address;
            words = new uint[13];
            for (int i = 0; i < words.Length; i++) words[i] = BitConverter.ToUInt32(bytes, 0x10 + i * 4);
        }
        internal uint Start => words[3];
        internal uint Duration => words[4];
        internal bool SpellSpecific => words[0] == 17364 && words[1] == 0 && words[2] == 0x3F800000 &&
            Start != 0 && words[5] == 0 && words[6] == Start && words[7] == 0 && words[8] == 0 &&
            words[9] == Start && words[10] == 0 && words[11] == 0 && words[12] == 0;
        internal bool SameExceptDuration(Node other)
        {
            if (State != other.State || Address != other.Address) return false;
            for (int i = 0; i < words.Length; i++)
                if (i != 4 && words[i] != other.words[i]) return false;
            return true;
        }
    }

    private const int WindowMs = 8000, MinClearSpacingMs = 100;
    private readonly HashSet<uint> previousStarts = new HashSet<uint>();
    private int attemptedAt, firstClearAt, clears;
    private Node active;
    private bool seen;
    internal bool Armed { get; private set; }
    internal bool ResetCandidate { get; private set; }

    internal void Begin(IReadOnlyList<Node>? baseline, int now)
    {
        previousStarts.Clear(); seen = false; clears = 0; ResetCandidate = false; Armed = false;
        if (baseline == null) return;
        foreach (Node node in baseline)
        {
            if (node.Start != 0) previousStarts.Add(node.Start);
            // Existing spell-specific duration can be stale; do not attribute it
            // to the new key attempt even when the estimate has expired.
            if (node.Duration != 0) return;
        }
        attemptedAt = now;
        Armed = true;
    }

    internal void Invalidate() { Armed = false; ResetCandidate = false; }

    internal void Observe(IReadOnlyList<Node>? nodes, int now)
    {
        if (!Armed) return;
        int elapsed = unchecked(now - attemptedAt);
        if (nodes == null || elapsed < 0 || elapsed >= WindowMs) { Invalidate(); return; }
        if (!seen)
        {
            int matches = 0;
            foreach (Node node in nodes)
            {
                if (node.Duration == 0) continue;
                if (!node.SpellSpecific || node.Duration != 8000 || previousStarts.Contains(node.Start))
                { Invalidate(); return; }
                active = node; matches++;
            }
            if (matches > 1) { Invalidate(); return; }
            if (matches == 1) seen = true;
            return;
        }
        int same = 0;
        Node current = default;
        foreach (Node node in nodes)
        {
            if (node.SameExceptDuration(active)) { same++; current = node; }
            else if (node.Duration != 0) { Invalidate(); return; }
        }
        if (same != 1 || (current.Duration != 8000 && current.Duration != 0)) { Invalidate(); return; }
        if (current.Duration == 8000)
        {
            if (clears != 0) Invalidate();
            return;
        }
        if (clears == 0) { clears = 1; firstClearAt = now; return; }
        if (unchecked(now - firstClearAt) >= MinClearSpacingMs) ResetCandidate = true;
    }
}

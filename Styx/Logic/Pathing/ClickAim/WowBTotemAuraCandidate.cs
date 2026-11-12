using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70862/70888; effect mapping observed only on 70862. This observes effects on the player, not totem
// existence. The mappings are limited to the ranks suggested by the user's
// single-totem/range traces and published spell tables, not all totem ranks.
internal sealed class WowBTotemAuraCandidate
{
    private const int ActiveCountOffset = 0x1A60, IdWithinSlot = 0xE8;
    private readonly HashSet<uint> _ids;
    private WowBTotemAuraCandidate(HashSet<uint> ids) { _ids = ids; }

    internal static int EffectForCast(int castId) => castId == 8160 ? 8162 : castId == 5394 ? 5672 : castId == 5675 ? 5677 : 0;
    // Rank 3 was observed here; other rank IDs are inferred from spell tables.
    // Presence is experimental, not proof of charges or cast success.
    internal bool LightningShieldActive()
    {
        foreach (uint id in _ids) if (id <= int.MaxValue && WowBLightningShield.Rank((int)id) != 0) return true;
        return false;
    }

    internal bool? Active(int castId)
    {
        int effect = EffectForCast(castId);
        return effect == 0 ? (bool?)null : _ids.Contains((uint)effect);
    }

    // Macro contents/rank are opaque. Treat any known Mana Spring candidate
    // as present, rather than repeatedly replacing a higher rank. Only 5677
    // has been observed in the user's traces; the others are Classic IDs and
    // their 70888 representation remains unverified.
    internal bool ManaSpringActive() => _ids.Contains(5677) || _ids.Contains(10491) ||
        _ids.Contains(10493) || _ids.Contains(10494);

    // Validate completeness using the separately observed player count. A bad
    // count, duplicate, changed buffer or unsupported header is unknown, not absent.
    internal static WowBTotemAuraCandidate? FromBytes(byte[]? bytes, uint count)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length % WowBCombatResearch.ExternalCandidateStride != 0 ||
            bytes.Length > WowBCombatResearch.ExternalMaxSlots * WowBCombatResearch.ExternalCandidateStride || count == 0) return null;
        var ids = new HashSet<uint>();
        for (int i = 0; i < bytes.Length / WowBCombatResearch.ExternalCandidateStride; i++)
        {
            uint id = BitConverter.ToUInt32(bytes, i * WowBCombatResearch.ExternalCandidateStride + IdWithinSlot);
            if (id != 0 && !ids.Add(id)) return null;
        }
        return ids.Count == count ? new WowBTotemAuraCandidate(ids) : null;
    }

    internal static WowBTotemAuraCandidate? Capture(Memory memory, WowBReader reader, WowBObservedUnit player)
        => Capture(memory, reader, player, out _);

    internal static WowBTotemAuraCandidate? Capture(Memory memory, WowBReader reader, WowBObservedUnit player, out string reason)
    {
        reason = "unsupported reader or player identity";
        if (!reader.Uses70862Features || player.Guid.IsEmpty || player.UnitAddress < 0x10000 ||
            player.UnitAddress > 0x00007FFFFFFFFFFFUL - (ulong)(ActiveCountOffset + 4)) return null;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            // Inline slots use the same stride and +0xE8 ID position as the
            // grown array. Accept only the existing strictly validated inline
            // snapshot and a nonzero count, just as for the external layout.
            WowBInlineAuraCandidate? inline = WowBInlineAuraCandidate.Capture(memory, player, out string inlineReason);
            if (inline != null)
            {
                var ids = new HashSet<uint>();
                foreach (uint id in inline.CopyIds()) if (id != 0) ids.Add(id);
                if (ids.Count != 0) { reason = "inline candidate"; return new WowBTotemAuraCandidate(ids); }
                inlineReason = "inline candidate empty";
            }
            byte[]? before = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            WowBCombatResearch.ExternalCandidateSample? sample = WowBCombatResearch.ReadExternalCandidate(memory, player.UnitAddress, out string externalReason);
            byte[]? after = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            reason = inlineReason + "; external " + (sample == null ? externalReason : "count/slot validation failed");
            if (before == null || after == null || before.Length != 4 || after.Length != 4 || sample == null) return null;
            uint count = BitConverter.ToUInt32(before, 0);
            if (count != BitConverter.ToUInt32(after, 0)) return null;
            WowBTotemAuraCandidate? result = FromBytes(sample.Bytes, count);
            if (result != null) reason = "external candidate";
            return result;
        }
        catch { reason = "aura candidate read failed"; return null; }
    }
}

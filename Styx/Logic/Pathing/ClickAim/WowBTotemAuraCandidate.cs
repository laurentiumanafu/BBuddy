using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70862 only. This observes effects on the player, not totem
// existence. The mappings are limited to the ranks suggested by the user's
// single-totem/range traces and published spell tables, not all totem ranks.
internal sealed class WowBTotemAuraCandidate
{
    private const int ActiveCountOffset = 0x1A60, IdWithinSlot = 0xE8;
    private readonly HashSet<uint> _ids;
    private WowBTotemAuraCandidate(HashSet<uint> ids) { _ids = ids; }

    internal static int EffectForCast(int castId) => castId == 8160 ? 8162 : castId == 5394 ? 5672 : castId == 5675 ? 5677 : 0;
    // 70862 rank 3 was observed appearing in this same candidate array.
    // Presence/absence is experimental, not proof of charges or cast success.
    internal bool LightningShield905Active() => _ids.Contains(905);

    internal bool? Active(int castId)
    {
        int effect = EffectForCast(castId);
        return effect == 0 ? (bool?)null : _ids.Contains((uint)effect);
    }

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
    {
        if (!reader.IsExperimental70862 || player.Guid.IsEmpty || player.UnitAddress < 0x10000 ||
            player.UnitAddress > 0x00007FFFFFFFFFFFUL - (ulong)(ActiveCountOffset + 4)) return null;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? before = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            WowBCombatResearch.ExternalCandidateSample? sample = WowBCombatResearch.ReadExternalCandidate(memory, player.UnitAddress, out _);
            byte[]? after = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            if (before == null || after == null || before.Length != 4 || after.Length != 4 || sample == null) return null;
            uint count = BitConverter.ToUInt32(before, 0);
            if (count != BitConverter.ToUInt32(after, 0)) return null;
            return FromBytes(sample.Bytes, count);
        }
        catch { return null; }
    }
}

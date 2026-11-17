using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Experimental 70862/70888/70938; effect mapping observed only on 70862. This observes effects on the player, not totem
// existence. The mappings are limited to the ranks suggested by the user's
// single-totem/range traces and published spell tables, not all totem ranks.
internal sealed class WowBTotemAuraCandidate
{
    private const int ActiveCountOffset = 0x1A60, IdWithinSlot = 0xE8;
    // Windfury Totem aura ranks on the player. Only 10609 (rank 2) appeared in
    // a captured 70938 trace labelled "windfury totem effect" (33-slot external
    // candidate, pointer 0x176927C5F50); 8515 and 10610 are the other published
    // WoW Forever aura ranks and are inferred, not observed.
    private const uint WindfuryRank1 = 8515, WindfuryRank2 = 10609, WindfuryRank3 = 10610;
    private readonly HashSet<uint> _ids;
    private WowBTotemAuraCandidate(HashSet<uint> ids) { _ids = ids; }

    // Windfury Totem cast ranks (Summon), separate from the aura they leave.
    internal static bool IsWindfuryCast(int castId) => castId == 8512 || castId == 10613 || castId == 10614;
    internal static int EffectForCast(int castId) => castId == 8160 ? 8162 : castId == 5394 ? 5672 :
        castId == 5675 ? 5677 : IsWindfuryCast(castId) ? (int)WindfuryRank2 : 0;
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
        if (effect == 0) return (bool?)null;
        // Any known Windfury aura rank counts as present, so an unknown macro
        // rank or a later rank-up is not treated as a missing effect.
        return IsWindfuryCast(castId) ? WindfuryActive() : _ids.Contains((uint)effect);
    }

    // The validated slot IDs, for checks this class does not map itself. The
    // inline and grown layouts share the same ID position inside a slot.
    internal HashSet<uint> CopyIds() => new HashSet<uint>(_ids);

    // Macro contents/rank are opaque. Treat any known Mana Spring candidate
    // as present, rather than repeatedly replacing a higher rank. Only 5677
    // has been observed in the user's traces; the others are Classic IDs and
    // their 70888/70938 representation remains unverified.
    // ID 1238640 populated in the user's 70938 Ghost Wolf trace; absence,
    // layout completeness and other builds remain experimental.
    internal bool GhostWolf70938Active() => _ids.Contains(1238640);

    internal bool ManaSpringActive() => _ids.Contains(5677) || _ids.Contains(10491) ||
        _ids.Contains(10493) || _ids.Contains(10494);

    // The macro's Windfury rank is opaque, so treat every mapped aura rank as
    // the effect being present rather than repeatedly replacing a higher rank.
    internal bool WindfuryActive() => _ids.Contains(WindfuryRank1) || _ids.Contains(WindfuryRank2) ||
        _ids.Contains(WindfuryRank3);

    // Validate completeness using the separately observed player count. A bad
    // count, duplicate, changed buffer or unsupported header is unknown, not absent.
    internal static WowBTotemAuraCandidate? FromBytes(byte[]? bytes, uint count, bool allow70888Extended = false, bool allow70938Extended = false)
    {
        int maxSlots = allow70938Extended ? WowBCombatResearch.External70938MaxSlots :
            allow70888Extended ? WowBCombatResearch.External70888MaxSlots : WowBCombatResearch.ExternalMaxSlots;
        if (bytes == null || bytes.Length == 0 || bytes.Length % WowBCombatResearch.ExternalCandidateStride != 0 ||
            bytes.Length > maxSlots * WowBCombatResearch.ExternalCandidateStride || count == 0) return null;
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
        => CaptureCore(memory, reader, player, out reason);

    /* Ghost Wolf is a form: a wrong "absent" verdict makes the bot press the key and
     * cancel the form it is already in, so this path used to carry its own tighter
     * slot bound. That bound was a hard-coded 29 slots, and the container grows as
     * auras accumulate - 28, 29, 30, 31, 32, 33 in successive logs - so the bound
     * silently expired and left Ghost Wolf permanently "unavailable" while the very
     * same read, seconds earlier, gave the combat-buff path a verdict. An expired
     * bound is not a safety check, it is a feature switch nobody can see, so Ghost
     * Wolf now uses the same bounds as every other aura read - the container's own
     * capacity, itself capped - and relies on what actually protects the form: two
     * reads that must agree, and a separate active count that must match the unique
     * nonzero ids. The reason is surfaced, so an unreadable candidate says why. */
    internal static WowBTotemAuraCandidate? CaptureForGhostWolf(Memory memory, WowBReader reader, WowBObservedUnit player)
        => CaptureCore(memory, reader, player, out _);

    internal static WowBTotemAuraCandidate? CaptureForGhostWolf(Memory memory, WowBReader reader, WowBObservedUnit player, out string reason)
        => CaptureCore(memory, reader, player, out reason);

    private static WowBTotemAuraCandidate? CaptureCore(Memory memory, WowBReader reader, WowBObservedUnit player,
        out string reason)
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
            WowBCombatResearch.ExternalCandidateSample? sample = WowBCombatResearch.ReadExternalCandidate(memory, player.UnitAddress, out string externalReason, reader.Uses70888Features, reader.Uses70938Features);
            byte[]? after = memory.ReadBytes64(player.UnitAddress + ActiveCountOffset, 4);
            reason = inlineReason + "; external " + (sample == null ? externalReason : "count/slot validation failed");
            if (before == null || after == null || before.Length != 4 || after.Length != 4 || sample == null) return null;
            uint count = BitConverter.ToUInt32(before, 0);
            if (count != BitConverter.ToUInt32(after, 0)) { reason = "external active count changed during read"; return null; }
            WowBTotemAuraCandidate? result = FromBytes(sample.Bytes, count, reader.Uses70888Features, reader.Uses70938Features);
            if (result != null) reason = "external candidate";
            else reason = "external candidate " + (sample.Bytes.Length / WowBCombatResearch.ExternalCandidateStride) +
                " slots, count " + count + "; empty, duplicate or count/slot mismatch";
            return result;
        }
        catch { reason = "aura candidate read failed"; return null; }
    }
}

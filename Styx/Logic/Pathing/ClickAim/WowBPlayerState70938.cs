using System;
using System.Globalization;
using System.Text;
using Styx.Offsets;

namespace Styx.Logic.Pathing;

// Read-only interpretation of user-supplied leads in CombatResearch's already
// identity-checked player window. The pool-derived base has NOT been proven to
// equal the supplied getter's return. Do not use these values for decisions.
internal static class WowBPlayerState70938
{
    private const int ChunkSize = 0x100;

    private static byte[]? Field(byte[]?[] chunks, int offset, int width)
    {
        if (chunks == null || offset < 0 || width <= 0 || offset / ChunkSize >= chunks.Length ||
            offset % ChunkSize + width > ChunkSize) return null;
        byte[]? chunk = chunks[offset / ChunkSize];
        if (chunk == null || chunk.Length != ChunkSize) return null;
        var result = new byte[width];
        Buffer.BlockCopy(chunk, offset % ChunkSize, result, 0, width);
        return result;
    }

    private static string Hex(byte[]? bytes)
    {
        if (bytes == null) return "unreadable";
        ulong value = bytes.Length switch
        {
            1 => bytes[0], 4 => BitConverter.ToUInt32(bytes, 0),
            8 => BitConverter.ToUInt64(bytes, 0), _ => throw new ArgumentException("width")
        };
        return "0x" + value.ToString("X" + (bytes.Length * 2), CultureInfo.InvariantCulture);
    }

    internal static string Format(byte[]?[] chunks, byte[]? healthCheck)
    {
        byte[]? attack = Field(chunks, Wow70938Offsets.AutoAttackCandidate, 1);
        byte[]? current = Field(chunks, Wow70938Offsets.CurrentTargetCandidate, 8);
        byte[]? alternate = Field(chunks, Wow70938Offsets.AttackTargetCandidate, 8);
        byte[]? flags = Field(chunks, Wow70938Offsets.ShapeshiftFlagsCandidate, 4);
        byte[]? form = Field(chunks, Wow70938Offsets.ShapeshiftCandidate, 1);
        byte[]? combo = Field(chunks, Wow70938Offsets.ComboPointsCandidate, 1);
        byte[]? state = Field(chunks, Wow70938Offsets.AttackStateFlagsCandidate, 4);
        byte[]? effect = Field(chunks, Wow70938Offsets.AttackEffectCandidate, 8);
        byte[]? hp = Field(chunks, (int)Wow70707Offsets.HealthCandidateOffset, 8);
        byte[]? max = Field(chunks, (int)Wow70707Offsets.MaxHealthCandidateOffset, 8);
        string gate = flags == null ? "unreadable" :
            ((BitConverter.ToUInt32(flags, 0) & Wow70938Offsets.ShapeshiftFlagCandidate) != 0 ? "set" : "clear");
        string signedCombo = combo == null ? "unreadable" : unchecked((sbyte)combo[0]).ToString(CultureInfo.InvariantCulture);
        string health = "unreadable";
        if (hp != null && max != null && healthCheck != null && healthCheck.Length == 16)
        {
            ulong hpValue = BitConverter.ToUInt64(hp, 0), maxValue = BitConverter.ToUInt64(max, 0);
            bool sane = maxValue > 0 && maxValue <= uint.MaxValue && hpValue <= maxValue;
            bool same = hpValue == BitConverter.ToUInt64(healthCheck, 0) && maxValue == BitConverter.ToUInt64(healthCheck, 8);
            health = "hp=" + hpValue.ToString(CultureInfo.InvariantCulture) + "/" + maxValue.ToString(CultureInfo.InvariantCulture) +
                ", plausible=" + sane + ", rereadMatch=" + same;
        }
        var text = new StringBuilder("70938 supplied-offset candidates (diagnostic only): ");
        text.Append("attackByte=").Append(Hex(attack)).Append(" currentTargetQword=").Append(Hex(current));
        text.Append(" alternateAttackTargetQword=").Append(Hex(alternate));
        text.Append(" formByte=").Append(Hex(form)).Append(" formFlags=").Append(Hex(flags)).Append(" formGate=").Append(gate);
        text.Append(" comboByte=").Append(Hex(combo)).Append(" comboSigned=").Append(signedCombo);
        text.Append(" attackFlags=").Append(Hex(state)).Append(" attackEffectPointer=").Append(Hex(effect));
        text.Append("; pool-base health check: ").Append(health);
        return text.ToString();
    }
}

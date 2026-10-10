using System;

namespace Styx.WoWInternals
{
    /// <summary>
    /// 128-bit GUID used by the modern Classic engine (WowB.exe).
    ///
    /// Unlike the 3.3.5a client where a GUID is a single 64-bit value,
    /// the modern engine stores GUIDs as two qwords. The low qword carries
    /// the legacy identity bits (entry/spawn id in the low 32 bits, type
    /// and flags above); the high qword carries server/creation bits.
    /// Identity comparisons must always use the full 128 bits.
    /// </summary>
    public readonly struct WowBGuid : IEquatable<WowBGuid>
    {
        public readonly ulong Low;
        public readonly ulong High;

        public WowBGuid(ulong low, ulong high)
        {
            Low = low;
            High = high;
        }

        public bool IsEmpty => Low == 0UL && High == 0UL;

        /// <summary>The classic-era identity bits (entry id for NPCs etc.).</summary>
        public uint EntryId => (uint)(Low & 0xFFFFFFUL);

        public bool Equals(WowBGuid other) => Low == other.Low && High == other.High;
        public override bool Equals(object? obj) => obj is WowBGuid other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Low, High);
        public static bool operator ==(WowBGuid left, WowBGuid right) => left.Equals(right);
        public static bool operator !=(WowBGuid left, WowBGuid right) => !left.Equals(right);

        public override string ToString() => $"0x{High:X16}{Low:X16}";
    }
}

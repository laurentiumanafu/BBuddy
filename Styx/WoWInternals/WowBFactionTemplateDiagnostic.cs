using System;
using System.Collections.Generic;

namespace Styx.WoWInternals
{
    /// <summary>
    /// Offline-style comparison of a bounded, observed 70291 primary table snapshot.
    /// NOT live reaction, attackability, aggression, or a gameplay filter. The caller
    /// must validate signatures, vtable, identity and consistency before using it.
    /// </summary>
    internal static class WowBFactionTemplateDiagnostic
    {
        private sealed class Row
        {
            public uint Faction, Flags, Group, Friendly, Hostile;
            public readonly ushort[] Enemies = new ushort[8], Friends = new ushort[8];
        }

        public static bool TryCompare(byte[]? storage, byte[]? fields, byte[]? rows, byte[]? index,
            byte[]? scalarPalette, byte[]? enemyPalette, byte[]? friendPalette,
            uint targetKey, uint playerKey, out int targetToPlayer, out int playerToTarget, out string reason)
        {
            targetToPlayer = 0; playerToTarget = 0; reason = "incomplete table snapshot";
            if (!BitConverter.IsLittleEndian || storage == null || storage.Length != 0x100 || fields == null || fields.Length != 7 * 24 ||
                rows == null || index == null || scalarPalette == null || enemyPalette == null || friendPalette == null)
                return false;
            uint stride = BitConverter.ToUInt32(storage, 0x38);
            uint min = BitConverter.ToUInt32(storage, 0x40), max = BitConverter.ToUInt32(storage, 0x44);
            uint count = BitConverter.ToUInt32(storage, 0x48);
            ulong indexCount = BitConverter.ToUInt64(storage, 0xB8);
            reason = "unsupported table dimensions";
            if (stride != 7 || count == 0 || count > 16384 || min > max || max - min > ushort.MaxValue ||
                rows.Length != (long)stride * count || indexCount == 0 || indexCount > 16384 ||
                (ulong)index.Length != indexCount * 4) return false;

            // Only the observed 70291 descriptor layout is supported; reject a new
            // layout rather than interpreting other compression schemes by guesswork.
            int[] modes = { 1, 5, 1, 3, 1, 4, 4 };
            int[] offsets = { 0, 12, 27, 31, 35, 39, 46 };
            int[] widths = { 12, 15, 4, 4, 4, 7, 8 };
            reason = "unsupported field descriptors or palettes";
            for (int i = 0; i < 7; i++)
            {
                int pos = i * 24;
                if (BitConverter.ToUInt32(fields, pos + 8) != modes[i] ||
                    BitConverter.ToUInt32(fields, pos + 12) != offsets[i] ||
                    BitConverter.ToUInt32(fields, pos + 16) != widths[i] || offsets[i] + widths[i] > stride * 8)
                    return false;
            }
            if (BitConverter.ToUInt32(fields, 3 * 24 + 4) != scalarPalette.Length ||
                scalarPalette.Length == 0 || scalarPalette.Length > 16384 || scalarPalette.Length % 4 != 0 ||
                BitConverter.ToUInt32(fields, 5 * 24 + 4) != enemyPalette.Length ||
                BitConverter.ToUInt32(fields, 6 * 24 + 4) != friendPalette.Length ||
                BitConverter.ToUInt32(fields, 5 * 24 + 20) != 8 || BitConverter.ToUInt32(fields, 6 * 24 + 20) != 8 ||
                enemyPalette.Length == 0 || enemyPalette.Length > 16384 || enemyPalette.Length % 32 != 0 ||
                friendPalette.Length == 0 || friendPalette.Length > 16384 || friendPalette.Length % 32 != 0)
                return false;

            reason = "missing, duplicate, or special/secondary index";
            var lookup = new Dictionary<ushort, ushort>();
            for (int i = 0; i < index.Length; i += 4)
            {
                ushort key = BitConverter.ToUInt16(index, i), row = BitConverter.ToUInt16(index, i + 2);
                if ((ulong)min + key > max || lookup.ContainsKey(key)) return false;
                lookup.Add(key, row);
            }
            if (targetKey < min || targetKey > max || playerKey < min || playerKey > max ||
                !lookup.TryGetValue((ushort)(targetKey - min), out ushort targetRow) ||
                !lookup.TryGetValue((ushort)(playerKey - min), out ushort playerRow) ||
                targetRow >= count || playerRow >= count) return false;
            reason = "palette index out of bounds";
            if (!TryRow(rows, targetRow * 7, scalarPalette, enemyPalette, friendPalette, out Row target) ||
                !TryRow(rows, playerRow * 7, scalarPalette, enemyPalette, friendPalette, out Row player)) return false;
            targetToPlayer = Relation(target, player);
            playerToTarget = Relation(player, target);
            reason = "ok";
            return true;
        }

        private static uint Bits(byte[] bytes, int rowOffset, int start, int width)
        {
            uint value = 0;
            for (int i = 0; i < width; i++)
                value |= (uint)((bytes[rowOffset + (start + i) / 8] >> ((start + i) % 8)) & 1) << i;
            return value;
        }

        private static bool TryRow(byte[] rows, int offset, byte[] scalar, byte[] enemies, byte[] friends, out Row row)
        {
            row = new Row();
            uint scalarIndex = Bits(rows, offset, 31, 4), enemyIndex = Bits(rows, offset, 39, 7), friendIndex = Bits(rows, offset, 46, 8);
            if ((ulong)scalarIndex * 4 + 4 > (ulong)scalar.Length ||
                ((ulong)enemyIndex + 1) * 32 > (ulong)enemies.Length ||
                ((ulong)friendIndex + 1) * 32 > (ulong)friends.Length) return false;
            row.Faction = Bits(rows, offset, 0, 12) & 0xFFFF;
            uint flags = Bits(rows, offset, 12, 15);
            row.Flags = (flags & 0x4000) == 0 ? flags : flags | 0xFFFF8000U;
            row.Group = Bits(rows, offset, 27, 4) & 0xFF;
            row.Friendly = BitConverter.ToUInt32(scalar, (int)scalarIndex * 4) & 0xFF;
            row.Hostile = Bits(rows, offset, 35, 4) & 0xFF;
            for (int i = 0; i < 8; i++)
            {
                row.Enemies[i] = BitConverter.ToUInt16(enemies, (int)enemyIndex * 32 + i * 4);
                row.Friends[i] = BitConverter.ToUInt16(friends, (int)friendIndex * 32 + i * 4);
            }
            return true;
        }

        private static bool ContainsPrefix(ushort[] array, uint id)
        {
            foreach (ushort value in array)
            {
                if (value == 0) break;
                if (value == (ushort)id) return true;
            }
            return false;
        }

        // The numeric branches mirror only the normal template helper RVA 0x27EE3B0.
        private static int Relation(Row a, Row b)
        {
            if ((a.Hostile & b.Group) != 0 || ContainsPrefix(a.Enemies, b.Faction)) return 1;
            if ((a.Friendly & b.Group) != 0 || ContainsPrefix(a.Friends, b.Faction)) return 4;
            if ((a.Group & b.Friendly) != 0 || ContainsPrefix(b.Friends, a.Faction)) return 4;
            return (a.Flags & 0x2000) != 0 ? 1 : 3;
        }
    }
}

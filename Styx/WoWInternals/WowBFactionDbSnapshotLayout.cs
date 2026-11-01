using System;

namespace Styx.WoWInternals
{
    /// <summary>
    /// Bounded diagnostic layout for one observed 70291 FactionTemplate table.
    /// These pointers and bytes are for offline investigation, never gameplay.
    /// The caller must verify build signatures, the storage vtable and identity.
    /// </summary>
    internal readonly struct WowBFactionDbSnapshotLayout
    {
        public readonly ulong Fields, Palettes, Rows, Index;
        public readonly int FieldsLength, PalettesLength, RowsLength, IndexLength;
        private WowBFactionDbSnapshotLayout(ulong fields, int fieldsLength, ulong palettes, int palettesLength,
            ulong rows, int rowsLength, ulong index, int indexLength)
        {
            Fields = fields; FieldsLength = fieldsLength;
            Palettes = palettes; PalettesLength = palettesLength;
            Rows = rows; RowsLength = rowsLength;
            Index = index; IndexLength = indexLength;
        }

        private static bool Region(ulong address, ulong count, ulong stride, ulong maxCount, ulong maxStride, out int length)
        {
            length = 0;
            if (address <= 0x10000UL || count == 0 || count > maxCount || stride == 0 || stride > maxStride)
                return false;
            ulong size = count * stride; // bounded above before multiplication
            if (size > 1024UL * 1024 || address >= 0x00007FFFFFFFFFFFUL - size) return false;
            length = (int)size;
            return true;
        }

        public static bool TryCreate(byte[]? root, byte[]? storage, out WowBFactionDbSnapshotLayout layout)
        {
            layout = default;
            if (root == null || root.Length < 0x168 || storage == null || storage.Length < 0xC0)
                return false;
            ulong fields = BitConverter.ToUInt64(root, 0x10), fieldCount = BitConverter.ToUInt64(root, 0x18);
            ulong palettes = BitConverter.ToUInt64(root, 0x28), paletteCount = BitConverter.ToUInt64(root, 0x30);
            ulong rows = BitConverter.ToUInt64(storage, 8), rowCount = BitConverter.ToUInt16(storage, 0x48);
            ulong rowStride = BitConverter.ToUInt32(storage, 0x38);
            ulong index = BitConverter.ToUInt64(storage, 0xB0), indexCount = BitConverter.ToUInt64(storage, 0xB8);
            if (!Region(fields, fieldCount, 24, 64, 24, out int fieldsLength) ||
                !Region(palettes, paletteCount, 24, 64, 24, out int palettesLength) ||
                !Region(rows, rowCount, rowStride, 16384, 512, out int rowsLength) ||
                !Region(index, indexCount, 4, 16384, 4, out int indexLength)) return false;
            layout = new WowBFactionDbSnapshotLayout(fields, fieldsLength, palettes, palettesLength,
                rows, rowsLength, index, indexLength);
            return true;
        }

        public bool SameRegions(WowBFactionDbSnapshotLayout other)
            => Fields == other.Fields && FieldsLength == other.FieldsLength && Palettes == other.Palettes &&
               PalettesLength == other.PalettesLength && Rows == other.Rows && RowsLength == other.RowsLength &&
               Index == other.Index && IndexLength == other.IndexLength;

        // A palette vector stores count and pointer. Reject suspicious or oversized data.
        public static bool TryPalette(byte[]? descriptors, int index, out ulong address, out int length)
        {
            address = 0; length = 0;
            if (descriptors == null || index < 0 || index >= descriptors.Length / 24) return false;
            int pos = index * 24;
            ulong ptr = BitConverter.ToUInt64(descriptors, pos), count = BitConverter.ToUInt64(descriptors, pos + 8);
            if (!Region(ptr, count, 4, 4096, 4, out length)) return false;
            address = ptr;
            return true;
        }
    }
}

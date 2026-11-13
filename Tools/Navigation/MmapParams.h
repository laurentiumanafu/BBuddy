#pragma once

// Shared, bounded reader for dtNavMeshParams in the native loader and
// mmapcheck. Legacy map files begin directly with the 28-byte Detour struct.
// An observed newer map file starts with MMAP_MAGIC and u32 version 16,
// followed by the same struct; it may have trailing data. Tile headers are a
// separate format and must not be confused with this map-level header.
#include "DetourNavMesh.h"
#include <cmath>
#include <cstdint>
#include <cstdio>

inline bool ReadMmapParams(FILE* file, dtNavMeshParams& params)
{
    static_assert(sizeof(dtNavMeshParams) == 28, "unexpected Detour params layout");
    if (!file || std::fseek(file, 0, SEEK_SET) != 0) return false;
    std::uint32_t prefix[2] = {0, 0};
    if (std::fread(prefix, sizeof(prefix), 1, file) != 1) return false;
    constexpr std::uint32_t magic = 0x4d4d4150u;
    if (prefix[0] == magic)
    {
        // Only version 16 has been observed in this layout. Do not guess the
        // offsets of a future header, or reinterpret it as bare parameters.
        if (prefix[1] != 16u) return false;
    }
    else if (std::fseek(file, 0, SEEK_SET) != 0) return false;
    dtNavMeshParams candidate{};
    if (std::fread(&candidate, sizeof(candidate), 1, file) != 1) return false;
    if (!std::isfinite(candidate.orig[0]) || !std::isfinite(candidate.orig[1]) ||
        !std::isfinite(candidate.orig[2]) || !std::isfinite(candidate.tileWidth) ||
        !std::isfinite(candidate.tileHeight) || candidate.tileWidth <= 0 || candidate.tileHeight <= 0 ||
        candidate.tileWidth > 100000 || candidate.tileHeight > 100000 ||
        candidate.maxTiles <= 0 || candidate.maxTiles > (1 << 20)) return false;
#ifndef DT_POLYREF64
    // 64-bit Detour ignores maxPolys when constructing the reference layout;
    // the captured v16 file contains 0x80000000 there. 32-bit Detour uses it.
    if (candidate.maxPolys <= 0) return false;
#endif
    params = candidate;
    return true;
}

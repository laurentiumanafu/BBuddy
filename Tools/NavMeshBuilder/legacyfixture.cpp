/*
 * legacyfixture - write one Detour tile in the MaNGOS / TrinityCore / AzerothCore
 * single-tile container, so the legacy loading path can be tested against a real
 * mesh instead of a hand-counted header.
 *
 * Stock 3.3.5a .mmtile files, and the rebuilds the servers ship, are:
 *
 *   MmapTileHeader { uint magic 'MMAP'; uint dtVersion; uint mmapVersion;
 *                    uint size; }            <- 16 bytes
 *   ... newer writers append uint usesLiquids   <- 20 bytes
 *   byte blob[size]                            <- dtCreateNavMeshData output
 *
 * One file per grid tile, one blob, at 533.33 yd tiles - not this build's 4x4
 * sub-tile container. The mesh here is a flat 533.33 yd square with a wall across
 * the middle and (by default) one doorway, so a route from one side to the other
 * has to bend through the gap: if the loader silently accepted a broken blob the
 * straight-line distance would come back instead.
 *
 *   legacyfixture --out <dir> [--tile 32 32] [--header 16|20] [--closed] [--cells 8]
 *                 [--door <cell row>]
 *
 * Writes <dir>/001.mmap (raw dtNavMeshParams, exactly what MapData::loadMap reads)
 * and <dir>/001<xx><yy>.mmtile.
 */
#include "DetourNavMesh.h"
#include "DetourNavMeshBuilder.h"
#include "DetourCommon.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    const unsigned int MMAP_MAGIC = 0x4d4d4150;
    const float TILE_SIZE = 533.33333f;
    const float GRID_ORIGIN = 32.0f * TILE_SIZE;
    const float CELL_QUANT = 0.1f;

    float quantise(float v) { return std::floor(v / CELL_QUANT + 0.5f) * CELL_QUANT; }

    /* Flat square, one quad per cell, walls as missing cells. Everything is built
     * straight in Detour's own frame (x, y up, z) - the swizzle from WoW
     * (x, y, z) = Detour (z, x, y) is the query side's job, as it is for
     * mmapbuild. */
    bool buildBlob(int cells, bool closed, int doorRow, int tileX, int tileY,
                   std::vector<unsigned char>& blob, int* polyCount)
    {
        const float cell = TILE_SIZE / (float)cells;
        const int stride = cells + 1;

        /* The geometry has to sit where the tile it claims to be actually is:
         * dtNavMesh places a tile by the params grid, but the vertices are relative
         * to bmin inside the blob, so a tile at (30,30) that stored vertices at
         * 0..533 would answer queries 500 yd away. Grid 32 happens to start at 0. */
        const float originX = -GRID_ORIGIN + (float)tileX * TILE_SIZE;
        const float originZ = -GRID_ORIGIN + (float)tileY * TILE_SIZE;

        std::vector<float> verts;
        std::vector<int> cornerIndex(stride * stride, -1);

        // Cell (ci, cj) covers x in [ci*cell, (ci+1)*cell], z likewise. The wall is
        // the column ci == cells/2, minus the doorway cell (cj == cells/2 - 1).
        const int wallCol = cells / 2;
        auto walkable = [&](int ci, int cj) -> bool
        {
            if (ci < 0 || cj < 0 || ci >= cells || cj >= cells) return false;
            if (ci != wallCol) return true;
            return !closed && cj == doorRow;
        };

        auto corner = [&](int ci, int cj) -> int
        {
            const int key = cj * stride + ci;
            if (cornerIndex[key] >= 0) return cornerIndex[key];

            const int index = (int)verts.size() / 3;
            verts.push_back(quantise(originX + (float)ci * cell));
            verts.push_back(0.0f);
            verts.push_back(quantise(originZ + (float)cj * cell));
            cornerIndex[key] = index;
            return index;
        };

        std::vector<unsigned short> polys, polyFlags;
        std::vector<unsigned char> polyAreas;
        std::vector<int> cellPoly(cells * cells, -1);

        for (int cj = 0; cj < cells; cj++)
        {
            for (int ci = 0; ci < cells; ci++)
            {
                if (!walkable(ci, cj)) continue;
                (void)corner(ci, cj); (void)corner(ci + 1, cj);
                (void)corner(ci, cj + 1); (void)corner(ci + 1, cj + 1);
                cellPoly[cj * cells + ci] = (int)polyFlags.size();
                polyFlags.push_back(1);
                polyAreas.push_back(0);
            }
        }
        if (polyFlags.empty()) return false;

        for (int cj = 0; cj < cells; cj++)
        {
            for (int ci = 0; ci < cells; ci++)
            {
                if (cellPoly[cj * cells + ci] < 0) continue;

                // Same corner order and edge table as mmapbuild: edge 0 faces
                // Detour x-, edge 1 z+, edge 2 x+, edge 3 z-.
                const int v[4] = {
                    cornerIndex[cj * stride + ci],
                    cornerIndex[(cj + 1) * stride + ci],
                    cornerIndex[(cj + 1) * stride + ci + 1],
                    cornerIndex[cj * stride + ci + 1]
                };
                for (int i = 0; i < 4; i++) polys.push_back((unsigned short)v[i]);

                const int nbCi[4] = { ci - 1, ci, ci + 1, ci };
                const int nbCj[4] = { cj, cj + 1, cj, cj - 1 };
                for (int i = 0; i < 4; i++)
                {
                    const bool outside = nbCi[i] < 0 || nbCi[i] >= cells ||
                                         nbCj[i] < 0 || nbCj[i] >= cells;
                    const int p = walkable(nbCi[i], nbCj[i]) ? cellPoly[nbCj[i] * cells + nbCi[i]] : -1;
                    if (p >= 0)
                        polys.push_back((unsigned short)p);
                    else if (outside)
                        polys.push_back((unsigned short)(0x8000 | i));   // portal to the neighbouring tile
                    else
                        polys.push_back((unsigned short)0xffff);         // wall
                }
            }
        }

        float bmin[3] = { 1e30f, 1e30f, 1e30f };
        float bmax[3] = { -1e30f, -1e30f, -1e30f };
        for (size_t i = 0; i < verts.size(); i += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                bmin[k] = (verts[i + k] < bmin[k]) ? verts[i + k] : bmin[k];
                bmax[k] = (verts[i + k] > bmax[k]) ? verts[i + k] : bmax[k];
            }
        }
        bmin[0] -= 1.0f; bmin[1] -= 1.0f; bmin[2] -= 1.0f;
        bmax[0] += 1.0f; bmax[1] += 1.0f; bmax[2] += 1.0f;

        std::vector<unsigned short> packed(verts.size());
        for (size_t i = 0; i < verts.size(); i++)
        {
            int q = (int)std::lround((verts[i] - bmin[i % 3]) / CELL_QUANT);
            if (q < 0) q = 0;
            if (q > 65535) q = 65535;
            packed[i] = (unsigned short)q;
        }

        dtNavMeshCreateParams params;
        memset(&params, 0, sizeof(params));
        params.verts = packed.data();
        params.vertCount = (int)(packed.size() / 3);
        params.polys = polys.data();
        params.polyFlags = polyFlags.data();
        params.polyAreas = polyAreas.data();
        params.polyCount = (int)polyFlags.size();
        params.nvp = 4;
        params.cs = CELL_QUANT;
        params.ch = CELL_QUANT;
        params.bmin[0] = bmin[0]; params.bmin[1] = bmin[1]; params.bmin[2] = bmin[2];
        params.bmax[0] = bmax[0]; params.bmax[1] = bmax[1]; params.bmax[2] = bmax[2];
        params.walkableHeight = 2.0f;
        params.walkableRadius = 0.5f;
        params.walkableClimb = 1.0f;
        params.tileX = tileX;
        params.tileY = tileY;
        params.tileLayer = 0;
        params.userId = (unsigned int)(tileX * 100 + tileY);

        unsigned char* data = nullptr;
        int dataSize = 0;
        if (!dtCreateNavMeshData(&params, &data, &dataSize))
        {
            fprintf(stderr, "legacyfixture: dtCreateNavMeshData failed\n");
            return false;
        }
        blob.assign(data, data + dataSize);
        dtFree(data);
        *polyCount = (int)polyFlags.size();
        return true;
    }
}

int main(int argc, char** argv)
{
    std::string outDir;
    int headerBytes = 16;
    int tileX = 32, tileY = 32, cells = 8;
    int doorRow = -1;
    bool closed = false;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--out") && i + 1 < argc) outDir = argv[++i];
        else if (!strcmp(argv[i], "--header") && i + 1 < argc) headerBytes = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--tile") && i + 2 < argc) { tileX = atoi(argv[++i]); tileY = atoi(argv[++i]); }
        else if (!strcmp(argv[i], "--cells") && i + 1 < argc) cells = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--door") && i + 1 < argc) doorRow = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--closed")) closed = true;
    }
    if (outDir.empty() || (headerBytes != 16 && headerBytes != 20) || cells < 4)
    {
        fprintf(stderr, "usage: legacyfixture --out <dir> [--header 16|20] [--tile x y] "
                        "[--cells n] [--closed]\n");
        return 2;
    }

    if (doorRow < 0) doorRow = cells / 2 - 1;

    std::vector<unsigned char> blob;
    int polyCount = 0;
    if (!buildBlob(cells, closed, doorRow, tileX, tileY, blob, &polyCount))
        return 1;

    char path[1024];
    snprintf(path, sizeof(path), "%s/%03d.mmap", outDir.c_str(), 1);
    dtNavMeshParams params;
    memset(&params, 0, sizeof(params));
    params.orig[0] = -GRID_ORIGIN;
    params.orig[1] = 0.0f;
    params.orig[2] = -GRID_ORIGIN;
    params.tileWidth = TILE_SIZE;
    params.tileHeight = TILE_SIZE;
    params.maxTiles = 8192;
    params.maxPolys = 8192;
    FILE* f = fopen(path, "wb");
    if (!f) { fprintf(stderr, "legacyfixture: cannot write %s\n", path); return 1; }
    fwrite(&params, sizeof(params), 1, f);
    fclose(f);

    /* The extractors name the file by grid coordinate, which runs the other way:
     * TileWorker derives the blob's own index as (centre - orig) / 533, while the
     * file name uses 32 - worldAxis / 533. The two are mirrors, so the file that
     * holds blob (tileX, tileY) is <map><64-tileY><64-tileX>.mmtile. Getting this
     * backwards is invisible near tile 32, where both give nearly the same number.
     * For a blob whose mesh-axis centre is v, blobIndex = floor(32 + v/533.33) and
     * the file number is floor(32 - v/533.33), so the file is 63 - blobIndex. */
    snprintf(path, sizeof(path), "%s/%03d%02d%02d.mmtile", outDir.c_str(), 1,
             63 - tileY, 63 - tileX);
    f = fopen(path, "wb");
    if (!f) { fprintf(stderr, "legacyfixture: cannot write %s\n", path); return 1; }
    unsigned int head[4] = { MMAP_MAGIC, (unsigned int)DT_NAVMESH_VERSION, 5,
                             (unsigned int)blob.size() };
    fwrite(head, sizeof(head), 1, f);                 // 16 bytes
    if (headerBytes == 20)
    {
        unsigned int usesLiquids = 1;
        fwrite(&usesLiquids, sizeof(usesLiquids), 1, f);
    }
    fwrite(blob.data(), blob.size(), 1, f);
    fclose(f);

    printf("legacyfixture: %s - %d polys, %u byte blob, %d byte header, wall %s\n",
           path, polyCount, (unsigned)blob.size(), headerBytes,
           closed ? "closed (no doorway)" : "with a doorway");
    return 0;
}

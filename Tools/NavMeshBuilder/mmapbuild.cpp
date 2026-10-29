/*
 * mmapbuild - turn baked walkability tiles (.navtile) into Detour mmaps.
 *
 * Pipeline this completes:
 *
 *   ADT  --navbake-->  .navtile  --mmapbuild-->  mmaps\<map>.mmap + <map><x><y>.mmtile
 *                                                      |
 *                                    Navigation.dll (x64) reads these
 *
 * Why no Recast: Recast turns a triangle soup into a smoothed polygon mesh. Our
 * input is already a quantised heightfield (~2 yd cells), so the honest thing is
 * one convex quad per walkable cell and let Detour do the querying - A*, raycasts,
 * off-mesh links, sliced searches. That is "real navigation"; it is also ~200
 * lines instead of a Recast port nobody here can test.
 *
 * Format written: MaNGOS multi-tile ("v6") - the loader's MMAP_MULTI_TILE_VERSION:
 *
 *   MmapMultiTileHeader { uint magic 'MMAP'; uint dtVersion; uint mmapVersion=6;
 *                         uint tileCount; uint flags; }
 *   then tileCount x { uint32 blobSize; byte blob[blobSize]; }   (0 = empty slot)
 *
 * One file per ADT, 4x4 = 16 sub-tiles of 67x67 cells. A single 533 yd blob cannot
 * be used: Detour indexes polygons with uint16, so a tile holds at most 65535, and
 * one ADT has ~71824 cells.
 *
 * Per-map {map:03d}.mmap holds one dtNavMeshParams (tileWidth = 533.3333 / 4).
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
#include <algorithm>

#ifdef _WIN32
#include <windows.h>
#else
#include <dirent.h>
#endif

extern "C"
{
#include "navgrid.h"
}

namespace
{
    const unsigned int MMAP_MAGIC = 0x4d4d4150;
    const unsigned int MMAP_MULTI_TILE_VERSION = 6;
    const float TILE_SIZE = 533.33333f;
    const float GRID_ORIGIN = 32.0f * TILE_SIZE;
    const float SUBTILES = 4.0f;
    const float SUB_TILE_SIZE = TILE_SIZE / SUBTILES;
    const float CELL_QUANT = 0.1f;      // vertices are stored in decimetres
    const int SUB_CELLS = NG_CELLS / 4; // 67

#pragma pack(push, 1)
    struct MmapMultiTileHeader
    {
        unsigned int mmapMagic;
        unsigned int dtVersion;
        unsigned int mmapVersion;
        unsigned int tileCount;
        unsigned int flags;
    };
#pragma pack(pop)


/* Directory listing: POSIX readdir and Win32 FindFirstFile, so the same source
 * cross-compiles for the machine that actually has the WoW data. */
static std::vector<std::string> listDir(const std::string& dir)
{
    std::vector<std::string> names;
#ifdef _WIN32
    WIN32_FIND_DATAA fd;
    const std::string pattern = dir + "/*";
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return names;
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        names.push_back(fd.cFileName);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR* d = opendir(dir.c_str());
    if (!d) return names;
    struct dirent* entry;
    while ((entry = readdir(d)) != nullptr)
    {
        if (entry->d_type == DT_DIR) continue;
        names.push_back(entry->d_name);
    }
    closedir(d);
#endif
    std::sort(names.begin(), names.end());
    return names;
}

    /* The loader's own filename convention (Navigation::WorldToTile). Note this
     * key grows as the coordinate decreases; it only locates the file, it is not
     * Detour's tile index. */
    void tileKeyFor(float x, float y, int* tileX, int* tileY)
    {
        *tileX = (int)((GRID_ORIGIN - x) / TILE_SIZE);
        *tileY = (int)((GRID_ORIGIN - y) / TILE_SIZE);
    }

    /* Global 0.1 yd lattice so vertices on a sub-tile seam quantise to the same
     * value on both sides; without this, Detour cannot match the portal and the
     * two sides stay disconnected. */
    float quantise(float v) { return std::lround(v * 10.0f) / 10.0f; }

    struct SubStats { int polys = 0; int verts = 0; };

    bool buildSubTile(const ng_tile* tile, int blockCol, int blockRow,
                      int blobTileX, int blobTileY,
                      std::vector<unsigned char>& blob, SubStats& stats)
    {
        const int col0 = blockCol * SUB_CELLS;
        const int row0 = blockRow * SUB_CELLS;
        const float cell = NG_CELL_YD;

        auto walkable = [&](int col, int row) -> bool
        {
            if (col < col0 || row < row0 || col >= col0 + SUB_CELLS || row >= row0 + SUB_CELLS) return false;
            return ng_tile_walkable(tile, col, row) != 0;
        };
        auto height = [&](int col, int row) { return ng_tile_height(tile, col, row); };

        // Corner lattice over this block: (SUB_CELLS+1)^2 shared positions.
        const int stride = SUB_CELLS + 1;
        std::vector<int> cornerIndex(stride * stride, -1);
        std::vector<float> wx, wy, wz;
        std::vector<unsigned short> polys, polyFlags;
        std::vector<unsigned char> polyAreas;

        float bmin[3] = { 1e30f, 1e30f, 1e30f };
        float bmax[3] = { -1e30f, -1e30f, -1e30f };

        auto corner = [&](int lc, int lr) -> int
        {
            const int key = lr * stride + lc;
            if (cornerIndex[key] >= 0) return cornerIndex[key];

            const int col = col0 + lc;
            const int row = row0 + lr;
            // Corner between the four cells touching (col,row); avg of walkable ones.
            const float x = quantise(tile->origin_x - (float)col * cell + cell * 0.5f);
            const float y = quantise(tile->origin_y - (float)row * cell + cell * 0.5f);
            /* Navigation.dll swizzles WoW (x,y,z) into Detour (y,z,x) before every
             * query (PathFinder.cpp: { start.y, start.z, start.x }), so the mesh has
             * to be built in that frame: Detour x = WoW y, Detour y = WoW z (up),
             * Detour z = WoW x. Getting this wrong produces a mesh that loads and
             * never finds a polygon. */

            float z = 0.0f;
            int n = 0;
            for (int dr = -1; dr <= 0; dr++)
            {
                for (int dc = -1; dc <= 0; dc++)
                {
                    const int c = col + dc, r = row + dr;
                    if (c < col0 || r < row0 || c >= col0 + SUB_CELLS || r >= row0 + SUB_CELLS) continue;
                    if (!walkable(c, r)) continue;
                    z += height(c, r);
                    n++;
                }
            }
            if (n == 0) return -1;
            z = quantise(z / (float)n);

            const int index = (int)wx.size();
            wx.push_back(x); wy.push_back(y); wz.push_back(z);
            bmin[0] = std::min(bmin[0], y); bmax[0] = std::max(bmax[0], y);   // Detour x = WoW y
            bmin[1] = std::min(bmin[1], z); bmax[1] = std::max(bmax[1], z);   // Detour y = WoW z
            bmin[2] = std::min(bmin[2], x); bmax[2] = std::max(bmax[2], x);   // Detour z = WoW x
            cornerIndex[key] = index;
            return index;
        };

        // Player must see the whole cell, including its four corners.
        std::vector<int> cellPoly(SUB_CELLS * SUB_CELLS, -1);
        for (int r = 0; r < SUB_CELLS; r++)
        {
            for (int c = 0; c < SUB_CELLS; c++)
            {
                if (!walkable(col0 + c, row0 + r)) continue;
                if (corner(c, r) < 0 || corner(c + 1, r) < 0 ||
                    corner(c, r + 1) < 0 || corner(c + 1, r + 1) < 0) continue;
                cellPoly[r * SUB_CELLS + c] = (int)polyFlags.size();
                polyFlags.push_back(1);
                polyAreas.push_back(0);
            }
        }
        if (polyFlags.empty()) return false;

        const int nvp = 4;
        polys.reserve(polyFlags.size() * nvp * 2);
        for (int r = 0; r < SUB_CELLS; r++)
        {
            for (int c = 0; c < SUB_CELLS; c++)
            {
                const int poly = cellPoly[r * SUB_CELLS + c];
                if (poly < 0) continue;

                // Cell (c,r) spans x in [origin-(c+1)cell, origin-c*cell], and the
                // same on y. Vertices in the order the neighbour table expects.
                const int v[4] = {
                    corner(c + 1, r + 1),   // x low,  y low
                    corner(c,     r + 1),   // x high, y low
                    corner(c,     r),       // x high, y high
                    corner(c + 1, r)        // x low,  y high
                };
                for (int i = 0; i < 4; i++) polys.push_back((unsigned short)v[i]);

                /* Edges in the order the quads above produce them, and the Detour
                 * side code each one becomes when it faces another sub-tile.
                 *
                 * dtCreateNavMeshData reads an edge value of 0x8000|dir as a portal:
                 * 0 = x-, 1 = z+, 2 = x+, 3 = z-, 0xf = solid border. In our frame
                 * Detour x is WoW y and Detour z is WoW x, and a cell's y decreases
                 * as its row rises - hence the direction table below. Getting these
                 * four numbers wrong produces a mesh that loads, paths inside one
                 * sub-tile, and stops dead at every seam. */
                const float here = height(col0 + c, row0 + r);
                const int nc[4] = { c, c - 1, c, c + 1 };
                const int nr[4] = { r + 1, r, r - 1, r };
                const unsigned short portal[4] = {
                    (unsigned short)(0x8000 | 0),   // y low  -> Detour x-
                    (unsigned short)(0x8000 | 1),   // x high -> Detour z+
                    (unsigned short)(0x8000 | 2),   // y high -> Detour x+
                    (unsigned short)(0x8000 | 3)    // x low  -> Detour z-
                };

                for (int i = 0; i < 4; i++)
                {
                    const int cc = nc[i], rr = nr[i];
                    if (cc < 0 || rr < 0 || cc >= SUB_CELLS || rr >= SUB_CELLS)
                    {
                        // Edge of this sub-tile: a portal to the neighbouring one.
                        polys.push_back(portal[i]);
                        continue;
                    }
                    const bool walkableNeighbour = walkable(col0 + cc, row0 + rr);
                    const bool climbable = walkableNeighbour &&
                        std::fabs(height(col0 + cc, row0 + rr) - here) <= 1.0f;
                    if (!climbable)
                    {
                        polys.push_back(0xffff);            // solid border
                        continue;
                    }
                    polys.push_back((unsigned short)cellPoly[rr * SUB_CELLS + cc]);
                }
                stats.polys++;
            }
        }
        if (stats.polys == 0) return false;

        // Pad the volume so quantisation cannot round a vertex outside it.
        bmin[0] -= 1.0f; bmin[1] -= 1.0f; bmin[2] -= 1.0f;
        bmax[0] += 1.0f; bmax[1] += 1.0f; bmax[2] += 1.0f;

        std::vector<unsigned short> packed(wx.size() * 3);
        for (size_t i = 0; i < wx.size(); i++)
        {
            int ix = (int)std::lround((wy[i] - bmin[0]) / CELL_QUANT);   // Detour x <- WoW y
            int iy = (int)std::lround((wz[i] - bmin[1]) / CELL_QUANT);   // Detour y <- WoW z
            int iz = (int)std::lround((wx[i] - bmin[2]) / CELL_QUANT);   // Detour z <- WoW x
            if (ix < 0) ix = 0; if (iy < 0) iy = 0; if (iz < 0) iz = 0;
            if (ix > 65535) ix = 65535; if (iy > 65535) iy = 65535; if (iz > 65535) iz = 65535;
            packed[i * 3 + 0] = (unsigned short)ix;
            packed[i * 3 + 1] = (unsigned short)iy;
            packed[i * 3 + 2] = (unsigned short)iz;
        }

        dtNavMeshCreateParams params;
        memset(&params, 0, sizeof(params));
        params.verts = packed.data();
        params.vertCount = (int)wx.size();
        params.polys = polys.data();
        params.polyFlags = polyFlags.data();
        params.polyAreas = polyAreas.data();
        params.polyCount = (int)polyFlags.size();
        params.nvp = nvp;
        params.cs = CELL_QUANT;
        params.ch = CELL_QUANT;
        params.bmin[0] = bmin[0]; params.bmin[1] = bmin[1]; params.bmin[2] = bmin[2];
        params.bmax[0] = bmax[0]; params.bmax[1] = bmax[1]; params.bmax[2] = bmax[2];
        params.walkableHeight = 2.0f;
        params.walkableRadius = 0.5f;
        params.walkableClimb = 1.0f;
        params.tileX = blobTileX;
        params.tileY = blobTileY;
        params.tileLayer = 0;
        params.userId = (unsigned int)(tile->tile_x * 100 + tile->tile_y);

        unsigned char* data = nullptr;
        int dataSize = 0;
        if (!dtCreateNavMeshData(&params, &data, &dataSize))
        {
            fprintf(stderr, "mmapbuild: dtCreateNavMeshData failed (%d,%d) block (%d,%d)\n",
                    blobTileX, blobTileY, blockCol, blockRow);
            return false;
        }
        blob.assign(data, data + dataSize);
        dtFree(data);
        stats.verts = (int)wx.size();
        return true;
    }

    bool buildAdt(const ng_tile* tile, const dtNavMeshParams& mapParams, int mapId,
                  const char* outDir, int* fileKeyX, int* fileKeyY, SubStats* total)
    {
        const float centreX = (32 - tile->tile_y) * TILE_SIZE - TILE_SIZE * 0.5f;
        const float centreY = (32 - tile->tile_x) * TILE_SIZE - TILE_SIZE * 0.5f;
        tileKeyFor(centreX, centreY, fileKeyX, fileKeyY);

        std::vector<unsigned char> blobs[16];
        bool any = false;
        for (int blockRow = 0; blockRow < 4; blockRow++)
        {
            for (int blockCol = 0; blockCol < 4; blockCol++)
            {
                // Blob index from the sub-tile's own centre, so it agrees with
                // dtNavMesh::calcTileLoc at query time.
                // Detour's own axes: x from WoW y, z from WoW x.
                const float detourX = tile->origin_y - ((float)blockRow + 0.5f) * (float)SUB_CELLS * NG_CELL_YD;
                const float detourZ = tile->origin_x - ((float)blockCol + 0.5f) * (float)SUB_CELLS * NG_CELL_YD;
                const int blobX = (int)floorf((detourX - mapParams.orig[0]) / mapParams.tileWidth);
                const int blobY = (int)floorf((detourZ - mapParams.orig[2]) / mapParams.tileHeight);

                SubStats stats;
                std::vector<unsigned char>& blob = blobs[blockRow * 4 + blockCol];
                if (buildSubTile(tile, blockCol, blockRow, blobX, blobY, blob, stats))
                {
                    total->polys += stats.polys;
                    total->verts += stats.verts;
                    any = true;
                }
            }
        }
        if (!any) return false;

        char path[1024];
        snprintf(path, sizeof(path), "%s/%03d%02d%02d.mmtile", outDir, mapId, *fileKeyX, *fileKeyY);
        FILE* f = fopen(path, "wb");
        if (!f)
        {
            fprintf(stderr, "mmapbuild: cannot write %s\n", path);
            return false;
        }
        MmapMultiTileHeader header;
        header.mmapMagic = MMAP_MAGIC;
        header.dtVersion = DT_NAVMESH_VERSION;
        header.mmapVersion = MMAP_MULTI_TILE_VERSION;
        header.tileCount = 16;
        header.flags = 1;
        fwrite(&header, sizeof(header), 1, f);
        for (int i = 0; i < 16; i++)
        {
            unsigned int size = (unsigned int)blobs[i].size();
            fwrite(&size, sizeof(size), 1, f);
            if (size) fwrite(blobs[i].data(), size, 1, f);
        }
        fclose(f);
        printf("  ADT(%d,%d) -> %03d%02d%02d.mmtile (%d/16 sub-tiles)\n",
               tile->tile_x, tile->tile_y, mapId, *fileKeyX, *fileKeyY,
               (int)std::count_if(std::begin(blobs), std::end(blobs),
                                  [](const std::vector<unsigned char>& b) { return !b.empty(); }));
        return true;
    }
}

int main(int argc, char** argv)
{
    std::string tilesDir, outDir;
    int mapId = -1;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--tiles") && i + 1 < argc) tilesDir = argv[++i];
        else if (!strcmp(argv[i], "--out") && i + 1 < argc) outDir = argv[++i];
        else if (!strcmp(argv[i], "--map") && i + 1 < argc) mapId = atoi(argv[++i]);
    }
    if (tilesDir.empty() || outDir.empty() || mapId < 0)
    {
        fprintf(stderr, "usage: mmapbuild --tiles <dir with .navtile> --map <id> --out <mmaps dir>\n");
        return 2;
    }

    dtNavMeshParams mapParams;
    memset(&mapParams, 0, sizeof(mapParams));
    mapParams.orig[0] = -GRID_ORIGIN;
    mapParams.orig[1] = 0.0f;
    mapParams.orig[2] = -GRID_ORIGIN;
    mapParams.tileWidth = SUB_TILE_SIZE;
    mapParams.tileHeight = SUB_TILE_SIZE;
    mapParams.maxTiles = 8192;
    mapParams.maxPolys = 8192;

    char mapPath[1024];
    snprintf(mapPath, sizeof(mapPath), "%s/%03d.mmap", outDir.c_str(), mapId);
    FILE* mf = fopen(mapPath, "wb");
    if (!mf)
    {
        fprintf(stderr, "mmapbuild: cannot write %s\n", mapPath);
        return 1;
    }
    fwrite(&mapParams, sizeof(mapParams), 1, mf);
    fclose(mf);
    printf("wrote %s (sub-tile %.2f yd, maxTiles %d, maxPolys %d)\n",
           mapPath, mapParams.tileWidth, mapParams.maxTiles, mapParams.maxPolys);

    const std::string prefix = std::to_string(mapId) + "_";
    std::vector<std::string> names;
    for (const std::string& name : listDir(tilesDir))
    {
        if (name.size() > 12 && name.compare(0, prefix.size(), prefix) == 0 &&
            name.compare(name.size() - 8, 8, ".navtile") == 0)
            names.push_back(name);
    }

    int written = 0;
    SubStats total;
    for (const std::string& name : names)
    {
        const std::string path = tilesDir + "/" + name;
        ng_tile* tile = (ng_tile*)malloc(sizeof(ng_tile));
        if (!tile) return 1;
        if (!ng_tile_read(tile, path.c_str()))
        {
            free(tile);
            continue;
        }
        int keyX = 0, keyY = 0;
        if (buildAdt(tile, mapParams, mapId, outDir.c_str(), &keyX, &keyY, &total)) written++;
        free(tile);
    }

    printf("%d ADT files written, %d polys, %d verts\n", written, total.polys, total.verts);
    return written > 0 ? 0 : 1;
}

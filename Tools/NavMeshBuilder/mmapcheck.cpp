/*
 * mmapcheck - loads generated mmaps with Detour exactly the way Navigation.dll
 * does, then answers a path query.
 *
 * This is the acceptance test for mmapbuild. It runs on any platform, and because
 * it links the same Detour sources the x64 DLL was built from, a path found here is
 * a path the bot can find - the only thing it cannot prove is the Windows loading
 * itself.
 *
 *   mmapcheck --mmaps <dir> --map <id> --from x,y,z --to x,y,z [--verbose]
 */
#include "DetourNavMesh.h"
#include "DetourNavMeshQuery.h"
#include "DetourCommon.h"
#include "DetourNavMeshBuilder.h"

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

static bool dumpHeaders = false;

namespace
{
    const unsigned int MMAP_MAGIC = 0x4d4d4150;
    const unsigned int MMAP_VERSION = 4;
    const unsigned int MMAP_MULTI_TILE_VERSION = 6;
    const float TILE_SIZE = 533.33333f;
    const float GRID_ORIGIN = 32.0f * TILE_SIZE;

#pragma pack(push, 1)
    struct MmapTileHeader { unsigned int magic, dtVersion, mmapVersion, size; };
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

    void tileKeyFor(float x, float y, int* tileX, int* tileY)
    {
        *tileX = (int)((GRID_ORIGIN - x) / TILE_SIZE);
        *tileY = (int)((GRID_ORIGIN - y) / TILE_SIZE);
    }

    struct Loaded { int files = 0; int blobs = 0; int failed = 0; int legacyFiles = 0; };

    bool addTile(dtNavMesh* nav, unsigned char* data, int size, Loaded* stats)
    {
        if (dumpHeaders)
        {
            const dtMeshHeader* h = (const dtMeshHeader*)data;
            printf("    blob: tile idx (%d,%d) layer %d, %d polys, %d verts, bmin (%.1f, %.1f, %.1f) bmax (%.1f, %.1f, %.1f)\n",
                   h->x, h->y, h->layer, h->polyCount, h->vertCount,
                   h->bmin[0], h->bmin[1], h->bmin[2], h->bmax[0], h->bmax[1], h->bmax[2]);
        }
        dtTileRef ref = 0;
        dtStatus status = nav->addTile(data, size, DT_TILE_FREE_DATA, 0, &ref);
        if (dtStatusFailed(status))
        {
            stats->failed++;
            dtFree(data);
            return false;
        }
        stats->blobs++;
        return true;
    }

    void loadFile(dtNavMesh* nav, const std::string& path, Loaded* stats)
    {
        FILE* f = fopen(path.c_str(), "rb");
        if (!f) return;
        unsigned int peek[3] = { 0, 0, 0 };
        if (fread(peek, sizeof(unsigned int), 3, f) != 3 || peek[0] != MMAP_MAGIC)
        {
            fclose(f);
            return;
        }
        stats->files++;

        if (peek[2] == MMAP_MULTI_TILE_VERSION)
        {
            unsigned int count = 0, flags = 0;
            if (fread(&count, sizeof(count), 1, f) != 1 || fread(&flags, sizeof(flags), 1, f) != 1)
            {
                fclose(f);
                return;
            }
            for (unsigned int i = 0; i < count; i++)
            {
                unsigned int size = 0;
                if (fread(&size, sizeof(size), 1, f) != 1) break;
                if (size == 0) continue;
                unsigned char* data = (unsigned char*)dtAlloc(size, DT_ALLOC_PERM);
                if (!data) break;
                if (fread(data, size, 1, f) != 1) { dtFree(data); break; }
                addTile(nav, data, (int)size, stats);
            }
        }
        else
        {
            /* One Detour tile per file: what the MaNGOS / TrinityCore /
             * AzerothCore extractors write, and what the 3.3.5a mmaps on disk are.
             * The version field in those files (4 through 8) is not this build's
             * business - Detour only needs the tile to match <map>.mmap - so the
             * header size is found by looking for the tile magic at 16 or at 20
             * bytes, and addTile decides whether the tile is usable at all. */
            unsigned int head[4] = { 0, 0, 0, 0 };
            if (fseek(f, 0, SEEK_SET) == 0 && fread(head, sizeof(unsigned int), 4, f) == 4 &&
                head[0] == MMAP_MAGIC && head[3] > 0 && head[3] <= 64u * 1024u * 1024u)
            {
                long start = -1;
                for (long at = 16; at <= 20; at += 4)
                {
                    unsigned int magic = 0;
                    if (fseek(f, at, SEEK_SET) != 0) break;
                    if (fread(&magic, sizeof(magic), 1, f) != 1) break;
                    if (magic == (unsigned int)DT_NAVMESH_MAGIC) { start = at; break; }
                }
                if (start > 0)
                {
                    unsigned char* data = (unsigned char*)dtAlloc(head[3], DT_ALLOC_PERM);
                    if (data && fseek(f, start, SEEK_SET) == 0 && fread(data, head[3], 1, f) == 1)
                    {
                        stats->legacyFiles++;
                        addTile(nav, data, (int)head[3], stats);
                    }
                    else
                    {
                        dtFree(data);
                    }
                }
            }
        }
        fclose(f);
    }

    std::vector<std::string> listMmtiles(const std::string& dir, int mapId)
    {
        std::vector<std::string> out;
        const std::string prefix = std::to_string(mapId);
        for (const std::string& name : listDir(dir))
        {
            // <map:03d><x:02d><y:02d>.mmtile
            if (name.size() != 7 + 7) continue;                    // "0013232.mmtile"
            if (name.compare(name.size() - 7, 7, ".mmtile") != 0) continue;
            if (name.compare(0, 3, std::string(3 - prefix.size(), '0') + prefix) != 0) continue;
            out.push_back(dir + "/" + name);
        }
        return out;
    }
}

int main(int argc, char** argv)
{
    std::string dir;
    int mapId = -1;
    float sx = 0, sy = 0, sz = 0, gx = 0, gy = 0, gz = 0;
    bool verbose = false;
    bool haveQuery = false;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--mmaps") && i + 1 < argc) dir = argv[++i];
        else if (!strcmp(argv[i], "--map") && i + 1 < argc) mapId = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--from") && i + 1 < argc) { sscanf(argv[++i], "%f,%f,%f", &sx, &sy, &sz); haveQuery = true; }
        else if (!strcmp(argv[i], "--to") && i + 1 < argc) { sscanf(argv[++i], "%f,%f,%f", &gx, &gy, &gz); haveQuery = true; }
        else if (!strcmp(argv[i], "--verbose")) verbose = true;
        else if (!strcmp(argv[i], "--dump")) dumpHeaders = true;
    }
    if (dir.empty() || mapId < 0)
    {
        fprintf(stderr, "usage: mmapcheck --mmaps <dir> --map <id> [--from x,y,z --to x,y,z]\n");
        return 2;
    }

    char mapPath[1024];
    snprintf(mapPath, sizeof(mapPath), "%s/%03d.mmap", dir.c_str(), mapId);
    FILE* mf = fopen(mapPath, "rb");
    if (!mf)
    {
        fprintf(stderr, "mmapcheck: no %s\n", mapPath);
        return 1;
    }
    dtNavMeshParams params;
    if (fread(&params, sizeof(params), 1, mf) != 1)
    {
        fclose(mf);
        fprintf(stderr, "mmapcheck: %s is truncated\n", mapPath);
        return 1;
    }
    fclose(mf);

    printf("params: orig (%.1f, %.1f, %.1f) tile %.2f x %.2f maxTiles %d maxPolys %d\n",
           params.orig[0], params.orig[1], params.orig[2], params.tileWidth,
           params.tileHeight, params.maxTiles, params.maxPolys);
    dtNavMesh* nav = dtAllocNavMesh();
    if (!nav || dtStatusFailed(nav->init(&params)))
    {
        fprintf(stderr, "mmapcheck: dtNavMesh::init failed\n");
        return 1;
    }

    std::vector<std::string> files = listMmtiles(dir, mapId);
    Loaded stats;
    for (const std::string& file : files) loadFile(nav, file, &stats);
    printf("map %d: %d tile files (%d single-tile), %d navmesh tiles added, %d rejected\n",
           mapId, stats.files, stats.legacyFiles, stats.blobs, stats.failed);
    printf("  dtNavMeshParams: orig (%.1f, %.1f, %.1f), tile %.2f x %.2f, maxTiles %d\n",
           params.orig[0], params.orig[1], params.orig[2],
           params.tileWidth, params.tileHeight, params.maxTiles);
    if (verbose)
    {
        int maxTiles = nav->getMaxTiles();
        for (int i = 0; i < maxTiles; i++)
        {
            const dtMeshTile* tile = nav->getTileAt(i % 64, i / 64, 0);
            if (!tile) continue;
            if (!tile || !tile->header) continue;
            printf("  tile idx (%d,%d) layer %d: %d polys, %d verts, bmin (%.1f, %.1f, %.1f)\n",
                   tile->header->x, tile->header->y, tile->header->layer,
                   tile->header->polyCount, tile->header->vertCount,
                   tile->header->bmin[0], tile->header->bmin[1], tile->header->bmin[2]);
        }
    }

    // No --from/--to: just prove the files load (this is what the bake script
    // runs, so a corrupt export fails before anyone flies into the game).
    if (!haveQuery)
    {
        printf("  load-only check: %s\n", stats.failed == 0 && stats.blobs > 0
               ? "OK - every blob was accepted by dtNavMesh"
               : "FAILED - see rejected count above");
        dtFreeNavMesh(nav);
        return stats.failed == 0 && stats.blobs > 0 ? 0 : 1;
    }

    dtNavMeshQuery* query = dtAllocNavMeshQuery();
    if (!query || dtStatusFailed(query->init(nav, 65536)))
    {
        fprintf(stderr, "mmapcheck: dtNavMeshQuery::init failed\n");
        return 1;
    }

    dtQueryFilter filter;
    filter.setIncludeFlags(0xffff);
    filter.setExcludeFlags(0);
    filter.setAreaCost(0, 1.0f);

    const float extents[3] = { 3.0f, 5.0f, 3.0f };
    dtPolyRef startRef = 0, endRef = 0;
    // Same swizzle Navigation.dll applies: Detour (x, y, z) = WoW (y, z, x).
    float startPt[3] = { sy, sz, sx }, endPt[3] = { gy, gz, gx };

    int sxTile = 0, syTile = 0;
    nav->calcTileLoc(startPt, &sxTile, &syTile);
    printf("  calcTileLoc(start) = (%d, %d)\n", sxTile, syTile);

    if (dtStatusFailed(query->findNearestPoly(startPt, extents, &filter, &startRef, startPt)) || !startRef)
    {
        fprintf(stderr, "mmapcheck: no polygon near the start (%.1f, %.1f, %.1f)\n", sx, sy, sz);
        return 1;
    }
    if (dtStatusFailed(query->findNearestPoly(endPt, extents, &filter, &endRef, endPt)) || !endRef)
    {
        fprintf(stderr, "mmapcheck: no polygon near the goal (%.1f, %.1f, %.1f)\n", gx, gy, gz);
        return 1;
    }
    printf("  start snapped to WoW (%.1f, %.1f, %.1f), goal snapped to WoW (%.1f, %.1f, %.1f)\n",
           startPt[2], startPt[0], startPt[1], endPt[2], endPt[0], endPt[1]);

    enum { PATH_BUF = 2048 };
    dtPolyRef path[PATH_BUF];
    int pathCount = 0;
    dtStatus status = query->findPath(startRef, endRef, startPt, endPt, &filter, path, &pathCount, PATH_BUF);
    printf("  findPath: status 0x%08x, %d polygons%s\n", status, pathCount,
           dtStatusSucceed(status) ? "" : " (FAILED)");

    if (!pathCount) return 1;

    static float straight[PATH_BUF * 3];
    static unsigned char straightFlags[PATH_BUF];
    int straightCount = 0;
    query->findStraightPath(startPt, endPt, path, pathCount, straight, straightFlags, nullptr, &straightCount, PATH_BUF);

    float length = 0.0f;
    for (int i = 0; i < straightCount; i++)
    {
        if (i > 0)
        {
            const float dx = straight[i * 3 + 0] - straight[(i - 1) * 3 + 0];
            const float dy = straight[i * 3 + 1] - straight[(i - 1) * 3 + 1];
            const float dz = straight[i * 3 + 2] - straight[(i - 1) * 3 + 2];
            length += sqrtf(dx * dx + dy * dy + dz * dz);
        }
        printf("    %2d: %9.2f %9.2f %7.2f\n", i,
               straight[i * 3 + 2], straight[i * 3 + 0], straight[i * 3 + 1]);
    }
    const float dx = gx - sx, dy = gy - sy, dz2 = gz - sz;   // 2D distance in WoW x/y
    printf("  path %.1f yd, straight line %.1f yd\n", length, sqrtf(dx * dx + dy * dy + dz2 * dz2));

    int keyX, keyY;
    tileKeyFor(sx, sy, &keyX, &keyY);
    printf("  the loader would look for tile file %03d%02d%02d.mmtile for the start position\n",
           mapId, keyX, keyY);

    dtFreeNavMeshQuery(query);
    dtFreeNavMesh(nav);
    return 0;
}

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
#include <cctype>
#include <set>
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

        /* Multi-tile container first, whatever the version field says: 6 is what
         * this build writes, 5 is what the 4x4 builds before it wrote, and a
         * single-tile file reaches the else branch by failing the count/size checks
         * rather than by being recognised. */
        (void)peek[2];
        const long afterPeek = ftell(f);
        if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return; }
        const long fileEnd = ftell(f);
        if (fseek(f, afterPeek, SEEK_SET) != 0) { fclose(f); return; }

        bool fromMulti = false;
        {
            unsigned int count = 0, flags = 0;
            const bool headerOk = fread(&count, sizeof(count), 1, f) == 1 &&
                                  fread(&flags, sizeof(flags), 1, f) == 1 &&
                                  count > 0 && count <= 64;
            if (headerOk)
            {
                long remaining = fileEnd - ftell(f);
                for (unsigned int i = 0; i < count; i++)
                {
                    unsigned int size = 0;
                    if (fread(&size, sizeof(size), 1, f) != 1) break;
                    if (size == 0) continue;
                    if (size > 64u * 1024u * 1024u || (long)size > remaining) break;
                    remaining -= (long)size;
                    unsigned char* data = (unsigned char*)dtAlloc(size, DT_ALLOC_PERM);
                    if (!data) break;
                    if (fread(data, size, 1, f) != 1) { dtFree(data); break; }
                    addTile(nav, data, (int)size, stats);
                    fromMulti = true;
                }
            }
        }

        if (!fromMulti)
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

    /* <map:03d><x:02d><y:02d>.mmtile (MaNGOS, cmangos, AzerothCore) or
     * <map:04d>_<x:02d>_<y:02d>.mmtile (TrinityCore and newer). Same parse as
     * parseTileFileName() in Navigation/MoveMap.cpp. */
    bool parseTileName(const std::string& name, int mapId, int* x, int* y)
    {
        const std::string suffix = ".mmtile";
        if (name.size() <= suffix.size() ||
            name.compare(name.size() - suffix.size(), suffix.size(), suffix) != 0)
            return false;

        const std::string stem = name.substr(0, name.size() - suffix.size());

        /* The map id and the tile digits are both numbers, so where the map id ends
         * has to come from the shape of the name: a "_" separator, or else the four
         * trailing digits of a 7- or 8-character run (3-digit or 4-digit map id). */
        std::string mapPart, rest;
        const size_t underscore = stem.find('_');
        if (underscore != std::string::npos)
        {
            mapPart = stem.substr(0, underscore);
            rest = stem.substr(underscore);
        }
        else
        {
            if (stem.size() != 7 && stem.size() != 8) return false;
            mapPart = stem.substr(0, stem.size() - 4);
            rest = stem.substr(stem.size() - 4);
        }
        if (mapPart.empty()) return false;
        for (size_t i = 0; i < mapPart.size(); i++)
            if (!isdigit((unsigned char)mapPart[i])) return false;
        if (atoi(mapPart.c_str()) != mapId) return false;
        if (rest.size() == 4 && isdigit((unsigned char)rest[0]) && isdigit((unsigned char)rest[1]) &&
            isdigit((unsigned char)rest[2]) && isdigit((unsigned char)rest[3]))
        {
            *x = (rest[0] - '0') * 10 + (rest[1] - '0');
            *y = (rest[2] - '0') * 10 + (rest[3] - '0');
            return true;
        }
        if (rest.size() == 6 && rest[0] == '_' && rest[3] == '_' &&
            isdigit((unsigned char)rest[1]) && isdigit((unsigned char)rest[2]) &&
            isdigit((unsigned char)rest[4]) && isdigit((unsigned char)rest[5]))
        {
            *x = (rest[1] - '0') * 10 + (rest[2] - '0');
            *y = (rest[4] - '0') * 10 + (rest[5] - '0');
            return true;
        }
        return false;
    }

    /* On-demand tile loading, mirroring Navigation::LoadTileForDetour: Detour asks
     * for the tile next to the one it is expanding, we turn that into grid names and
     * try both naming schemes plus the 3x3 neighbourhood. A long route can then run
     * across tiles that were never loaded up front - which is what happens in the
     * game when the bot walks further than the preload radius. */
    struct LoaderCtx
    {
        dtNavMesh* nav = nullptr;
        std::string dir;
        int mapId = 0;
        int filesLoaded = 0;
        std::set<std::string> tried;
    };

    bool loadNamedTile(LoaderCtx* ctx, int gx, int gy)
    {
        char classic[64], trinity[64];
        snprintf(classic, sizeof(classic), "%03d%02d%02d.mmtile", ctx->mapId, gx, gy);
        snprintf(trinity, sizeof(trinity), "%04d_%02d_%02d.mmtile", ctx->mapId, gx, gy);
        const char* names[2] = { classic, trinity };
        for (int i = 0; i < 2; i++)
        {
            if (!ctx->tried.insert(names[i]).second) continue;
            const std::string path = ctx->dir + "/" + names[i];
            FILE* probe = fopen(path.c_str(), "rb");
            if (!probe) continue;
            fclose(probe);
            Loaded stats;
            loadFile(ctx->nav, path, &stats);
            ctx->filesLoaded++;
            if (stats.blobs > 0) return true;
        }
        return false;
    }

    bool onDemandLoader(int x, int y, void* userArg)
    {
        LoaderCtx* ctx = (LoaderCtx*)userArg;
        const dtNavMeshParams* params = ctx->nav->getParams();
        if (!params) return false;

        // Detour (x, z) -> WoW (y, x), the inverse of the swizzle queries use.
        const float worldY = params->orig[0] + ((float)x + 0.5f) * params->tileWidth;
        const float worldX = params->orig[2] + ((float)y + 0.5f) * params->tileHeight;
        int gx = 0, gy = 0;
        tileKeyFor(worldX, worldY, &gx, &gy);

        bool loaded = false;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                const int nx = gx + dx, ny = gy + dy;
                if (nx < 0 || nx >= 64 || ny < 0 || ny >= 64) continue;
                if (loadNamedTile(ctx, nx, ny)) loaded = true;
            }
        }
        return loaded;
    }

    std::vector<std::string> listMmtiles(const std::string& dir, int mapId)
    {
        std::vector<std::string> out;
        for (const std::string& name : listDir(dir))
        {
            int x = 0, y = 0;
            if (!parseTileName(name, mapId, &x, &y)) continue;
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
    bool onDemand = false;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--mmaps") && i + 1 < argc) dir = argv[++i];
        else if (!strcmp(argv[i], "--map") && i + 1 < argc) mapId = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--from") && i + 1 < argc) { sscanf(argv[++i], "%f,%f,%f", &sx, &sy, &sz); haveQuery = true; }
        else if (!strcmp(argv[i], "--to") && i + 1 < argc) { sscanf(argv[++i], "%f,%f,%f", &gx, &gy, &gz); haveQuery = true; }
        else if (!strcmp(argv[i], "--ondemand")) onDemand = true;
        else if (!strcmp(argv[i], "--verbose")) verbose = true;
        else if (!strcmp(argv[i], "--dump")) dumpHeaders = true;
    }
    if (dir.empty() || mapId < 0)
    {
        fprintf(stderr, "usage: mmapcheck --mmaps <dir> --map <id> [--from x,y,z --to x,y,z]\n");
        return 2;
    }

    char mapPath[1024];
    char trinityPath[1024];
    snprintf(trinityPath, sizeof(trinityPath), "%s/%04d.mmap", dir.c_str(), mapId);
    snprintf(mapPath, sizeof(mapPath), "%s/%03d.mmap", dir.c_str(), mapId);
    FILE* mf = fopen(mapPath, "rb");
    if (!mf)
    {
        // TrinityCore and newer: 0001.mmap beside 0001_32_32.mmtile
        mf = fopen(trinityPath, "rb");
        if (!mf)
        {
            fprintf(stderr, "mmapcheck: no %s (or %s)\n", mapPath, trinityPath);
            return 1;
        }
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

    LoaderCtx loader;
    loader.nav = nav;
    loader.dir = dir;
    loader.mapId = mapId;

    Loaded stats;
    if (!onDemand)
    {
        std::vector<std::string> files = listMmtiles(dir, mapId);
        for (const std::string& file : files) loadFile(nav, file, &stats);
    }
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

    float seedPt[3] = { sy, sz, sx };
    int seedX = 0, seedY = 0;
    nav->calcTileLoc(seedPt, &seedX, &seedY);
    if (onDemand)
    {
        query->setTileLoader(onDemandLoader, &loader);
        onDemandLoader(seedX, seedY, &loader);   // the tile the player stands on
        printf("  on-demand: loaded the start tile, %d file(s) total\n", loader.filesLoaded);
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

    if (onDemand)
        printf("  on-demand: %d tile file(s) loaded during the search\n", loader.filesLoaded);

    int keyX, keyY;
    tileKeyFor(sx, sy, &keyX, &keyY);
    printf("  the loader would look for tile file %03d%02d%02d.mmtile for the start position\n",
           mapId, keyX, keyY);

    dtFreeNavMeshQuery(query);
    dtFreeNavMesh(nav);
    return 0;
}

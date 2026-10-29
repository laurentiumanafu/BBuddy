/*
 * navbake - bake ADT tiles into .navtile files, and query them.
 *
 *   navbake bake --adt <file> --map <id> --tx <n> --ty <n> [--slope 45] --out <dir>
 *   navbake bakeall --adts <dir> --map <id> [--slope 45] --out <dir>
 *   navbake path --tiles <dir> --map <id> --from x,y[,z] --to x,y[,z]
 *   navbake probe --tiles <dir> --map <id> x,y[,z]
 *
 * `probe` is the sanity check that matters: stand on a known slope in game, run
 * probe with your coordinates, and compare the reported height with the Z the bot
 * logs. If they disagree by tens of yards, the ADT axis convention in navgrid.c is
 * flipped for that client - one function to fix, and this command proves it.
 */
#include "adt.h"
#include "navgrid.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <dirent.h>
#endif

static const char *arg_value(int argc, char **argv, const char *name)
{
    for (int i = 2; i + 1 < argc; i++)
    {
        if (strcmp(argv[i], name) == 0) return argv[i + 1];
    }
    return NULL;
}

static int arg_int(int argc, char **argv, const char *name, int fallback)
{
    const char *value = arg_value(argc, argv, name);
    return value == NULL ? fallback : atoi(value);
}

static float arg_float(int argc, char **argv, const char *name, float fallback)
{
    const char *value = arg_value(argc, argv, name);
    return value == NULL ? fallback : (float)atof(value);
}

static void tile_path(char *out, size_t size, const char *dir, int map_id, int tx, int ty)
{
    snprintf(out, size, "%s/%d_%d_%d.navtile", dir, map_id, tx, ty);
}

static int load_tile(const char *dir, int map_id, int tx, int ty, ng_tile **out)
{
    char path[1024];
    tile_path(path, sizeof(path), dir, map_id, tx, ty);

    ng_tile *t = (ng_tile *)malloc(sizeof(ng_tile));
    if (t == NULL) return 0;
    if (!ng_tile_read(t, path))
    {
        free(t);
        return 0;
    }
    *out = t;
    return 1;
}

static int cmd_bake(int argc, char **argv)
{
    const char *adt = arg_value(argc, argv, "--adt");
    const char *out = arg_value(argc, argv, "--out");
    int map_id = arg_int(argc, argv, "--map", -1);
    int tx = arg_int(argc, argv, "--tx", -9999);
    int ty = arg_int(argc, argv, "--ty", -9999);
    float slope = arg_float(argc, argv, "--slope", 45.0f);

    if (adt == NULL || out == NULL || map_id < 0 || tx == -9999 || ty == -9999)
    {
        fprintf(stderr, "usage: navbake bake --adt <file> --map <id> --tx <n> --ty <n> "
                        "[--slope deg] --out <dir>\n");
        return 2;
    }

    ng_tile *tile = (ng_tile *)malloc(sizeof(ng_tile));
    if (tile == NULL) return 1;

    adt_stats stats;
    int rc = ng_bake_adt(adt, map_id, tx, ty, slope, tile, &stats);
    if (rc <= 0)
    {
        fprintf(stderr, "navbake: no MCNK/MCVT read from %s (rc=%d)\n", adt, rc);
        free(tile);
        return 1;
    }

    char path[1024];
    tile_path(path, sizeof(path), out, map_id, tx, ty);
    if (!ng_tile_write(tile, path))
    {
        fprintf(stderr, "navbake: cannot write %s\n", path);
        free(tile);
        return 1;
    }

    long walkable = 0;
    for (int i = 0; i < NG_CELL_COUNT; i++)
        if (tile->flags[i] & NG_WALK) walkable++;

    printf("baked %s -> %s\n", adt, path);
    printf("  map %d tile (%d,%d), chunks %d (missing %d), heights %.1f..%.1f, "
           "holes %d, walkable cells %ld of %d (%.1f%%)\n",
           map_id, tx, ty, stats.chunks_read, stats.chunks_missing,
           stats.min_height, stats.max_height, stats.holes,
           walkable, NG_CELL_COUNT, 100.0 * (double)walkable / (double)NG_CELL_COUNT);
    free(tile);
    return 0;
}

/* Builds `<dir>/<name>` without assuming a path separator, so the same binary
 * works whether it was handed `C:\adts` or `/tmp/adts`. */
static void join_path(char *out, size_t size, const char *dir, const char *name)
{
    const size_t n = strlen(dir);
    const int slash = n > 0 && (dir[n - 1] == '/' || dir[n - 1] == '\\');
    snprintf(out, size, "%s%s%s", dir, slash ? "" : "/", name);
}

/* Pulls the X_Y out of `Kalimdor_32_32.adt` / `1_32_32.adt`. Returns 0 when the
 * name does not end in the two tile digits, in which case the caller must skip
 * the file rather than guess - a wrong tile index misplaces every height. */
static int tile_from_name(const char *name, int *tx, int *ty)
{
    const char *dot = strrchr(name, '.');
    if (dot == NULL || dot == name) return 0;

    char stem[512];
    const size_t len = (size_t)(dot - name);
    if (len >= sizeof(stem)) return 0;
    memcpy(stem, name, len);
    stem[len] = 0;

    char *last = strrchr(stem, '_');
    if (last == NULL) return 0;
    *last = 0;
    char *prev = strrchr(stem, '_');
    if (prev == NULL) return 0;

    const char *a = prev + 1;
    const char *b = last + 1;
    if (*a == 0 || *b == 0) return 0;
    for (const char *c = a; *c; c++) if (*c < '0' || *c > '9') return 0;
    for (const char *c = b; *c; c++) if (*c < '0' || *c > '9') return 0;

    *tx = atoi(a);
    *ty = atoi(b);
    return *tx >= 0 && *tx < 64 && *ty >= 0 && *ty < 64;
}

static int opens_with(const char *name, const char *suffix)
{
    const size_t n = strlen(name);
    const size_t m = strlen(suffix);
    return n > m && strcmp(name + n - m, suffix) == 0;
}

/* Whole-map bake: walk an ADT export folder and bake every root ADT in it.
 * `_tex0`/`_obj0` siblings share the tile index and are skipped by the suffix
 * test, so an unmodified CASC export can be pointed at directly. */
static int cmd_bakeall(int argc, char **argv)
{
    const char *dir = arg_value(argc, argv, "--adts");
    const char *out = arg_value(argc, argv, "--out");
    int map_id = arg_int(argc, argv, "--map", -1);
    float slope = arg_float(argc, argv, "--slope", 45.0f);
    int verbose = 0;
    for (int i = 0; i < argc; i++) if (strcmp(argv[i], "--verbose") == 0) verbose = 1;

    if (dir == NULL || out == NULL || map_id < 0)
    {
        fprintf(stderr, "usage: navbake bakeall --adts <dir> --map <id> [--slope deg] --out <dir>\n");
        return 2;
    }

    char names[4096][256];
    int count = 0;
#ifdef _WIN32
    char pattern[1024];
    join_path(pattern, sizeof(pattern), dir, "*");
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE)
    {
        fprintf(stderr, "navbake: cannot list %s\n", dir);
        return 1;
    }
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (count >= 4096) break;
        snprintf(names[count], sizeof(names[0]), "%s", fd.cFileName);
        count++;
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR *d = opendir(dir);
    if (d == NULL)
    {
        fprintf(stderr, "navbake: cannot list %s\n", dir);
        return 1;
    }
    struct dirent *entry;
    while ((entry = readdir(d)) != NULL)
    {
        if (count >= 4096) break;
        snprintf(names[count], sizeof(names[0]), "%s", entry->d_name);
        count++;
    }
    closedir(d);
#endif

    ng_tile *tile = (ng_tile *)malloc(sizeof(ng_tile));
    if (tile == NULL) return 1;

    int baked = 0, skipped = 0, failed = 0;
    long total_walkable = 0;
    for (int i = 0; i < count; i++)
    {
        if (!opens_with(names[i], ".adt")) continue;
        if (opens_with(names[i], "_tex0.adt") || opens_with(names[i], "_obj0.adt") ||
            opens_with(names[i], "_tex1.adt") || opens_with(names[i], "_obj1.adt") ||
            opens_with(names[i], "_lod.adt"))
        {
            skipped++;
            continue;
        }

        int tx, ty;
        if (!tile_from_name(names[i], &tx, &ty))
        {
            fprintf(stderr, "navbake: %s has no _X_Y suffix, skipped\n", names[i]);
            skipped++;
            continue;
        }

        char adt_path[1024];
        join_path(adt_path, sizeof(adt_path), dir, names[i]);

        adt_stats stats;
        if (ng_bake_adt(adt_path, map_id, tx, ty, slope, tile, &stats) <= 0)
        {
            fprintf(stderr, "navbake: %s has no MCNK/MCVT\n", names[i]);
            failed++;
            continue;
        }

        char tile_file[1024];
        tile_path(tile_file, sizeof(tile_file), out, map_id, tx, ty);
        if (!ng_tile_write(tile, tile_file))
        {
            fprintf(stderr, "navbake: cannot write %s\n", tile_file);
            failed++;
            continue;
        }

        long walkable = 0;
        for (int c = 0; c < NG_CELL_COUNT; c++)
            if (tile->flags[c] & NG_WALK) walkable++;
        total_walkable += walkable;
        baked++;
        if (verbose)
            printf("  %s -> (%d,%d) heights %.1f..%.1f, walkable %ld/%d\n",
                   names[i], tx, ty, stats.min_height, stats.max_height,
                   walkable, NG_CELL_COUNT);
    }
    free(tile);

    printf("bakeall: %d ADTs baked, %d skipped, %d failed, %.2fM walkable cells -> %s\n",
           baked, skipped, failed, (double)total_walkable / 1e6, out);
    return baked > 0 ? 0 : 1;
}

static int parse_xy(const char *text, float *x, float *y, float *z)
{
    if (text == NULL) return 0;
    *z = 0.0f;
    return sscanf(text, "%f,%f,%f", x, y, z) >= 2;
}

static int cmd_path(int argc, char **argv)
{
    const char *dir = arg_value(argc, argv, "--tiles");
    int map_id = arg_int(argc, argv, "--map", -1);
    float sx, sy, sz, gx, gy, gz;
    if (dir == NULL || map_id < 0 ||
        !parse_xy(arg_value(argc, argv, "--from"), &sx, &sy, &sz) ||
        !parse_xy(arg_value(argc, argv, "--to"), &gx, &gy, &gz))
    {
        fprintf(stderr, "usage: navbake path --tiles <dir> --map <id> --from x,y[,z] --to x,y[,z]\n");
        return 2;
    }

    int32_t stx, sty, gtx, gty;
    ng_adt_tile_of(sx, sy, &stx, &sty);
    ng_adt_tile_of(gx, gy, &gtx, &gty);

    int32_t tx0 = stx < gtx ? stx : gtx;
    int32_t tx1 = stx > gtx ? stx : gtx;
    int32_t ty0 = sty < gty ? sty : gty;
    int32_t ty1 = sty > gty ? sty : gty;

    ng_map map;
    float origin_x = (float)(32 - ty0) * NG_ADT_YD;
    float origin_y = (float)(32 - tx0) * NG_ADT_YD;
    if (!ng_map_init(&map, tx0, ty0, tx1 - tx0 + 1, ty1 - ty0 + 1, origin_x, origin_y))
    {
        fprintf(stderr, "navbake: tile span too large for A*\n");
        return 1;
    }

    int loaded = 0;
    for (int32_t tx = tx0; tx <= tx1; tx++)
    {
        for (int32_t ty = ty0; ty <= ty1; ty++)
        {
            ng_tile *tile = NULL;
            if (load_tile(dir, map_id, tx, ty, &tile))
            {
                ng_map_put(&map, tile);
                loaded++;
            }
        }
    }
    if (loaded == 0)
    {
        fprintf(stderr, "navbake: no tiles for map %d in %s (looked for %d_*_*.navtile)\n",
                map_id, dir, map_id);
        ng_map_free(&map);
        return 1;
    }

    enum { MAX_POINTS = 256 };
    ng_point points[MAX_POINTS];
    int explored = 0;
    int count = ng_find_path(&map, sx, sy, sz, gx, gy, gz, points, MAX_POINTS, &explored);

    printf("tiles loaded %d, cells explored %d, points %d\n", loaded, explored, count);
    float length = 0.0f;
    for (int i = 0; i < count; i++)
    {
        if (i > 0)
        {
            float dx = points[i].x - points[i - 1].x;
            float dy = points[i].y - points[i - 1].y;
            length += sqrtf(dx * dx + dy * dy);
        }
        printf("  %2d: %9.2f %9.2f %7.2f\n", i, points[i].x, points[i].y, points[i].z);
    }
    printf("path length %.1f yd, straight line %.1f yd\n", length,
           sqrtf((gx - sx) * (gx - sx) + (gy - sy) * (gy - sy)));

    ng_map_free(&map);
    return count > 0 ? 0 : 1;
}

static int cmd_probe(int argc, char **argv)
{
    const char *dir = arg_value(argc, argv, "--tiles");
    int map_id = arg_int(argc, argv, "--map", -1);
    /* The position is the last bare argument: "--map 1 3.5,-2" would otherwise
     * pick up the map id. */
    const char *position = NULL;
    for (int i = argc - 1; i >= 2; i--)
    {
        if (argv[i][0] == '-' && argv[i][1] == '-') continue;   /* a flag, not -283,-216 */
        if (i >= 3 && (strcmp(argv[i - 1], "--tiles") == 0 || strcmp(argv[i - 1], "--map") == 0)) continue;
        if (strchr(argv[i], ',') == NULL) continue;
        position = argv[i];
        break;
    }

    float x, y, z;
    if (dir == NULL || map_id < 0 || !parse_xy(position, &x, &y, &z))
    {
        fprintf(stderr, "usage: navbake probe --tiles <dir> --map <id> x,y[,z]\n");
        return 2;
    }

    int32_t tx, ty;
    ng_adt_tile_of(x, y, &tx, &ty);
    ng_tile *tile = NULL;
    if (!load_tile(dir, map_id, tx, ty, &tile))
    {
        fprintf(stderr, "navbake: no tile %d/%d/%d in %s\n", map_id, tx, ty, dir);
        return 1;
    }

    int col = 0, row = 0;
    if (!ng_tile_world_to_cell(tile, x, y, &col, &row))
    {
        fprintf(stderr, "navbake: (%.1f, %.1f) is outside tile (%d,%d)\n", x, y, tx, ty);
        free(tile);
        return 1;
    }

    float height = ng_tile_height(tile, col, row);
    int walkable = ng_tile_walkable(tile, col, row);
    unsigned flags = tile->flags[ng_tile_index(col, row)];

    printf("tile (%d,%d) cell (%d,%d) flags 0x%02x walkable=%d height=%.2f",
           tx, ty, col, row, flags, walkable, height);
    if (z != 0.0f) printf(" in-game z=%.2f delta=%+.2f", z, height - z);
    printf("\n");

    if (!walkable)
    {
        int best = -1, best_col = 0, best_row = 0;
        for (int r = row - 6; r <= row + 6; r++)
        {
            for (int c = col - 6; c <= col + 6; c++)
            {
                if (!ng_tile_walkable(tile, c, r)) continue;
                int d = (c - col) * (c - col) + (r - row) * (r - row);
                if (best < 0 || d < best) { best = d; best_col = c; best_row = r; }
            }
        }
        if (best >= 0)
        {
            float nx, ny;
            ng_tile_cell_to_world(tile, best_col, best_row, &nx, &ny);
            printf("nearest walkable cell (%d,%d) at %.1f,%.1f,%.2f\n",
                   best_col, best_row, nx, ny, ng_tile_height(tile, best_col, best_row));
        }
        else
        {
            printf("no walkable cell within 6 cells\n");
        }
    }

    free(tile);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc < 2)
    {
        fprintf(stderr, "usage: navbake <bake|bakeall|path|probe> ...\n");
        return 2;
    }
    if (strcmp(argv[1], "bake") == 0) return cmd_bake(argc, argv);
    if (strcmp(argv[1], "bakeall") == 0) return cmd_bakeall(argc, argv);
    if (strcmp(argv[1], "path") == 0) return cmd_path(argc, argv);
    if (strcmp(argv[1], "probe") == 0) return cmd_probe(argc, argv);
    fprintf(stderr, "unknown command %s\n", argv[1]);
    return 2;
}

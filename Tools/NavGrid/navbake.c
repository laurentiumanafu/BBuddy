/*
 * navbake - bake ADT tiles into .navtile files, and query them.
 *
 *   navbake bake --adt <file> --map <id> --tx <n> --ty <n> [--slope 45] --out <dir>
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
        fprintf(stderr, "usage: navbake <bake|path|probe> ...\n");
        return 2;
    }
    if (strcmp(argv[1], "bake") == 0) return cmd_bake(argc, argv);
    if (strcmp(argv[1], "path") == 0) return cmd_path(argc, argv);
    if (strcmp(argv[1], "probe") == 0) return cmd_probe(argc, argv);
    fprintf(stderr, "unknown command %s\n", argv[1]);
    return 2;
}

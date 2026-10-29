/*
 * Unit tests for the navgrid reference implementation.
 *
 * These are the assertions the C# port mirrors in NavGridPathfinder.SelfTest,
 * exactly like aimmath.h / ClickAimMath. Nothing here needs a WoW client: the
 * tiles are synthetic, so a failure means the routing code is wrong, not the data.
 */
#include "adt.h"
#include "navgrid.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;
static int checks;

static void check(const char *name, int condition)
{
    checks++;
    if (condition) printf("  PASS %s\n", name);
    else { printf("  FAIL %s\n", name); failures++; }
}

static void flat_tile(ng_tile *t, int32_t tx, int32_t ty, float height)
{
    ng_tile_init(t, 1, tx, ty);
    memset(t->flags, 0, sizeof(t->flags));
    for (int row = 0; row < NG_CELLS; row++)
    {
        for (int col = 0; col < NG_CELLS; col++)
        {
            ng_tile_set_height(t, col, row, height);
            ng_tile_set_flag(t, col, row, NG_WALK, 1);
        }
    }
}

static void world_of(const ng_tile *t, int col, int row, float *x, float *y)
{
    ng_tile_cell_to_world(t, col, row, x, y);
}

static float polyline_length(const ng_point *points, int count)
{
    float length = 0.0f;
    for (int i = 1; i < count; i++)
    {
        float dx = points[i].x - points[i - 1].x;
        float dy = points[i].y - points[i - 1].y;
        length += sqrtf(dx * dx + dy * dy);
    }
    return length;
}

static int all_segments_walkable(const ng_map *map, const ng_point *points, int count)
{
    for (int i = 1; i < count; i++)
    {
        if (!ng_line_walkable(map, points[i - 1].x, points[i - 1].y, points[i].x, points[i].y))
            return 0;
    }
    return 1;
}

/* ------------------------------------------------------------------ tests */

static void test_open_ground(void)
{
    printf("open ground\n");
    static ng_tile tile;
    flat_tile(&tile, 32, 32, 10.0f);

    ng_map map;
    ng_map_init(&map, 32, 32, 1, 1, (float)(32 - 32) * NG_ADT_YD, (float)(32 - 32) * NG_ADT_YD);
    ng_map_put(&map, &tile);

    float sx, sy, gx, gy;
    world_of(&tile, 20, 20, &sx, &sy);
    world_of(&tile, 200, 200, &gx, &gy);

    ng_point points[64];
    int explored = 0;
    int count = ng_find_path(&map, sx, sy, 10.0f, gx, gy, 10.0f, points, 64, &explored);

    check("path found", count >= 2);
    check("starts at the player", count >= 1 && fabsf(points[0].x - sx) < 0.01f);
    check("ends at the goal", count >= 2 && fabsf(points[count - 1].x - gx) < 0.01f);
    check("smoothed to two points on open ground", count == 2);
    check("straight line is walkable", ng_line_walkable(&map, sx, sy, gx, gy) == 1);
    check("heights carried through", points[count - 1].z > 9.5f && points[count - 1].z < 10.5f);

    ng_map_free(&map);
}

static void test_wall_with_gap(void)
{
    printf("wall with a gap\n");
    static ng_tile tile;
    flat_tile(&tile, 32, 32, 4.0f);

    for (int row = 0; row < NG_CELLS; row++)
    {
        if (row >= 120 && row <= 124) continue;          /* the gap */
        ng_tile_set_flag(&tile, 100, row, NG_WALK, 0);
    }

    ng_map map;
    ng_map_init(&map, 32, 32, 1, 1, 0.0f, 0.0f);
    ng_map_put(&map, &tile);

    float sx, sy, gx, gy;
    world_of(&tile, 50, 60, &sx, &sy);
    world_of(&tile, 150, 60, &gx, &gy);

    check("straight line blocked by the wall", ng_line_walkable(&map, sx, sy, gx, gy) == 0);

    ng_point points[128];
    int count = ng_find_path(&map, sx, sy, 4.0f, gx, gy, 4.0f, points, 128, NULL);
    check("detour found", count >= 2);
    check("every segment walkable", all_segments_walkable(&map, points, count));

    float length = polyline_length(points, count);
    check("detour is longer than the straight line", length > 220.0f);

    /* Walk the polyline and record where it crosses the wall column: a smoothed
     * path emits far-apart points, so sampling the segments is the only way to
     * see whether it actually used the gap. */
    int crossing_row = -1;
    for (int i = 1; i < count && crossing_row < 0; i++)
    {
        float ax = points[i - 1].x, ay = points[i - 1].y;
        float bx = points[i].x, by = points[i].y;
        int acol, arow, bcol, brow;
        ng_map_world_to_global(&map, ax, ay, &acol, &arow);
        ng_map_world_to_global(&map, bx, by, &bcol, &brow);
        if (acol == bcol) continue;
        int wall_col = 100;
        if ((acol < wall_col) != (bcol < wall_col)) continue;
        float t = ((float)wall_col - (float)acol) / ((float)bcol - (float)acol);
        float row_at_wall = (float)arow + t * ((float)brow - (float)arow);
        crossing_row = (int)lroundf(row_at_wall);
    }
    check("path crosses the wall column", crossing_row >= 0);
    check("it crosses through the gap", crossing_row >= 118 && crossing_row <= 126);

    ng_map_free(&map);
}

static void test_enclosed_goal(void)
{
    printf("enclosed goal\n");
    static ng_tile tile;
    flat_tile(&tile, 32, 32, 0.0f);

    for (int row = 196; row <= 204; row++)
    {
        for (int col = 196; col <= 204; col++)
        {
            int edge = (row == 196 || row == 204 || col == 196 || col == 204);
            if (edge) ng_tile_set_flag(&tile, col, row, NG_WALK, 0);
        }
    }

    ng_map map;
    ng_map_init(&map, 32, 32, 1, 1, 0.0f, 0.0f);
    ng_map_put(&map, &tile);

    float sx, sy, gx, gy;
    world_of(&tile, 40, 40, &sx, &sy);
    world_of(&tile, 200, 200, &gx, &gy);

    ng_point points[64];
    int count = ng_find_path(&map, sx, sy, 0.0f, gx, gy, 0.0f, points, 64, NULL);
    check("no path into a sealed box", count == 0);

    ng_map_free(&map);
}

static void test_no_corner_cut(void)
{
    printf("corner cutting\n");
    static ng_tile tile;
    flat_tile(&tile, 32, 32, 0.0f);

    /* A diagonal pinch: both orthogonal neighbours of the diagonal are blocked. */
    ng_tile_set_flag(&tile, 151, 150, NG_WALK, 0);
    ng_tile_set_flag(&tile, 150, 151, NG_WALK, 0);

    ng_map map;
    ng_map_init(&map, 32, 32, 1, 1, 0.0f, 0.0f);
    ng_map_put(&map, &tile);

    float sx, sy, gx, gy;
    world_of(&tile, 150, 150, &sx, &sy);
    world_of(&tile, 151, 151, &gx, &gy);

    ng_point points[16];
    int count = ng_find_path(&map, sx, sy, 0.0f, gx, gy, 0.0f, points, 16, NULL);
    check("path exists around the pinch", count >= 2);
    if (count >= 2)
    {
        float length = polyline_length(points, count);
        check("did not squeeze through the diagonal", length > 3.0f);
        check("segments stay walkable", all_segments_walkable(&map, points, count));
    }

    ng_map_free(&map);
}

static void test_slope_marking(void)
{
    printf("slopes\n");
    static ng_tile gentle;
    flat_tile(&gentle, 32, 32, 0.0f);
    for (int row = 0; row < NG_CELLS; row++)
    {
        for (int col = 100; col < NG_CELLS; col++) ng_tile_set_height(&gentle, col, row, 0.4f);
    }
    ng_tile_mark_slopes(&gentle, 45.0f);
    check("a 0.4 yd step stays walkable", ng_tile_walkable(&gentle, 99, 50) == 1
                                       && ng_tile_walkable(&gentle, 100, 50) == 1);

    static ng_tile cliff;
    flat_tile(&cliff, 32, 32, 0.0f);
    for (int row = 0; row < NG_CELLS; row++)
    {
        for (int col = 100; col < NG_CELLS; col++) ng_tile_set_height(&cliff, col, row, 6.0f);
    }
    ng_tile_mark_slopes(&cliff, 45.0f);
    check("a 6 yd cliff edge is flagged steep", (cliff.flags[ng_tile_index(99, 50)] & NG_STEEP) != 0);
    check("the steeper side is not walkable", ng_tile_walkable(&cliff, 99, 50) == 0);
    check("the high ground itself is still walkable", ng_tile_walkable(&cliff, 150, 50) == 1);
}

static void test_file_round_trip(void)
{
    printf("file format\n");
    static ng_tile tile;
    flat_tile(&tile, 32, 33, 12.5f);
    ng_tile_set_flag(&tile, 10, 10, NG_WALK, 0);
    ng_tile_set_height(&tile, 10, 10, -3.25f);

    char path[] = "/tmp/ng_round_trip.navtile";
    check("write", ng_tile_write(&tile, path) == 1);

    static ng_tile loaded;
    check("read", ng_tile_read(&loaded, path) == 1);
    check("tile indices survive", loaded.tile_x == 32 && loaded.tile_y == 33);
    check("height survives", fabsf(ng_tile_height(&loaded, 10, 10) + 3.25f) < 0.01f);
    check("walkability survives", ng_tile_walkable(&loaded, 10, 10) == 0);
    check("origin survives", fabsf(loaded.origin_x - tile.origin_x) < 0.001f);

    /* Corrupt one height byte: the CRC must reject the file. */
    FILE *f = fopen(path, "r+b");
    check("file open for corruption", f != NULL);
    if (f != NULL)
    {
        fseek(f, 4096, SEEK_SET);
        fputc(0x7F, f);
        fclose(f);
        static ng_tile broken;
        check("corrupted tile rejected", ng_tile_read(&broken, path) == 0);
    }
    remove(path);
}

static void test_multi_tile_seam(void)
{
    printf("tile seams\n");
    static ng_tile east, west;
    flat_tile(&east, 32, 32, 3.0f);
    flat_tile(&west, 32, 33, 3.0f);

    ng_map map;
    ng_map_init(&map, 32, 32, 1, 2, (float)(32 - 32) * NG_ADT_YD, (float)(32 - 32) * NG_ADT_YD);
    ng_map_put(&map, &east);
    ng_map_put(&map, &west);

    float sx, sy, gx, gy;
    world_of(&east, 250, 100, &sx, &sy);
    world_of(&west, 20, 100, &gx, &gy);

    check("points are on different tiles",
          (sx > -NG_ADT_YD && sx <= 0.0f) && (gx > -2.0f * NG_ADT_YD && gx <= -NG_ADT_YD));

    ng_point points[32];
    int count = ng_find_path(&map, sx, sy, 3.0f, gx, gy, 3.0f, points, 32, NULL);
    check("path crosses the seam", count >= 2);
    check("seam segments walkable", all_segments_walkable(&map, points, count));

    ng_map_free(&map);
}

/* ---- synthetic ADT ---------------------------------------------------- */

static void put_chunk(uint8_t *dst, const char *fourcc, const void *body, uint32_t size)
{
    memcpy(dst, fourcc, 4);
    uint32_t value = size;
    memcpy(dst + 4, &value, 4);
    if (body != NULL && size > 0) memcpy(dst + 8, body, size);
}

static int write_synthetic_adt(const char *path, int chunks, float base_height)
{
    /* One MCNK per call site, laid out in a 2x2 block, all flat. */
    FILE *f = fopen(path, "wb");
    if (f == NULL) return 0;

    {
        uint32_t version = 18;
        uint8_t chunk[12];
        put_chunk(chunk, "MVER", &version, 4);
        fwrite(chunk, 1, 12, f);
    }
    {
        uint8_t header[32];
        memset(header, 0, sizeof(header));
        put_chunk(header, "MHDR", NULL, 24);
        fwrite(header, 1, 32, f);
    }

    for (int index = 0; index < chunks; index++)
    {
        uint8_t body[128 + 8 + 145 * 4];
        memset(body, 0, sizeof(body));

        int32_t index_x = index / 2;
        int32_t index_y = index % 2;
        memcpy(body + 4, &index_x, 4);
        memcpy(body + 8, &index_y, 4);
        uint16_t holes = 0;
        memcpy(body + 0x3C, &holes, 2);

        float heights[145];
        for (int i = 0; i < 145; i++) heights[i] = base_height + (float)index * 5.0f;
        put_chunk(body + 128, "MCVT", heights, sizeof(heights));

        uint32_t body_size = (uint32_t)sizeof(body);
        uint8_t header[8];
        put_chunk(header, "MCNK", NULL, body_size);
        fwrite(header, 1, 8, f);
        fwrite(body, 1, sizeof(body), f);
    }

    fclose(f);
    return 1;
}

static void test_adt_bake(void)
{
    printf("ADT bake\n");
    char path[] = "/tmp/ng_synthetic.adt";
    check("synthetic ADT written", write_synthetic_adt(path, 4, 10.0f) == 1);

    static ng_tile tile;
    adt_stats stats;
    int rc = ng_bake_adt(path, 1, 32, 32, 45.0f, &tile, &stats);
    check("bake succeeded", rc == 1);
    check("four chunks read", stats.chunks_read == 4);
    check("no chunks missing", stats.chunks_missing == 0);
    check("no holes", stats.holes == 0);

    /* Four chunks is 2x2 of a 16x16 ADT, so the walkable part is the baked
     * corner only - the rest of the tile has no MCNK and must stay unwalkable. */
    int walkable = 0;
    for (int i = 0; i < NG_CELL_COUNT; i++) if (tile.flags[i] & NG_WALK) walkable++;
    check("the four baked chunks are walkable", walkable > 250);
    check("unbaked cells stay unwalkable", ng_tile_walkable(&tile, 200, 200) == 0);

    /* Chunk (0,0) is flat at 10, chunk (0,1) at 15, chunk (1,0) at 20, (1,1) at 25. */
    int col = 0, row = 0;
    float wx, wy;
    world_of(&tile, 8, 8, &wx, &wy);
    check("north-west chunk height", ng_tile_world_to_cell(&tile, wx, wy, &col, &row)
          && fabsf(ng_tile_height(&tile, col, row) - 10.0f) < 0.5f);

    /* A chunk is 33.3 yd, so cell 25 of 267 is in chunk (1,1). */
    world_of(&tile, 25, 25, &wx, &wy);
    ng_tile_world_to_cell(&tile, wx, wy, &col, &row);
    check("chunk (1,1) height", fabsf(ng_tile_height(&tile, col, row) - 25.0f) < 0.5f);

    /* cell (col,row) sits in chunk (IndexX=row/16.67, IndexY=col/16.67) */
    world_of(&tile, 25, 8, &wx, &wy);
    ng_tile_world_to_cell(&tile, wx, wy, &col, &row);
    check("chunk IndexY=1 height (15)", fabsf(ng_tile_height(&tile, col, row) - 15.0f) < 0.5f);

    world_of(&tile, 8, 25, &wx, &wy);
    ng_tile_world_to_cell(&tile, wx, wy, &col, &row);
    check("chunk IndexX=1 height (20)", fabsf(ng_tile_height(&tile, col, row) - 20.0f) < 0.5f);

    check("height range reported", stats.min_height < 11.0f && stats.max_height > 24.0f);

    /* A file without MCNK must be refused, not silently baked as flat ground. */
    char garbage[] = "/tmp/ng_garbage.adt";
    FILE *f = fopen(garbage, "wb");
    if (f != NULL)
    {
        uint32_t version = 18;
        uint8_t chunk[12];
        put_chunk(chunk, "MVER", &version, 4);
        fwrite(chunk, 1, 12, f);
        fclose(f);
    }
    static ng_tile nothing;
    check("MCNK-less file refused", ng_bake_adt(garbage, 1, 32, 32, 45.0f, &nothing, NULL) == 0);

    remove(path);
    remove(garbage);
}

int main(void)
{
    printf("navgrid self-test\n");
    test_open_ground();
    test_wall_with_gap();
    test_enclosed_goal();
    test_no_corner_cut();
    test_slope_marking();
    test_file_round_trip();
    test_multi_tile_seam();
    test_adt_bake();

    printf("\n%d checks, %d failed\n", checks, failures);
    return failures == 0 ? 0 : 1;
}

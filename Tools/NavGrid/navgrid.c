#include "navgrid.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

/* ---------------------------------------------------------------------------
 * Coordinate convention
 *
 * WoW map files are addressed by ADT index (tx, ty). One ADT covers 533.3333 yd.
 * The client's own mapping (TrinityCore's Map2GridCoord) is
 *     tx = floor(32 - y / 533.3333)
 *     ty = floor(32 - x / 533.3333)
 * so tile (tx, ty) spans
 *     x in [ (32 - ty - 1) * SIZE , (32 - ty) * SIZE )
 *     y in [ (32 - tx - 1) * SIZE , (32 - tx) * SIZE )
 * and cell (0,0) of a tile is its north-west corner.
 *
 * That mapping is easy to get backwards, and a flipped axis produces a path that
 * looks plausible and walks into a hillside. `navbake probe x,y,z` therefore
 * reports the tile height and walkability at a real position: stand somewhere
 * with a known slope and compare. If heights are off by tens of yards, the
 * convention is wrong for that client and this function is the single place to
 * fix.
 * ------------------------------------------------------------------------- */

static void ng_warn(const char *message)
{
    fprintf(stderr, "navgrid: %s\n", message);
}

/* ---- tile ------------------------------------------------------------- */

int ng_tile_init(ng_tile *t, int32_t map_id, int32_t tx, int32_t ty)
{
    if (t == NULL) return 0;
    memset(t, 0, sizeof(*t));
    t->map_id = map_id;
    t->tile_x = tx;
    t->tile_y = ty;
    t->cells = NG_CELLS;
    t->height_q = NG_HEIGHT_Q;
    /* centre of the north-west cell */
    t->origin_x = (float)(32 - ty) * NG_ADT_YD - NG_CELL_YD * 0.5f;
    t->origin_y = (float)(32 - tx) * NG_ADT_YD - NG_CELL_YD * 0.5f;
    return 1;
}

void ng_tile_free(ng_tile *t)
{
    if (t != NULL) free(t);
}

int ng_tile_walkable(const ng_tile *t, int col, int row)
{
    if (t == NULL || col < 0 || row < 0 || col >= t->cells || row >= t->cells) return 0;
    return (t->flags[ng_tile_index(col, row)] & NG_WALK) != 0;
}

void ng_tile_set_flag(ng_tile *t, int col, int row, uint8_t flag, int on)
{
    if (t == NULL || col < 0 || row < 0 || col >= t->cells || row >= t->cells) return;
    int index = ng_tile_index(col, row);
    if (on) t->flags[index] |= flag;
    else t->flags[index] &= (uint8_t)~flag;
}

float ng_tile_height(const ng_tile *t, int col, int row)
{
    if (t == NULL || col < 0 || row < 0 || col >= t->cells || row >= t->cells) return 0.0f;
    return (float)t->heights[ng_tile_index(col, row)] / (float)t->height_q;
}

void ng_tile_set_height(ng_tile *t, int col, int row, float yards)
{
    if (t == NULL || col < 0 || row < 0 || col >= t->cells || row >= t->cells) return;
    float value = yards * (float)t->height_q;
    if (value > 32000.0f) value = 32000.0f;
    if (value < -32000.0f) value = -32000.0f;
    t->heights[ng_tile_index(col, row)] = (int16_t)lroundf(value);
}

int ng_tile_world_to_cell(const ng_tile *t, float x, float y, int *col, int *row)
{
    if (t == NULL) return 0;
    float fx = (t->origin_x - x) / NG_CELL_YD;
    float fy = (t->origin_y - y) / NG_CELL_YD;
    if (fx < -0.5f || fy < -0.5f || fx > (float)(t->cells - 0.5f) || fy > (float)(t->cells - 0.5f)) return 0;
    int c = (int)floorf(fx + 0.5f);
    int r = (int)floorf(fy + 0.5f);
    if (c < 0) c = 0;
    if (r < 0) r = 0;
    if (c >= t->cells) c = t->cells - 1;
    if (r >= t->cells) r = t->cells - 1;
    *col = c;
    *row = r;
    return 1;
}

void ng_tile_cell_to_world(const ng_tile *t, int col, int row, float *x, float *y)
{
    if (t == NULL) return;
    if (x != NULL) *x = t->origin_x - (float)col * NG_CELL_YD;
    if (y != NULL) *y = t->origin_y - (float)row * NG_CELL_YD;
}

void ng_tile_mark_slopes(ng_tile *t, float max_slope_deg)
{
    if (t == NULL) return;

    /* Two passes: flag first, then take the cell out of the walkable set. Deciding
     * to clear the walk bit while still scanning would make the result depend on
     * scan order, because neighbours would see half-updated state. */
    float max_rise = tanf(max_slope_deg * (float)M_PI / 180.0f) * NG_CELL_YD;
    static const int dc[4] = { 1, -1, 0, 0 };
    static const int dr[4] = { 0, 0, 1, -1 };

    for (int row = 0; row < t->cells; row++)
    {
        for (int col = 0; col < t->cells; col++)
        {
            if (!ng_tile_walkable(t, col, row)) continue;

            float here = ng_tile_height(t, col, row);
            int steep = 0;
            for (int k = 0; k < 4 && !steep; k++)
            {
                int nc = col + dc[k];
                int nr = row + dr[k];
                if (nc < 0 || nr < 0 || nc >= t->cells || nr >= t->cells) continue;
                if (!ng_tile_walkable(t, nc, nr)) continue;
                if (fabsf(ng_tile_height(t, nc, nr) - here) > max_rise) steep = 1;
            }
            if (steep) ng_tile_set_flag(t, col, row, NG_STEEP, 1);
        }
    }

    for (int row = 0; row < t->cells; row++)
    {
        for (int col = 0; col < t->cells; col++)
        {
            int index = ng_tile_index(col, row);
            if (t->flags[index] & NG_STEEP) t->flags[index] &= (uint8_t)~NG_WALK;
        }
    }
}

/* ---- map -------------------------------------------------------------- */

int ng_map_init(ng_map *m, int32_t base_tx, int32_t base_ty, int32_t rows, int32_t cols,
                float origin_x, float origin_y)
{
    if (m == NULL || rows <= 0 || cols <= 0) return 0;
    long long total = (long long)rows * cols * NG_CELL_COUNT;
    if (total > NG_MAX_CELLS) return 0;

    memset(m, 0, sizeof(*m));
    m->base_tx = base_tx;
    m->base_ty = base_ty;
    m->tile_rows = rows;
    m->tile_cols = cols;
    m->origin_x = origin_x;
    m->origin_y = origin_y;
    m->tiles = (ng_tile **)calloc((size_t)(rows * cols), sizeof(ng_tile *));
    return m->tiles != NULL;
}

void ng_map_free(ng_map *m)
{
    if (m == NULL) return;
    free(m->tiles);
    m->tiles = NULL;
}

void ng_map_put(ng_map *m, ng_tile *t)
{
    if (m == NULL || t == NULL || m->tiles == NULL) return;
    int row = t->tile_x - m->base_tx;
    int col = t->tile_y - m->base_ty;
    if (row < 0 || col < 0 || row >= m->tile_rows || col >= m->tile_cols) return;
    m->tiles[row * m->tile_cols + col] = t;
}

int ng_map_global_size(const ng_map *m)
{
    if (m == NULL) return 0;
    return m->tile_rows * m->tile_cols * NG_CELL_COUNT;
}

int ng_map_world_to_global(const ng_map *m, float x, float y, int *gcol, int *grow)
{
    if (m == NULL) return 0;
    float fx = (m->origin_x - x) / NG_CELL_YD - 0.5f;
    float fy = (m->origin_y - y) / NG_CELL_YD - 0.5f;
    int max_cols = m->tile_cols * NG_CELLS;
    int max_rows = m->tile_rows * NG_CELLS;
    if (fx < -1.0f || fy < -1.0f || fx > (float)max_cols || fy > (float)max_rows) return 0;
    int c = (int)floorf(fx + 0.5f);
    int r = (int)floorf(fy + 0.5f);
    if (c < 0) c = 0;
    if (r < 0) r = 0;
    if (c >= max_cols) c = max_cols - 1;
    if (r >= max_rows) r = max_rows - 1;
    *gcol = c;
    *grow = r;
    return 1;
}

void ng_map_global_to_world(const ng_map *m, int gcol, int grow, float *x, float *y)
{
    if (m == NULL) return;
    if (x != NULL) *x = m->origin_x - (float)gcol * NG_CELL_YD - NG_CELL_YD * 0.5f;
    if (y != NULL) *y = m->origin_y - (float)grow * NG_CELL_YD - NG_CELL_YD * 0.5f;
}

ng_tile *ng_map_tile_at(const ng_map *m, int gcol, int grow, int *local_col, int *local_row)
{
    if (m == NULL || m->tiles == NULL) return NULL;
    if (gcol < 0 || grow < 0) return NULL;
    int tile_col = gcol / NG_CELLS;
    int tile_row = grow / NG_CELLS;
    if (tile_col >= m->tile_cols || tile_row >= m->tile_rows) return NULL;
    ng_tile *t = m->tiles[tile_row * m->tile_cols + tile_col];
    if (t == NULL) return NULL;
    if (local_col != NULL) *local_col = gcol - tile_col * NG_CELLS;
    if (local_row != NULL) *local_row = grow - tile_row * NG_CELLS;
    return t;
}

/* ---- file format ------------------------------------------------------ */

typedef struct
{
    char     magic[4];
    uint16_t version;
    uint16_t header_bytes;
    int32_t  map_id;
    int32_t  tile_x;
    int32_t  tile_y;
    int16_t  cells;
    int16_t  height_q;
    float    origin_x;
    float    origin_y;
    int32_t  height_bytes;
    int32_t  flag_bytes;
    int32_t  crc32;
} ng_file_header;

static uint32_t ng_crc32(const void *data, size_t length)
{
    uint32_t crc = 0xFFFFFFFFu;
    const uint8_t *p = (const uint8_t *)data;
    for (size_t i = 0; i < length; i++)
    {
        crc ^= p[i];
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ (0xEDB88320u & (uint32_t)(-(int32_t)(crc & 1u)));
    }
    return ~crc;
}

int ng_tile_write(const ng_tile *t, const char *path)
{
    if (t == NULL || path == NULL) return 0;

    ng_file_header header;
    memset(&header, 0, sizeof(header));
    memcpy(header.magic, NG_MAGIC, 4);
    header.version = NG_VERSION;
    header.header_bytes = (uint16_t)sizeof(header);
    header.map_id = t->map_id;
    header.tile_x = t->tile_x;
    header.tile_y = t->tile_y;
    header.cells = t->cells;
    header.height_q = t->height_q;
    header.origin_x = t->origin_x;
    header.origin_y = t->origin_y;
    header.height_bytes = (int32_t)sizeof(t->heights);
    header.flag_bytes = (int32_t)sizeof(t->flags);

    header.crc32 = 0;
    uint32_t crc = ng_crc32(&header, sizeof(header));
    crc = ng_crc32(t->heights, sizeof(t->heights)) ^ crc;
    crc = ng_crc32(t->flags, sizeof(t->flags)) ^ crc;
    header.crc32 = (int32_t)crc;

    FILE *f = fopen(path, "wb");
    if (f == NULL)
    {
        ng_warn("cannot open tile for writing");
        return 0;
    }
    int ok = fwrite(&header, sizeof(header), 1, f) == 1
          && fwrite(t->heights, sizeof(t->heights), 1, f) == 1
          && fwrite(t->flags, sizeof(t->flags), 1, f) == 1;
    fclose(f);
    if (!ok) ng_warn("short write on tile");
    return ok;
}

int ng_tile_read(ng_tile *t, const char *path)
{
    if (t == NULL || path == NULL) return 0;
    FILE *f = fopen(path, "rb");
    if (f == NULL) return 0;

    ng_file_header header;
    if (fread(&header, sizeof(header), 1, f) != 1) { fclose(f); return 0; }
    if (memcmp(header.magic, NG_MAGIC, 4) != 0 || header.version != NG_VERSION ||
        header.cells != NG_CELLS || header.height_bytes != (int32_t)sizeof(t->heights))
    {
        fclose(f);
        ng_warn("tile has an unexpected header");
        return 0;
    }

    int32_t stored_crc = header.crc32;
    header.crc32 = 0;
    uint32_t crc = ng_crc32(&header, sizeof(header));

    if (fread(t->heights, sizeof(t->heights), 1, f) != 1 ||
        fread(t->flags, sizeof(t->flags), 1, f) != 1)
    {
        fclose(f);
        ng_warn("truncated tile");
        return 0;
    }
    fclose(f);

    crc = ng_crc32(t->heights, sizeof(t->heights)) ^ crc;
    crc = ng_crc32(t->flags, sizeof(t->flags)) ^ crc;
    if ((int32_t)crc != stored_crc)
    {
        ng_warn("tile CRC mismatch");
        return 0;
    }

    t->map_id = header.map_id;
    t->tile_x = header.tile_x;
    t->tile_y = header.tile_y;
    t->cells = header.cells;
    t->height_q = header.height_q;
    t->origin_x = header.origin_x;
    t->origin_y = header.origin_y;
    return 1;
}

/* ---- A* --------------------------------------------------------------- */

typedef struct
{
    float g;
    float f;
    int32_t parent;
    uint8_t state;          /* 0 new, 1 open, 2 closed */
} ng_node;

typedef struct
{
    int *items;
    float *scores;
    int count;
    int capacity;
} ng_heap;

static int ng_heap_push(ng_heap *h, int item, float score)
{
    if (h->count >= h->capacity) return 0;
    int i = h->count++;
    h->items[i] = item;
    h->scores[i] = score;
    while (i > 0)
    {
        int parent = (i - 1) / 2;
        if (h->scores[parent] <= h->scores[i]) break;
        int ti = h->items[parent]; h->items[parent] = h->items[i]; h->items[i] = ti;
        float ts = h->scores[parent]; h->scores[parent] = h->scores[i]; h->scores[i] = ts;
        i = parent;
    }
    return 1;
}

static int ng_heap_pop(ng_heap *h, float *score)
{
    if (h->count <= 0) return -1;
    int top = h->items[0];
    *score = h->scores[0];
    h->count--;
    if (h->count > 0)
    {
        h->items[0] = h->items[h->count];
        h->scores[0] = h->scores[h->count];
        int i = 0;
        for (;;)
        {
            int left = i * 2 + 1, right = left + 1, smallest = i;
            if (left < h->count && h->scores[left] < h->scores[smallest]) smallest = left;
            if (right < h->count && h->scores[right] < h->scores[smallest]) smallest = right;
            if (smallest == i) break;
            int ti = h->items[smallest]; h->items[smallest] = h->items[i]; h->items[i] = ti;
            float ts = h->scores[smallest]; h->scores[smallest] = h->scores[i]; h->scores[i] = ts;
            i = smallest;
        }
    }
    return top;
}

/* Cell walkability and height for a global index, following tile boundaries. */
static int ng_global_cell(const ng_map *m, int gcol, int grow, float *height)
{
    int local_col, local_row;
    ng_tile *t = ng_map_tile_at(m, gcol, grow, &local_col, &local_row);
    if (t == NULL) return 0;
    if (!ng_tile_walkable(t, local_col, local_row)) return 0;
    if (height != NULL) *height = ng_tile_height(t, local_col, local_row);
    return 1;
}

static int ng_step_ok(const ng_map *m, int ncol, int nrow, float from_height, float *height_out)
{
    float to_height = 0.0f;
    if (!ng_global_cell(m, ncol, nrow, &to_height)) return 0;
    /* A 2 yd cell with a 1.5 yd rise is a wall or a cliff edge, not a step. */
    if (fabsf(to_height - from_height) > 1.5f) return 0;
    if (height_out != NULL) *height_out = to_height;
    return 1;
}

int ng_line_walkable(const ng_map *m, float x0, float y0, float x1, float y1)
{
    int c0, r0, c1, r1;
    if (!ng_map_world_to_global(m, x0, y0, &c0, &r0)) return 0;
    if (!ng_map_world_to_global(m, x1, y1, &c1, &r1)) return 0;

    int dx = abs(c1 - c0), dy = abs(r1 - r0);
    int sx = c0 < c1 ? 1 : -1, sy = r0 < r1 ? 1 : -1;
    int err = dx - dy;
    float height = 0.0f;
    if (!ng_global_cell(m, c0, r0, &height)) return 0;

    int guard = 0;
    int limit = (abs(c1 - c0) + abs(r1 - r0)) * 4 + 16;
    int c = c0, r = r0;
    while (guard++ < limit)
    {
        if (c == c1 && r == r1) return 1;
        int e2 = err * 2;
        int nc = c, nr = r;
        if (e2 > -dy) { err -= dy; nc += sx; }
        if (e2 < dx) { err += dx; nr += sy; }

        /* A diagonal step may not slip between two blocked cells - that is the
         * same rule A* applies, and the reason a grid line-of-sight check cannot
         * simply walk the Bresenham cells. */
        if (nc != c && nr != r)
        {
            float ignored = 0.0f;
            if (!ng_global_cell(m, nc, r, &ignored)) return 0;
            if (!ng_global_cell(m, c, nr, &ignored)) return 0;
        }

        if (!ng_step_ok(m, nc, nr, height, NULL)) return 0;
        c = nc;
        r = nr;
        ng_global_cell(m, c, r, &height);
    }
    return 0;
}

static int ng_points_push(ng_point *out, int max_out, int *count, float x, float y, float z)
{
    if (*count > 0)
    {
        float dx = out[*count - 1].x - x;
        float dy = out[*count - 1].y - y;
        if (dx * dx + dy * dy < 0.04f) return 1;     /* 0.2 yd */
    }
    if (*count >= max_out) return 0;
    out[*count].x = x;
    out[*count].y = y;
    out[*count].z = z;
    (*count)++;
    return 1;
}

int ng_find_path(const ng_map *m, float sx, float sy, float sz,
                 float gx, float gy, float gz,
                 ng_point *out, int max_out, int *cells_explored)
{
    if (cells_explored != NULL) *cells_explored = 0;
    if (m == NULL || out == NULL || max_out < 2) return -1;

    int total = ng_map_global_size(m);
    if (total <= 0 || total > NG_MAX_CELLS) return -1;

    int start_col, start_row, goal_col, goal_row;
    if (!ng_map_world_to_global(m, sx, sy, &start_col, &start_row)) return 0;
    if (!ng_map_world_to_global(m, gx, gy, &goal_col, &goal_row)) return 0;

    float start_height = 0.0f, goal_height = 0.0f;
    if (!ng_global_cell(m, start_col, start_row, &start_height))
    {
        /* The player can stand somewhere the bake called unwalkable (a bridge, a
         * doorway, a cell the slope test dislikes). Search outwards for the
         * nearest walkable cell instead of refusing the whole path. */
        int found = 0;
        for (int radius = 1; radius <= 4 && !found; radius++)
        {
            for (int dr = -radius; dr <= radius && !found; dr++)
            {
                for (int dc = -radius; dc <= radius && !found; dc++)
                {
                    if (ng_global_cell(m, start_col + dc, start_row + dr, &start_height))
                    {
                        start_col += dc;
                        start_row += dr;
                        found = 1;
                    }
                }
            }
        }
        if (!found) return 0;
    }
    (void)start_height;

    if (!ng_global_cell(m, goal_col, goal_row, &goal_height))
    {
        int found = 0;
        for (int radius = 1; radius <= 6 && !found; radius++)
        {
            for (int dr = -radius; dr <= radius && !found; dr++)
            {
                for (int dc = -radius; dc <= radius && !found; dc++)
                {
                    if (ng_global_cell(m, goal_col + dc, goal_row + dr, &goal_height))
                    {
                        goal_col += dc;
                        goal_row += dr;
                        found = 1;
                    }
                }
            }
        }
        if (!found) return 0;
    }
    (void)goal_height;

    int cols = m->tile_cols * NG_CELLS;
    int start = start_row * cols + start_col;
    int goal = goal_row * cols + goal_col;

    ng_node *nodes = (ng_node *)calloc((size_t)total, sizeof(ng_node));
    if (nodes == NULL) return -1;

    ng_heap heap;
    heap.capacity = total;
    heap.count = 0;
    heap.items = (int *)malloc(sizeof(int) * (size_t)total);
    heap.scores = (float *)malloc(sizeof(float) * (size_t)total);
    if (heap.items == NULL || heap.scores == NULL)
    {
        free(nodes);
        free(heap.items);
        free(heap.scores);
        return -1;
    }

    const float diag = 1.41421356f;
    nodes[start].g = 0.0f;
    nodes[start].f = 0.0f;
    nodes[start].parent = -1;
    nodes[start].state = 1;
    ng_heap_push(&heap, start, 0.0f);

    int found = 0;
    int explored = 0;

    while (heap.count > 0)
    {
        float score;
        int current = ng_heap_pop(&heap, &score);
        if (current < 0) break;
        if (nodes[current].state == 2) continue;
        nodes[current].state = 2;
        explored++;
        if (current == goal) { found = 1; break; }

        int ccol = current % cols;
        int crow = current / cols;
        float cheight = 0.0f;
        ng_global_cell(m, ccol, crow, &cheight);

        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dc == 0 && dr == 0) continue;
                int ncol = ccol + dc;
                int nrow = crow + dr;
                if (ncol < 0 || nrow < 0 || ncol >= cols || nrow >= m->tile_rows * NG_CELLS) continue;

                float nheight = 0.0f;
                if (!ng_step_ok(m, ncol, nrow, cheight, &nheight)) continue;

                if (dc != 0 && dr != 0)
                {
                    /* No cutting a corner between two blocked cells: that is how
                     * grid pathfinders walk through doorframes. */
                    float ignored;
                    if (!ng_global_cell(m, ccol + dc, crow, &ignored)) continue;
                    if (!ng_global_cell(m, ccol, crow + dr, &ignored)) continue;
                }

                int neighbour = nrow * cols + ncol;
                if (nodes[neighbour].state == 2) continue;

                float step = (dc != 0 && dr != 0) ? diag : 1.0f;
                /* Prefer flat ground: a 1 yd rise over a 2 yd cell costs more. */
                float rise = fabsf(nheight - cheight);
                step += (rise > 0.5f) ? (rise - 0.5f) * 2.0f : 0.0f;
                float candidate = nodes[current].g + step;

                if (nodes[neighbour].state == 1 && candidate >= nodes[neighbour].g) continue;

                nodes[neighbour].parent = current;
                nodes[neighbour].g = candidate;
                int dxg = abs(goal_col - ncol), dyg = abs(goal_row - nrow);
                int mind = dxg < dyg ? dxg : dyg;
                int maxd = dxg < dyg ? dyg : dxg;
                nodes[neighbour].f = candidate + (float)(maxd - mind) + diag * (float)mind;
                nodes[neighbour].state = 1;
                if (!ng_heap_push(&heap, neighbour, nodes[neighbour].f)) { /* heap full */ }
            }
        }
    }

    if (cells_explored != NULL) *cells_explored = explored;

    int written = 0;
    if (found)
    {
        /* Walk the parents back, then emit forward with string pulling. */
        int length = 0;
        for (int node = goal; node >= 0; node = nodes[node].parent) length++;
        int *chain = (int *)malloc(sizeof(int) * (size_t)length);
        if (chain == NULL)
        {
            free(nodes); free(heap.items); free(heap.scores);
            return -1;
        }
        int index = length - 1;
        for (int node = goal; node >= 0; node = nodes[node].parent) chain[index--] = node;

        int anchor = 0;
        ng_points_push(out, max_out, &written, sx, sy, sz);
        while (anchor < length - 1)
        {
            int best = anchor + 1;
            for (int probe = length - 1; probe > anchor; probe--)
            {
                int acol = chain[anchor] % cols, arow = chain[anchor] / cols;
                int pcol = chain[probe] % cols, prow = chain[probe] / cols;
                float ax, ay, px, py;
                ng_map_global_to_world(m, acol, arow, &ax, &ay);
                ng_map_global_to_world(m, pcol, prow, &px, &py);
                if (ng_line_walkable(m, ax, ay, px, py)) { best = probe; break; }
            }

            int bcol = chain[best] % cols, brow = chain[best] / cols;
            float bx, by, bz = 0.0f;
            ng_map_global_to_world(m, bcol, brow, &bx, &by);
            ng_global_cell(m, bcol, brow, &bz);
            if (best == length - 1)
            {
                ng_points_push(out, max_out, &written, gx, gy, gz);
                anchor = best;
            }
            else
            {
                if (!ng_points_push(out, max_out, &written, bx, by, bz))
                {
                    free(chain); free(nodes); free(heap.items); free(heap.scores);
                    return written;
                }
                anchor = best;
            }
        }
        free(chain);
    }

    free(nodes);
    free(heap.items);
    free(heap.scores);
    return written;
}

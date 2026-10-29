#include "adt.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define ADT_CHUNK_YD   33.333328f
#define ADT_CHUNKS     16                 /* 16 x 16 MCNK per ADT */
#define ADT_MCVT_FLOATS 145
#define ADT_MCNK_HEADER 128

static uint32_t rd32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t rd16(const uint8_t *p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static float rdf(const uint8_t *p)
{
    float f = 0.0f;
    memcpy(&f, p, sizeof(f));
    return f;
}

static int fourcc_is(const uint8_t *p, const char *name)
{
    return p[0] == (uint8_t)name[0] && p[1] == (uint8_t)name[1]
        && p[2] == (uint8_t)name[2] && p[3] == (uint8_t)name[3];
}

void ng_adt_tile_of(float x, float y, int32_t *tile_x, int32_t *tile_y)
{
    /* Truncation toward zero, not floorf: the client's own Map2GridCoord is
     * 32 - (int)(coord / 533.3333), and for negative coordinates floorf would
     * shift every western tile by one. */
    if (tile_x != NULL) *tile_x = 32 - (int32_t)(y / NG_ADT_YD);
    if (tile_y != NULL) *tile_y = 32 - (int32_t)(x / NG_ADT_YD);
}

/* 9x9 heights of one chunk, copied out of the interleaved 17-row MCVT layout. */
typedef struct
{
    float h[9][9];
} mcnk_grid;

static int read_mcvt(const uint8_t *body, uint32_t body_size, mcnk_grid *grid)
{
    if (body_size <= ADT_MCNK_HEADER) return 0;

    uint32_t offset = ADT_MCNK_HEADER;
    while (offset + 8 <= body_size)
    {
        const uint8_t *sub = body + offset;
        uint32_t size = rd32(sub + 4);
        if (size > body_size || offset + 8 + size > body_size) return 0;

        if (fourcc_is(sub, "MCVT"))
        {
            if (size < ADT_MCVT_FLOATS * 4) return 0;
            const uint8_t *values = sub + 8;
            static const int row_offset[9] = { 0, 17, 34, 51, 68, 85, 102, 119, 136 };
            for (int i = 0; i < 9; i++)
            {
                for (int j = 0; j < 9; j++)
                {
                    float value = rdf(values + (size_t)(row_offset[i] + j) * 4);
                    if (!isfinite(value)) return 0;
                    grid->h[i][j] = value;
                }
            }
            return 1;
        }

        offset += 8 + size;
    }
    return 0;
}

static float sample_grid(const mcnk_grid *grid, float v, float u)
{
    /* v, u in [0,1]: v north->south, u west->east. */
    float fi = v * 8.0f;
    float fj = u * 8.0f;
    int i0 = (int)floorf(fi);
    int j0 = (int)floorf(fj);
    if (i0 < 0) i0 = 0;
    if (j0 < 0) j0 = 0;
    if (i0 > 7) i0 = 7;
    if (j0 > 7) j0 = 7;
    float ti = fi - (float)i0;
    float tj = fj - (float)j0;
    if (ti < 0.0f) ti = 0.0f;
    if (tj < 0.0f) tj = 0.0f;
    if (ti > 1.0f) ti = 1.0f;
    if (tj > 1.0f) tj = 1.0f;

    float a = grid->h[i0][j0] * (1.0f - tj) + grid->h[i0][j0 + 1] * tj;
    float b = grid->h[i0 + 1][j0] * (1.0f - tj) + grid->h[i0 + 1][j0 + 1] * tj;
    return a * (1.0f - ti) + b * ti;
}

int ng_bake_adt(const char *path, int32_t map_id, int32_t tile_x, int32_t tile_y,
                float max_slope_deg, ng_tile *out, adt_stats *stats)
{
    if (path == NULL || out == NULL) return -1;

    FILE *f = fopen(path, "rb");
    if (f == NULL)
    {
        fprintf(stderr, "navgrid: cannot open %s\n", path);
        return -1;
    }
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return -1; }
    long size = ftell(f);
    if (size <= 0 || size > 64 * 1024 * 1024) { fclose(f); return -1; }
    rewind(f);

    uint8_t *data = (uint8_t *)malloc((size_t)size);
    if (data == NULL) { fclose(f); return -1; }
    if (fread(data, 1, (size_t)size, f) != (size_t)size) { free(data); fclose(f); return -1; }
    fclose(f);

    ng_tile_init(out, map_id, tile_x, tile_y);
    memset(out->flags, 0, sizeof(out->flags));

    if (stats != NULL) memset(stats, 0, sizeof(*stats));
    if (stats != NULL)
    {
        stats->min_height = 1e9f;
        stats->max_height = -1e9f;
    }

    uint32_t offset = 0;
    int chunks = 0;

    while (offset + 8 <= (uint32_t)size)
    {
        const uint8_t *chunk = data + offset;
        uint32_t chunk_size = rd32(chunk + 4);
        if (chunk_size > (uint32_t)size || offset + 8 + chunk_size > (uint32_t)size) break;

        if (fourcc_is(chunk, "MCNK"))
        {
            const uint8_t *body = chunk + 8;
            int32_t index_x = (int32_t)rd32(body + 4);      /* row, north -> south */
            int32_t index_y = (int32_t)rd32(body + 8);      /* column, west -> east */
            uint16_t holes = rd16(body + 0x3C);

            if (index_x >= 0 && index_y >= 0 && index_x < ADT_CHUNKS && index_y < ADT_CHUNKS)
            {
                mcnk_grid grid;
                if (read_mcvt(body, chunk_size, &grid))
                {
                    chunks++;
                    if (stats != NULL) stats->chunks_read++;

                    /* Cell range covered by this chunk, in tile cell coordinates. */
                    float dy0 = (float)index_x * ADT_CHUNK_YD;
                    float dx0 = (float)index_y * ADT_CHUNK_YD;

                    int row_min = (int)floorf(dy0 / NG_CELL_YD) - 1;
                    int row_max = (int)ceilf((dy0 + ADT_CHUNK_YD) / NG_CELL_YD) + 1;
                    int col_min = (int)floorf(dx0 / NG_CELL_YD) - 1;
                    int col_max = (int)ceilf((dx0 + ADT_CHUNK_YD) / NG_CELL_YD) + 1;
                    if (row_min < 0) row_min = 0;
                    if (col_min < 0) col_min = 0;
                    if (row_max > out->cells) row_max = out->cells;
                    if (col_max > out->cells) col_max = out->cells;

                    for (int row = row_min; row < row_max; row++)
                    {
                        float v = ((float)row + 0.5f) * NG_CELL_YD - dy0;
                        if (v < 0.0f || v >= ADT_CHUNK_YD) continue;
                        for (int col = col_min; col < col_max; col++)
                        {
                            float u = ((float)col + 0.5f) * NG_CELL_YD - dx0;
                            if (u < 0.0f || u >= ADT_CHUNK_YD) continue;

                            float height = sample_grid(&grid, v / ADT_CHUNK_YD, u / ADT_CHUNK_YD);
                            ng_tile_set_height(out, col, row, height);
                            ng_tile_set_flag(out, col, row, NG_WALK, 1);

                            if (stats != NULL)
                            {
                                if (height < stats->min_height) stats->min_height = height;
                                if (height > stats->max_height) stats->max_height = height;
                            }

                            if (holes != 0)
                            {
                                /* The hole mask is a 4x4 grid of 8.33 yd squares. */
                                int sub_row = (int)((v / ADT_CHUNK_YD) * 4.0f);
                                int sub_col = (int)((u / ADT_CHUNK_YD) * 4.0f);
                                if (sub_row > 3) sub_row = 3;
                                if (sub_col > 3) sub_col = 3;
                                int bit = sub_row * 4 + sub_col;
                                if ((holes & (1u << bit)) != 0)
                                {
                                    ng_tile_set_flag(out, col, row, NG_HOLE, 1);
                                    ng_tile_set_flag(out, col, row, NG_WALK, 0);
                                    if (stats != NULL) stats->holes++;
                                }
                            }
                        }
                    }
                }
                else if (stats != NULL)
                {
                    stats->chunks_missing++;
                }
            }
        }

        offset += 8 + chunk_size;
    }

    free(data);

    if (chunks == 0) return 0;
    ng_tile_mark_slopes(out, max_slope_deg);
    return 1;
}

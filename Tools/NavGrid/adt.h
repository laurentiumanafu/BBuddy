/*
 * Root-ADT terrain reader.
 *
 * It reads only what a walkability grid needs: the per-chunk height field (MCVT),
 * the hole mask (a chunk hole means "no terrain here", so not walkable), and it
 * ignores textures, normals, shadows and doodads.
 *
 * Works on the root .adt of both layouts in use today:
 *   * 3.3.5a single-file ADT (extracted from MPQ),
 *   * Legion+ split ADTs (the root _x_y.adt still carries MCNK/MCVT; the _tex0 and
 *     _obj0 siblings do not matter here).
 *
 * Chunks are located by fourcc and validated by size, so an unexpected extra
 * header field does not silently shift every height.
 */
#ifndef ADT_H
#define ADT_H

#include <stddef.h>
#include <stdint.h>
#include "navgrid.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct
{
    int chunks_read;
    int chunks_missing;
    int holes;
    float min_height;
    float max_height;
} adt_stats;

/* Parses one ADT and fills a tile's heights and walkability.
 * Returns 1 on success, 0 when no MCNK was found, <0 on a malformed file. */
int ng_bake_adt(const char *path, int32_t map_id, int32_t tile_x, int32_t tile_y,
                float max_slope_deg, ng_tile *out, adt_stats *stats);

/* Same, on an ADT already in memory (the CASC extractor decodes a payload and
 * has no file to hand over). */
int ng_bake_adt_buffer(const uint8_t *data, size_t size, int32_t map_id,
                       int32_t tile_x, int32_t tile_y, float max_slope_deg,
                       ng_tile *out, adt_stats *stats);

/* Tile indices for a world position, using the client's own convention. */
void ng_adt_tile_of(float x, float y, int32_t *tile_x, int32_t *tile_y);


#ifdef __cplusplus
}    /* extern "C" */
#endif

#endif /* ADT_H */

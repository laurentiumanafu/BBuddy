/*
 * navgrid - routing geometry for a client that has no usable mesh.
 *
 * Why this exists: Tripper/Detour is a 32-bit native library (Lib\Navigation.dll,
 * PE machine 0x014C) with tiles baked from 3.3.5a ADT data, so the x64 bot cannot
 * load it and could not use those tiles even if it could. This is the replacement
 * that does not need native code at all: a walkability grid per ADT tile, baked
 * offline, plus A* and line-of-sight smoothing.
 *
 * Resolution is ~2 yd per cell over one 533.33 yd ADT tile = 267x267 cells. That is
 * coarser than a Detour polygon mesh and much simpler: it stores "can I stand here
 * and how high is it", which is all a click-walk mover needs to turn a long leg into
 * a chain of reachable 20-40 yd clicks.
 *
 * The C here is the reference implementation and is unit-tested on the build
 * machine; Styx/Logic/Pathing/NavGrid/ mirrors it in C# with the same assertions.
 */
#ifndef NAVGRID_H
#define NAVGRID_H

#include <stdint.h>
#include <stddef.h>

#define NG_MAGIC      "NAVG"
#define NG_VERSION    1
#define NG_ADT_YD     533.3333f
/* 267 cells over one ADT: exactly contiguous, so tiles never drift apart. */
#define NG_CELL_YD    (NG_ADT_YD / (float)NG_CELLS)
#define NG_CELLS      267                 /* ceil(533.3333 / 2) */
#define NG_CELL_COUNT (NG_CELLS * NG_CELLS)
#define NG_HEIGHT_Q   4                   /* height unit: quarter yard */
#define NG_MAX_CELLS  4000000             /* A* refuses more than this */

/* Cell flags. bit 0 is "walkable"; the rest are reasons, kept for diagnostics. */
#define NG_WALK       0x01
#define NG_HOLE       0x02                /* MCNK hole: no terrain at all */
#define NG_LIQUID     0x04                /* water/lava surface, not floor */
#define NG_STEEP      0x08                /* slope above the limit */
#define NG_DOODAD     0x10                /* reserved: buildings/props */

typedef struct
{
    int32_t map_id;
    int32_t tile_x;                       /* ADT tile index, north-south */
    int32_t tile_y;                       /* ADT tile index, west-east   */
    float   origin_x;                     /* world x of local cell (0,0) centre */
    float   origin_y;                     /* world y of local cell (0,0) centre */
    int16_t cells;
    int16_t height_q;                     /* quarter yards per height unit */
    int16_t reserved[2];
    int16_t heights[NG_CELL_COUNT];       /* quarter yards, one per cell      */
    uint8_t flags[NG_CELL_COUNT];         /* bit mask per cell, see NG_* above */
} ng_tile;

typedef struct
{
    float x, y, z;
} ng_point;

typedef struct
{
    int32_t base_tx, base_ty;             /* smallest tile indices in the set */
    int32_t tile_rows, tile_cols;
    float   origin_x, origin_y;           /* world position of global cell (0,0) */
    ng_tile **tiles;                      /* tile_rows * tile_cols, may contain NULL */
} ng_map;

/* ---- tile ------------------------------------------------------------- */
int  ng_tile_init(ng_tile *t, int32_t map_id, int32_t tx, int32_t ty);
void ng_tile_free(ng_tile *t);
static inline int ng_tile_index(int col, int row) { return row * NG_CELLS + col; }
int  ng_tile_walkable(const ng_tile *t, int col, int row);
void ng_tile_set_flag(ng_tile *t, int col, int row, uint8_t flag, int on);
float ng_tile_height(const ng_tile *t, int col, int row);
void ng_tile_set_height(ng_tile *t, int col, int row, float yards);
int  ng_tile_write(const ng_tile *t, const char *path);
int  ng_tile_read(ng_tile *t, const char *path);
/* Computes slope flags from the heights. Call once after filling heights. */
void ng_tile_mark_slopes(ng_tile *t, float max_slope_deg);
/* World <-> local cell. Returns 0 when the point is outside the tile. */
int  ng_tile_world_to_cell(const ng_tile *t, float x, float y, int *col, int *row);
void ng_tile_cell_to_world(const ng_tile *t, int col, int row, float *x, float *y);

/* ---- map / A* --------------------------------------------------------- */
int  ng_map_init(ng_map *m, int32_t base_tx, int32_t base_ty, int32_t rows, int32_t cols,
                 float origin_x, float origin_y);
void ng_map_free(ng_map *m);
void ng_map_put(ng_map *m, ng_tile *t);
int  ng_map_global_size(const ng_map *m);
int  ng_map_world_to_global(const ng_map *m, float x, float y, int *gcol, int *grow);
void ng_map_global_to_world(const ng_map *m, int gcol, int grow, float *x, float *y);
ng_tile *ng_map_tile_at(const ng_map *m, int gcol, int grow, int *local_col, int *local_row);

/* Finds a path. Returns the number of points written (0 = unreachable, <0 = error). */
int ng_find_path(const ng_map *m, float sx, float sy, float sz,
                 float gx, float gy, float gz,
                 ng_point *out, int max_out,
                 int *cells_explored);

/* Straight walkability between two world points on the map. */
int ng_line_walkable(const ng_map *m, float x0, float y0, float x1, float y1);

#endif /* NAVGRID_H */

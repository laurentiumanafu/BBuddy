/*
 * mapdump - convert server-side terrain files (`.map`) into .navtile tiles.
 *
 * Why this exists. The CASC route needs an ADT extractor, and the public ones are
 * MPQ-only or need a CDN that is gone for this build. A server repack that ships
 * alongside the client already carries the same terrain in its own format:
 *
 *     <repack>\data\maps\0013232.map      (MaNGOS / VMaNGOS / TrinityCore style)
 *
 * Those files hold the terrain height field for one 533.33 yd tile as a 129x129
 * float grid plus a 128x128 half-grid and a per-chunk hole mask - everything a
 * walkability grid needs, and nothing that depends on the client's CASC store.
 * Converting them gives real mmaps for a whole continent without extracting a
 * single ADT.
 *
 * What is lost: the .map files describe terrain only. Buildings, bridges, walls
 * and other WMO geometry live in the separate `vmaps` folder and are not read
 * here, so a route can still walk through a house. Slopes, hills, cliffs, holes
 * (cave mouths, terrain cut-outs) and the shoreline are real.
 *
 * The file is read through its own offset table rather than by assuming a fixed
 * layout, so the 1.12/2.4.3/3.3.5a revisions of the format all work.
 *
 *   mapdump convert --maps <dir> --out <navtile dir> [--map <id>] [--slope 45]
 *   mapdump info    --maps <dir> [--map <id>]
 */
#include "navgrid.h"

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <dirent.h>
#endif

#define MAP_SIZE_OF_GRIDS 533.33333f
#define MAP_RESOLUTION    128
#define MAP_V9_COUNT      ((MAP_RESOLUTION + 1) * (MAP_RESOLUTION + 1))   /* 129*129 */
#define MAP_V8_COUNT      (MAP_RESOLUTION * MAP_RESOLUTION)               /* 128*128 */

#define MAP_AREA_NO_AREA     0x0001
#define MAP_HEIGHT_NO_HEIGHT 0x0001
#define MAP_HEIGHT_AS_INT16  0x0002
#define MAP_HEIGHT_AS_INT8   0x0004

typedef struct
{
    uint32_t map_magic;
    uint32_t version_magic;
    uint32_t area_offset, area_size;
    uint32_t height_offset, height_size;
    uint32_t liquid_offset, liquid_size;
    uint32_t holes_offset, holes_size;
} map_header;

typedef struct
{
    uint32_t fourcc;
    uint32_t flags;
    float grid_height;
    float grid_max_height;
} map_height_header;

typedef struct
{
    int map_id;
    int gx;                     /* tile index from world X  -> navgrid tile_y */
    int gy;                     /* tile index from world Y  -> navgrid tile_x */
} map_name;

/* --- little helpers ------------------------------------------------------- */

static uint32_t rd32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t rd16(const uint8_t *p)
{
    return (uint16_t)((uint32_t)p[0] | ((uint32_t)p[1] << 8));
}

static float rdf32(const uint8_t *p)
{
    uint32_t bits = rd32(p);
    float value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

static const char *base_name(const char *path)
{
    const char *slash = strrchr(path, '/');
    if (slash == NULL) slash = strrchr(path, '\\');
    return slash ? slash + 1 : path;
}

static void join_path(char *out, size_t size, const char *dir, const char *name)
{
    size_t len = strlen(dir);
    if (len > 0 && (dir[len - 1] == '/' || dir[len - 1] == '\\'))
        snprintf(out, size, "%s%s", dir, name);
    else
        snprintf(out, size, "%s/%s", dir, name);
}

static int list_dir(const char *dir, char names[][256], int max)
{
    int count = 0;
#ifdef _WIN32
    char pattern[1024];
    join_path(pattern, sizeof(pattern), dir, "*");
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (count >= max) break;
        snprintf(names[count], 256, "%s", fd.cFileName);
        count++;
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR *d = opendir(dir);
    if (d == NULL) return 0;
    struct dirent *entry;
    while ((entry = readdir(d)) != NULL)
    {
        if (count >= max) break;
        snprintf(names[count], 256, "%s", entry->d_name);
        count++;
    }
    closedir(d);
#endif
    return count;
}

/* --- file names ----------------------------------------------------------- */

/* MaNGOS writes "%03u%02u%02u.map" = map, gy, gx. Repacks also ship variants with
 * separators ("001_32_24.map") or map-name prefixes, so all the numbers in the name
 * are collected and the last two are taken as the tile indices. */
static int parse_name(const char *name, map_name *out)
{
    int groups[8];
    int count = 0;
    const char *p = base_name(name);

    while (*p != '\0' && count < 8)
    {
        if (*p >= '0' && *p <= '9')
        {
            long value = 0;
            int digits = 0;
            while (*p >= '0' && *p <= '9')
            {
                value = value * 10 + (*p - '0');
                p++;
                digits++;
            }
            /* A run of 5+ digits is the map id and both tile indices run together. */
            if (digits > 4)
            {
                char tmp[16];
                if (digits > 9) digits = 9;
                snprintf(tmp, sizeof(tmp), "%s", p - digits);
                if (count + 3 <= 8)
                {
                    groups[count++] = atoi(tmp) / 10000;
                    groups[count++] = (atoi(tmp) / 100) % 100;
                    groups[count++] = atoi(tmp) % 100;
                }
            }
            else
            {
                groups[count++] = (int)value;
            }
        }
        else
        {
            p++;
        }
    }

    if (count < 3) return 0;

    out->map_id = groups[0];
    out->gy = groups[count - 2];
    out->gx = groups[count - 1];
    if (out->gx < 0 || out->gy < 0 || out->gx > 63 || out->gy > 63) return 0;
    return 1;
}

/* --- reading -------------------------------------------------------------- */

typedef struct
{
    int has_height;
    int int16;
    int int8;
    float base;
    float scale;
    const uint8_t *v9;          /* 129*129 samples, in file order            */
    const uint8_t *v8;          /* 128*128 samples, .map has it but we do not
                                 * need the half grid: V9 is dense enough at
                                 * 4.17 yd and bilinear on it is monotone. */
} map_height_source;

static float sample_v9(const map_height_source *h, int i, int j)
{
    if (i < 0) i = 0;
    if (j < 0) j = 0;
    if (i > MAP_RESOLUTION) i = MAP_RESOLUTION;
    if (j > MAP_RESOLUTION) j = MAP_RESOLUTION;

    const uint8_t *p = h->v9 + ((size_t)i * (MAP_RESOLUTION + 1) + (size_t)j) *
                                 (h->int8 ? 1u : (h->int16 ? 2u : 4u));
    if (h->int8) return h->base + (float)p[0] * h->scale;
    if (h->int16) return h->base + (float)rd16(p) * h->scale;
    return rdf32(p);
}

/* Height at a fractional grid position, in the same 0..128 space the file uses:
 * index i runs along world X, index j along world Y, both growing as the world
 * coordinate decreases. */
static float sample_bilinear(const map_height_source *h, float i, float j)
{
    if (i < 0.0f) i = 0.0f;
    if (j < 0.0f) j = 0.0f;
    if (i > (float)MAP_RESOLUTION - 0.001f) i = (float)MAP_RESOLUTION - 0.001f;
    if (j > (float)MAP_RESOLUTION - 0.001f) j = (float)MAP_RESOLUTION - 0.001f;

    int i0 = (int)i;
    int j0 = (int)j;
    float fi = i - (float)i0;
    float fj = j - (float)j0;

    float h00 = sample_v9(h, i0, j0);
    float h10 = sample_v9(h, i0 + 1, j0);
    float h01 = sample_v9(h, i0, j0 + 1);
    float h11 = sample_v9(h, i0 + 1, j0 + 1);

    return h00 * (1.0f - fi) * (1.0f - fj) + h10 * fi * (1.0f - fj) +
           h01 * (1.0f - fi) * fj + h11 * fi * fj;
}

/* The hole mask: a 16x16 array of 16-bit masks, one bit per 2x2 subdivision of an
 * 8x8 cell block. Same test the server itself uses, expressed in the file's own
 * index space so no axis convention has to be guessed. */
static int is_hole(const uint8_t *holes, int i, int j)
{
    static const uint16_t hole_h[4] = { 0x1111, 0x2222, 0x4444, 0x8888 };
    static const uint16_t hole_v[4] = { 0x000F, 0x00F0, 0x0F00, 0xF000 };

    if (i < 0 || j < 0 || i >= MAP_RESOLUTION || j >= MAP_RESOLUTION) return 0;

    int cell_row = i / 8;
    int cell_col = j / 8;
    int hole_row = (i % 8) / 2;
    int hole_col = (j % 8) / 2;

    uint16_t mask = rd16(holes + ((size_t)cell_row * 16 + (size_t)cell_col) * 2);
    return (mask & hole_h[hole_col] & hole_v[hole_row]) != 0;
}

static uint8_t *read_file(const char *path, size_t *size)
{
    FILE *f = fopen(path, "rb");
    if (f == NULL) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    long length = ftell(f);
    if (length <= 0 || length > 32 * 1024 * 1024) { fclose(f); return NULL; }
    rewind(f);

    uint8_t *data = (uint8_t *)malloc((size_t)length);
    if (data == NULL) { fclose(f); return NULL; }
    if (fread(data, 1, (size_t)length, f) != (size_t)length) { free(data); fclose(f); return NULL; }
    fclose(f);
    *size = (size_t)length;
    return data;
}

static void magic_string(uint32_t value, char out[5])
{
    out[0] = (char)(value & 0xFF);
    out[1] = (char)((value >> 8) & 0xFF);
    out[2] = (char)((value >> 16) & 0xFF);
    out[3] = (char)((value >> 24) & 0xFF);
    out[4] = '\0';
}

static int parse_header(const uint8_t *data, size_t size, map_header *h)
{
    if (size < sizeof(map_header)) return 0;
    h->map_magic = rd32(data + 0);
    h->version_magic = rd32(data + 4);
    h->area_offset = rd32(data + 8);
    h->area_size = rd32(data + 12);
    h->height_offset = rd32(data + 16);
    h->height_size = rd32(data + 20);
    h->liquid_offset = rd32(data + 24);
    h->liquid_size = rd32(data + 28);
    h->holes_offset = rd32(data + 32);
    h->holes_size = rd32(data + 36);
    return 1;
}

/* --- conversion ----------------------------------------------------------- */

static int convert_one(const char *path, const char *name, const char *out_dir,
                       float max_slope_deg, int verbose, int *holes_out)
{
    map_name id;
    if (!parse_name(name, &id)) return 0;

    size_t size = 0;
    uint8_t *data = read_file(path, &size);
    if (data == NULL)
    {
        fprintf(stderr, "mapdump: cannot read %s\n", path);
        return -1;
    }

    map_header header;
    if (!parse_header(data, size, &header))
    {
        fprintf(stderr, "mapdump: %s is too short for a header\n", name);
        free(data);
        return -1;
    }

    char magic[5], version[5];
    magic_string(header.map_magic, magic);
    magic_string(header.version_magic, version);

    if (memcmp(magic, "MAPS", 4) != 0)
    {
        fprintf(stderr, "mapdump: %s does not start with MAPS (got \"%s\")\n", name, magic);
        free(data);
        return -1;
    }

    map_height_source source;
    memset(&source, 0, sizeof(source));

    if (header.height_size >= sizeof(map_height_header) &&
        (size_t)header.height_offset + header.height_size <= size)
    {
        const uint8_t *p = data + header.height_offset;
        map_height_header hh;
        hh.fourcc = rd32(p + 0);
        hh.flags = rd32(p + 4);
        hh.grid_height = rdf32(p + 8);
        hh.grid_max_height = rdf32(p + 12);

        if ((hh.flags & MAP_HEIGHT_NO_HEIGHT) == 0)
        {
            const uint8_t *body = p + sizeof(map_height_header);
            size_t available = header.height_size - sizeof(map_height_header);
            size_t need = 0;

            if (hh.flags & MAP_HEIGHT_AS_INT8) need = MAP_V9_COUNT + MAP_V8_COUNT;
            else if (hh.flags & MAP_HEIGHT_AS_INT16) need = (MAP_V9_COUNT + MAP_V8_COUNT) * 2;
            else need = (MAP_V9_COUNT + MAP_V8_COUNT) * 4;

            if (available < need)
            {
                fprintf(stderr, "mapdump: %s height section is %zu bytes, need %zu\n",
                        name, available, need);
                free(data);
                return -1;
            }

            source.has_height = 1;
            source.int8 = (hh.flags & MAP_HEIGHT_AS_INT8) != 0;
            source.int16 = !source.int8 && (hh.flags & MAP_HEIGHT_AS_INT16) != 0;
            source.v9 = body;
            source.v8 = body + (source.int8 ? MAP_V9_COUNT : (source.int16 ? MAP_V9_COUNT * 2u
                                                                          : MAP_V9_COUNT * 4u));
            if (source.int8)
            {
                source.base = hh.grid_height;
                source.scale = (hh.grid_max_height - hh.grid_height) / 255.0f;
            }
            else if (source.int16)
            {
                source.base = hh.grid_height;
                source.scale = (hh.grid_max_height - hh.grid_height) / 65535.0f;
            }
            else
            {
                source.base = 0.0f;
                source.scale = 1.0f;
            }

            if (verbose)
                printf("  %s: heights %s, base %.1f, range %.1f\n", name,
                       source.int8 ? "int8" : (source.int16 ? "int16" : "float"),
                       source.base, hh.grid_max_height - hh.grid_height);
        }
        else if (verbose)
        {
            printf("  %s: flat terrain at %.1f (no height field)\n", name, hh.grid_height);
        }

        if (!source.has_height)
        {
            /* Nothing but a constant height: the tile is flat ground. */
            source.has_height = 2;
            source.base = hh.grid_height;
            source.scale = 0.0f;
        }
    }
    else
    {
        fprintf(stderr, "mapdump: %s has no height section\n", name);
        free(data);
        return -1;
    }

    const uint8_t *holes = NULL;
    if (header.holes_size >= 512 && (size_t)header.holes_offset + header.holes_size <= size)
        holes = data + header.holes_offset;

    ng_tile *tile = (ng_tile *)malloc(sizeof(ng_tile));
    if (tile == NULL) { free(data); return -1; }

    /* MaNGOS grid index from world Y is navgrid's tile_x; from world X is tile_y. */
    ng_tile_init(tile, id.map_id, id.gy, id.gx);

    int unwalkable = 0, hole_cells = 0;
    float min_h = 1e9f, max_h = -1e9f;
    int weird = 0;

    for (int row = 0; row < NG_CELLS; row++)
    {
        for (int col = 0; col < NG_CELLS; col++)
        {
            float x = 0.0f, y = 0.0f;
            ng_tile_cell_to_world(tile, col, row, &x, &y);

            /* The file's own index space is 0..128 inside the tile, not the global
             * 32-grid: `32 - world / 533.33` is the tile index plus a fraction, so
             * the tile index has to come back out or every sample lands outside. */
            float i_pos = (float)MAP_RESOLUTION *
                          ((32.0f - (float)tile->tile_y) - x / MAP_SIZE_OF_GRIDS);
            float j_pos = (float)MAP_RESOLUTION *
                          ((32.0f - (float)tile->tile_x) - y / MAP_SIZE_OF_GRIDS);

            float height;
            if (source.has_height == 2)
                height = source.base;
            else
                height = sample_bilinear(&source, i_pos, j_pos);

            if (!(height > -5000.0f && height < 5000.0f))
            {
                weird++;
                height = 0.0f;
            }

            ng_tile_set_height(tile, col, row, height);
            ng_tile_set_flag(tile, col, row, NG_WALK, 1);

            if (height < min_h) min_h = height;
            if (height > max_h) max_h = height;

            if (holes != NULL && is_hole(holes, (int)i_pos, (int)j_pos))
            {
                ng_tile_set_flag(tile, col, row, NG_HOLE, 1);
                ng_tile_set_flag(tile, col, row, NG_WALK, 0);
                hole_cells++;
            }
        }
    }

    ng_tile_mark_slopes(tile, max_slope_deg);

    for (int index = 0; index < NG_CELL_COUNT; index++)
        if ((tile->flags[index] & NG_WALK) == 0) unwalkable++;

    char out_name[128];
    snprintf(out_name, sizeof(out_name), "%d_%d_%d.navtile", id.map_id, tile->tile_x, tile->tile_y);
    char out_path[1024];
    join_path(out_path, sizeof(out_path), out_dir, out_name);

    if (!ng_tile_write(tile, out_path))
    {
        fprintf(stderr, "mapdump: cannot write %s\n", out_path);
        free(tile);
        free(data);
        return -1;
    }

    printf("  %s (\"%s\") -> %s  terrain %.0f..%.0f, %d holes, %d unwalkable of %d\n",
           name, version, out_name, min_h, max_h, hole_cells, unwalkable, NG_CELL_COUNT);
    if (weird > 0) printf("    %d cells had implausible heights and were flattened\n", weird);
    if (holes_out != NULL) *holes_out = hole_cells;

    free(tile);
    free(data);
    return 1;
}

static int cmd_convert(const char *maps_dir, const char *out_dir, int map_filter,
                       float slope, int verbose)
{
    char names[4096][256];
    int count = list_dir(maps_dir, names, 4096);
    if (count == 0)
    {
        fprintf(stderr, "mapdump: no files in %s\n", maps_dir);
        return 1;
    }

    int converted = 0, skipped = 0;
    for (int i = 0; i < count; i++)
    {
        size_t len = strlen(names[i]);
        if (len < 5 || strcmp(names[i] + len - 4, ".map") != 0)
        {
            skipped++;
            continue;
        }
        map_name id;
        if (!parse_name(names[i], &id)) { skipped++; continue; }
        if (map_filter >= 0 && id.map_id != map_filter) { skipped++; continue; }

        char path[1024];
        join_path(path, sizeof(path), maps_dir, names[i]);
        int rc = convert_one(path, names[i], out_dir, slope, verbose, NULL);
        if (rc > 0) converted++;
        else skipped++;
    }

    printf("%d tiles converted, %d skipped\n", converted, skipped);
    if (converted == 0)
    {
        printf("\nNothing converted. Run mapdump info --maps <dir> on the same folder: it\n"
               "prints the magic, version and section table of each file, which says\n"
               "whether these are terrain maps at all.\n");
        return 1;
    }
    return 0;
}

static int cmd_info(const char *maps_dir, int map_filter)
{
    char names[4096][256];
    int count = list_dir(maps_dir, names, 4096);
    if (count == 0)
    {
        fprintf(stderr, "mapdump: no files in %s\n", maps_dir);
        return 1;
    }

    int shown = 0, maps = 0, bad = 0;
    int seen_map[64];
    memset(seen_map, 0, sizeof(seen_map));

    printf("%d files in %s\n\n", count, maps_dir);
    for (int i = 0; i < count; i++)
    {
        size_t len = strlen(names[i]);
        if (len < 5 || strcmp(names[i] + len - 4, ".map") != 0) continue;

        map_name id;
        if (!parse_name(names[i], &id)) continue;
        if (map_filter >= 0 && id.map_id != map_filter) continue;

        if (id.map_id >= 0 && id.map_id < 64 && !seen_map[id.map_id])
        {
            seen_map[id.map_id] = 1;
            maps++;
        }

        if (shown >= 5) continue;

        char path[1024];
        join_path(path, sizeof(path), maps_dir, names[i]);
        size_t size = 0;
        uint8_t *data = read_file(path, &size);
        if (data == NULL) { bad++; continue; }

        map_header header;
        char magic[5], version[5];
        if (!parse_header(data, size, &header))
        {
            printf("%s: too short\n\n", names[i]);
            free(data);
            bad++;
            continue;
        }
        magic_string(header.map_magic, magic);
        magic_string(header.version_magic, version);

        printf("%s  %zu bytes  magic \"%s\" version \"%s\"  map %d tile (x=%d y=%d)\n",
               names[i], size, magic, version, id.map_id, id.gy, id.gx);
        printf("  area   %8u %8u   height %8u %8u   liquid %8u %8u   holes %8u %8u\n",
               header.area_offset, header.area_size, header.height_offset, header.height_size,
               header.liquid_offset, header.liquid_size, header.holes_offset, header.holes_size);

        if (header.height_size >= sizeof(map_height_header) &&
            (size_t)header.height_offset + header.height_size <= size)
        {
            const uint8_t *p = data + header.height_offset;
            map_height_header hh;
            hh.fourcc = rd32(p + 0);
            hh.flags = rd32(p + 4);
            hh.grid_height = rdf32(p + 8);
            hh.grid_max_height = rdf32(p + 12);
            char fourcc[5];
            magic_string(hh.fourcc, fourcc);
            printf("  heights \"%s\" flags 0x%x, base %.1f, max %.1f (%s)\n",
                   fourcc, hh.flags, hh.grid_height, hh.grid_max_height,
                   (hh.flags & MAP_HEIGHT_NO_HEIGHT) ? "flat" :
                   ((hh.flags & MAP_HEIGHT_AS_INT8) ? "int8" :
                    ((hh.flags & MAP_HEIGHT_AS_INT16) ? "int16" : "float")));
        }
        else
        {
            printf("  no readable height section\n");
            bad++;
        }
        printf("\n");
        free(data);
        shown++;
    }

    printf("%d map ids present", maps);
    if (map_filter >= 0) printf(" (filtered to %d)", map_filter);
    printf("\n");
    if (shown == 0)
    {
        printf("\nThis folder holds no .map files. Look for a \"maps\" folder next to\n"
               "\"vmaps\" and \"mmaps\" in the server's data directory.\n");
        return 1;
    }
    return 0;
}

/* --- seam check ----------------------------------------------------------- */

/* Compares the facing edges of neighbouring tiles.
 *
 * This is the one mistake that cannot be seen from inside a single tile: if the two
 * axes are swapped, every tile still looks like a plausible landscape, but tiles do
 * not line up with the tiles next to them. Real terrain does, because a tile shares
 * its edge with its neighbour's edge. A median absolute difference of a few yards is
 * normal (the two edge cells are one cell apart); tens of yards means the grid does
 * not belong together.
 */
static int compare_edges(const ng_tile *a, const ng_tile *b, int along_x, float diffs[], int max)
{
    int count = 0;
    for (int i = 0; i < a->cells && count < max; i++)
    {
        float ha = along_x ? ng_tile_height(a, a->cells - 1, i)      /* east edge of a */
                           : ng_tile_height(a, i, a->cells - 1);     /* south edge of a */
        float hb = along_x ? ng_tile_height(b, 0, i)
                           : ng_tile_height(b, i, 0);
        diffs[count++] = fabsf(ha - hb);
    }
    return count;
}

static int by_float(const void *lhs, const void *rhs)
{
    float a = *(const float *)lhs, b = *(const float *)rhs;
    return (a > b) - (a < b);
}

static int cmd_check(const char *tiles_dir, int map_filter, float tolerance)
{
    char names[4096][256];
    int count = list_dir(tiles_dir, names, 4096);
    if (count == 0)
    {
        fprintf(stderr, "mapdump: no files in %s\n", tiles_dir);
        return 1;
    }

    enum { MAX_TILES = 512 };
    static ng_tile tiles[MAX_TILES];
    int tile_count = 0;

    for (int i = 0; i < count && tile_count < MAX_TILES; i++)
    {
        size_t len = strlen(names[i]);
        if (len < 9 || strcmp(names[i] + len - 8, ".navtile") != 0) continue;

        char path[1024];
        join_path(path, sizeof(path), tiles_dir, names[i]);
        ng_tile *tile = &tiles[tile_count];
        if (!ng_tile_read(tile, path)) continue;
        if (map_filter >= 0 && tile->map_id != map_filter) continue;
        tile_count++;
    }

    if (tile_count < 2)
    {
        printf("%d tiles for map %d - a seam needs two, nothing to check\n",
               tile_count, map_filter);
        return 0;
    }

    static float diffs[NG_CELLS * 2];
    int pairs = 0, bad_pairs = 0;
    float all[4096];
    int all_count = 0;

    for (int i = 0; i < tile_count; i++)
    {
        for (int k = 0; k < tile_count; k++)
        {
            if (i == k) continue;

            /* tile_x is the Y index and tile_y the X index, so a neighbour one tile
             * further west differs in tile_y by one. */
            int dx = tiles[k].tile_y - tiles[i].tile_y;
            int dy = tiles[k].tile_x - tiles[i].tile_x;
            if (!((dx == 1 && dy == 0) || (dx == 0 && dy == 1))) continue;

            int n = compare_edges(&tiles[i], &tiles[k], dy == 0, diffs, NG_CELLS);
            if (n < 2) continue;
            memcpy(all + all_count, diffs, sizeof(float) * (size_t)n);
            all_count += n;

            float sorted[NG_CELLS];
            memcpy(sorted, diffs, sizeof(float) * (size_t)n);
            qsort(sorted, (size_t)n, sizeof(float), by_float);
            float median = sorted[n / 2];

            pairs++;
            if (median > tolerance) bad_pairs++;
            printf("  (%d,%d)<->(%d,%d) %s edge: median %.1f yd%s\n",
                   tiles[i].tile_x, tiles[i].tile_y, tiles[k].tile_x, tiles[k].tile_y,
                   dy == 0 ? "west" : "south", median,
                   median > tolerance ? "   <- does not line up" : "");
        }
    }

    if (pairs == 0)
    {
        printf("%d tiles, no two of them adjacent - nothing to check\n", tile_count);
        return 0;
    }

    qsort(all, (size_t)all_count, sizeof(float), by_float);
    float overall = all[all_count / 2];

    printf("\n%d tiles, %d seams, median edge difference %.1f yd (limit %.1f)\n",
           tile_count, pairs, overall, tolerance);
    if (bad_pairs > 0)
    {
        printf("%d of %d seams disagree. Either the tiles are not contiguous, or the\n"
               "tile naming is transposed: tile_x is the Y index and tile_y the X index.\n",
               bad_pairs, pairs);
        return 1;
    }
    printf("seams agree: the tiles are placed the right way round\n");
    return 0;
}

int main(int argc, char **argv)
{
    if (argc < 2)
    {
        fprintf(stderr,
                "usage: mapdump convert --maps <dir> --out <navtile dir> [--map <id>] [--slope 45]\n"
                "       mapdump info    --maps <dir> [--map <id>]\n"
                "       mapdump check   --tiles <navtile dir> [--map <id>]\n");
        return 2;
    }

    const char *command = argv[1];
    const char *maps_dir = NULL;
    const char *out_dir = NULL;
    const char *tiles_dir = NULL;
    int map_filter = -1;
    float slope = 45.0f;
    int verbose = 0;

    for (int i = 2; i < argc; i++)
    {
        if (strcmp(argv[i], "--maps") == 0 && i + 1 < argc) maps_dir = argv[++i];
        else if (strcmp(argv[i], "--out") == 0 && i + 1 < argc) out_dir = argv[++i];
        else if (strcmp(argv[i], "--tiles") == 0 && i + 1 < argc) tiles_dir = argv[++i];
        else if (strcmp(argv[i], "--map") == 0 && i + 1 < argc) map_filter = atoi(argv[++i]);
        else if (strcmp(argv[i], "--slope") == 0 && i + 1 < argc) slope = (float)atof(argv[++i]);
        else if (strcmp(argv[i], "--verbose") == 0) verbose = 1;
    }

    if (strcmp(command, "check") == 0)
    {
        if (tiles_dir == NULL)
        {
            fprintf(stderr, "mapdump: --tiles <dir> is required for check\n");
            return 2;
        }
        return cmd_check(tiles_dir, map_filter, 8.0f);
    }

    if (maps_dir == NULL)
    {
        fprintf(stderr, "mapdump: --maps <dir> is required\n");
        return 2;
    }

    if (strcmp(command, "convert") == 0)
    {
        if (out_dir == NULL)
        {
            fprintf(stderr, "mapdump: --out <dir> is required for convert\n");
            return 2;
        }
        return cmd_convert(maps_dir, out_dir, map_filter, slope, verbose);
    }
    if (strcmp(command, "info") == 0) return cmd_info(maps_dir, map_filter);

    fprintf(stderr, "mapdump: unknown command \"%s\"\n", command);
    return 2;
}

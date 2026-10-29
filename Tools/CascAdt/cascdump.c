/*
 * cascdump - pull the terrain ADTs out of a local World of Warcraft CASC install,
 * without a listfile, without the root manifest and without internet access.
 *
 *   cascdump list --data "<wow>\Data\data"
 *   cascdump dump --data "<wow>\Data\data" --out <dir> [--ref x,y,z] [--limit n]
 *
 * Why it works the way it does
 * ---------------------------
 * Reading a CASC file by name needs the root manifest (name -> CKey) and the
 * encoding file (CKey -> EKey), and Blizzard does not ship either one inside the
 * game folder - those come from the CDN. For a closed-beta build the CDN content
 * is long gone, which is why CASCExplorer's online mode cannot help here.
 *
 * Neither file is needed for what a navmesh builder wants:
 *
 *     the *.idx files list every EKey in the install, with archive and offset
 *     the data.0xx files hold the BLTE-wrapped payloads
 *
 * So this walks every index entry, decodes the payload, and keeps the ones that
 * look like terrain. A root ADT always starts with MVER and contains MCNK, which
 * no other file type in the store does. The tile index is not in the file name,
 * but each MCNK header carries the chunk's world position and its 16x16 index, so
 * the ADT tile can be solved for directly:
 *
 *     world_x = (32 - tile_y) * 533.3333 - chunk_y * 33.3333
 *     world_y = (32 - tile_x) * 533.3333 - chunk_x * 33.3333
 *
 * Output is the naming navbake already accepts: <map>_<x>_<y>.adt. The map id is
 * asked for, not guessed, because the data does not say which continent a tile
 * belongs to. `--ref` prints the baked terrain height at a coordinate you know
 * from the game, so you can confirm you picked the right continent before baking
 * a whole map.
 *
 * Formats, all cross-checked against CascLib's reader:
 *   index v2  guarded block (u32 size, u32 hash) + 16-byte header, then either one
 *             guarded entry block or 0x200-byte pages of guarded entries
 *   entry     9-byte EKey + 5-byte (archive<<segmentBits | offset, big endian)
 *             + 4-byte encoded size (big endian)
 *   payload   "BLTE" + u32 header size (big endian); header size 0 means a single
 *             frame. Each frame is 4-byte encoded size, 4-byte content size,
 *             16-byte hash, then (mode byte + data), mode 'N' stored or 'Z' zlib.
 */
#include "adt.h"
#include "navgrid.h"
#include "zlib.h"

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <errno.h>
#ifdef _WIN32
#include <direct.h>
#include <windows.h>
#else
#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>
#endif

#define ADT_GRID       64
#define ADT_YD         533.3333f
#define CHUNK_YD       (ADT_YD / 16.0f)
#define MAX_PAYLOAD    (32u * 1024u * 1024u)
#define MAX_ENTRIES    8000000

/* ------------------------------------------------------------------------- */
/* small helpers                                                             */
/* ------------------------------------------------------------------------- */

static uint32_t rd32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint16_t rd16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint64_t rd64(const uint8_t *p) { return (uint64_t)rd32(p) | ((uint64_t)rd32(p + 4) << 32); }
static uint32_t be32(const uint8_t *p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3]; }
static uint64_t be40(const uint8_t *p)
{
    return ((uint64_t)p[0] << 32) | ((uint64_t)p[1] << 24) | ((uint64_t)p[2] << 16) |
           ((uint64_t)p[3] << 8) | (uint64_t)p[4];
}
static float rdf32(const uint8_t *p) { float f; memcpy(&f, p, 4); return f; }

/* Bob Jenkins lookup3 hashlittle, the 32-bit hash CASC uses to guard blocks. */
#define ROT32(x, k) (((x) << (k)) | ((x) >> (32 - (k))))
static uint32_t hashlittle(const void *key, size_t length, uint32_t initval)
{
    uint32_t a, b, c;
    const uint8_t *k = (const uint8_t *)key;

    a = b = c = 0xdeadbeefu + (uint32_t)length + initval;

    while (length > 12)
    {
        a += (uint32_t)k[0] | ((uint32_t)k[1] << 8) | ((uint32_t)k[2] << 16) | ((uint32_t)k[3] << 24);
        b += (uint32_t)k[4] | ((uint32_t)k[5] << 8) | ((uint32_t)k[6] << 16) | ((uint32_t)k[7] << 24);
        c += (uint32_t)k[8] | ((uint32_t)k[9] << 8) | ((uint32_t)k[10] << 16) | ((uint32_t)k[11] << 24);

        a -= c; a ^= ROT32(c, 4);  c += b;
        b -= a; b ^= ROT32(a, 6);  a += c;
        c -= b; c ^= ROT32(b, 8);  b += a;
        a -= c; a ^= ROT32(c, 16); c += b;
        b -= a; b ^= ROT32(a, 19); a += c;
        c -= b; c ^= ROT32(b, 4);  b += a;

        k += 12;
        length -= 12;
    }

    switch (length)
    {
        case 12: c += (uint32_t)k[11] << 24; /* fall through */
        case 11: c += (uint32_t)k[10] << 16; /* fall through */
        case 10: c += (uint32_t)k[9] << 8;   /* fall through */
        case 9:  c += k[8];                  /* fall through */
        case 8:  b += (uint32_t)k[7] << 24;  /* fall through */
        case 7:  b += (uint32_t)k[6] << 16;  /* fall through */
        case 6:  b += (uint32_t)k[5] << 8;   /* fall through */
        case 5:  b += k[4];                  /* fall through */
        case 4:  a += (uint32_t)k[3] << 24;  /* fall through */
        case 3:  a += (uint32_t)k[2] << 16;  /* fall through */
        case 2:  a += (uint32_t)k[1] << 8;   /* fall through */
        case 1:  a += k[0];
                 break;
        case 0:  return c;
    }

    c ^= b; c -= ROT32(b, 14);
    a ^= c; a -= ROT32(c, 11);
    b ^= a; b -= ROT32(a, 25);
    c ^= b; c -= ROT32(b, 16);
    a ^= c; a -= ROT32(c, 4);
    b ^= a; b -= ROT32(a, 14);
    c ^= b; c -= ROT32(b, 24);
    return c;
}

static void join_path(char *out, size_t size, const char *dir, const char *name)
{
    size_t n = strlen(dir);
    int slash = n > 0 && (dir[n - 1] == '/' || dir[n - 1] == '\\');
    snprintf(out, size, "%s%s%s", dir, slash ? "" : "/", name);
}

static int make_dir(const char *path)
{
#ifdef _WIN32
    return _mkdir(path) == 0 || errno == EEXIST;
#else
    return mkdir(path, 0755) == 0 || errno == EEXIST;
#endif
}

static int ends_with(const char *name, const char *suffix)
{
    size_t n = strlen(name), m = strlen(suffix);
    return n > m && strcmp(name + n - m, suffix) == 0;
}

static int list_dir(const char *dir, char names[][256], int max)
{
    int count = 0;
#ifdef _WIN32
    char pattern[1024];
    WIN32_FIND_DATAA fd;
    join_path(pattern, sizeof(pattern), dir, "*");
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (count >= max) break;
        snprintf(names[count++], 256, "%s", fd.cFileName);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR *d = opendir(dir);
    if (d == NULL) return 0;
    struct dirent *entry;
    while ((entry = readdir(d)) != NULL)
    {
        /* d_type is not filled in on every filesystem, so ask the stat instead. */
        char full[1024];
        struct stat st;
        join_path(full, sizeof(full), dir, entry->d_name);
        if (stat(full, &st) == 0 && S_ISDIR(st.st_mode)) continue;
        if (count >= max) break;
        snprintf(names[count++], 256, "%s", entry->d_name);
    }
    closedir(d);
#endif
    return count;
}

static uint8_t *read_file(const char *path, size_t *size_out)
{
    FILE *f = fopen(path, "rb");
    if (f == NULL) return NULL;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size <= 0)
    {
        fclose(f);
        return NULL;
    }
    uint8_t *data = (uint8_t *)malloc((size_t)size);
    if (data != NULL && fread(data, 1, (size_t)size, f) != (size_t)size)
    {
        free(data);
        data = NULL;
    }
    fclose(f);
    *size_out = (size_t)size;
    return data;
}

/* Finds a fourcc at a chunk boundary, walking the chunk chain from the start.
 * Returns the offset of the chunk body, or -1. Used only to confirm a payload is
 * an ADT, so a linear scan of the headers is plenty. */
static long find_chunk(const uint8_t *data, size_t size, const char *fourcc, int skip)
{
    for (size_t i = 0; i + 8 <= size; i++)
    {
        if (memcmp(data + i, fourcc, 4) != 0) continue;
        uint32_t body = rd32(data + i + 4);
        if (body > size || i + 8 + body > size) continue;
        if (skip-- > 0) continue;
        return (long)(i + 8);
    }
    return -1;
}

/* ------------------------------------------------------------------------- */
/* index files                                                               */
/* ------------------------------------------------------------------------- */

typedef struct
{
    uint8_t ekey[9];
    uint64_t span;       /* archive index in the high bits, archive offset below */
    uint32_t enc_size;
} casc_entry;

typedef struct
{
    casc_entry *items;
    size_t count;
    size_t capacity;
    unsigned segment_bits;
    uint64_t max_file_offset;
    int bad_hash;
} entry_list;

static void push_entry(entry_list *list, const uint8_t *entry, unsigned segment_bits, uint64_t max_file_offset)
{
    /* An entry whose size is zero is the head of a multi-archive span, which needs
     * the file span table to reassemble. Terrain never needs it, so skip it. */
    uint32_t enc_size = be32(entry + 14);
    if (enc_size == 0 || enc_size > MAX_PAYLOAD) return;

    uint64_t span = be40(entry + 9);
    uint64_t offset = span & (((uint64_t)1 << segment_bits) - 1);
    if (offset >= max_file_offset) return;

    if (list->count == list->capacity)
    {
        size_t next = list->capacity ? list->capacity * 2 : 4096;
        if (next > MAX_ENTRIES) return;
        casc_entry *grown = (casc_entry *)realloc(list->items, next * sizeof(casc_entry));
        if (grown == NULL) return;
        list->items = grown;
        list->capacity = next;
    }

    memcpy(list->items[list->count].ekey, entry, 9);
    list->items[list->count].span = span;
    list->items[list->count].enc_size = enc_size;
    list->count++;
}

/* Parses one .idx file. Returns the number of entries taken from it, or -1. */
static int parse_index(entry_list *list, const char *path, int verbose)
{
    size_t size = 0;
    uint8_t *data = read_file(path, &size);
    if (data == NULL) return -1;

    int result = -1;
    int found = 0;
    size_t before = list->count;

    do
    {
        if (size < 24)
        {
            if (verbose) fprintf(stderr, "  %s: too small\n", path);
            break;
        }

        /* Guarded header: u32 block size, u32 hash, then the version 2 header. */
        uint32_t block_size = rd32(data);
        uint16_t revision = rd16(data + 8);
        if (revision != 0x0007)
        {
            if (verbose) fprintf(stderr, "  %s: index revision 0x%04x, only 7 is supported\n", path, revision);
            break;
        }
        if (block_size != 16)
        {
            if (verbose) fprintf(stderr, "  %s: unexpected header block size %u\n", path, block_size);
            break;
        }
        /* Version 2 readers do not verify this hash (only version 1 does), so a
         * mismatch is a note, not a failure. */
        if (hashlittle(data + 8, 16, 0) != rd32(data + 4)) list->bad_hash++;

        uint8_t span_size_bytes = data[12];
        uint8_t span_offset_bytes = data[13];
        uint8_t key_bytes = data[14];
        unsigned segment_bits = data[15];
        uint64_t max_file_offset = rd64(data + 16);

        if (span_size_bytes != 4 || span_offset_bytes != 5 || key_bytes != 9 || segment_bits == 0 || segment_bits > 40)
        {
            if (verbose) fprintf(stderr, "  %s: unsupported entry layout (size %u, offset %u, key %u, bits %u)\n",
                                 path, span_size_bytes, span_offset_bytes, key_bytes, segment_bits);
            break;
        }
        list->segment_bits = segment_bits;
        list->max_file_offset = max_file_offset;

        size_t entry_length = (size_t)key_bytes + span_offset_bytes + span_size_bytes;   /* 18 */

        /* Layout A: one guarded block with every entry in it. Where the block
         * starts depends on whether the writer emitted the 8 bytes of padding
         * CascLib skips after the 16-byte header, so both offsets are tried and
         * the block hash decides. */
        static const size_t block_offsets[2] = { 32, 24 };
        for (int candidate = 0; candidate < 2; candidate++)
        {
            size_t at = block_offsets[candidate];
            if (at + 8 > size) continue;

            uint32_t entries_size = rd32(data + at);
            uint32_t entries_hash = rd32(data + at + 4);
            size_t entries_at = at + 8;
            if (entries_size == 0 || entries_size % entry_length != 0) continue;
            if (entries_at + (size_t)entries_size > size) continue;
            if (entries_hash != 0 && hashlittle(data + entries_at, entries_size, 0) != entries_hash) continue;

            size_t n = entries_size / entry_length;
            for (size_t i = 0; i < n; i++) push_entry(list, data + entries_at + i * entry_length, segment_bits, max_file_offset);
            if (verbose) fprintf(stderr, "  %s: %zu entries (single block at %zu)\n", path, n, at);
            result = (int)(list->count - before);
            found = (n > 0);
            break;
        }
        if (found) break;

        /* Layout B: 0x1000-aligned pages, each entry guarded by its own 32-bit
         * hash and padded to 4 bytes. This is what retail installs use. */
        size_t record = 4 + ((entry_length + 3) & ~(size_t)3);       /* 4 + 20 = 24 */
        size_t pages = 0, taken = 0;
        for (size_t page = 0x1000; page < size; page += 0x200)
        {
            pages++;
            for (size_t at = page; at + 4 + entry_length <= size && at < page + 0x200; at += record)
            {
                const uint8_t *entry = data + at + 4;
                uint32_t guard = rd32(data + at);
                int all_zero = 1;
                for (int i = 0; i < 9; i++) if (entry[i] != 0) { all_zero = 0; break; }
                if (all_zero && guard == 0) break;                   /* end of page */
                /* The stored guard is the entry hash with the top bit forced on. */
                if (guard != 0 && (hashlittle(entry, entry_length + 1, 0) | 0x80000000u) != guard)
                {
                    /* Not a guarded entry; this page is finished. */
                    break;
                }
                push_entry(list, entry, segment_bits, max_file_offset);
                taken++;
            }
        }
        if (taken > 0 || pages > 0)
        {
            if (verbose) fprintf(stderr, "  %s: %zu entries in %zu pages\n", path, taken, pages);
            result = (int)(list->count - before);
        }
    } while (0);

    free(data);
    return result;
}

/* ------------------------------------------------------------------------- */
/* BLTE                                                                      */
/* ------------------------------------------------------------------------- */

static int blte_decode(const uint8_t *data, size_t size, uint8_t **out, size_t *out_size, char *why, size_t why_size)
{
    /* Inside a data.### archive the payload can carry the extra 0x1E-byte encoded
     * header before the BLTE signature. Loose files start with the signature. */
    size_t start = 0;
    if (size >= 4 && memcmp(data, "BLTE", 4) == 0) start = 0;
    else if (size >= 0x1E + 4 && memcmp(data + 0x1E, "BLTE", 4) == 0) start = 0x1E;
    else
    {
        snprintf(why, why_size, "no BLTE signature");
        return 0;
    }

    const uint8_t *blte = data + start;
    size_t avail = size - start;
    uint32_t header_size = be32(blte + 4);

    uint32_t frame_count = 1;
    size_t frames_at = 0;
    size_t payload_at;

    if (header_size == 0)
    {
        /* Single frame, no table: the whole rest is one chunk. */
        payload_at = 8;
        frames_at = 0;
    }
    else
    {
        if (header_size < 12 || header_size > avail)
        {
            snprintf(why, why_size, "bad BLTE header size %u", header_size);
            return 0;
        }
        frame_count = ((uint32_t)blte[9] << 16) | ((uint32_t)blte[10] << 8) | blte[11];
        frames_at = 12;
        /* HeaderSize counts the first 12 bytes plus one 24-byte entry per frame,
         * so the frame data starts exactly at HeaderSize from the signature. */
        payload_at = header_size;
        if (frame_count == 0 || 12 + (size_t)frame_count * 24 != (size_t)header_size)
        {
            snprintf(why, why_size, "frame table %u entries does not fit header %u", frame_count, header_size);
            return 0;
        }
    }

    uint8_t *result = NULL;
    size_t produced = 0, capacity = 0;
    int ok = 0;

    for (uint32_t i = 0; i < frame_count; i++)
    {
        uint32_t enc_size, content_size;
        if (header_size == 0)
        {
            enc_size = (uint32_t)(avail - payload_at);
            content_size = enc_size;
        }
        else
        {
            const uint8_t *frame = blte + frames_at + (size_t)i * 24;
            enc_size = be32(frame);
            content_size = be32(frame + 4);
        }
        if (payload_at + enc_size > avail || enc_size == 0)
        {
            snprintf(why, why_size, "frame %u runs past the payload", i);
            break;
        }
        if (produced + content_size + 1 > capacity)
        {
            size_t next = produced + content_size + 1;
            uint8_t *grown = (uint8_t *)realloc(result, next);
            if (grown == NULL) break;
            result = grown;
            capacity = next;
        }

        const uint8_t *frame_data = blte + payload_at;
        char mode = (char)frame_data[0];
        if (mode == 'N')
        {
            if (content_size > enc_size - 1) content_size = enc_size - 1;
            memcpy(result + produced, frame_data + 1, content_size);
        }
        else if (mode == 'Z')
        {
            z_stream stream;
            memset(&stream, 0, sizeof(stream));
            if (inflateInit(&stream) != Z_OK) break;
            stream.next_in = (Bytef *)(frame_data + 1);
            stream.avail_in = enc_size - 1;
            stream.next_out = result + produced;
            stream.avail_out = content_size;
            int rc = inflate(&stream, Z_FINISH);
            size_t got = content_size - stream.avail_out;
            inflateEnd(&stream);
            if (rc != Z_STREAM_END && rc != Z_BUF_ERROR && rc != Z_OK)
            {
                snprintf(why, why_size, "frame %u zlib error %d", i, rc);
                break;
            }
            content_size = got;
        }
        else
        {
            snprintf(why, why_size, "frame %u uses unsupported BLTE mode '%c'", i, mode);
            break;
        }

        produced += content_size;
        payload_at += enc_size;
        if (i + 1 == frame_count) ok = 1;
    }

    if (!ok)
    {
        free(result);
        return 0;
    }
    *out = result;
    *out_size = produced;
    return 1;
}

/* Cheap "is this worth decoding" test.
 *
 * A full install has hundreds of thousands of index entries and inflating all of
 * them is minutes of CPU for nothing. Every WoW data file type that matters here
 * starts with MVER, and that is in the first few bytes of the first BLTE frame, so
 * reading a few KB and inflating just the start of frame 0 rejects almost
 * everything without a full decode.
 *
 * Returns 1 when the payload definitely starts with MVER. A zero means "not
 * interesting", so a false negative would silently lose a tile - which is why the
 * caller can turn the pre-filter off with --no-prefilter. */
static int payload_starts_with_mver(FILE *archive, long offset, uint32_t enc_size)
{
    static const size_t attempts[2] = { 4096, 65536 };
    uint8_t *head = (uint8_t *)malloc(attempts[1]);
    if (head == NULL) return 1;              /* no memory: do not lose the entry */

    int verdict = 0;
    for (int attempt = 0; attempt < 2 && !verdict; attempt++)
    {
        size_t want = attempts[attempt];
        if (want > enc_size) want = enc_size;

        if (fseek(archive, offset, SEEK_SET) != 0) break;
        if (fread(head, 1, want, archive) != want) break;

        size_t start = 0;
        if (want >= 4 && memcmp(head, "BLTE", 4) == 0) start = 0;
        else if (want >= 0x1E + 4 && memcmp(head + 0x1E, "BLTE", 4) == 0) start = 0x1E;
        else break;                                            /* not BLTE at all */

        const uint8_t *blte = head + start;
        size_t avail = want - start;
        uint32_t header_size = be32(blte + 4);
        size_t data_at = header_size == 0 ? 8 : header_size;
        if (data_at + 1 >= avail) continue;                     /* need a bigger read */

        char mode = (char)blte[data_at];
        if (mode == 'N')
        {
            verdict = avail >= data_at + 5 && memcmp(blte + data_at + 1, "MVER", 4) == 0;
        }
        else if (mode == 'Z')
        {
            uint8_t out[8];
            z_stream stream;
            memset(&stream, 0, sizeof(stream));
            if (inflateInit(&stream) == Z_OK)
            {
                stream.next_in = (Bytef *)(blte + data_at + 1);
                stream.avail_in = (uInt)(avail - data_at - 1);
                stream.next_out = out;
                stream.avail_out = sizeof(out);
                inflate(&stream, Z_SYNC_FLUSH);
                size_t produced = sizeof(out) - stream.avail_out;
                inflateEnd(&stream);
                verdict = produced >= 4 && memcmp(out, "MVER", 4) == 0;
            }
        }
        else
        {
            break;                                              /* E, F: skip */
        }
    }

    free(head);
    return verdict;
}

/* ------------------------------------------------------------------------- */
/* ADT inspection                                                            */
/* ------------------------------------------------------------------------- */

typedef struct
{
    int is_adt;
    int chunks;
    int have_tile;
    int tile_x, tile_y;
    int tile_from_doodad;      /* 1 = placement record, 0 = chunk position */
    int chunks_agreeing;
    int chunks_disagreeing;
    float height_at_ref;
    int ref_inside;
} adt_info;

/* Tile index of a world position, the client's own convention:
 *
 *   tile_x = 32 - (int)(y / 533.3333)
 *   tile_y = 32 - (int)(x / 533.3333)
 */
static int tile_of_world(float x, float y, int *tile_x, int *tile_y)
{
    if (!isfinite(x) || !isfinite(y)) return 0;
    if (x <= -20000.0f || x >= 20000.0f || y <= -20000.0f || y >= 20000.0f) return 0;

    int tx = 32 - (int)(y / ADT_YD);
    int ty = 32 - (int)(x / ADT_YD);
    if (tx < 0 || tx >= ADT_GRID || ty < 0 || ty >= ADT_GRID) return 0;

    *tile_x = tx;
    *tile_y = ty;
    return 1;
}

/* Doodad and world-model placements carry absolute world coordinates, and the
 * first record of MDDF or MODF is enough to place the whole tile. Both structures
 * start with nameId, uniqueId and then position, which has not moved since 1.x, so
 * this works without knowing the record size of a given build. */
static int tile_from_placements(const uint8_t *data, size_t size, int *tile_x, int *tile_y)
{
    static const char *const fourcc[2] = { "MDDF", "MODF" };

    for (int i = 0; i < 2; i++)
    {
        long body = find_chunk(data, size, fourcc[i], 0);
        if (body < 0) continue;
        if ((size_t)body + 20 > size) continue;

        uint32_t body_size = rd32(data + body - 4);
        if (body_size < 20) continue;

        if (tile_of_world(rdf32(data + body + 8), rdf32(data + body + 12), tile_x, tile_y)) return 1;
    }
    return 0;
}

/* MCNK carries the chunk's world position in newer builds, which solves the tile
 * on its own:
 *
 *   world_x = (32 - tile_y) * 533.3333 - index_y * 33.3333 - half chunk
 *   world_y = (32 - tile_x) * 533.3333 - index_x * 33.3333 - half chunk
 *
 * The offset moved between expansions, and a wrong offset produces garbage that
 * scatters over the whole grid, so every chunk has to agree before the result is
 * believed. */
static int tile_from_chunk_positions(const uint8_t *data, size_t size, int pos_offset,
                                    int *tile_x, int *tile_y, int *agreeing, int *disagreeing)
{
    int first_x = -1, first_y = -1;
    int agree = 0, disagree = 0;

    for (size_t at = 0; at + 8 + 0x88 <= size; )
    {
        if (memcmp(data + at, "MCNK", 4) != 0)
        {
            uint32_t body = rd32(data + at + 4);
            if (body > size || at + 8 + body > size) { at++; continue; }
            at += 8 + body;
            continue;
        }

        uint32_t body = rd32(data + at + 4);
        const uint8_t *chunk = data + at + 8;
        if (at + 8 + body > size) break;

        /* `chunk` points past the fourcc and the chunk size, so the MCNK fields
         * sit 8 bytes earlier than their file offsets: IndexX is at 0x0C in the
         * file and at 0x04 here. */
        int index_x = (int)rd32(chunk + 0x04);
        int index_y = (int)rd32(chunk + 0x08);
        if (index_x >= 0 && index_x < 16 && index_y >= 0 && index_y < 16)
        {
            float px = rdf32(chunk + pos_offset);
            float py = rdf32(chunk + pos_offset + 4);
            float half = CHUNK_YD * 0.5f;

            if (isfinite(px) && isfinite(py) && (px != 0.0f || py != 0.0f))
            {
                int tx, ty;
                float base_x = px + (float)index_y * CHUNK_YD + half;
                float base_y = py + (float)index_x * CHUNK_YD + half;
                if (tile_of_world(base_x, base_y, &tx, &ty))
                {
                    if (first_x < 0) { first_x = tx; first_y = ty; agree = 1; }
                    else if (tx == first_x && ty == first_y) agree++;
                    else disagree++;
                }
            }
        }

        at += 8 + body;
        if (body == 0) break;
    }

    *agreeing = agree;
    *disagreeing = disagree;
    if (first_x >= 0 && disagree == 0 && agree >= 8)
    {
        *tile_x = first_x;
        *tile_y = first_y;
        return 1;
    }
    return 0;
}

static void inspect_adt(const uint8_t *data, size_t size, int32_t map_id, adt_info *info, const float *ref)
{
    memset(info, 0, sizeof(*info));
    if (size < 16 || memcmp(data, "MVER", 4) != 0) return;
    if (find_chunk(data, size, "MCNK", 0) < 0) return;

    info->is_adt = 1;

    for (size_t at = 0; at + 8 <= size; )
    {
        if (memcmp(data + at, "MCNK", 4) != 0)
        {
            uint32_t body = rd32(data + at + 4);
            if (body > size || at + 8 + body > size) { at++; continue; }
            at += 8 + body;
            continue;
        }
        info->chunks++;
        uint32_t body = rd32(data + at + 4);
        if (body == 0) break;
        at += 8 + body;
    }

    if (tile_from_placements(data, size, &info->tile_x, &info->tile_y))
    {
        info->have_tile = 1;
        info->tile_from_doodad = 1;
    }
    else
    {
        /* 0x7C is the modern layout; 0x68 fits inside the older 128-byte header and
         * is tried second so a shifted field cannot be mistaken for a real one. */
        static const int offsets[2] = { 0x7C, 0x68 };
        for (int i = 0; i < 2 && !info->have_tile; i++)
        {
            int agree = 0, disagree = 0;
            if (tile_from_chunk_positions(data, size, offsets[i], &info->tile_x, &info->tile_y, &agree, &disagree))
            {
                info->have_tile = 1;
                info->tile_from_doodad = 0;
                info->chunks_agreeing = agree;
                info->chunks_disagreeing = disagree;
            }
        }
    }

    ng_tile *tile = (ng_tile *)malloc(sizeof(ng_tile));
    if (tile != NULL)
    {
        int tx = info->have_tile ? info->tile_x : 32;
        int ty = info->have_tile ? info->tile_y : 32;
        adt_stats stats;

        if (ng_bake_adt_buffer(data, size, map_id, tx, ty, 45.0f, tile, &stats) > 0 && ref != NULL)
        {
            int col, row;
            if (ng_tile_world_to_cell(tile, ref[0], ref[1], &col, &row))
            {
                info->ref_inside = 1;
                info->height_at_ref = ng_tile_height(tile, col, row);
            }
        }
        free(tile);
    }
}

/* ------------------------------------------------------------------------- */
/* commands                                                                  */
/* ------------------------------------------------------------------------- */

static int collect_indexes(const char *data_dir, char names[][256], int max)
{
    char all[4096][256];
    int count = list_dir(data_dir, all, 4096);
    int kept = 0;
    for (int i = 0; i < count && kept < max; i++)
    {
        if (!ends_with(all[i], ".idx")) continue;
        snprintf(names[kept++], 256, "%s", all[i]);
    }
    /* Ascending by name, so bucket order matches the files. */
    for (int i = 0; i < kept; i++)
        for (int j = i + 1; j < kept; j++)
            if (strcmp(names[i], names[j]) > 0)
            {
                char swap[256];
                memcpy(swap, names[i], 256);
                memcpy(names[i], names[j], 256);
                memcpy(names[j], swap, 256);
            }
    return kept;
}

static int cmd_list(const char *data_dir, int verbose)
{
    char names[64][256];
    int idx_count = collect_indexes(data_dir, names, 64);
    if (idx_count == 0)
    {
        fprintf(stderr, "cascdump: no .idx files in %s\n", data_dir);
        fprintf(stderr, "  point --data at the folder that holds data.000 and the .idx files,\n");
        fprintf(stderr, "  usually \"<wow>\\Data\\data\".\n");
        return 1;
    }

    entry_list list;
    memset(&list, 0, sizeof(list));
    for (int i = 0; i < idx_count; i++)
    {
        char path[1024];
        join_path(path, sizeof(path), data_dir, names[i]);
        if (parse_index(&list, path, verbose) < 0) fprintf(stderr, "  %s: not a version 7 index\n", names[i]);
    }

    char data_names[4096][256];
    int data_count = list_dir(data_dir, data_names, 4096);
    int archives = 0;
    for (int i = 0; i < data_count; i++)
        if (strncmp(data_names[i], "data.", 5) == 0) archives++;

    printf("index files: %d, entries: %zu, data archives: %d\n", idx_count, list.count, archives);
    if (list.bad_hash) printf("entries with a header hash mismatch: %d (parsed anyway)\n", list.bad_hash);
    printf("segment bits: %u, max archive offset: %llu\n",
           list.segment_bits, (unsigned long long)list.max_file_offset);

    size_t adt_range = 0;
    for (size_t i = 0; i < list.count; i++)
        if (list.items[i].enc_size > 2000 && list.items[i].enc_size < 4u * 1024u * 1024u) adt_range++;
    printf("entries in ADT size range (2 KB - 4 MB): %zu\n", adt_range);
    printf("\nnext: cascdump dump --data \"%s\" --out <dir>\n", data_dir);
    free(list.items);
    return 0;
}

static int cmd_dump(const char *data_dir, const char *out_dir, int32_t map_id, const float *ref,
                    long limit, int verbose, int prefilter)
{
    char names[64][256];
    int idx_count = collect_indexes(data_dir, names, 64);
    if (idx_count == 0)
    {
        fprintf(stderr, "cascdump: no .idx files in %s\n", data_dir);
        return 1;
    }

    entry_list list;
    memset(&list, 0, sizeof(list));
    for (int i = 0; i < idx_count; i++)
    {
        char path[1024];
        join_path(path, sizeof(path), data_dir, names[i]);
        parse_index(&list, path, verbose);
    }
    printf("%zu index entries\n", list.count);

    FILE *csv = NULL;
    char csv_path[1024];
    join_path(csv_path, sizeof(csv_path), out_dir, "adt-index.csv");
    csv = fopen(csv_path, "wb");
    if (csv == NULL)
    {
        fprintf(stderr, "cascdump: cannot write %s\n", csv_path);
        free(list.items);
        return 1;
    }
    fprintf(csv, "file,tile_x,tile_y,source,chunks,encoded_size,height_at_ref\n");

    /* One archive handle at a time: entries come out grouped by archive in
     * practice, and reopening costs less than keeping 4096 handles around. */
    FILE *archive = NULL;
    int open_archive = -1;
    uint8_t *payload = (uint8_t *)malloc(MAX_PAYLOAD);

    long read = 0, adts = 0, tiles = 0, wrote = 0, at_ref = 0, alternatives = 0, skipped = 0;
    for (size_t i = 0; i < list.count; i++)
    {
        if (limit > 0 && wrote >= limit) break;

        casc_entry *entry = &list.items[i];
        unsigned archive_index = (unsigned)(entry->span >> list.segment_bits);
        uint64_t offset = entry->span & (((uint64_t)1 << list.segment_bits) - 1);

        if ((int)archive_index != open_archive)
        {
            if (archive != NULL) fclose(archive);
            char archive_name[64], archive_path[1024];
            snprintf(archive_name, sizeof(archive_name), "data.%03u", archive_index);
            join_path(archive_path, sizeof(archive_path), data_dir, archive_name);
            archive = fopen(archive_path, "rb");
            open_archive = archive != NULL ? (int)archive_index : -1;
            if (archive == NULL)
            {
                if (verbose) fprintf(stderr, "  missing %s\n", archive_name);
                continue;
            }
        }
        if (entry->enc_size > MAX_PAYLOAD) continue;

        if (prefilter && !payload_starts_with_mver(archive, (long)offset, entry->enc_size))
        {
            skipped++;
            continue;
        }

        if (verbose && i < 8) fprintf(stderr, "  entry %zu: archive %u offset %llu size %u\n",
                                     i, archive_index, (unsigned long long)offset, entry->enc_size);
        if (fseek(archive, (long)offset, SEEK_SET) != 0) continue;
        if (fread(payload, 1, entry->enc_size, archive) != entry->enc_size) continue;
        read++;

        uint8_t *decoded = NULL;
        size_t decoded_size = 0;
        char why[128] = {0};
        if (!blte_decode(payload, entry->enc_size, &decoded, &decoded_size, why, sizeof(why)))
        {
            if (verbose) fprintf(stderr, "  decode failed at %zu: %s\n", i, why);
            continue;
        }

        adt_info info;
        inspect_adt(decoded, decoded_size, map_id, &info, ref);
        if (info.is_adt) adts++;

        if (info.is_adt && info.have_tile)
        {
            char file_name[128];
            snprintf(file_name, sizeof(file_name), "%d_%d_%d.adt", (int)map_id, info.tile_x, info.tile_y);
            char out_path[1024];
            join_path(out_path, sizeof(out_path), out_dir, file_name);

            /* Two continents can own the same tile index, and the store does not
             * say which map an ADT belongs to. The first candidate keeps the plain
             * name; any other is parked in alternates/ with its EKey so nothing is
             * lost and the choice can be checked against --ref. */
            int duplicate = 0;
            FILE *probe = fopen(out_path, "rb");
            if (probe != NULL) { duplicate = 1; fclose(probe); }

            if (duplicate)
            {
                char alt_dir[1024];
                join_path(alt_dir, sizeof(alt_dir), out_dir, "alternates");
                make_dir(alt_dir);

                char alt_name[96];
                snprintf(alt_name, sizeof(alt_name), "%d_%d_%d__%02x%02x%02x.adt", (int)map_id,
                         info.tile_x, info.tile_y, entry->ekey[0], entry->ekey[1], entry->ekey[2]);
                join_path(out_path, sizeof(out_path), alt_dir, alt_name);
                snprintf(file_name, sizeof(file_name), "alternates/%s", alt_name);
                alternatives++;
            }

            FILE *out = fopen(out_path, "wb");
            if (out != NULL)
            {
                fwrite(decoded, 1, decoded_size, out);
                fclose(out);
                wrote++;
            }
            tiles++;
            fprintf(csv, "%s,%d,%d,%s,%d,%u,%.2f\n", file_name, info.tile_x, info.tile_y,
                    info.tile_from_doodad ? "placement" : "chunkpos",
                    info.chunks, entry->enc_size, info.height_at_ref);
            if (info.ref_inside)
            {
                at_ref++;
                printf("  %s: %d chunks, height at ref %.2f\n", file_name, info.chunks, info.height_at_ref);
            }
        }
        else if (info.is_adt)
        {
            /* ADT without usable chunk positions: keep it under its EKey so it is
             * not lost, and report it so the caller knows why the tile is unknown. */
            char file_name[128];
            snprintf(file_name, sizeof(file_name), "unknown_%02x%02x%02x%02x.adt",
                     entry->ekey[0], entry->ekey[1], entry->ekey[2], entry->ekey[3]);
            char out_path[1024];
            join_path(out_path, sizeof(out_path), out_dir, file_name);
            FILE *out = fopen(out_path, "wb");
            if (out != NULL)
            {
                fwrite(decoded, 1, decoded_size, out);
                fclose(out);
                wrote++;
            }
            fprintf(csv, "%s,,,,%d,%u,%.2f\n", file_name, info.chunks, entry->enc_size, info.height_at_ref);
        }

        free(decoded);
    }

    if (archive != NULL) fclose(archive);
    fclose(csv);
    free(payload);
    free(list.items);

    printf("%ld entries screened out by the MVER pre-filter\n", skipped);
    printf("%ld payloads decoded, %ld ADTs, %ld with a solved tile, %ld written (%ld as alternates)\n",
           read, adts, tiles, wrote, alternatives);
    if (ref != NULL) printf("%ld tiles cover the reference point and report a height\n", at_ref);
    printf("index: %s\n", csv_path);
    if (tiles == 0)
    {
        printf("\nnothing looked like a root ADT. The store may be encrypted, or --data may\n"
               "point at a folder without data.### archives.\n");
        return 1;
    }
    return 0;
}

static int parse_xyz(const char *text, float *xyz)
{
    if (text == NULL) return 0;
    return sscanf(text, "%f,%f,%f", &xyz[0], &xyz[1], &xyz[2]) >= 2;
}

int main(int argc, char **argv)
{
    const char *data_dir = NULL;
    const char *out_dir = NULL;
    float ref[3];
    int have_ref = 0;
    int verbose = 0;
    long limit = 0;
    int32_t map_id = 1;
    int prefilter = 1;

    if (argc < 2)
    {
        fprintf(stderr,
                "usage: cascdump list --data <dir> [--verbose]\n"
                "       cascdump dump --data <dir> --out <dir> [--map id] [--ref x,y,z] [--limit n]\n"
                "                     [--no-prefilter] [--verbose]\n"
                "\n"
                "  --data   folder holding data.000 and the .idx files, usually \"<wow>\\Data\\data\"\n"
                "  --out    folder for the extracted ADTs (created by the caller)\n"
                "  --map    map id written into the file names, default 1 (Kalimdor)\n"
                "  --ref    a coordinate you know from the game; the tool prints the terrain\n"
                "           height of every candidate tile that covers it\n");
        return 2;
    }

    const char *command = argv[1];
    for (int i = 2; i < argc; i++)
    {
        if (!strcmp(argv[i], "--data") && i + 1 < argc) data_dir = argv[++i];
        else if (!strcmp(argv[i], "--out") && i + 1 < argc) out_dir = argv[++i];
        else if (!strcmp(argv[i], "--map") && i + 1 < argc) map_id = (int32_t)atoi(argv[++i]);
        else if (!strcmp(argv[i], "--ref") && i + 1 < argc) have_ref = parse_xyz(argv[++i], ref);
        else if (!strcmp(argv[i], "--limit") && i + 1 < argc) limit = atol(argv[++i]);
        else if (!strcmp(argv[i], "--verbose")) verbose = 1;
        else if (!strcmp(argv[i], "--no-prefilter")) prefilter = 0;
    }

    if (data_dir == NULL)
    {
        fprintf(stderr, "cascdump: --data is required\n");
        return 2;
    }

    if (!strcmp(command, "list")) return cmd_list(data_dir, verbose);

    if (!strcmp(command, "dump"))
    {
        if (out_dir == NULL)
        {
            fprintf(stderr, "cascdump: --out is required for dump\n");
            return 2;
        }
        return cmd_dump(data_dir, out_dir, map_id, have_ref ? ref : NULL, limit, verbose, prefilter);
    }

    fprintf(stderr, "cascdump: unknown command %s\n", command);
    return 2;
}

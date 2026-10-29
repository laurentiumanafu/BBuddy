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
/* fseeko/ftello and friends, needed because archives are larger than 2 GB. */
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#define _FILE_OFFSET_BITS 64
#endif

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

/* Archives are over a gigabyte, and `long` is 32 bits on Windows, so every seek
 * goes through a 64-bit call. */
static int file_seek(FILE *f, uint64_t offset)
{
#ifdef _WIN32
    return _fseeki64(f, (long long)offset, SEEK_SET);
#else
    return fseeko(f, (off_t)offset, SEEK_SET);
#endif
}

static int64_t file_size(FILE *f)
{
#ifdef _WIN32
    if (_fseeki64(f, 0, SEEK_END) != 0) return -1;
    return (int64_t)_ftelli64(f);
#else
    if (fseeko(f, 0, SEEK_END) != 0) return -1;
    return (int64_t)ftello(f);
#endif
}

static uint8_t *read_file(const char *path, size_t *size_out)
{
    FILE *f = fopen(path, "rb");
    if (f == NULL) return NULL;
    int64_t size = file_size(f);
    file_seek(f, 0);
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

/* Does one 18-byte record look like a real index entry?
 *
 * An entry is a 9-byte EKey, a 40-bit span and a 32-bit size. There is no checksum
 * over the fields themselves, but the span has to point inside the store (archive
 * number below the archive count, offset below the segment size) and the size has
 * to be a plausible encoded size. Random bytes clear that about 1-2 % of the time,
 * real entries essentially always do, which is enough to tell layouts apart. */
static int entry_is_plausible(const uint8_t *e, unsigned segment_bits, uint64_t max_file_offset,
                             unsigned archive_count)
{
    int nonzero = 0, all_ff = 1;
    for (int i = 0; i < 9; i++)
    {
        if (e[i] != 0) nonzero = 1;
        if (e[i] != 0xFF) all_ff = 0;
    }
    if (!nonzero || all_ff) return 0;

    uint64_t span = be40(e + 9);
    uint64_t offset = span & (((uint64_t)1 << segment_bits) - 1);
    uint64_t archive = span >> segment_bits;

    if (archive_count > 0 && archive >= archive_count) return 0;
    if (archive_count == 0 && archive > 0x10000) return 0;
    if (max_file_offset > 0 && offset >= max_file_offset) return 0;

    uint32_t size = be32(e + 14);
    /* A zero size is legitimate: it is the head of a multi-archive span. */
    if (size > 64u * 1024u * 1024u) return 0;
    return 1;
}

typedef struct
{
    int valid;
    int guarded;          /* 4-byte hash in front of every record */
    int paged;            /* records restart on 0x200-byte pages */
    size_t start;         /* where the first record begins */
    size_t stride;        /* distance to the next record */
    size_t entry_offset;  /* record start -> entry start (0 or 4) */
    size_t block_size;    /* layout A: byte length of the entry block */
    unsigned segment_bits;/* bit width of the span that worked */
    double score;
    size_t seen;
} idx_layout;

/* Scores a candidate layout by sampling records and counting plausible entries.
 *
 * Paged layouts restart on every 0x200-byte boundary, and a page ends as soon as a
 * record stops looking like an entry - trailing zero padding is normal, so counting
 * it against the candidate would reject the correct layout. */
/* Blizzard's blocked-index hash is chained per entry, not a single hash over the
 * whole block: every 18-byte record is hashed with the previous result as the seed.
 * Both forms are accepted because tools in the wild wrote either one. */
static uint32_t block_hash_chained(const uint8_t *data, size_t length, size_t stride)
{
    uint32_t hash = 0;
    for (size_t at = 0; stride > 0 && at + stride <= length; at += stride)
        hash = hashlittle(data + at, stride, hash);
    return hash;
}

static double score_layout(const uint8_t *data, size_t size, unsigned segment_bits,
                           uint64_t max_file_offset, unsigned archive_count,
                           size_t start, size_t stride, size_t entry_offset, int paged,
                           size_t samples, size_t *seen, double *quality)
{
    size_t plausible = 0, total = 0, solid = 0;
    *seen = 0;
    *quality = 0.0;

    if (!paged)
    {
        for (size_t at = start; total < samples && at + 18 <= size; at += stride)
        {
            total++;
            const uint8_t *entry = data + at;
            if (entry_is_plausible(entry, segment_bits, max_file_offset, archive_count))
            {
                plausible++;
                int solid_key = 1;
                for (int b = 0; b < 9; b++) if (entry[b] == 0) { solid_key = 0; break; }
                if (solid_key) solid++;
            }
        }
    }
    else
    {
        for (size_t page = start; page + 18 <= size && total < samples; page += 0x200)
        {
            for (size_t at = page + entry_offset; at + 18 <= size && at < page + 0x200; at += stride)
            {
                const uint8_t *entry = data + at;
                if (!entry_is_plausible(entry, segment_bits, max_file_offset, archive_count))
                    break;                                   /* page finished */
                total++;
                plausible++;
                int solid_key = 1;
                for (int b = 0; b < 9; b++) if (entry[b] == 0) { solid_key = 0; break; }
                if (solid_key) solid++;
                if (total >= samples) break;
            }
        }
    }

    *seen = total;
    if (total < 2) return 0.0;

    /* A real EKey is a hash prefix, so all nine bytes are set. A record that is
     * misaligned runs into padding and has zeros in the key, which separates a
     * correct stride from a plausible-looking wrong one. */
    *quality = (double)solid / (double)total;
    return (double)plausible / (double)total + 0.05 * *quality;
}

/* A candidate layout is believed when most sampled records are plausible. With few
 * samples the bar is higher, so a short file cannot be "detected" by luck. */
static int layout_is_believable(double score, size_t seen)
{
    if (seen >= 8) return score >= 0.9;
    return seen >= 2 && score >= 0.999;
}

/* Better score wins; on a tie the candidate that parsed more real records wins, so
 * a wrong stride cannot beat the right one by sampling the same two entries. */
static int layout_is_better(double score, size_t seen, const idx_layout *best)
{
    if (!best->valid) return 1;
    if (score > best->score + 0.001) return 1;
    if (score < best->score - 0.001) return 0;
    return seen > best->seen;
}

/* Tries every layout seen in the wild and keeps the best scoring one.
 *
 *   A  one guarded block: u32 block size + u32 hash, then entries back to back
 *   B  the same entries with no block guard at all
 *   C  per-record guarded pages: 0x200-byte pages, u32 hash in front of each entry
 *   D  pages with the entries packed and no per-record hash
 *
 * Which one a store uses depends on the writer, so nothing here is assumed: the
 * candidates are scored against the store's own limits and the winner is reported
 * with --verbose. */
static idx_layout search_layout(const uint8_t *data, size_t size, unsigned segment_bits,
                                uint64_t max_file_offset, unsigned archive_count)
{
    static const size_t starts[] = { 24, 32, 40, 48, 0x200, 0x400, 0x800, 0x1000 };
    static const size_t strides[] = { 24, 18, 20, 22, 26, 28 };

    idx_layout best;
    memset(&best, 0, sizeof(best));

    for (size_t si = 0; si < sizeof(starts) / sizeof(starts[0]); si++)
    {
        size_t start = starts[si];
        if (start >= size) continue;

        /* Layout A / B: one run of entries. The block guard, if present, gives the
         * exact length, which is the strongest signal available. */
        for (size_t so = 0; so < 2; so++)
        {
            size_t entry_offset = so == 0 ? 8 : 0;      /* 8 = skip block size + hash */
            if (start + entry_offset + 18 > size) continue;

            size_t block_size = 0;
            if (so == 0)
            {
                uint32_t declared = rd32(data + start);
                uint32_t declared_hash = rd32(data + start + 4);
                if (declared == 0 || declared % 18 != 0) continue;
                if (start + 8 + declared > size) continue;
                block_size = declared;
                int hash_ok = hashlittle(data + start + 8, declared, 0) == declared_hash;
                (void)hash_ok;
            }

            size_t seen = 0;
            double quality = 0.0;
            double score = score_layout(data, size, segment_bits, max_file_offset, archive_count,
                                       start + entry_offset, 18, 0, 0, 256, &seen, &quality);
            if (!layout_is_believable(score, seen)) continue;

            /* A declared block length that also hashes correctly is decisive. */
            if (so == 0)
            {
                uint32_t stored = rd32(data + start + 4);
                if (block_hash_chained(data + start + 8, block_size, 18) == stored ||
                    hashlittle(data + start + 8, block_size, 0) == stored)
                    score = 1.5;
            }
            if (block_size > 0) score += 0.25;          /* an exact length beats a guess */

            if (layout_is_better(score, seen, &best))
            {
                memset(&best, 0, sizeof(best));
                best.valid = 1;
                best.guarded = so == 0;
                best.paged = 0;
                best.start = start + entry_offset;
                best.stride = 18;
                best.entry_offset = 0;
                best.block_size = block_size;
                best.score = score;
                best.seen = seen;
            }
        }

        /* Layout C / D: pages. Restarting on a 0x200-byte boundary keeps the
         * alignment when a page ends in the middle of a record. Only page-aligned
         * starts are candidates: a block of entries near the header is covered
         * above, and treating it as pages would parse it with the wrong stride. */
        if (start < 0x200) continue;
        for (size_t st = 0; st < sizeof(strides) / sizeof(strides[0]); st++)
        {
            for (size_t eo = 0; eo < 2; eo++)
            {
                size_t entry_offset = eo == 0 ? 4 : 0;
                size_t stride = strides[st];
                if (start + entry_offset + 18 > size) continue;

                size_t seen = 0;
                double quality = 0.0;
                double score = score_layout(data, size, segment_bits, max_file_offset, archive_count,
                                           start, stride, entry_offset, 1, 256, &seen, &quality);
                if (!layout_is_believable(score, seen)) continue;

                if (layout_is_better(score, seen, &best))
                {
                    memset(&best, 0, sizeof(best));
                    best.valid = 1;
                    best.guarded = eo == 0;
                    best.paged = 1;
                    best.start = start;
                    best.stride = stride;
                    best.entry_offset = entry_offset;
                    best.score = score;
                    best.seen = seen;
                }
            }
        }
    }

    if (best.valid && best.paged)
    {
        for (size_t page = best.start; page + 18 <= size; page += 0x200)
        {
            if (entry_is_plausible(data + page + best.entry_offset, segment_bits, max_file_offset, archive_count))
            {
                best.start = page;
                break;
            }
        }
    }
    best.segment_bits = segment_bits;
    return best;
}

/* Detects the layout, and with it the width of the span field.
 *
 * The width says how the 40-bit span is split into archive number and offset, and
 * getting it wrong shifts every archive number - so the store's own archive count
 * is used to sanity check it. The value in the header is tried first, then the
 * neighbouring widths, and the best scoring combination wins. */
static idx_layout detect_layout(const uint8_t *data, size_t size, unsigned segment_bits,
                               uint64_t max_file_offset, unsigned archive_count, int verbose,
                               const char *path)
{
    static const unsigned fallback_bits[] = { 30, 31, 32, 29, 28, 27, 26 };

    idx_layout best;
    memset(&best, 0, sizeof(best));
    unsigned best_bits = segment_bits;

    for (int attempt = -1; attempt < (int)(sizeof(fallback_bits) / sizeof(fallback_bits[0])); attempt++)
    {
        unsigned bits = attempt < 0 ? segment_bits : fallback_bits[attempt];
        if (bits < 20 || bits > 40) continue;

        idx_layout found = search_layout(data, size, bits, max_file_offset, archive_count);
        if (!found.valid) continue;

        if (layout_is_better(found.score, found.seen, &best))
        {
            best = found;
            best_bits = bits;
        }
    }

    if (best.valid)
    {
        best.segment_bits = best_bits;
        if (verbose)
            fprintf(stderr, "  %s: layout %s%s, first record at %zu, stride %zu, span split at %u bits, score %.2f\n",
                    path, best.paged ? "paged" : "block", best.guarded ? "+guard" : "",
                    best.start, best.stride, best_bits, best.score);
    }
    else if (verbose)
    {
        fprintf(stderr, "  %s: no known entry layout matched\n", path);
    }
    return best;
}

static void collect_entries(entry_list *list, const uint8_t *data, size_t size, const idx_layout *layout,
                            unsigned segment_bits, uint64_t max_file_offset)
{
    if (layout->paged)
    {
        for (size_t page = layout->start; page < size; page += 0x200)
        {
            size_t missing = 0;
            for (size_t at = page; at + layout->entry_offset + 18 <= size && at < page + 0x200; at += layout->stride)
            {
                const uint8_t *entry = data + at + layout->entry_offset;
                if (!entry_is_plausible(entry, segment_bits, max_file_offset, 0))
                {
                    /* Two bad records in a row end the page, one is tolerated. */
                    if (++missing >= 2) break;
                    continue;
                }
                missing = 0;
                push_entry(list, entry, segment_bits, max_file_offset);
            }
        }
        return;
    }

    size_t end = layout->block_size ? layout->start + layout->block_size : size;
    if (end > size) end = size;
    for (size_t at = layout->start; at + 18 <= end; at += layout->stride)
        push_entry(list, data + at, segment_bits, max_file_offset);
}

/* Parses one .idx file. Returns the number of entries taken from it, or -1. */
static int parse_index(entry_list *list, const char *path, int verbose, unsigned archive_count)
{
    size_t size = 0;
    uint8_t *data = read_file(path, &size);
    if (data == NULL) return -1;

    int result = -1;
    size_t before = list->count;

    do
    {
        if (size < 32)
        {
            if (verbose) fprintf(stderr, "  %s: too small\n", path);
            break;
        }

        /* Header block: u32 length + u32 hash, then the version 2 header. Version 1
         * stores are older than anything this tool needs, but reporting the revision
         * turns "no entries" into an answerable question. */
        uint32_t block_size = rd32(data);
        uint16_t revision = rd16(data + 8);

        if (block_size != 16 || revision != 7)
        {
            if (verbose)
                fprintf(stderr, "  %s: header block %u, revision %u (expected 16 and 7)\n",
                        path, block_size, revision);
        }

        uint8_t span_size_bytes = data[12];
        uint8_t span_offset_bytes = data[13];
        uint8_t key_bytes = data[14];
        unsigned segment_bits = data[15];
        uint64_t max_file_offset = rd64(data + 16);

        if (span_size_bytes != 4 || span_offset_bytes != 5 || key_bytes != 9 ||
            segment_bits == 0 || segment_bits > 40)
        {
            if (verbose)
                fprintf(stderr, "  %s: entry fields size %u/offset %u/key %u/bits %u, falling back to a scan\n",
                        path, span_size_bytes, span_offset_bytes, key_bytes, segment_bits);
            segment_bits = 30;
            if (max_file_offset == 0) max_file_offset = 0x40000000u;
        }

        list->segment_bits = segment_bits;
        list->max_file_offset = max_file_offset;
        if (hashlittle(data + 8, 16, 0) != rd32(data + 4)) list->bad_hash++;

        idx_layout layout = detect_layout(data, size, segment_bits, max_file_offset, archive_count,
                                         verbose, path);
        if (!layout.valid)
        {
            if (verbose) fprintf(stderr, "  %s: %zu bytes, no entries found\n", path, size);
            break;
        }

        if (layout.segment_bits) list->segment_bits = layout.segment_bits;
        collect_entries(list, data, size, &layout, layout.segment_bits ? layout.segment_bits : segment_bits,
                        max_file_offset);
        result = (int)(list->count - before);
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
static int payload_starts_with_mver(FILE *archive, uint64_t offset, uint32_t enc_size)
{
    static const size_t attempts[2] = { 4096, 65536 };
    uint8_t *head = (uint8_t *)malloc(attempts[1]);
    if (head == NULL) return 1;              /* no memory: do not lose the entry */

    int verdict = 0;
    for (int attempt = 0; attempt < 2 && !verdict; attempt++)
    {
        size_t want = attempts[attempt];
        if (want > enc_size) want = enc_size;

        if (file_seek(archive, offset) != 0) break;
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

    char data_names[4096][256];
    int data_count = list_dir(data_dir, data_names, 4096);
    unsigned archives = 0;
    for (int i = 0; i < data_count; i++)
        if (strncmp(data_names[i], "data.", 5) == 0) archives++;

    entry_list list;
    memset(&list, 0, sizeof(list));
    for (int i = 0; i < idx_count; i++)
    {
        char path[1024];
        join_path(path, sizeof(path), data_dir, names[i]);
        if (parse_index(&list, path, verbose, archives) < 0)
            fprintf(stderr, "  %s: no entries read - run \"cascdump inspect --data <dir>\" for the header\n", names[i]);
    }

    printf("index files: %d, entries: %zu, data archives: %u\n", idx_count, list.count, archives);
    if (list.bad_hash) printf("entries with a header hash mismatch: %d (parsed anyway)\n", list.bad_hash);
    printf("segment bits: %u, max archive offset: %llu\n",
           list.segment_bits, (unsigned long long)list.max_file_offset);

    size_t adt_range = 0;
    for (size_t i = 0; i < list.count; i++)
        if (list.items[i].enc_size > 2000 && list.items[i].enc_size < 4u * 1024u * 1024u) adt_range++;
    printf("entries in ADT size range (2 KB - 4 MB): %zu\n", adt_range);
    if (list.count == 0)
    {
        printf("\nno entries could be read from these index files.\n"
               "run \"cascdump inspect --data \"%s>\"\" and keep the output: it shows the header\n"
               "and the first bytes of each index, which is what identifies the layout.\n", data_dir);
        free(list.items);
        return 1;
    }

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

    char data_names[4096][256];
    int data_count = list_dir(data_dir, data_names, 4096);
    unsigned archives = 0;
    for (int i = 0; i < data_count; i++)
        if (strncmp(data_names[i], "data.", 5) == 0) archives++;

    entry_list list;
    memset(&list, 0, sizeof(list));
    for (int i = 0; i < idx_count; i++)
    {
        char path[1024];
        join_path(path, sizeof(path), data_dir, names[i]);
        parse_index(&list, path, verbose, archives);
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

        if (prefilter && !payload_starts_with_mver(archive, offset, entry->enc_size))
        {
            skipped++;
            continue;
        }

        if (verbose && i < 8) fprintf(stderr, "  entry %zu: archive %u offset %llu size %u\n",
                                     i, archive_index, (unsigned long long)offset, entry->enc_size);
        if (file_seek(archive, offset) != 0) continue;
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

/* Header report for one index file, plus the first bytes of the file and of the
 * first page. This exists because "0 entries" on an unfamiliar store is not
 * answerable without seeing the bytes. */
static void inspect_one(const char *path, unsigned archive_count, uint64_t largest_archive)
{
    size_t size = 0;
    uint8_t *data = read_file(path, &size);
    if (data == NULL)
    {
        printf("%s: cannot read\n\n", path);
        return;
    }

    const char *name = strrchr(path, '/');
    if (name == NULL) name = strrchr(path, '\\');
    name = name ? name + 1 : path;

    printf("%s  %zu bytes\n", name, size);
    if (size < 32)
    {
        printf("  too small to be an index\n\n");
        free(data);
        return;
    }

    uint32_t block_size = rd32(data);
    uint32_t block_hash = rd32(data + 4);
    int hash_ok = hashlittle(data + 8, block_size < size - 8 ? block_size : 16, 0) == block_hash;

    printf("  header block %u (hash %s), revision %u, bucket %u, flags %u\n",
           block_size, hash_ok ? "ok" : "mismatch", rd16(data + 8), data[10], data[11]);
    printf("  entry fields: span size %u, span offset %u, key %u, segment bits %u\n",
           data[12], data[13], data[14], data[15]);

    uint64_t max_file_offset = rd64(data + 16);
    printf("  max file offset %llu (%.1f GiB), data archives %u, largest archive %llu\n",
           (unsigned long long)max_file_offset, (double)max_file_offset / (1024.0 * 1024.0 * 1024.0),
           archive_count, (unsigned long long)largest_archive);

    printf("  first 48 bytes:");
    for (int i = 0; i < 48 && (size_t)i < size; i++) printf(" %02x", data[i]);
    printf("\n");

    if (size > 0x1000 + 48)
    {
        printf("  page 0x1000:    ");
        for (int i = 0; i < 48; i++) printf(" %02x", data[0x1000 + i]);
        printf("\n");
    }

    unsigned segment_bits = data[15];
    if (segment_bits == 0 || segment_bits > 40) segment_bits = 30;

    idx_layout layout = detect_layout(data, size, segment_bits, max_file_offset, archive_count, 1, path);
    if (!layout.valid)
    {
        printf("  detected:       nothing. If every field above looks sane, send this report.\n\n");
        free(data);
        return;
    }

    printf("  detected:       %s%s, first record at %zu, entry offset %zu, stride %zu\n",
           layout.paged ? "paged" : "block", layout.guarded ? "+guard" : "",
           layout.start, layout.entry_offset, layout.stride);

    entry_list sample;
    memset(&sample, 0, sizeof(sample));
    sample.segment_bits = segment_bits;
    sample.max_file_offset = max_file_offset;

    idx_layout capped = layout;
    capped.block_size = 0;
    size_t guard = 0;
    for (size_t at = layout.start; guard < 3 && at + layout.entry_offset + 18 <= size; )
    {
        const uint8_t *entry = data + at + layout.entry_offset;
        if (entry_is_plausible(entry, segment_bits, max_file_offset, archive_count))
        {
            uint64_t span = be40(entry + 9);
            printf("    entry %zu: ekey %02x%02x%02x.. archive %llu offset %llu size %u\n", guard++,
                   entry[0], entry[1], entry[2],
                   (unsigned long long)(span >> segment_bits),
                   (unsigned long long)(span & (((uint64_t)1 << segment_bits) - 1)),
                   be32(entry + 14));
        }
        at += layout.stride;
        if (!layout.paged && at > layout.start + 4096) break;
    }

    entry_list all;
    memset(&all, 0, sizeof(all));
    collect_entries(&all, data, size, &capped, segment_bits, max_file_offset);
    printf("    %zu entries in this file\n\n", all.count);
    free(all.items);
    free(sample.items);
    free(data);
}

static int cmd_inspect(const char *data_dir, int verbose)
{
    char names[64][256];
    int idx_count = collect_indexes(data_dir, names, 64);
    if (idx_count == 0)
    {
        fprintf(stderr, "cascdump: no .idx files in %s\n", data_dir);
        return 1;
    }

    char data_names[4096][256];
    int data_count = list_dir(data_dir, data_names, 4096);
    unsigned archives = 0;
    uint64_t largest = 0;
    for (int i = 0; i < data_count; i++)
    {
        if (strncmp(data_names[i], "data.", 5) != 0) continue;
        archives++;
        char full[1024];
        join_path(full, sizeof(full), data_dir, data_names[i]);
        FILE *f = fopen(full, "rb");
        if (f == NULL) continue;
        int64_t size = file_size(f);
        fclose(f);
        if (size > 0 && (uint64_t)size > largest) largest = (uint64_t)size;
    }

    printf("%d index files, %u data archives, largest archive %llu bytes\n\n",
           idx_count, archives, (unsigned long long)largest);

    int shown = 0;
    for (int i = 0; i < idx_count; i++)
    {
        if (!verbose && shown >= 3) break;      /* three is enough to see the pattern */
        char path[1024];
        join_path(path, sizeof(path), data_dir, names[i]);
        inspect_one(path, archives, largest);
        shown++;
    }
    if (!verbose && idx_count > shown)
        printf("(%d more index files, add --verbose to list them all)\n", idx_count - shown);
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
                "       cascdump inspect --data <dir> [--verbose]\n"
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
    if (!strcmp(command, "inspect")) return cmd_inspect(data_dir, verbose);

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

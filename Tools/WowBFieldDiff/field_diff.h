#ifndef FIELD_DIFF_H
#define FIELD_DIFF_H
// Pure word-level change tracking for a remote memory window. No OS calls, so
// it can be exercised offline. A "word" is 4 bytes; offsets are byte offsets
// relative to the window start.
#include <stdint.h>
#include <stddef.h>

#define FD_MAX_WORDS 2048      // 8 KiB window
#define FD_MAX_TRACKED 512     // distinct offsets reported
#define FD_MIN_CHANGES 1

typedef struct fd_entry {
    uint32_t offset;        // byte offset in window, 4-byte aligned
    uint32_t changes;       // number of polls in which it differed
    uint64_t first_tick;    // ms since capture start of the first change
    uint64_t last_tick;     // ms of the most recent change
    uint32_t first_old, first_new, last_new;
} fd_entry;

typedef struct fd_state {
    uint32_t prev[FD_MAX_WORDS];
    uint32_t cur[FD_MAX_WORDS];
    unsigned words;
    unsigned tracked;       // entries in use
    unsigned dropped;       // changes seen for offsets beyond FD_MAX_TRACKED
    uint64_t changes;       // total changed words over all polls
    uint64_t polls;
    fd_entry entry[FD_MAX_TRACKED];
} fd_state;

static void fd_init(fd_state *s, const void *snapshot, unsigned bytes) {
    unsigned limit = bytes/4;
    if (limit > FD_MAX_WORDS) limit = FD_MAX_WORDS;
    s->words = limit; s->tracked = 0; s->dropped = 0; s->changes = 0; s->polls = 0;
    for (unsigned i = 0; i < limit; i++) {
        s->prev[i] = ((const uint32_t *)snapshot)[i];
        s->cur[i] = s->prev[i];
    }
}

// Compares the freshly read buffer with the previous poll and updates the
// per-offset summary. Returns the number of changed words in this poll.
static unsigned fd_compare(fd_state *s, const void *latest, uint64_t tick) {
    unsigned changed = 0;
    s->polls++;
    for (unsigned i = 0; i < s->words; i++) {
        uint32_t v = ((const uint32_t *)latest)[i];
        s->cur[i] = v;
        if (v == s->prev[i]) continue;
        changed++;
        unsigned offset = i * 4;
        unsigned found = s->tracked;
        for (unsigned k = 0; k < s->tracked; k++) {
            if (s->entry[k].offset == offset) { found = k; break; }
        }
        if (found == s->tracked) {
            if (s->tracked >= FD_MAX_TRACKED) { s->dropped++; continue; }
            fd_entry *e = &s->entry[s->tracked++];
            e->offset = offset; e->changes = 0; e->first_tick = tick;
            e->first_old = s->prev[i]; e->first_new = v;
        }
        fd_entry *e = &s->entry[found];
        if (e->changes == 0) { e->first_old = s->prev[i]; e->first_new = v; e->first_tick = tick; }
        e->changes++;
        e->last_tick = tick;
        e->last_new = v;
        s->prev[i] = v;
    }
    s->changes += changed;
    return changed;
}

// Insertion sort by change count, descending. Small arrays only.
static void fd_sort(fd_state *s) {
    for (unsigned i = 1; i < s->tracked; i++) {
        fd_entry key = s->entry[i];
        unsigned j = i;
        while (j > 0 && s->entry[j-1].changes < key.changes) { s->entry[j] = s->entry[j-1]; j--; }
        s->entry[j] = key;
    }
}

static int fd_count(const fd_state *s, uint32_t offset) {
    for (unsigned k = 0; k < s->tracked; k++)
        if (s->entry[k].offset == offset) return (int)s->entry[k].changes;
    return 0;
}
#endif

#pragma once
#include <stddef.h>
#include <stdint.h>
#define PLAYER_GUID_RVA 0x61C72F0ULL
#define POOLS_RVA 0x582D150ULL
#define PLAYER_MAX_USER 0x00007FFFFFFFFFFFULL

typedef int (*player_read_fn)(void *context, uint64_t address, void *output, size_t size);
typedef struct player_anchor {
    uint64_t guid[2], record, pool, elements, slot;
    uint32_t tag, marker, family, index;
} player_anchor;
int player_anchor_current(player_read_fn read, void *context, uint64_t base, const player_anchor *a);
int player_find(player_read_fn read, void *context, uint64_t base, player_anchor *out);

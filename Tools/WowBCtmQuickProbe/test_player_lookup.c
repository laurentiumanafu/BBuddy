// Offline synthetic tests of the exact lookup code; no Windows or game involved.
#include "player_lookup.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define BASE 0x140000000ULL
#define POOL 0x300000ULL
#define SLOTS 0x400000ULL
#define REC 0x500000ULL
#define REC2 0x600000ULL
typedef struct fixture { unsigned char guid[16],table[256],pool[24],slots[64],record[128],record2[128]; int mutate, reads; } fixture;
static void w32(unsigned char *p,uint32_t n) { memcpy(p,&n,4); }
static void w64(unsigned char *p,uint64_t n) { memcpy(p,&n,8); }
static int mem_read(void *ctx, uint64_t address, void *out, size_t size) {
    fixture *f=(fixture*)ctx;
    if (address==BASE+PLAYER_GUID_RVA && f->mutate && ++f->reads==2) f->guid[0]^=1;
    struct region { uint64_t base; size_t len; unsigned char *data; } r[]={
        {BASE+PLAYER_GUID_RVA,sizeof(f->guid),f->guid},{BASE+POOLS_RVA,sizeof(f->table),f->table},
        {POOL,sizeof(f->pool),f->pool},{SLOTS,sizeof(f->slots),f->slots},
        {REC,sizeof(f->record),f->record},{REC2,sizeof(f->record2),f->record2}};
    for (size_t i=0;i<sizeof(r)/sizeof(r[0]);i++)
        if (address>=r[i].base && address-r[i].base<=r[i].len && size<=r[i].len-(size_t)(address-r[i].base)) {
            memcpy(out,r[i].data+(size_t)(address-r[i].base),size); return 1;
        }
    return 0;
}
static void setup(fixture *f) {
    memset(f,0,sizeof(*f)); w64(f->guid,0x12345678); w64(f->guid+8,0xabcdef12);
    w64(f->table+8,POOL); w64(f->pool+8,SLOTS); w32(f->pool+16,4);
    w32(f->slots+32,0x80000001); w32(f->slots+36,77); w64(f->slots+40,REC);
    memcpy(f->record+0x18,f->guid,16); memcpy(f->record2+0x18,f->guid,16);
}
int main(void) {
    fixture f; player_anchor a;
    setup(&f); assert(player_find(mem_read,&f,BASE,&a));
    assert(a.record==REC && a.family==1 && a.index==2 && a.slot==SLOTS+32);
    assert(player_anchor_current(mem_read,&f,BASE,&a));
    f.guid[0]^=1; assert(!player_anchor_current(mem_read,&f,BASE,&a));
    setup(&f); assert(player_find(mem_read,&f,BASE,&a)); f.slots[36]^=1; assert(!player_anchor_current(mem_read,&f,BASE,&a));
    setup(&f); w32(f.slots+48,0x80000001); w64(f.slots+56,REC2); assert(!player_find(mem_read,&f,BASE,&a));
    setup(&f); w32(f.pool+16,20001); assert(!player_find(mem_read,&f,BASE,&a));
    setup(&f); w32(f.slots+32,1); assert(!player_find(mem_read,&f,BASE,&a));
    setup(&f); w64(f.slots+40,REC+1); assert(!player_find(mem_read,&f,BASE,&a));
    setup(&f); f.mutate=1; assert(!player_find(mem_read,&f,BASE,&a));
    puts("player lookup synthetic tests passed (no Windows/game APIs)"); return 0;
}

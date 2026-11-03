#include "player_lookup.h"
#include <stdlib.h>
#include <string.h>
static uint64_t le64(const unsigned char *p) { uint64_t v; memcpy(&v,p,8); return v; }
static uint32_t le32(const unsigned char *p) { uint32_t v; memcpy(&v,p,4); return v; }
static int pointer64(uint64_t p) { return p>=0x10000 && p<PLAYER_MAX_USER && !(p&7); }
int player_anchor_current(player_read_fn read, void *context, uint64_t base, const player_anchor *a) {
    if (!read || !a || a->family>=32 || a->index>=20000 || !pointer64(a->record) ||
        !pointer64(a->pool) || !pointer64(a->elements) || a->slot != a->elements+(uint64_t)a->index*16) return 0;
    unsigned char global[16], pool[8], header[16], slot[16], guid[16];
    return read(context,base+PLAYER_GUID_RVA,global,sizeof(global)) &&
        read(context,base+POOLS_RVA+(uint64_t)a->family*8,pool,sizeof(pool)) &&
        read(context,a->pool+8,header,sizeof(header)) &&
        read(context,a->slot,slot,sizeof(slot)) &&
        read(context,a->record+0x18,guid,sizeof(guid)) &&
        le64(global)==a->guid[0] && le64(global+8)==a->guid[1] &&
        le64(pool)==a->pool && le64(header)==a->elements &&
        le32(header+8)>a->index && le32(header+8)<=20000 &&
        le32(slot)==a->tag && (a->tag&0x80000000u) && le32(slot+4)==a->marker &&
        le64(slot+8)==a->record && le64(guid)==a->guid[0] && le64(guid+8)==a->guid[1];
}
int player_find(player_read_fn read, void *context, uint64_t base, player_anchor *out) {
    if (!read || !out) return 0;
    unsigned char guid[16], table[256];
    if (!read(context,base+PLAYER_GUID_RVA,guid,16) || !read(context,base+POOLS_RVA,table,sizeof(table))) return 0;
    uint64_t lo=le64(guid), hi=le64(guid+8); if (!(lo|hi)) return 0;
    int matches=0; player_anchor found; memset(&found,0,sizeof(found));
    for (uint32_t family=0;family<32;family++) {
        uint64_t pool=le64(table+8*family); unsigned char head[24];
        if (!pointer64(pool) || !read(context,pool,head,sizeof(head))) continue;
        uint64_t elems=le64(head+8); uint32_t count=le32(head+16);
        if (!pointer64(elems) || !count || count>20000) continue;
        size_t size=(size_t)count*16; unsigned char *slots=(unsigned char*)malloc(size);
        if (!slots) return 0;
        if (!read(context,elems,slots,size)) { free(slots); continue; }
        for (uint32_t i=0;i<count;i++) {
            const unsigned char *s=slots+16*i;
            uint32_t tag=le32(s); uint64_t rec=le64(s+8); unsigned char rg[16];
            if (!(tag&0x80000000u) || !pointer64(rec) || !read(context,rec+0x18,rg,16)) continue;
            if (le64(rg)!=lo || le64(rg+8)!=hi) continue;
            matches++;
            found.guid[0]=lo; found.guid[1]=hi; found.pool=pool; found.elements=elems; found.record=rec;
            found.slot=elems+(uint64_t)16*i; found.tag=tag; found.marker=le32(s+4); found.family=family; found.index=i;
            if (matches>1) { free(slots); return 0; }
        }
        free(slots);
    }
    if (matches!=1 || !player_anchor_current(read,context,base,&found)) return 0;
    *out=found; return 1;
}

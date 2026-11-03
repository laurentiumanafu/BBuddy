#pragma once
// Pure scan core: no Windows headers, so the exact bug class this tool shipped
// with can be tested offline (patterns near buffer edges, unreadable holes,
// wildcards). The Windows layer only supplies buffers and addresses.
#include <stdint.h>
#include <string.h>

typedef struct {
    uint64_t rva;
    unsigned char at[16];   // first bytes at the site, for the report
    unsigned char prev4;    // byte before the site (padding/tail hint)
} sc_hit_t;

// Wildcard match: mask[i]=='x' requires equality; any other symbol means
// "don't care" (rel32 displacements, call targets).
static int sc_match(const unsigned char *p, const unsigned char *pat, const char *mask, unsigned len) {
    for (unsigned i=0;i<len;i++) {
        if (!mask[i]) return 0;               // malformed mask: refuse
        if (mask[i]=='x' && p[i]!=pat[i]) return 0;
    }
    return 1;
}

// Removes duplicate sites (same rva), keeping the first of each, in order.
// Watch mode rescans the same process, so every site is found again on each
// pass: without this a unique function would be counted twice and reported
// AMBIGUOUS - the tool inventing a collision out of its own repetition.
static unsigned sc_dedupe_hits(sc_hit_t *h, unsigned n) {
    unsigned out=0;
    for (unsigned i=0;i<n;i++) {
        int dup=0;
        for (unsigned j=0;j<out;j++) if (h[j].rva==h[i].rva) { dup=1; break; }
        if (!dup) h[out++]=h[i];
    }
    return out;
}

// Scans a buffer for one pattern. A match is only reported when every byte it
// covers is marked readable in `valid`, so a match straddling an unreadable page
// is not silently invented from zero-filled memory. Pass valid=NULL when the
// whole buffer is known good (file scans), which also avoids a needless mask
// allocation for 140 MB images.
// Returns the total number of matches found (which may exceed `cap`); writes at
// most `cap` details and sets *truncated when more existed.
static unsigned sc_scan(const unsigned char *buf, unsigned len,
                        const unsigned char *valid,
                        const unsigned char *pat, const char *mask, unsigned plen,
                        uint64_t baseRva,
                        sc_hit_t *out, unsigned cap, int *truncated) {
    unsigned count=0;
    if (truncated) *truncated=0;
    if (plen==0 || plen>len) return 0;
    unsigned limit = len - plen + 1;
    int firstFixed = (mask[0]=='x');
    unsigned off=0;
    while (off<limit) {
        if (firstFixed) {
            const unsigned char *p=(const unsigned char*)memchr(buf+off,pat[0],limit-off);
            if (!p) break;
            off=(unsigned)(p-buf);
        }
        int readable=1;
        if (valid) for (unsigned i=0;i<plen;i++) if (!valid[off+i]) { readable=0; break; }
        if (readable && sc_match(buf+off,pat,mask,plen)) {
            if (count<cap) {
                sc_hit_t *h=&out[count];
                h->rva=baseRva+off;
                memset(h->at,0,sizeof(h->at));
                memcpy(h->at,buf+off,(plen<16)?plen:16);
                h->prev4 = (off>=1) ? buf[off-1] : 0;
            } else if (truncated) *truncated=1;
            count++;
        }
        off++;
    }
    return count;
}

// Appends this pass's matches after the ones already recorded (`*count`), never
// from index 0. Multi-pass scanning needs this: writing from 0 each pass advances
// the count past the data it has written, and the report then prints an
// uninitialised slot as a second site at rva 0 - which turned four unique
// functions into "AMBIGUOUS" in the first v5 field report.
static unsigned sc_scan_into(const unsigned char *buf, unsigned len, const unsigned char *valid,
                             const unsigned char *pat, const char *mask, unsigned plen,
                             uint64_t baseRva, sc_hit_t *slot, unsigned cap,
                             unsigned *count, int *truncated) {
    if (*count >= cap) {
        // No room left: still count what is there, but nothing can be stored.
        unsigned extra = sc_scan(buf,len,valid,pat,mask,plen,baseRva,NULL,0,truncated);
        *count += extra;
        return extra;
    }
    unsigned c = sc_scan(buf,len,valid,pat,mask,plen,baseRva,slot+*count,cap-*count,truncated);
    *count += c;
    return c;
}

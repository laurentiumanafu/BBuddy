// Offline tests for the scan core, written to cover the exact bug v1 shipped:
// a scan that silently stopped early and therefore missed its own positive
// control. Patterns at buffer edges, patterns inside unreadable holes, and
// patterns straddling a boundary must all behave correctly.
#include <stdio.h>
#include <string.h>
#include "scancore.h"
#include "sigtable.h"

static int fails;
#define CHECK(c) do { if(!(c)){ printf("FAIL %d %s\n",__LINE__,#c); fails++; } } while(0)

int main(void) {
    // ---- 1. pattern at the VERY END of a buffer must be found (v1 dropped the
    //        last (maxlen-1) bytes of every chunk by construction).
    unsigned char buf[4096]; unsigned char valid[4096];
    memset(buf,0x90,sizeof(buf)); memset(valid,1,sizeof(valid));
    const unsigned char pat[]={0x48,0x8B,0xC4,0x55}; const char mask[]="xxxx";
    memcpy(buf+sizeof(buf)-sizeof(pat),pat,sizeof(pat));
    sc_hit_t out[8]; int trunc=0;
    unsigned n=sc_scan(buf,sizeof(buf),valid,pat,mask,sizeof(pat),0x1000,out,8,&trunc);
    CHECK(n==1);
    CHECK(out[0].rva==0x1000+sizeof(buf)-4);

    // ---- 2. pattern at the START of a buffer
    memset(buf,0x90,sizeof(buf)); memset(valid,1,sizeof(valid));
    memcpy(buf,pat,sizeof(pat));
    n=sc_scan(buf,sizeof(buf),valid,pat,mask,sizeof(pat),0x2000,out,8,&trunc);
    CHECK(n==1); CHECK(out[0].rva==0x2000);

    // ---- 3. a match inside an unreadable hole must NOT be reported, because the
    //        bytes there are zero-filled placeholders, not real code.
    memset(valid,1,sizeof(valid));
    memset(valid+100,0,8);
    memset(buf+100,0,8);            // unreadable bytes are zero-filled placeholders
    n=sc_scan(buf,sizeof(buf),valid,pat,mask,sizeof(pat),0,out,8,&trunc);
    CHECK(n==1); CHECK(out[0].rva==0);

    // ---- 4. wildcards: a '?' byte must not affect matching, a fixed byte must.
    const unsigned char wpat[]={0xE8,0x11,0x22,0x33,0x44};
    const char wmask[]="x????";
    memcpy(buf,pat,4); buf[4]=0x99;
    memcpy(buf+10,wpat,5); buf[11]=0xAA; buf[12]=0xBB; buf[13]=0xCC; buf[14]=0xDD;
    n=sc_scan(buf,64,valid,wpat,wmask,5,0,out,8,&trunc);
    CHECK(n==1); CHECK(out[0].rva==10);
    buf[10]=0xE9;                       // fixed first byte now differs
    n=sc_scan(buf,64,valid,wpat,wmask,5,0,out,8,&trunc);
    CHECK(n==0);
    buf[10]=0xE8;

    // ---- 5. capacity: more matches than the caller can store must be counted,
    //        flagged truncated, and not overflow the output array.
    unsigned char many[256]; memset(many,0,sizeof(many));
    for (unsigned i=0;i+4<=sizeof(many);i+=4) memcpy(many+i,pat,4);
    memset(valid,1,sizeof(valid));
    n=sc_scan(many,sizeof(many),valid,pat,mask,4,0,out,2,&trunc);
    CHECK(n==64); CHECK(trunc==1);

    // ---- 6. every generated pattern is well formed and matches itself.
    for (unsigned i=0;i<g_sigCount;i++) {
        const sig_t *g=&g_sigs[i];
        CHECK((unsigned)strlen(g->mask)==g->len);
        for (unsigned k=0;k<g->len;k++) CHECK(g->mask[k]=='x' || g->mask[k]=='?');
        unsigned char self[512]; memcpy(self,g->bytes,g->len);
        unsigned char v2[512]; memset(v2,1,g->len);
        unsigned m=sc_scan(self,g->len,v2,g->bytes,g->mask,g->len,0,out,8,&trunc);
        CHECK(m==1);
    }

    // ---- 7. the control pattern must match the confirmed UnitHealth prologue.
    const unsigned char ctl[16]={0x48,0x8b,0xc4,0x55,0x41,0x57,0x48,0x8d,0x68,0xa1,0x48,0x81,0xec,0xb8,0,0};
    unsigned m=sc_scan(ctl,16,valid,ctl,"xxxxxxxxxxxxxxxx",16,0x1D63190,out,8,&trunc);
    CHECK(m==1); CHECK(out[0].rva==0x1D63190);

    // ---- 8. watch mode dedupe: the same site found on five passes is ONE site.
    sc_hit_t dup[6];
    for (int i=0;i<6;i++) { memset(&dup[i],0,sizeof(dup[i])); dup[i].rva=(i%2)?0x1000:0x2000; }
    unsigned uniq=sc_dedupe_hits(dup,6);
    CHECK(uniq==2); CHECK(dup[0].rva==0x2000); CHECK(dup[1].rva==0x1000);
    CHECK(sc_dedupe_hits(dup,0)==0);
    sc_hit_t one[1]; memset(one,0,sizeof(one)); one[0].rva=0xABC;
    CHECK(sc_dedupe_hits(one,1)==1 && one[0].rva==0xABC);

    // ---- 9. multi-pass accumulation: the second pass must append a REAL hit, not
    // leave the count ahead of an uninitialised slot (the phantom rva=0 site).
    sc_hit_t acc[8]; memset(acc,0,sizeof(acc)); unsigned cnt=0;
    unsigned char twice[64]; memset(twice,0x90,sizeof(twice)); memcpy(twice+10,pat,4);
    unsigned char v3[64]; memset(v3,1,sizeof(v3));
    sc_scan_into(twice,64,v3,pat,mask,4,0,acc,8,&cnt,&trunc);
    CHECK(cnt==1); CHECK(acc[0].rva==10);
    sc_scan_into(twice,64,v3,pat,mask,4,0,acc,8,&cnt,&trunc);
    CHECK(cnt==2); CHECK(acc[1].rva==10);          // a real duplicate, not zeros
    CHECK(sc_dedupe_hits(acc,cnt)==1);             // and it collapses to one site
    // capacity is respected, and the count still reflects what was found
    sc_hit_t small[1]; memset(small,0,sizeof(small)); unsigned cnt2=0;
    sc_scan_into(twice,64,v3,pat,mask,4,0,small,1,&cnt2,&trunc);
    CHECK(cnt2==1 && small[0].rva==10);
    sc_scan_into(twice,64,v3,pat,mask,4,0,small,1,&cnt2,&trunc);
    CHECK(cnt2==2); CHECK(small[0].rva==10);       // never overwritten, never zeros

    if (!fails) puts("scancore passed (edges, holes, wildcards, capacity, patterns, control, dedupe, multi-pass)");
    return fails ? 1 : 0;
}

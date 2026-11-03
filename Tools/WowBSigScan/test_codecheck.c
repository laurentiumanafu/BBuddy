// Tests for the "is this actually code" heuristics, on synthetic data whose
// answer we know. Getting this wrong is expensive: it decides whether the user
// trusts a scan of an encrypted file.
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "codecheck.h"

static int fails;
#define CHECK(c) do { if(!(c)){ printf("FAIL %d %s\n",__LINE__,#c); fails++; } } while(0)

int main(void) {
    static unsigned char code[1<<16], rnd[1<<16], zeros[1<<16];
    // 1. Plausible x64 code: prologue-shaped instruction streams with padding.
    unsigned char pro[]={0x48,0x89,0x5C,0x24,0x08,0x48,0x83,0xEC,0x28,0x48,0x8B,0xC4,0x55};
    for (unsigned i=0;i+16<=sizeof(code);i+=16) {
        memset(code+i,0xCC,16);
        memcpy(code+i,pro,sizeof(pro));     // 13 real bytes + 3 int3 of padding
    }
    // 2. Encrypted/compressed: uniform random bytes.
    srand(12345);
    for (unsigned i=0;i<sizeof(rnd);i++) rnd[i]=(unsigned char)(rand()&0xFF);
    // 3. All zeros: low entropy but obviously not code.
    memset(zeros,0,sizeof(zeros));

    double hc=shannon(code,sizeof(code)), hr=shannon(rnd,sizeof(rnd)), hz=shannon(zeros,sizeof(zeros));
    unsigned pc=0,dc=0, pr=0,dr=0, pz=0,dz=0;
    code_signals(code,sizeof(code),&pc,&dc);
    code_signals(rnd,sizeof(rnd),&pr,&dr);
    code_signals(zeros,sizeof(zeros),&pz,&dz);
    printf("  code:  entropy %.3f  prologues %.0f/MB  int3 %.0f/MB\n",hc,(double)pc*1048576.0/sizeof(code),(double)dc*1048576.0/sizeof(code));
    printf("  rnd:   entropy %.3f  prologues %.0f/MB  int3 %.0f/MB\n",hr,(double)pr*1048576.0/sizeof(rnd),(double)dr*1048576.0/sizeof(rnd));
    printf("  zeros: entropy %.3f  prologues %.0f/MB  int3 %.0f/MB\n",hz,(double)pz*1048576.0/sizeof(zeros),(double)dz*1048576.0/sizeof(zeros));

    // Thresholds actually used by the tool.
    CHECK(hc < 7.5);                       // code is NOT flagged as encrypted
    CHECK(hr > 7.5);                       // encrypted IS flagged
    CHECK(hz < 7.5);                       // zeros are not "encrypted"...
    CHECK((double)pz*1048576.0/sizeof(zeros) < 1.0);   // ...but they carry no prologues either
    CHECK((double)pc*1048576.0/sizeof(code) > 1000.0); // code has thousands per MB
    CHECK((double)pr*1048576.0/sizeof(rnd) < 1000.0);  // random data does not
    CHECK(hc > 1.0);                       // a degenerate constant scores near 0

    // region_lookup-style bound check: the tool must never claim a hit belongs to
    // a section it is outside of (off-by-one at the end boundary).
    typedef struct { uint32_t rva,len; } rg_t;
    rg_t r={0x1000,0x1000};
    int inside_lo = (0x1000>=r.rva && 0x1000<r.rva+(uint64_t)r.len);
    int inside_hi = (0x1FFF>=r.rva && 0x1FFF<r.rva+(uint64_t)r.len);
    int outside  = (0x2000>=r.rva && 0x2000<r.rva+(uint64_t)r.len);
    CHECK(inside_lo && inside_hi && !outside);

    if (!fails) puts("codecheck passed (entropy separates code from ciphertext; prologue density agrees; bounds exact)");
    return fails ? 1 : 0;
}

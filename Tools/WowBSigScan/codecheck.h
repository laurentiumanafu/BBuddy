#pragma once
// Content heuristics used to decide whether a file or region is really code.
// Free of Windows headers so the thresholds can be tested: a wrong verdict here
// sends the user off to analyse an encrypted file.
#include <string.h>
#include <math.h>

// Shannon entropy in bits/byte. Decrypted x64 code sits around 6.0-6.8;
// encrypted or compressed data sits at 7.9+.
static double shannon(const unsigned char *p, unsigned n) {
    if (!n) return 0.0;
    unsigned cnt[256]; memset(cnt,0,sizeof(cnt));
    for (unsigned i=0;i<n;i++) cnt[p[i]]++;
    double h=0.0;
    for (int i=0;i<256;i++) if (cnt[i]) {
        double q=(double)cnt[i]/(double)n;
        h -= q*log2(q);
    }
    return h;
}

// Counts of x64 prologue starts and int3 padding: the two cheapest "is this
// code" signals. Real code has thousands per MB; compressed data has almost none.
static void code_signals(const unsigned char *p, unsigned n, unsigned *prologues, unsigned *padding) {
    unsigned pr=0, pd=0;
    for (unsigned i=0;i+4<=n;i++) {
        if (p[i]==0xCC) pd++;
        if ((p[i]==0x48 && (p[i+1]==0x89 || p[i+1]==0x8B)) ||
            (p[i]==0x40 && (p[i+1]==0x53 || p[i+1]==0x55 || p[i+1]==0x56 || p[i+1]==0x57)) ||
            (p[i]==0x55 && p[i+1]==0x48 && p[i+2]==0x8B) ||
            (p[i]==0x48 && p[i+1]==0x83 && p[i+2]==0xEC)) pr++;
    }
    *prologues=pr; *padding=pd;
}

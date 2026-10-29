#pragma once
// Pure helpers shared by the tool and its offline test: no Windows headers.
#include <math.h>

static double pm_dist3(const float *a, const float *b) {
    double dx=(double)a[0]-b[0], dy=(double)a[1]-b[1], dz=(double)a[2]-b[2];
    return sqrt(dx*dx+dy*dy+dz*dz);
}
// World coordinates in this client's zones sit in the low thousands and are
// finite; reject zeros, denormals and absurd magnitudes so a bad offset is
// obvious instead of silently reported.
static int pm_plausible(const float *v) {
    for (int i=0;i<3;i++) {
        float f=v[i];
        if (!(f==f)) return 0;
        if (f>1e6f || f<-1e6f) return 0;
    }
    double n=fabs(v[0])+fabs(v[1])+fabs(v[2]);
    return n>1.0 && n<1e6;
}

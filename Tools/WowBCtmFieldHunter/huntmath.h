#pragma once
// Pure helper math for the click-to-move destination hunt. No Windows headers,
// so it can be unit-tested offline.
#include <math.h>

static double fh_dist3(const float *a, const float *b) {
    double dx=(double)a[0]-b[0], dy=(double)a[1]-b[1], dz=(double)a[2]-b[2];
    return sqrt(dx*dx+dy*dy+dz*dz);
}

// Horizontal distance only: a destination stored as (x,y) with z from terrain
// would fail a 3-D test but pass this one.
static double fh_dist2(const float *a, const float *b) {
    double dx=(double)a[0]-b[0], dy=(double)a[1]-b[1];
    return sqrt(dx*dx+dy*dy);
}

// A coordinate triple must be finite, non-trivial and in a plausible world range.
static int fh_plausible(const float *v) {
    for (int i=0;i<3;i++) {
        float f=v[i];
        if (!(f==f)) return 0;                    // NaN
        if (f>1e6f || f<-1e6f) return 0;
    }
    double n=fabs(v[0])+fabs(v[1])+fabs(v[2]);
    return n>1.0 && n<1e6;
}

// Horizontal versions. A destination stored as (x,y) with z taken from the
// terrain would be rejected by the 3-D tests: a wrong z of even 50 yd dominates
// the cosine and the approach delta, even though x and y are exactly right.
static double fh_forward_cosine2(const float *candidate, const float *positionAtClick,
                                 const float *start, const float *end) {
    double dirx=(double)end[0]-start[0], diry=(double)end[1]-start[1];
    double vx=(double)candidate[0]-positionAtClick[0];
    double vy=(double)candidate[1]-positionAtClick[1];
    double dn=sqrt(dirx*dirx+diry*diry), vn=sqrt(vx*vx+vy*vy);
    if (dn<1e-6 || vn<1e-6) return 0.0;
    return (dirx*vx+diry*vy)/(dn*vn);
}
static int fh_approaching2(const float *candidate, const float *earlier, const float *later,
                           double minStep) {
    return fh_dist2(candidate,earlier) > fh_dist2(candidate,later) + minStep;
}

// Decides whether a value behaves like a stored click destination: identical
// across the two during-walk samples, inside click range of the player, ahead
// along the walked direction and getting closer. Without a measurable walk the
// geometric tests are skipped, because they would be meaningless.
static int fh_destination_like(const float *candidate, const float *walk1, const float *walk2,
                               int stable, double minStep) {
    if (!stable) return 0;
    if (!fh_plausible(candidate)) return 0;
    double d1=fh_dist2(candidate,walk1);
    if (d1<4.0 || d1>200.0) return 0;
    if (fh_dist2(walk1,walk2)>0.5) {
        if (fh_forward_cosine2(candidate,walk1,walk1,walk2)<0.90) return 0;
        if (!fh_approaching2(candidate,walk1,walk2,minStep)) return 0;
    }
    return 1;
}

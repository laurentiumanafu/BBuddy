import re
p='aimmath.h'; s=open(p).read()
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt, ("count", s.count(old), old[:80])
    s=s.replace(old,new)

# ---- A. slope fit that can reject its own samples -------------------------
rep('''// Least-squares slope of value against px, through the origin.''',
'''// The same fit, but each sample has to earn its place. A click probe can walk
// into a tree, up a slope or along a wall, and the heading it reports is then the
// terrain's, not the camera's: the 2026-10-29 15:18 run kept a +225 px probe that
// walked 2.78 yd and produced -0.16083 deg/px against a 4-probe median of
// -0.12674. A sample is dropped when its own slope is more than `reject` times
// away from the median of all the samples, which needs no absolute reference and
// simply refuses to average a measurement with an obstacle in it.
// Returns how many samples were kept, or 0 when the fit has nothing left.
static int am_median_slope(const int *px, const double *deg, int n,
                           double reject, double *slope) {
    double s[32], res[32];
    int m=0;
    if (n<=0||n>32) return 0;
    for (int i=0;i<n;i++) {
        if (!px[i]) continue;
        double v=deg[i]/(double)px[i];
        if (!am_finite(v)) return 0;
        res[m]=v; s[m]=v; m++;
    }
    if (!m) return 0;
    for (int i=1;i<m;i++) { double k=s[i]; int j=i-1;
        while (j>=0 && s[j]>k) { s[j+1]=s[j]; j--; } s[j+1]=k; }
    double med = (m%2) ? s[m/2] : 0.5*(s[m/2-1]+s[m/2]);
    double sxx=0, sxy=0; int kept=0;
    for (int i=0;i<m;i++) {
        (void)res[i];
        (void)med;
    }
    // second pass with the rejection test
    for (int i=0, k=0;i<n;i++) {
        if (!px[i]) continue;
        double v=res[k++];
        if (m>=4 && fabs(v-med) > reject*fabs(med)) continue;
        sxx+=(double)px[i]*px[i]; sxy+=(double)px[i]*deg[i]; kept++;
    }
    if (slope && sxx>0.0) *slope=sxy/sxx;
    return kept;
}

// Least-squares slope of value against px, through the origin.''')

# ---- B. pass-by detection ------------------------------------------------
rep('''// Does a walk move roughly straight, at roughly walking pace?''',
'''// A click is immediate-mode: the client knows the point it was given and nothing
// else, so when the terrain route bends around a rock the character walks PAST the
// goal and the distance starts to grow again. Nothing in the client stops that,
// and v6 did not either: it kept walking, re-planned from 20+ yd out, and burned
// legs on a goal it had already been 4 yd from. This watches for the closest
// approach and cancels the leg once the goal is clearly behind.
typedef struct { double best; int receding; } am_passby_t;

static void am_passby_reset(am_passby_t *pb) { pb->best=1e30; pb->receding=0; }

// Returns 1 when the walk should be cancelled because it has turned away.
// `passRadius` is how close it must have got for this to be a pass-by rather than
// a leg that simply never approached, and `giveUp` is the growth that counts as
// "moving away" rather than sampling noise.
static int am_passby_stop(am_passby_t *pb, double remaining,
                          double passRadius, double giveUp) {
    if (remaining < pb->best) { pb->best=remaining; pb->receding=0; return 0; }
    if (pb->best <= passRadius && remaining > pb->best + giveUp) {
        if (++pb->receding >= 3) return 1;
    } else {
        pb->receding=0;
    }
    return 0;
}

// Does a walk move roughly straight, at roughly walking pace?''')

# ---- C. only learn a stop distance from a real coast ---------------------
rep('''// Fold one observed coast into the stop distance.''',
'''// Fold one observed coast into the stop distance. A coast under a third of a yard
// is not a reaction time: the character was already slowing when the key landed,
// so the sample says nothing about latency and must leave the estimate alone. The
// 2026-10-29 15:18 run learned 0.18 yd from exactly that and moved the stop
// distance for no reason.''')
open(p,'w').write(s)
print("aimmath patched")

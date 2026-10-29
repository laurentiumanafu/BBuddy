#pragma once
// Pure geometry and control for click-to-move. No Windows headers, so the whole
// planning loop can be simulated offline before it is pointed at a client.
//
// v3 change of policy, driven by measurement: pixel dy does NOT control how far
// the character walks. On the live client a single dy=-20 produced 0.09 to 12.35
// yd across legs (a 150x spread), because the click point is wherever the ray
// meets the terrain - slope, walls and camera pitch decide, not the pixel. The
// fitted "dist = a + b*dy" model was therefore meaningless (and in two runs it
// predicted NEGATIVE distances). Distance is now handled by iteration: click, let
// the character arrive, measure, repeat. dy is only a starting preference.
//
// v5 is about the two ways the v4 field run lied to itself. First, two legs
// reported an impossible geometry: the character ended up FURTHER from a fixed
// goal than it started plus the distance it travelled (2.04 -> 30.83 yd after
// moving 5.45 yd, then 30.83 -> 110.23 yd after moving 0.63 yd). The triangle
// inequality cannot be broken by walking, so the position read was not the
// character's position at that moment - it was a stale or discontinuous sample.
// v5 plans from the position it measured immediately before the click, checks
// every leg against that inequality, and refuses to learn from a leg that fails.
// Second, the click slope drifted from the calibrated -0.1017 to -0.1817 (78%)
// because a 4.3 yd leg carried as much weight as a 21 yd one. Learning is now
// weighted by how far the leg actually travelled, which is the quantity that sets
// how good its heading measurement can be.
//
// v4 adds the second turn channel. The v3 calibration never established
// degPerDragPx, and a walk whose goal sat outside the camera cone then died on
// the first leg with "cannot plan a leg" - the planner had no way to rotate the
// camera. Dragging with the mouse only works while the client is receiving real
// mouse deltas, which a background window may not be doing, so the planner can
// now also rotate by holding a turn key, and learns degrees-per-millisecond the
// same way it learns degrees-per-drag-pixel. Either channel may be the only one
// that works on a given client, so the calibration probes both and the planner
// uses whichever produced a measurable turn.
#include <math.h>

#define AM_D2R 0.017453292519943295
#define AM_R2D 57.29577951308232

#define AM_TURN_NONE 0
#define AM_TURN_DRAG 1
#define AM_TURN_KEY  2

static double am_len(double dx, double dy) { return sqrt(dx*dx + dy*dy); }
static double am_heading_deg(double dx, double dy) { return atan2(dy, dx) * AM_R2D; }

static double am_wrap_deg(double d) {
    while (d > 180.0) d -= 360.0;
    while (d <= -180.0) d += 360.0;
    return d;
}

#ifndef AIM_NO_FIT
static int am_fit(const double *xs, const double *ys, int n, double *a, double *b) {
    if (n < 2 || !a || !b) return 0;
    double sx=0, sy=0, sxx=0, sxy=0;
    for (int i=0;i<n;i++) { sx+=xs[i]; sy+=ys[i]; sxx+=xs[i]*xs[i]; sxy+=xs[i]*ys[i]; }
    double denom = (double)n*sxx - sx*sx;
    if (fabs(denom) < 1e-9) return 0;
    *a = ((double)n*sxy - sx*sy) / denom;
    *b = (sy - (*a)*sx) / (double)n;
    return 1;
}

static double am_rms(const double *xs, const double *ys, int n, double a, double b) {
    if (n <= 0) return 0.0;
    double s=0;
    for (int i=0;i<n;i++) { double e = ys[i] - (a*xs[i] + b); s += e*e; }
    return sqrt(s / (double)n);
}
#endif   // AIM_NO_FIT

#ifndef AIM_NO_INVERT
static int am_invert(double a, double b, double target, double *x) {
    if (!x || fabs(a) < 1e-9) return 0;
    *x = (target - b) / a;
    return 1;
}
#endif

// What the tool has learned about this camera, and what it can command.
typedef struct {
    double headingAtZero;   // world heading walked when clicking screen centre
    double degPerPx;        // heading change per pixel of horizontal click offset
    double degPerDragPx;    // heading change per pixel of horizontal right-drag
    double degPerKeyMs;     // heading change per ms of held turn key
    double degPerPxRef;     // calibrated magnitude, used to reject nonsense updates
    double degPerDragPxRef;
    double degPerKeyMsRef;
    int halfWidth;          // half the client width in pixels
    double coneHalfDeg;     // half the angular width reachable by clicking
    double tol;             // goal tolerance in yards
    int preferredDy;        // vertical click offset to use for every leg
    int nearDy;             // shorter hop, used when turning the camera
    double minLearnMove;    // a leg must move this far before it teaches anything
    double maxLearnMove;    // beyond this the chord has been deflected by terrain
    double nearGoal;        // inside this distance, use the short hop
    int turnKey;            // 1 = rotate with the key channel, 0 = with drags
    double maxTurnMs;       // cap on one key hold
} aim_params;

typedef struct {
    int clickPx;
    int dy;
    int turn;               // AM_TURN_*
    int dragPx;             // valid when turn == AM_TURN_DRAG
    int keyMs;              // valid when turn == AM_TURN_KEY
} leg_t;

// Should a running click be cancelled now?
//
// The stop is not instantaneous: the client keeps running for a moment after the
// key is pressed, so the walk must be cancelled EARLY by however far it coasts.
// `coast` is measured per stop (distance from the key press to standstill), not
// assumed, and `capture` is the radius we are willing to call arrived.
static int am_should_stop(double remaining, double coast, double capture) {
    return remaining <= capture + coast;
}

// The smallest coast that counts as evidence. A character that was ALREADY
// slowing when the key arrived coasts a few centimetres, and that number says
// nothing about how long the client takes to react to the key - it is a sample
// of the character stopping anyway. v6 learned from one such stop (0.07 yd) and
// pulled the stop distance from 0.80 to 0.58 yd on no evidence at all.
#define AM_MIN_COAST_LEARN 0.30

// Learns how far the character coasts after the stop key. Anything past 8 yd
// means the key was not seen at all and the sample must not be believed.
static double am_learn_coast(double estimate, double measured) {
    if (measured < 0.0) measured = 0.0;
    if (measured < AM_MIN_COAST_LEARN) return estimate < 0.0 ? 0.0 : estimate;
    if (measured > 8.0) return estimate < 0.0 ? 0.0 : estimate;
    if (estimate < 0.0) estimate = 0.0;
    return 0.7*estimate + 0.3*measured;
}

// ------------------------------------------------------------- sensitivity fit
// The camera yaw at the moment of a click is the heading of the PREVIOUS walk,
// not the heading the calibration started from: WoW's follow camera swings back
// behind the character while it runs. Measured on 2026-10-29, six probes taken as
// one sequence gave slopes of -0.086, -0.070, -0.142, -0.039, -0.106, -0.085
// against their predecessor's heading, and +0.084, +0.003, +0.025 for the three
// negative offsets when measured against the fixed start heading. One reference
// is a camera model; the other is a camera plus six unmeasured rotations.
//
// A slope per probe, then the median, is the estimator that survives this: it
// needs no assumption that the response is linear across the whole range and it
// throws away the probes whose walk was cut short (the -0.142 above came from a
// walk that stopped after 8.33 yd, so the camera had barely re-centred).
static int am_median_slope(int n, const double *driver, const double *turn,
                           double rejectRatio, double *slope) {
    double s[64];
    int m=0;
    if (n<=0 || n>64 || !slope) return 0;
    for (int i=0;i<n;i++) {
        if (driver[i]==0.0) continue;
        s[m++]=turn[i]/driver[i];
    }
    if (m<2) return 0;
    for (int i=1;i<m;i++) {          // insertion sort: a handful of probes
        double v=s[i]; int j=i-1;
        while (j>=0 && s[j]>v) { s[j+1]=s[j]; j--; }
        s[j+1]=v;
    }
    double med = (m&1) ? s[m/2] : 0.5*(s[m/2-1]+s[m/2]);
    if (med==0.0) return 0;
    // Reject probes more than rejectRatio times the median away from it, then
    // average what is left: the median of a handful is coarse, the surviving
    // mean is not.
    double sum=0.0; int kept=0;
    for (int i=0;i<m;i++) {
        if (fabs(s[i]-med) > rejectRatio*fabs(med)) continue;
        sum+=s[i]; kept++;
    }
    if (!kept) return 0;
    *slope=sum/kept;
    return kept;
}

// Does the fitted model predict a measurement it was not fitted on? Anything
// outside max(absTol, relTol*|predicted|) falsifies the fit. This is the check
// the last two field runs did not have: both were one-sample fits, both claimed a
// slope, and they differed by 3.3x.
static int am_prediction_ok(double measured, double predicted, double relTol, double absTol) {
    return fabs(measured-predicted) <= fmax(absTol, relTol*fabs(predicted));
}

// ---------------------------------------------------------------- pass-by stop
// Click-to-move is immediate-mode: the client knows only the click point, so a
// click that lands a few degrees off - or on terrain whose route bends around an
// obstacle - walks PAST the goal and `remaining` starts growing again. v6 kept
// walking in those cases and re-planned from 20+ yd away four times (9.70 ->
// 18.68, 2.72 -> 21.38, 17.06 -> 20.10 yd), which looks like a broken model and
// is really a lateral miss. There is nothing to gain by letting it run: the leg
// will not come back on its own. So the walk is cancelled at the closest
// approach instead, and the next leg plans from there.
typedef struct {
    double best;    // smallest remaining seen since this leg's click
    int    receding;
} am_passby_t;

static void am_passby_reset(am_passby_t *s) { s->best=1e9; s->receding=0; }

// One entry per poll. Returns 1 when the character has clearly passed the goal:
// it came within `passRadius` and has since moved more than `giveUp` further
// away, three polls in a row (so one noisy sample cannot end a good leg).
static int am_passby_stop(am_passby_t *s, double remaining, double passRadius, double giveUp) {
    if (remaining < s->best) { s->best=remaining; s->receding=0; return 0; }
    if (s->best<=passRadius && remaining>s->best+giveUp) {
        // Three polls of grace, or 4x the give-up distance, whichever comes first:
        // at run speed a 60 ms poll is about 0.4 yd, so three polls is already a
        // yard and a half of running the wrong way.
        if (++s->receding>=3) return 1;
        if (remaining > s->best + 4.0*giveUp) return 1;
        return 0;
    }
    s->receding=0;
    return 0;
}

// A leg's measurements must satisfy the triangle inequality. For a fixed goal,
// whatever the character did, it cannot finish further away than it started plus
// the distance it travelled. A leg that violates this was measured against a
// position that is not the character's real position, so its heading is fiction.
static int am_leg_consistent(double remainingBefore, double remainingAfter,
                             double moved, double slack) {
    return remainingAfter <= remainingBefore + moved + slack;
}

// A whole LEG is judged by its average speed, not by the per-poll cap. v8 put a
// 15.70 s leg through am_plausible_step, which clamps dt at 3 s and so allows at
// most 45 yd - and threw away a real 50.19 yd leg as "discontinuous geometry" on
// the 2026-10-29 run, losing the one leg that was making progress.
static int am_plausible_leg(double dist, double secs) {
    if (secs < 0.0) secs = 0.0;
    if (secs > 120.0) secs = 120.0;
    return dist <= 15.0*secs + 1.0;
}

// Which way to step around whatever is in the way. Alternating matters: a bot
// that always tries the same side spends every attempt against the same rock,
// and one that alternates walks a box around it. Attempt 1 goes to +1.
static int am_detour_side(int attempt) {
    return (attempt & 1) ? +1 : -1;
}

// Would a step of `dist` yards between two reads `dtSec` apart be physically
// possible? Run speed is about 7 yd/s; 15 gives room for a fall, a knockback or a
// slow sample without admitting a garbage read (the field run produced a 79 yd
// jump between two consecutive positions).
static int am_plausible_step(double dist, double dtSec) {
    if (dtSec < 0) dtSec = 0;
    if (dtSec > 3.0) dtSec = 3.0;      // longer gaps are judged by the leg check
    return dist <= 1.0 + 12.0*dtSec;
}

// Updates a slope estimate from one observation, robustly.
//
// A leg that barely moved, or moved while being pushed along a wall, reports a
// heading that has nothing to do with the click. Feeding that in raw is what
// flipped degPerPx from -0.10 to -0.31 and degPerDragPx from -0.38 to +0.17 in
// the field run. So: ignore small movements, ignore estimates that disagree with
// the calibrated magnitude by more than 3x, and smooth what is accepted.
// Returns 1 when the estimate was updated.
static int am_learn_slope(double *est, double reference, double measured, double driver,
                          double minMove, double maxMove, double moved, double alpha) {
    if (!est || fabs(driver) < 1e-6) return 0;
    if (moved < minMove) return 0;                  // too little travel to trust
    if (maxMove > minMove && moved > maxMove) return 0;   // long chord, deflected
    double candidate = measured / driver;
    if (fabs(candidate) < 1e-6) return 0;
    double refMag = fabs(reference);
    if (refMag > 1e-6) {
        // Sign must agree with the calibrated sense, magnitude within 2x. The
        // field run accepted a 1.8x outlier on a 9 yd leg and the model ended 78%
        // off calibration; the calibrated probe is a controlled measurement, an
        // in-run heading is a chord through terrain.
        if ((candidate > 0) != (reference > 0)) return 0;
        if (fabs(candidate) > refMag*2.0 || fabs(candidate) < refMag/2.0) return 0;
    }
    // Weight by travel: a position read is good to a fraction of a yard, so a
    // 4 yd chord carries several times the angular error of a 20 yd one.
    double q = moved/12.0;
    if (q > 1.0) q = 1.0;
    if (q < 0.2) q = 0.2;
    double a = alpha*q;
    *est = (1.0-a)*(*est) + a*candidate;
    return 1;
}

// Plans one leg. 1 = planned, 0 = within tolerance, -1 = cannot plan.
//
// dy is a preference, never a distance prediction: how far a click walks is
// decided by the terrain under the cursor, so the loop closes on measurement.
static int am_plan_leg(const aim_params *p, double remx, double remy, leg_t *out) {
    if (!p || !out) return -1;
    double remaining = am_len(remx, remy);
    if (remaining <= p->tol) return 0;
    int limit = p->halfWidth - 40;
    if (limit < 20) return -1;

    double desired = am_heading_deg(remx, remy);
    double delta = am_wrap_deg(desired - p->headingAtZero);

    out->turn = AM_TURN_NONE;
    out->dragPx = 0;
    out->keyMs = 0;
    out->clickPx = 0;
    // Short hops when the goal is near or the camera has to turn.
    out->dy = (remaining < p->nearGoal) ? p->nearDy : p->preferredDy;

    if (fabs(delta) > p->coneHalfDeg) {
        // A click cannot reach this heading: the camera has to rotate first.
        if (p->turnKey && fabs(p->degPerKeyMs) > 1e-6) {
            double ms = delta / p->degPerKeyMs;
            double cap = (p->maxTurnMs > 1.0) ? p->maxTurnMs : 1200.0;
            if (ms >  cap) ms =  cap;
            if (ms < -cap) ms = -cap;
            out->turn = AM_TURN_KEY;
            out->keyMs = (int)(ms >= 0 ? ms + 0.5 : ms - 0.5);
            // A turn leg walks along the NEW heading, so with a long way to go it
            // should walk properly: a 0.6-1.3 yd hop cannot re-measure the camera
            // yaw, and v13 then turned again from the same stale yaw, leg after
            // leg. A short goal keeps the short hop, where overshooting costs more
            // than a stale yaw does.
            out->dy = (remaining > 2.0*p->nearGoal) ? p->preferredDy : p->nearDy;
            return 1;
        }
        if (fabs(p->degPerDragPx) > 1e-6) {
            double dpx = delta / p->degPerDragPx;
            if (dpx >  limit) dpx =  limit;
            if (dpx < -limit) dpx = -limit;
            out->turn = AM_TURN_DRAG;
            out->dragPx = (int)(dpx >= 0 ? dpx + 0.5 : dpx - 0.5);
            out->dy = (remaining > 2.0*p->nearGoal) ? p->preferredDy : p->nearDy;
            return 1;
        }
        return -1;
    }
    if (fabs(p->degPerPx) < 1e-6) return -1;
    double px = delta / p->degPerPx;
    if (px >  limit) px =  limit;
    if (px < -limit) px = -limit;
    out->clickPx = (int)(px >= 0 ? px + 0.5 : px - 0.5);
    return 1;
}

// Half-angle reachable by clicking, from the measured sensitivity.
// The response is not linear all the way to the screen edge (clicks at the very
// edge fall short or land on unwalkable terrain), so this deliberately stays
// inside the geometric limit - the loop re-plans after every leg, and a leg that
// under-turns costs one extra leg, while a leg that aims past the edge costs a
// whole walk in the wrong direction.
static double am_cone_half_deg(double degPerPx, int halfWidth) {
    double reach = fabs(degPerPx) * (double)halfWidth * 0.75;
    if (reach > 75.0) reach = 75.0;
    if (reach < 5.0) reach = 5.0;
    return reach;
}

// Offline tests: maths, robust learning, and a simulated client whose distance
// does NOT depend on dy (which is what the live client does).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "aimmath.h"

static int fails;
#define CHECK(c) do { if(!(c)){ printf("FAIL %d %s\n",__LINE__,#c); fails++; } } while(0)
#define NEAR(x,y,t) (fabs((x)-(y)) <= (t))

typedef struct {
    double h0;              // true heading walked at screen centre
    double degPerDragPx;    // true camera turn per drag pixel
    double degPerKeyMs;     // true camera turn per ms of held turn key
    double degPerClickPx;   // true walk heading per pixel of horizontal click
    double hopMin, hopMax;  // true distance is terrain-driven, not dy-driven
    int terrainCycle;       // deterministic pseudo-random terrain variation
    int turnWithKey;        // 1 = the client responds to the turn key, not drags
} plant_t;

typedef struct { int ok, legs, drags, keys, blocked; double path; } simres_t;

// Simulated client. Distance comes from a terrain hash, NOT from dy - this is the
// behaviour measured on the live client (dy=-20 gave 0.09 to 12.35 yd).
static double plant_distance(plant_t *pt, int leg) {
    unsigned x=(unsigned)(leg*2654435761u + pt->terrainCycle*40503u);
    x^=x>>13; x*=2246822519u; x^=x>>16;
    double f=(double)(x & 0xFFFF)/65535.0;
    return pt->hopMin + f*(pt->hopMax-pt->hopMin);
}

static simres_t simulate(plant_t plant, double goalx, double goaly, int maxLegs, int verbose) {
    aim_params p;
    memset(&p,0,sizeof(p));
    p.headingAtZero=plant.h0;
    p.degPerPx=-0.05;  p.degPerPxRef=-0.10;      // deliberately wrong start
    p.degPerDragPx=-0.20; p.degPerDragPxRef=-0.37;
    p.degPerKeyMs=-0.030; p.degPerKeyMsRef=-0.050;
    p.halfWidth=960; p.coneHalfDeg=am_cone_half_deg(p.degPerPxRef,960); p.tol=2.0;
    p.preferredDy=-140; p.nearDy=-70; p.minLearnMove=3.0;
    p.maxLearnMove=22.0; p.nearGoal=15.0;
    p.turnKey=plant.turnWithKey; p.maxTurnMs=1200.0;

    double px=0, py=0;
    simres_t r; memset(&r,0,sizeof(r));
    int noProgress=0; double best=1e9;
    for (int leg=1; leg<=maxLegs; leg++) {
        double remx=goalx-px, remy=goaly-py;
        double remaining=am_len(remx,remy);
        leg_t l;
        int rc=am_plan_leg(&p,remx,remy,&l);
        if (rc==0) { r.ok=1; r.legs=leg-1; return r; }
        if (rc<0) { r.legs=leg-1; return r; }

        double headingBefore=p.headingAtZero;
        if (l.turn==AM_TURN_DRAG) {
            p.headingAtZero=am_wrap_deg(p.headingAtZero + l.dragPx*plant.degPerDragPx);
            r.drags++;
        } else if (l.turn==AM_TURN_KEY) {
            p.headingAtZero=am_wrap_deg(p.headingAtZero + l.keyMs*plant.degPerKeyMs);
            r.keys++;
        }
        double walked=am_wrap_deg(p.headingAtZero + plant.degPerClickPx*l.clickPx);
        double dist=plant_distance(&plant,leg);
        double bx=px, by=py;
        px+=dist*cos(walked*AM_D2R); py+=dist*sin(walked*AM_D2R);
        r.path+=dist;
        double moved=am_len(px-bx,py-by);

        // learn exactly as the tool does
        double turned=am_wrap_deg(walked-headingBefore);
        if (l.turn==AM_TURN_DRAG && l.dragPx) {
            am_learn_slope(&p.degPerDragPx,p.degPerDragPxRef,turned,(double)l.dragPx,
                           p.minLearnMove,p.maxLearnMove,moved,0.5);
        }
        if (l.turn==AM_TURN_KEY && l.keyMs) {
            am_learn_slope(&p.degPerKeyMs,p.degPerKeyMsRef,turned,(double)l.keyMs,
                           p.minLearnMove,p.maxLearnMove,moved,0.5);
        }
        if (abs(l.clickPx)>=40) {
            am_learn_slope(&p.degPerPx,p.degPerPxRef,turned,(double)l.clickPx,
                           p.minLearnMove,p.maxLearnMove,moved,0.35);
        }
        p.headingAtZero=am_wrap_deg(walked - p.degPerPx*l.clickPx);

        double now=am_len(goalx-px,goaly-py);
        if (verbose) printf("    leg %2d: turn=%d drag=%+5d key=%+5d click=%+5d dy=%+4d dist=%5.2f -> remaining %6.2f\n",
                            leg,l.turn,l.dragPx,l.keyMs,l.clickPx,l.dy,dist,now);
        if (now > remaining-0.2) noProgress++; else noProgress=0;
        if (now<best) best=now;
        if (noProgress>=4) { r.blocked=1; r.legs=leg; return r; }
    }
    r.legs=maxLegs;
    return r;
}

int main(void) {
    // ---- maths
    double xs[]={-2,-1,0,1,2}, ys[]={-3,-1,1,3,5}, a=0, b=0;
    CHECK(am_fit(xs,ys,5,&a,&b)==1); CHECK(NEAR(a,2.0,1e-9));
    double o=0; CHECK(am_invert(a,b,7.0,&o)==1); CHECK(NEAR(o,3.0,1e-9));
    CHECK(NEAR(am_wrap_deg(190.0),-170.0,1e-9));
    CHECK(NEAR(am_len(3,4),5.0,1e-12));
    double nx[]={-300,-150,0,150,300}, ny[]={-12.1,-6.2,0.3,5.9,12.4};
    CHECK(am_fit(nx,ny,5,&a,&b)==1); CHECK(am_rms(nx,ny,5,a,b) < 0.3);

    // ---- cone estimate: never the full screen width, never zero
    CHECK(NEAR(am_cone_half_deg(-0.10,960), 72.0, 1e-9));
    CHECK(NEAR(am_cone_half_deg(-0.20,960), 75.0, 1e-9));   // capped
    CHECK(NEAR(am_cone_half_deg(0.0,960),    5.0, 1e-9));   // floor, not 0
    CHECK(NEAR(am_cone_half_deg(-0.10,960), am_cone_half_deg(0.10,960), 1e-12));

    // ---- a turn leg must walk far enough to re-measure the camera yaw
    // v13 sent turn legs off on a dy=-60 hop, which moved 0.6-1.3 yd, which the
    // walker then refused to read a yaw from - so the next leg turned again from
    // the same stale yaw. Turn legs plan the far click.
    {
        aim_params t; memset(&t,0,sizeof(t));
        t.tol=2.0; t.halfWidth=960; t.preferredDy=-140; t.nearDy=-60;
        t.headingAtZero=0.0; t.coneHalfDeg=60.0; t.degPerPx=-0.10; t.degPerDragPx=-0.20;
        t.minLearnMove=6.0; t.maxLearnMove=30.0; t.nearGoal=15.0; t.maxTurnMs=1500.0;
        leg_t L; memset(&L,0,sizeof(L));
        // goal 80 deg to the left: outside the 60 deg cone, needs a drag
        CHECK(am_plan_leg(&t,-80.0,0.0,&L)==1);
        CHECK(L.turn==AM_TURN_DRAG);
        CHECK(L.dy==t.preferredDy);
        // and with no drag channel it is a refusal, never a silent centre click
        t.degPerDragPx=0.0;
        CHECK(am_plan_leg(&t,-80.0,0.0,&L)==-1);
    }

    // ---- a leg that breaks the triangle inequality is not a measurement
    // The two legs from the v4 field run that broke it:
    CHECK(am_leg_consistent(2.04, 30.83, 5.45, 0.5)==0);
    CHECK(am_leg_consistent(30.83, 110.23, 0.63, 0.5)==0);
    // and the ones that did not:
    CHECK(am_leg_consistent(30.00, 9.21, 21.51, 0.5)==1);
    CHECK(am_leg_consistent(9.21, 21.16, 29.77, 0.5)==1);
    CHECK(am_leg_consistent(2.04, 2.41, 4.29, 0.5)==1);
    // plausibility of a single step between two reads
    CHECK(am_plausible_step(79.4, 0.3)==0);     // the 110 yd jump
    CHECK(am_plausible_step(7.0, 1.0)==1);      // running
    CHECK(am_plausible_step(30.0, 4.5)==1);     // a long leg with a long sample
    CHECK(am_plausible_step(0.0, 0.0)==1);
    CHECK(am_plausible_step(200.0, 20.0)==0);   // even a teleport has a ceiling
    CHECK(am_plausible_step(1.6, 0.05)==1);     // one 50 ms sample of running
    CHECK(am_plausible_step(4.0, 0.05)==0);     // too far for 50 ms

    // ---- stopping a walk that is already running
    // The click decides the direction; the stop decides when it ends. Without a
    // stop the walk goes wherever the terrain sends it (5.6 to 72 yd for the same
    // click in the field data), which is why final approach missed by 1.7-2 yd.
    CHECK(am_should_stop(1.0, 2.5, 1.0)==1);    // will coast past, stop now
    CHECK(am_should_stop(10.0, 2.5, 1.0)==0);   // still far away
    CHECK(am_should_stop(3.5, 2.5, 1.0)==1);    // exactly the firing distance
    CHECK(am_should_stop(0.2, 0.0, 1.0)==1);    // already there
    CHECK(am_learn_coast(0.3, 2.9) > 0.9 && am_learn_coast(0.3, 2.9) < 1.3);
    CHECK(NEAR(am_learn_coast(0.5, 40.0), 0.5, 1e-12));   // key not seen: ignore
    // a coast too short to be the client's reaction is not evidence: the field
    // run learned 0.07 yd and dragged the stop distance from 0.80 to 0.58
    CHECK(NEAR(am_learn_coast(0.8, 0.07), 0.8, 1e-12));
    CHECK(NEAR(am_learn_coast(0.8, -3.0), 0.8, 1e-12));
    CHECK(NEAR(am_learn_coast(0.8, 0.31), 0.7*0.8+0.3*0.31, 1e-12));
    {
        // Continuous approach: run at a fixed speed, poll, cancel when the rule
        // says so, then coast for the latency. With a correct coast estimate the
        // character stops inside the capture radius every time.
        double speed=7.0, latency=0.35, capture=1.0;
        double coast=speed*latency;                     // what the stop really costs
        for (double start=3.0; start<=60.0; start+=7.0) {
            double rem=start;
            int stopped=0;
            for (int i=0;i<5000;i++) {
                if (rem<=0) break;
                if (am_should_stop(rem,coast,capture)) {
                    rem-=coast; stopped=1; break;
                }
                rem-=speed*0.06;                        // 60 ms polling
            }
            CHECK(stopped==1);
            CHECK(fabs(rem)<=capture+1e-9);
        }
        // and an under-estimated coast lands short by exactly the deficit, which
        // is why the coast is measured rather than assumed
        double badCoast=0.3;
        double rem=30.0;
        for (int i=0;i<5000 && rem>0;i++) {
            if (am_should_stop(rem,badCoast,capture)) { rem-=coast; break; }
            rem-=speed*0.06;
        }
        CHECK(rem < -capture);                          // overshot the goal
    }

    // ---- a whole leg is judged by average speed, not by the 3 s poll cap
    // 50.19 yd in 15.70 s is 3.2 yd/s: a real leg that v8 discarded as impossible.
    CHECK(am_plausible_leg(50.19,15.70)==1);
    CHECK(am_plausible_step(50.19,15.70)==0);   // the old, wrong question
    CHECK(am_plausible_leg(79.0,0.30)==0);      // the 79 yd jump really is garbage
    CHECK(am_plausible_leg(6.95,1.0)==1);
    CHECK(am_plausible_leg(1.0,0.0)==1);        // a stationary leg is not a teleport
    // detours alternate sides so six attempts are not six pushes at one rock
    CHECK(am_detour_side(1)==+1 && am_detour_side(2)==-1 && am_detour_side(3)==+1);

    // ---- the camera moved between probes, so the reference must move too
    // Real sequence from WowBClickAim-20261029-144250.txt: centre click 47.0 deg,
    // then +150 -> 34.1, +300 -> 13.1, -150 -> 34.4, -300 -> 46.0, +225 -> 22.1,
    // -225 -> 41.3. Against the fixed 47.0 start the negative offsets come out
    // POSITIVE (+0.084, +0.003, +0.025 deg/px) and no line fits; against each
    // probe's predecessor they all agree in sign and roughly in size.
    {
        double dx[6]={150,300,-150,-300,225,-225};
        double seqTurn[6]={-12.90,-21.00,+21.30,+11.60,-23.90,+19.20};
        double staleTurn[6]={-12.90,-33.90,-12.60,-1.00,-24.90,-5.70};
        double slope=0;
        CHECK(am_median_slope(6,dx,seqTurn,2.0,&slope)>=4);
        CHECK(NEAR(slope,-0.0857,0.006));           // the honest answer
        // what v7's fixed-origin least squares made of the same data: a fiction
        double sxx=0, sxy=0;
        for (int i=0;i<6;i++) { sxx+=dx[i]*dx[i]; sxy+=dx[i]*staleTurn[i]; }
        CHECK(NEAR(sxy/sxx,-0.0436,0.001));
        // the 8.33 yd probe that stopped early is the outlier and is dropped
        double dx2[4]={150,300,-150,-225};
        double t2[4]={-12.90,-21.00,+21.30,-19.20};
        slope=0;
        CHECK(am_median_slope(4,dx2,t2,2.0,&slope)>0);
        CHECK(slope < 0.0);
        // one probe is not a slope, and a zero driver cannot be divided by
        double one=100, t1=-9.0;
        CHECK(am_median_slope(1,&one,&t1,2.0,&slope)==0);
        double z[2]={0,0}, tz[2]={1.0,2.0};
        CHECK(am_median_slope(2,z,tz,2.0,&slope)==0);
    }

    // ---- sensitivity fit: one sample is not a measurement
    {
        // the v5 and v6 field values, and the truth in the middle: what the two
        // single-sample runs disagreed about by 3.3x
        double dx[4]={150,300,-150,-300};
        double turn[4]={-16.4,-33.1,16.1,32.6}, slope=0;
        CHECK(am_median_slope(4,dx,turn,2.0,&slope)==4);
        CHECK(NEAR(slope,-0.10927,5e-4));
        // every sample is predicted by the fit that used it
        for (int i=0;i<4;i++) CHECK(am_prediction_ok(turn[i],slope*dx[i],0.35,3.0)==1);
        // a noisy sample that the fit does NOT predict (the v6 value) is caught
        CHECK(am_prediction_ok(-108.3,-0.1098*300,0.35,3.0)==0);
        double zero[2]={0,0}, tz[2]={1.0,2.0};
        CHECK(am_median_slope(2,zero,tz,2.0,&slope)==0);   // a zero driver is not a slope
        // absolute tolerance saves a small turn that relative tolerance would kill
        CHECK(am_prediction_ok(2.0,0.2,0.35,3.0)==1);
        // and a big turn off by 40 % is still rejected
        CHECK(am_prediction_ok(60.0,100.0,0.35,3.0)==0);
    }

    // ---- pass-by stop: a leg that comes close and then recedes is over
    // With a fixed goal the only ways remaining can grow are a lateral miss or a
    // route that bends away. Either way the leg will not recover, so it must be
    // cancelled near the closest approach instead of being walked to the end.
    {
        am_passby_t s; am_passby_reset(&s);
        // a clean approach never fires, however long it runs
        for (double rem=30.0; rem>0.5; rem-=0.4) CHECK(am_passby_stop(&s,rem,6.0,0.5)==0);
        am_passby_reset(&s);
        // the v6 leg 5 shape: 2.72 -> 21.38 yd while moving. It gets 3 polls of
        // grace after the minimum plus the give-up distance, and no more.
        double trace[]={30,20,12,8,6.4,5.2,4.6,5.0,6.2,8.0,11.0,15.0,21.4};
        int fired=0; double atFired=0;
        for (int i=0;i<13;i++) if (am_passby_stop(&s,trace[i],6.0,0.5)) { fired=1; atFired=trace[i]; break; }
        CHECK(fired==1);
        CHECK(atFired>=4.6 && atFired<=8.0);   // cancelled just past the minimum
        // never inside the pass radius: a leg that walks wide must be left alone
        // (its minimum is not a "closest approach to this goal" at all)
        am_passby_reset(&s);
        double wide[]={40,38,37.5,38.6,40.0,42.0,44.0};
        int f2=0;
        for (int i=0;i<7;i++) if (am_passby_stop(&s,wide[i],6.0,0.5)) f2=1;
        CHECK(f2==0);
        // one noisy sample is not a receding character
        am_passby_reset(&s);
        CHECK(am_passby_stop(&s,4.0,6.0,0.5)==0);
        CHECK(am_passby_stop(&s,4.9,6.0,0.5)==0);
        CHECK(am_passby_stop(&s,3.9,6.0,0.5)==0);   // came back: counter cleared
        CHECK(s.receding==0);
    }

    // ---- robust learning: the field failure must NOT reproduce
    double est=-0.10;
    // a leg that barely moved must teach nothing
    CHECK(am_learn_slope(&est,-0.10, 40.0, -160.0, 3.0, 22.0, 0.09, 0.35)==0);
    CHECK(NEAR(est,-0.10,1e-12));
    // a sign-flipped estimate is rejected: driver -160 wants measured < 0
    CHECK(am_learn_slope(&est,-0.10, -30.0, -160.0, 3.0, 22.0, 5.0, 0.35)==0);
    CHECK(est < 0.0);
    // an estimate beyond 2x the calibration is rejected (here 5x)
    CHECK(am_learn_slope(&est,-0.10, 80.0, -160.0, 3.0, 22.0, 5.0, 0.35)==0);
    // a leg longer than maxLearnMove has been deflected by terrain
    CHECK(am_learn_slope(&est,-0.10, 35.2, -160.0, 3.0, 22.0, 25.0, 0.35)==0);
    // a plausible estimate is accepted, weighted by how far it travelled
    CHECK(am_learn_slope(&est,-0.10, 17.6, -160.0, 3.0, 22.0, 6.0, 0.35)==1);
    CHECK(NEAR(est, -0.10*(1-0.35*0.5) + (-0.11)*(0.35*0.5), 1e-6));
    // the same estimate on a 21 yd leg moves the model much more than on a 4 yd one
    double shortLeg=-0.10, longLeg=-0.10;
    am_learn_slope(&shortLeg,-0.10, 17.6, -160.0, 3.0, 22.0, 4.0, 0.35);
    am_learn_slope(&longLeg,-0.10, 17.6, -160.0, 3.0, 22.0, 21.0, 0.35);
    CHECK(fabs(shortLeg+0.10) < fabs(longLeg+0.10));
    CHECK(fabs(longLeg+0.10) > 3.0*fabs(shortLeg+0.10));
    // same rules on the key channel
    double km=-0.050;
    CHECK(am_learn_slope(&km,-0.050, 20.0, 500.0, 3.0, 22.0, 8.0, 0.5)==0);   // sign flip
    CHECK(NEAR(km,-0.050,1e-12));
    CHECK(am_learn_slope(&km,-0.050, -24.0, 500.0, 3.0, 22.0, 8.0, 0.5)==1);
    CHECK(NEAR(km, -0.050*(1-0.5*8.0/12.0) + (-0.048)*(0.5*8.0/12.0), 1e-9));
    printf("  learning: robust to tiny legs, sign flips, 2x outliers and over-long legs; est=%.4f key=%.4f\n",est,km);

    // ---- planner: dy is a preference, not a distance claim
    aim_params p; memset(&p,0,sizeof(p));
    p.headingAtZero=90.0; p.degPerPx=-0.10; p.degPerDragPx=-0.37;
    p.halfWidth=960; p.coneHalfDeg=am_cone_half_deg(-0.10,960); p.tol=2.0;
    p.preferredDy=-140; p.nearDy=-70; p.minLearnMove=3.0; p.maxTurnMs=1200.0;
    p.maxLearnMove=22.0; p.nearGoal=15.0;
    leg_t l;
    CHECK(am_plan_leg(&p,0,50,&l)==1);
    CHECK(l.turn==AM_TURN_NONE); CHECK(l.dy==-140);
    CHECK(am_plan_leg(&p,0,5,&l)==1);
    CHECK(l.dy==-70);                              // near goal -> short hop
    CHECK(am_plan_leg(&p,-1e-9,-1e-9,&l)==0);      // at the goal
    p.degPerDragPx=0.0;
    CHECK(am_plan_leg(&p,10,0,&l)==-1);            // needs a turn, cannot plan one

    // a goal behind the camera is reachable once either channel is calibrated
    p.degPerDragPx=-0.37;
    CHECK(am_plan_leg(&p,-40,-30,&l)==1);          // due west of a north-facing camera
    // 50 yd left, so the turn leg plans the FAR click: the walk after the turn is
    // what re-measures the camera yaw, and a hop too short to measure it leaves
    // the next leg turning from the same stale yaw (v13's 100 yd run circled for
    // a dozen legs exactly that way).
    CHECK(l.turn==AM_TURN_DRAG); CHECK(l.dragPx<0); CHECK(l.dy==-140);
    CHECK(am_plan_leg(&p,-8,-6,&l)==1);            // 10 yd away, behind the camera
    CHECK(l.turn==AM_TURN_DRAG); CHECK(l.dy==-70); // still a short hop when near
    CHECK(am_plan_leg(&p,40,-30,&l)==1);           // other side
    CHECK(l.turn==AM_TURN_DRAG); CHECK(l.dragPx>0);

    // the key channel is preferred when it is the one that works
    p.turnKey=1; p.degPerKeyMs=-0.05;
    CHECK(am_plan_leg(&p,-40,-30,&l)==1);
    CHECK(l.turn==AM_TURN_KEY); CHECK(l.keyMs<0); CHECK(l.dragPx==0);
    p.maxTurnMs=300.0;                              // clamping must not lose the leg
    CHECK(am_plan_leg(&p,-40,-30,&l)==1);
    CHECK(l.keyMs==-300);
    p.degPerKeyMs=0.0; p.turnKey=1;                 // nothing calibrated -> refuse
    CHECK(am_plan_leg(&p,-40,-30,&l)==1);           // falls back to the drag channel
    CHECK(l.turn==AM_TURN_DRAG);

    // ---- simulated client: dy-independent distance (the real behaviour)
    plant_t plant={137.0,-0.37,-0.050,-0.10,3.0,12.0,7,0};
    simres_t r=simulate(plant,20.0,30.0,20,1);
    printf("  front goal:  ok=%d legs=%d drags=%d path=%.1f yd\n",r.ok,r.legs,r.drags,r.path);
    CHECK(r.ok==1);

    plant.terrainCycle=19;
    r=simulate(plant,-40.0,-30.0,26,1);
    printf("  behind goal: ok=%d legs=%d drags=%d path=%.1f yd\n",r.ok,r.legs,r.drags,r.path);
    CHECK(r.ok==1);

    // the failure that killed the v3 field run: a client where dragging turns
    // nothing, so only the key channel can rotate the camera.
    plant_t keyonly={137.0, 0.0, -0.050, -0.10, 3.0, 12.0, 19, 1};
    r=simulate(keyonly,-40.0,-30.0,26,1);
    printf("  key-only:    ok=%d legs=%d keys=%d path=%.1f yd\n",r.ok,r.legs,r.keys,r.path);
    CHECK(r.ok==1); CHECK(r.keys>0);

    // worst case: every click only walks 3 yd, so converging needs many legs
    plant_t slow={-20.0,-0.37,-0.050,-0.10,1.0,3.5,3,0};
    r=simulate(slow,15.0,10.0,30,0);
    printf("  tiny hops:   ok=%d legs=%d path=%.1f yd\n",r.ok,r.legs,r.path);
    CHECK(r.ok==1);

    if (!fails) puts("aimmath passed (maths, robust learning, dy-independent convergence, both turn channels)");
    return fails ? 1 : 0;
}

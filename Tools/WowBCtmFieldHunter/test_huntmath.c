// Offline tests for the destination-selection maths.
#include "huntmath.h"
#include <stdio.h>

static int fails;
#define CHECK(c) do { if(!(c)){ printf("FAIL %d %s\n",__LINE__,#c); fails++; } } while(0)

int main(void) {
    // Real numbers from WowBCtmPathWatch-20261029-095131.txt: a 44 yd walk.
    float start[3] = {10511.293f, 956.254f, 1317.160f};
    float end[3]   = {10501.409f, 998.992f, 1319.615f};
    // Two samples during the walk, ~3 yd apart along the same line.
    float w1[3] = {10510.400f, 959.600f, 1317.380f};
    float w2[3] = {10509.700f, 962.600f, 1317.560f};

    CHECK(fh_dist3(start,end)>43.0 && fh_dist3(start,end)<45.0);
    CHECK(fh_dist2(start,end)>43.0 && fh_dist2(start,end)<45.0);

    // The true destination passes, and passes only while it holds still.
    CHECK(fh_destination_like(end,w1,w2,1,0.20)==1);
    CHECK(fh_destination_like(end,w1,w2,0,0.20)==0);   // changed during the walk -> reject

    // Behind the player: rejected by the forward test.
    float behind[3] = {10520.0f, 914.0f, 1317.0f};
    CHECK(fh_forward_cosine2(behind,w1,w1,w2) < 0.0);
    CHECK(fh_destination_like(behind,w1,w2,1,0.20)==0);

    // A value the player is walking AWAY from is rejected.
    // Walk direction is roughly (-0.7, +3.0) per sample, so -Y is behind.
    float receding[3] = {10512.5f, 940.0f, 1317.0f};
    CHECK(fh_approaching2(receding,w1,w2,0.20)==0);
    CHECK(fh_approaching2(receding,w1,w2,0.20)==0);
    CHECK(fh_destination_like(receding,w1,w2,1,0.20)==0);

    // Perpendicular to the walk: rejected.
    float perp[3] = {w1[0]+30.0f, w1[1]+7.0f, w1[2]};
    CHECK(fh_forward_cosine2(perp,w1,w1,w2) < 0.05);

    // Too close = the player's own position field, too far = outside click range.
    CHECK(fh_destination_like(w1,w1,w2,1,0.20)==0);
    float faraway[3] = {start[0]+300.0f, start[1], start[2]};
    CHECK(fh_destination_like(faraway,w1,w2,1,0.20)==0);

    // Plausibility.
    CHECK(fh_plausible(start)==1);
    float zero[3]={0,0,0};             CHECK(fh_plausible(zero)==0);
    float huge[3]={1e7f,0,0};          CHECK(fh_plausible(huge)==0);
    float nan3[3]={0.0f/0.0f,1,1};     CHECK(fh_plausible(nan3)==0);
    float neg[3]={-2000.0f,-1500.5f,300.0f}; CHECK(fh_plausible(neg)==1);

    // Degenerate: no movement between the samples -> geometry skipped, not divided by zero.
    CHECK(fh_forward_cosine2(end,w1,w1,w1)==0.0);
    CHECK(fh_destination_like(end,w1,w1,1,0.20)==1);

    // A 2-D destination (x,y correct, z from terrain) must still be recognised,
    // even though its z is far from the player's.
    float planar[3] = {end[0], end[1], 0.0f};
    CHECK(fh_destination_like(planar,w1,w2,1,0.20)==1);
    float planarHigh[3] = {end[0], end[1], end[2]+80.0f};
    CHECK(fh_destination_like(planarHigh,w1,w2,1,0.20)==1);

    if (!fails) puts("huntmath passed (destination_like, forward cosine, approach, 2-D, plausibility, degenerate)");
    return fails ? 1 : 0;
}

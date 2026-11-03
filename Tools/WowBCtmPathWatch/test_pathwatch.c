// Offline test of the pure helpers used to judge sampled triples.
#include "pathmath.h"
#include <stdio.h>

static int fails;
#define CHECK(c) do { if(!(c)){ printf("FAIL %d %s\n",__LINE__,#c); fails++; } } while(0)

int main(void) {
    float a[3]={10589.378f,958.600f,1317.354f};
    float b[3]={10589.378f,958.600f,1317.354f};
    float c[3]={10561.596f,907.090f,1310.127f};
    CHECK(pm_dist3(a,b)<1e-4);
    CHECK(pm_dist3(a,c)>58.0 && pm_dist3(a,c)<60.0);   // 58.97 yd, matching the report
    CHECK(pm_plausible(a)==1);
    CHECK(pm_plausible(c)==1);
    float zero[3]={0,0,0};             CHECK(pm_plausible(zero)==0);
    float tiny[3]={0.1f,0.1f,0.1f};    CHECK(pm_plausible(tiny)==0);
    float huge[3]={1e7f,0,0};          CHECK(pm_plausible(huge)==0);
    float nan3[3]={0.0f/0.0f,1,1};     CHECK(pm_plausible(nan3)==0);
    float neg[3]={-2000.0f,-1500.5f,300.0f}; CHECK(pm_plausible(neg)==1);
    // A destination 96 yards away, as seen for +0x150 and +0x7D0.
    float far[3]={10516.585f,896.190f,1308.983f};
    CHECK(pm_dist3(a,far)>95.0 && pm_dist3(a,far)<100.0);
    if (!fails) puts("pathwatch helpers passed (distance, plausibility, NaN/zero/huge rejection)");
    return fails ? 1 : 0;
}

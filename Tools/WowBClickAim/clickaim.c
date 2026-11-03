#define AIM_NO_INVERT 1
#define AIM_NO_FIT 1   // the tool no longer models distance; iteration handles it
// WowBClickAim v9 - click-to-move that measures instead of assuming.
//
// v1 proved synthesised right-clicks move the character (8 of 8 probes) but its
// numbers were useless, and the reason was the measurement: it sampled a fixed
// window without waiting for the character to STOP, so every probe measured the
// tail of the previous click blended with the start of the next. Distances
// saturated at run-speed * window (6.63 +/- 0.27 yd every time) and the two
// centre clicks disagreed by 15 degrees, which no fixed camera can do.
//
// v2 waits for rest before and after every click, refits from what it observes,
// and adds a closed-loop walker. It also handles the thing v1 could not: a click
// only reaches headings inside the camera cone, so a goal behind the camera needs
// the camera turned first. Turning the camera by clicking at the screen edge does
// not work - it walks the wrong way and never converges.
//
// v6 stops the walk. Every version up to here treated a leg as click-then-wait:
// the click decides a DIRECTION, and the distance is whatever the terrain lets the
// character run (measured spread: 5.6 to 72 yd for the same click offset). So the
// final approach could only get lucky - v4 ended 1.68 yd from the goal after
// overshooting it three times, and the legs that "converged" walked 7-11 yd to
// close a 3 yd gap. v6 watches the position while the character runs and cancels
// the walk (tap W, which starts a new movement and so clears click-to-move) once
// the remaining distance is within reach of where the stop will actually land. The
// coast distance is measured per stop, not assumed: the stop is late by however
// long the client takes to act on the key.
//
// v5 against the v4 field run, which did converge (1.74 yd in 17 legs) but did so
// with two lies in the log. Leg 7 reported "moved 5.45 yd" while the distance to
// the goal went UP by 28.79 yd, and leg 8 reported "moved 0.63 yd" while it went
// up by 79.4 yd. Walking cannot do that: for a fixed goal the distance can grow by
// at most the distance travelled. So those two position reads were not the
// character's position - one came right after "still moving after 20000 ms" - and
// v5 now plans from the position it measured immediately before the click, checks
// every sample and every leg against what is physically possible, and refuses to
// learn from a leg that fails. It also weights learning by travel: the click slope
// drifted from the calibrated -0.1017 to -0.1817 because a 4.3 yd leg counted as
// much as a 21 yd one.
//
// v4 exists because v3 shipped without three things it needed. The calibration
// never measured degPerDragPx, the planner's cone was left at zero, and dy stopped
// being a distance model - so the field run calibrated, then died on its first
// leg with "cannot plan a leg": with a zero cone every goal is "outside the cone"
// and with no drag slope there is nothing to turn with. v4 measures BOTH turn
// channels - a right-drag and a held turn key - and uses whichever actually moved
// the camera, because a background window may not be receiving real mouse deltas
// at all. Every parameter the planner reads is now set by measurement or by a
// documented default, never left at zero.
#define WIN32_LEAN_AND_MEAN
//
// v9 answers what the 2026-10-29 v8 run actually showed: the world is full of
// obstacles and a straight line is often not walkable. Three fixes:
//   * the rest detector is armed 900 ms after the click, so a click that has not
//     started walking yet is no longer recorded as "moved 0.00 yd";
//   * a whole leg is judged by average speed (am_plausible_leg), so a real 50 yd
//     leg is not thrown away as "impossible geometry" because the per-poll cap
//     clamps at 3 s;
//   * when two legs in a row come out short, the next leg is a DETOUR: turn 55 deg
//     off the goal bearing and walk the tangent, alternating sides, up to six
//     times, before calling the goal walled in.
// The drag channel also gets the click channel's treatment: four probes, both
// directions, median - because v8 measured one direction and got -0.19706 deg/px
// from a single sample, then asked for +532 px per leg.
//
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include "player_lookup.h"
#include "aimmath.h"
#define AIM_VERSION "v15"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define POS_RVA 0x60
#define REST_TOL 0.08          // yd per 50 ms sample counted as "standing still"
#define REST_STABLE 4          // consecutive still samples required
// v9 waited 20 s on a character that was RUNNING AWAY and then carried on from
// wherever it had got to. 8 s is long enough for any legitimate walk to settle and
// short enough that a runaway is caught while it is still only a few dozen yards.
// How long a click is allowed to keep walking before the wait calls it stuck.
// v11 cut this to 8 s to stop burning time on a runaway, and the 15:45 run shows
// what that costs: a single centre click walked 56.09 yd at 6.9 yd/s, needed
// 8.1 s, and the tool declared "never came to rest" and refused to walk at all.
// A click legitimately means up to ~100 yd of walking, so the wait gets 20 s and
// an independent DISTANCE limit decides what a runaway is.
#define REST_TIMEOUT_MS 20000
// Manual click-to-move reaches about 100 yd on this client, and every field walk
// agrees (45.15, 56.09, 99.35 yd). Continuous travel past this in one wait is not
// a click being followed - it is a button left down, or a click the client
// resolved somewhere else. v9 disaster was 171.52 yd in 25.00 s.
#define WANDER_MAX_YD 120.0
#define RUNAWAY_MOVE 40.0     // yd in one leg that a click cannot plausibly mean
#define RUNAWAY_FACTOR 3.0    // goal-distance growth that means "this is not a path"
#define MAX_LEGS 24
#define TURN_PROBE_PX 200      // drag distance used to calibrate the drag channel
                               // (kept well under half a turn: a 500 px drag at the
                               // measured 0.37 deg/px rotates past 180 deg and the
                               // heading would alias to the wrong sign)
#define TURN_PROBE_MS 500      // key hold used to calibrate the key channel
#define TURN_PROBE_MIN_DEG 2.0 // below this the channel is treated as dead
#define PREF_DY (-140)         // click offset that usually lands on flat ground
#define NEAR_DY (-60)          // short hop, used while turning or near the goal
#define MIN_LEARN_MOVE 3.0     // a leg shorter than this teaches nothing
#define MAX_LEARN_MOVE 22.0    // a leg longer than this has been deflected by terrain
#define NEAR_GOAL 15.0         // inside this, click the short hop instead
#define CAPTURE_TIMEOUT_MS 25000
#define CAPTURE_POLL_MS 60
// Right after the click the character has not started walking yet, so the first
// few polls all look like "standing still". v8 counted those and ended the leg
// with "moved 0.00 yd in 0.25 s" (leg 1 of the 2026-10-29 run), then treated the
// click as blocked. The rest detector is armed only after this long.
#define CLICK_ARM_MS 900
// A drag larger than this is outside the range the calibration measured, and a
// saturated drag is free to be wrong; v8 sent +532 px in one leg.
#define DRAG_MAX_PX 420
// The click model was measured over +/-300 px. Beyond that the response saturates
// (v8 measured +225 and +300 buying about the same angle), so a planned offset
// outside that range is extrapolation: v9 asked for -584 px in leg 3 on 2026-10-29.
#define CLICK_MAX_PX 300
// What a PLAN may ask for. The fit is checked against +/-300 px but the two field
// calibrations both show the response steepening near the edge (2026-10-29 16:0x:
// 0.086 deg/px near 150 px, 0.108 near 300), so a planned 300 px click turns
// further than the line predicts. Turning is cheap to repeat and expensive to
// overshoot, so plans stay inside 225 px and the next leg takes the rest.
#define CLICK_PLAN_MAX_PX 225
// A single drag is a rotation the client must read from successive mouse deltas,
// and its measured response scatters by ~30 % probe to probe. Asking one drag for
// more than this overshoots: leg 4 of the 2026-10-29 walk asked for 420 px, ran
// 37.10 yd, and ended FURTHER from the goal (23.47 -> 38.49). Big turns are split.
#define TURN_MAX_DEG 45.0
#define PANIC_SETTLE_MS 250
// How far to step sideways when the straight line is blocked, and how many times.
#define DETOUR_DEG 55.0
#define DETOUR_MOVE 0.5        // a leg shorter than this is a blocked leg
// The shortest walk whose heading is worth using as the next camera yaw. Below
// this the heading is set by whatever the character bumped into.
#define HEADING_MIN_WALK 3.0
#define MAX_DETOURS 6
// How many legs may be spent walking at the edge of the cone when there is no turn
// channel. Each one ends with the follow camera re-centred on the new heading, so
// the heading the next leg starts from has moved toward the goal.
#define MAX_REACH 8
#define STOP_KEY 'W'           // starting a new movement cancels click-to-move
#define STOP_HOLD_MS 70
#define DEFAULT_CAPTURE 1.0    // stop this close and call it arrived
// Calibration walks are deliberately SHORT. Every probe walks wherever its click
// lands, so the probe sequence is itself a random walk - about 230 yd of it in the
// two 2026-10-29 runs, which is how the character ended up jammed against terrain
// with every later click moving it 0.07-2.9 yd and no turn channel measurable. A
// 6 yd settled walk still gives a heading to a couple of degrees.
#define CAL_PROBE_DY (-100)    // click height used by every calibration probe
#define CAL_MIN_WALK 6.0       // a walk shorter than this has a heading worth nothing
#define CAL_MIN_SPAN 30.0      // 600 px of offset must turn the camera at least this much
#define CAL_MAX_SPAN 150.0     // ...and at most this much, or the wrap is ambiguous
// A turn needs a CLEAN walk, not a long one: 3 yd of a settled walk gives a
// heading to within a few degrees. Demanding 8-10 yd threw away every drag and key
// probe on 2026-10-29 (the clicks there moved the character only 0.07-6.37 yd), so
// the run calibrated NO turn channel at all and could not then plan a single leg.
#define CAL_MIN_TURN_WALK 2.5
#define PASSBY_RADIUS 6.0      // a leg that ever comes this close to the goal...
#define PASSBY_GIVEUP 0.5      // ...is cancelled once it is this much further away

static uint64_t ui(const void *p) { return (uint64_t)(uintptr_t)p; }
static int read_exact(HANDLE p, uint64_t addr, void *out, SIZE_T n) {
    SIZE_T got=0;
    return n && addr>=0x10000 && addr<MAX_USER && n<=MAX_USER-addr &&
        ReadProcessMemory(p,(void*)(uintptr_t)addr,out,n,&got) && got==n;
}
static uint32_t le32(const unsigned char *p) { uint32_t v; memcpy(&v,p,4); return v; }
static uint64_t filetime64(FILETIME f) { return ((uint64_t)f.dwHighDateTime<<32)|f.dwLowDateTime; }
static DWORD find_pid(void) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e);
    DWORD pid=0,count=0;
    if (Process32FirstW(snap,&e)) do {
        if (_wcsicmp(e.szExeFile,L"wowb.exe")==0) { pid=e.th32ProcessID; count++; }
    } while (Process32NextW(snap,&e));
    CloseHandle(snap); return count==1 ? pid : 0;
}
static uint64_t module_base(DWORD pid, DWORD *size) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); uint64_t found=0;
    if (Module32FirstW(snap,&e)) do {
        if (_wcsicmp(e.szModule,L"wowb.exe")==0) { found=ui(e.modBaseAddr); if (size) *size=e.modBaseSize; break; }
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return found;
}
static int player_read(void *context, uint64_t address, void *output, size_t size) {
    return read_exact((HANDLE)context,address,output,size);
}
static int valid_identity(HANDLE process, uint64_t imageBase) {
    unsigned char dos[64], pe[6], sample[16];
    // Presence check only: this prologue has 5-9 copies in the image, so it
    // identifies the build, never a unique function.
    static const unsigned char expected[16]={0x48,0x8b,0xc4,0x55,0x41,0x57,0x48,0x8d,0x68,0xa1,0x48,0x81,0xec,0xb8,0,0};
    if (!read_exact(process,imageBase,dos,sizeof(dos)) || dos[0]!='M' || dos[1]!='Z') return 0;
    uint32_t peoff=le32(dos+0x3c); if (peoff<64 || peoff>0x100000) return 0;
    if (!read_exact(process,imageBase+peoff,pe,6) || memcmp(pe,"PE\0\0",4) || pe[4]!=0x64 || pe[5]!=0x86) return 0;
    return read_exact(process,imageBase+HEALTH_RVA,sample,16) && memcmp(sample,expected,16)==0;
}

typedef struct { DWORD pid; HWND best; long area; } wf_t;

static BOOL CALLBACK enum_win(HWND hwnd, LPARAM param) {
    wf_t *f=(wf_t*)param;
    DWORD pid=0; GetWindowThreadProcessId(hwnd,&pid);
    if (pid!=f->pid || !IsWindowVisible(hwnd)) return TRUE;
    RECT r; if (!GetClientRect(hwnd,&r)) return TRUE;
    long area=(r.right-r.left)*(r.bottom-r.top);
    if (area>f->area) { f->area=area; f->best=hwnd; }
    return TRUE;
}

typedef struct {
    HANDLE process; uint64_t record;
    HWND hwnd; int cw, ch; POINT origin;
    int useSendInput; FILE *log;
    unsigned legs;
    int lastRestClean;   // 1 = the last wait_rest ended on a stationary character
    int lastRestTimedOut;// 1 = the character was STILL MOVING when the wait gave up
    int stopEnabled;     // 1 = cancel the walk once it is close enough
    double stopDistance; // yd the character coasts after the stop key (measured)
} ctx_t;

static int pos_now(ctx_t *c, float out[3]) {
    return read_exact(c->process,c->record+POS_RVA,out,12);
}

// Waits until the character stops moving. This is the fix for v1: without it
// every measurement includes leftover motion from the previous click.
static int wait_rest(ctx_t *c, float out[3], const char *tag) {
    float prev[3];
    if (!pos_now(c,prev)) return 0;
    uint64_t tPrev=GetTickCount64(), t0=tPrev; int stable=0, jumps=0;
    double path=0.0;
    while (GetTickCount64()-t0 < REST_TIMEOUT_MS) {
        if (WaitForSingleObject(c->process,0)!=WAIT_TIMEOUT) return 0;
        Sleep(50);
        float cur[3];
        if (!pos_now(c,cur)) return 0;
        uint64_t now=GetTickCount64();
        double dt=(double)(now-tPrev)/1000.0; tPrev=now;
        double d=am_len(cur[0]-prev[0],cur[1]-prev[1]);
        if (!am_plausible_step(d,dt)) {
            // Not the character's position: a discontinuous read (or a teleport).
            // Re-baseline on it so the stability test can still settle, but never
            // let it look like movement.
            if (++jumps<=3)
                fprintf(c->log,"  [%s] POSITION JUMP %.1f yd in %.2f s: read is discontinuous,\n"
                            "    re-baselining on it\n",tag,d,dt);
            memcpy(prev,cur,sizeof(prev));
            stable=0;
            continue;
        }
        if (d<REST_TOL) { if (++stable>=REST_STABLE) break; } else stable=0;
        path+=d;
        if (path>WANDER_MAX_YD) {
            fprintf(c->log,"  [%s] walked %.1f yd without stopping; a click cannot mean\n"
                        "    that far, so this is not a click being followed\n",tag,path);
            break;
        }
        memcpy(prev,cur,sizeof(prev));
    }
    c->lastRestClean=(stable>=REST_STABLE);
    c->lastRestTimedOut = !c->lastRestClean;
    if (stable<REST_STABLE) {
        fprintf(c->log,"  [%s] still moving after %d ms; using current position\n",tag,REST_TIMEOUT_MS);
    }
    memcpy(out,prev,sizeof(prev));
    return 1;
}

static void post_button(ctx_t *c, int x, int y, WPARAM down, WPARAM up) {
    LPARAM lp=MAKELPARAM(x,y);
    PostMessageW(c->hwnd,WM_MOUSEMOVE,0,lp);
    Sleep(10);
    PostMessageW(c->hwnd,down,MK_RBUTTON,lp);
    Sleep(45);
    PostMessageW(c->hwnd,up,0,lp);
}

static void send_click(ctx_t *c, int dx, int dy) {
    int x=c->cw/2+dx, y=c->ch/2+dy;
    if (c->useSendInput) {
        SetCursorPos(c->origin.x+x,c->origin.y+y);
        Sleep(25);
        mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,0);
        Sleep(45);
        mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,0);
    } else {
        post_button(c,x,y,WM_RBUTTONDOWN,WM_RBUTTONUP);
    }
}

// One right-click at a client-space pixel, from rest, measured to rest.
static int rest_click(ctx_t *c, int dx, int dy, float before[3], float after[3], double *secs,
                      int haveBefore) {
    if (!haveBefore && !wait_rest(c,before,"before click")) return 0;
    uint64_t t0=GetTickCount64();
    send_click(c,dx,dy);
    if (!wait_rest(c,after,"after click")) return 0;
    if (secs) *secs=(double)(GetTickCount64()-t0)/1000.0;
    return 1;
}

// Horizontal right-drag: turns the camera. Sent as many small moves because the
// client rotates from successive mouse deltas, not from one jump.
static void rest_drag(ctx_t *c, int dragPx) {
    int cx=c->cw/2, cy=c->ch/2;
    int x0=cx-dragPx/2;
    if (c->useSendInput) {
        SetCursorPos(c->origin.x+x0,c->origin.y+cy);
        Sleep(25);
        mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,0);
        Sleep(25);
        int steps=12;
        for (int i=1;i<=steps;i++) {
            int x=x0+(int)((long long)dragPx*i/steps);
            SetCursorPos(c->origin.x+x,c->origin.y+cy);
            Sleep(12);
        }
        mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,0);
    } else {
        PostMessageW(c->hwnd,WM_MOUSEMOVE,0,MAKELPARAM(x0,cy));
        Sleep(15);
        PostMessageW(c->hwnd,WM_RBUTTONDOWN,MK_RBUTTON,MAKELPARAM(x0,cy));
        Sleep(25);
        int steps=12;
        for (int i=1;i<=steps;i++) {
            int x=x0+(int)((long long)dragPx*i/steps);
            PostMessageW(c->hwnd,WM_MOUSEMOVE,MK_RBUTTON,MAKELPARAM(x,cy));
            Sleep(12);
        }
        // Send the release twice. A lost WM_RBUTTONUP leaves the client with the
        // right button held, and a held right button means "run toward the cursor"
        // in this client - which is what a 171.52 yd leg in 25.00 s looks like
        // (2026-10-29, leg 6). The second copy is free; the first can be dropped.
        PostMessageW(c->hwnd,WM_RBUTTONUP,0,MAKELPARAM(x0+dragPx,cy));
        Sleep(20);
        PostMessageW(c->hwnd,WM_RBUTTONUP,0,MAKELPARAM(x0+dragPx,cy));
    }
    c->legs++;
    Sleep(250);
}

// Holds a turn key. PostMessage is the channel that is known to reach the client
// from outside (screen clicks already work that way); SendInput needs the window
// to be foreground, which is why it is only used when asked for.
static void rest_key(ctx_t *c, WORD vk, int holdMs) {
    if (holdMs < 0) holdMs = -holdMs;
    UINT scan=MapVirtualKeyW(vk,MAPVK_VK_TO_VSC);
    LPARAM down = 1 | ((LPARAM)scan << 16);
    LPARAM up   = 1 | ((LPARAM)scan << 16) | ((LPARAM)1 << 30) | ((LPARAM)1 << 31);
    if (c->useSendInput) {
        keybd_event((BYTE)vk,(BYTE)scan,0,0);
        Sleep(holdMs);
        keybd_event((BYTE)vk,(BYTE)scan,KEYEVENTF_KEYUP,0);
    } else {
        PostMessageW(c->hwnd,WM_KEYDOWN,vk,down);
        Sleep(holdMs);
        PostMessageW(c->hwnd,WM_KEYUP,vk,up);
    }
    Sleep(200);
}

// Cancels a running click-to-move by starting a new movement for a moment. This
// is the only stop the client offers from outside: it keeps no destination to
// clear and there is no "stop" key, but any movement key press replaces the
// pathing move. The cost is one short step forward, which the capture radius
// absorbs.
static void tap_stop(ctx_t *c) {
    rest_key(c,(WORD)STOP_KEY,STOP_HOLD_MS);
}

// Issues a click and then WATCHES the character run, cancelling the walk when the
// remaining distance is close enough that the stop will land inside `capture`.
// Without this the leg ends wherever the terrain sent the click; with it the leg
// ends where the goal is. Returns 0 on a read failure.
// What one leg's click actually did, as opposed to what it was planned to do.
// `closest` is the nearest the character came to the goal during the leg: on a
// lateral miss the leg ENDS further away than it started, and only this number
// shows that it did pass close, which is the difference between "the camera model
// is wrong" and "the route bent around a rock".
typedef struct {
    double closest;      // minimum remaining seen, yards
    double stopAt;       // remaining when the stop key was tapped, 0 if never
    int    stopped;      // 1 = stop key tapped
    int    passby;       // 1 = cancelled at closest approach after receding
    int    polled;       // 1 = at least one sample was taken
} legcap_t;

// Sends every "stop moving" signal this client understands. A stuck right button
// is the worst case: the client keeps walking toward the cursor and no click can
// cancel it, because the click is read as another direction for the same held
// button. Releasing it twice, clicking once and tapping the stop key covers all of
// the ways the position can be left running.
static void panic_stop(ctx_t *c, const char *why) {
    fprintf(c->log,"      PANIC STOP (%s): releasing the mouse buttons, clicking once at the\n"
                   "      centre and tapping %c.\n",why,STOP_KEY);
    int cx=c->cw/2, cy=c->ch/2;
    if (c->useSendInput) {
        mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,0);
        Sleep(30);
        mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,0);
        Sleep(30);
        SetCursorPos(c->origin.x+cx,c->origin.y+cy);
        Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,0);
        Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,0);
    } else {
        PostMessageW(c->hwnd,WM_RBUTTONUP,0,MAKELPARAM(cx,cy));
        Sleep(30);
        PostMessageW(c->hwnd,WM_RBUTTONUP,0,MAKELPARAM(cx,cy));
        Sleep(30);
        PostMessageW(c->hwnd,WM_MOUSEMOVE,0,MAKELPARAM(cx,cy));
        Sleep(20);
        PostMessageW(c->hwnd,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(cx,cy));
        Sleep(40);
        PostMessageW(c->hwnd,WM_LBUTTONUP,0,MAKELPARAM(cx,cy));
    }
    Sleep(60);
    tap_stop(c);
    Sleep(PANIC_SETTLE_MS);
}

static int click_and_capture(ctx_t *c, int dx, int dy, double gx, double gy, double capture,
                             const float before[3], float out[3], double *secs, legcap_t *cap) {
    uint64_t t0=GetTickCount64();
    send_click(c,dx,dy);
    float prev[3]; memcpy(prev,before,sizeof(prev));
    uint64_t tPrev=t0, tStop=0;
    int stable=0, didStop=0;
    double stopAt=0;
    am_passby_t pb; am_passby_reset(&pb);
    if (cap) memset(cap,0,sizeof(*cap));
    int armed=0;
    while (GetTickCount64()-t0 < CAPTURE_TIMEOUT_MS) {
        if (WaitForSingleObject(c->process,0)!=WAIT_TIMEOUT) return 0;
        Sleep(CAPTURE_POLL_MS);
        float cur[3];
        if (!pos_now(c,cur)) return 0;
        uint64_t now=GetTickCount64();
        double dt=(double)(now-tPrev)/1000.0; tPrev=now;
        double step=am_len(cur[0]-prev[0],cur[1]-prev[1]);
        memcpy(prev,cur,sizeof(prev));
        if (!am_plausible_step(step,dt)) { stable=0; continue; }
        // Do not judge "at rest" before the client has had time to start walking,
        // or a click that works looks identical to one that hit a wall.
        if (!armed) {
            if (step>REST_TOL) armed=1;
            else if (now-t0 < CLICK_ARM_MS) { stable=0; continue; }
            else armed=1;
        }
        double rem=am_len(gx-cur[0],gy-cur[1]);
        if (cap) {
            if (!cap->polled || rem < cap->closest) cap->closest=rem;
            cap->polled=1;
        }
        if (step<REST_TOL) { if (++stable>=3) break; } else stable=0;
        if (c->stopEnabled && !didStop) {
            if (am_should_stop(rem,c->stopDistance,capture)) {
                stopAt=rem;
                tap_stop(c);
                didStop=1; tStop=now;
                if (cap) cap->stopped=1;
            } else if (am_passby_stop(&pb,rem,PASSBY_RADIUS,PASSBY_GIVEUP)) {
                // Passed the goal: closer is not coming. End the leg here instead
                // of letting it run to wherever the click point was.
                stopAt=rem;
                tap_stop(c);
                didStop=1; tStop=now;
                if (cap) { cap->passby=1; cap->stopped=1; }
            }
        } else if (didStop && step>REST_TOL && now-tStop>6000) {
            // The key did not cancel the walk: stop pretending it works.
            c->stopEnabled=0;
            fprintf(c->log,"      (the stop key did not cancel the walk after 6 s;\n"
                           "       stops disabled for the rest of this run)\n");
            didStop=0;
            if (cap) cap->stopped=0;
        }
    }
    memcpy(out,prev,sizeof(out[0])*3);
    c->lastRestClean=(stable>=3);
    c->lastRestTimedOut=!c->lastRestClean;
    if (secs) *secs=(double)(GetTickCount64()-t0)/1000.0;
    if (cap) cap->stopAt=stopAt;
    return 1;
}

// Robust value of one channel over all saved calibrations. Zeroes are skipped:
// a saved zero means "that channel was never measured", not "it is zero", and the
// two verified runs so far gave -0.10709 and -0.12674 deg/px, 18 % apart.
static int pool_channel(const double *v, int n, double *out) {
    double ones[64], keep[64];
    int m=0;
    if (n>64) n=64;
    for (int i=0;i<n;i++) if (fabs(v[i])>1e-9) keep[m++]=v[i];
    if (!m || !out) return 0;
    for (int i=0;i<m;i++) ones[i]=1.0;
    int kept=am_median_slope(m,ones,keep,2.0,out);
    if (!kept && m) { *out=keep[0]; kept=1; }   // one sample cannot be trimmed
    return kept;
}

static double heading_of(const float *a, const float *b) {
    return am_heading_deg(b[0]-a[0], b[1]-a[1]);
}
static double dist_of(const float *a, const float *b) {
    return am_len(b[0]-a[0], b[1]-a[1]);
}

// One measured click with everything a calibration decision needs: how far it
// walked, which way, and whether the character actually came to rest. A walk that
// never settled, or one that terrain bent after 4 yards, has no usable heading -
// and v6 built its camera model from exactly such a walk.
typedef struct { double travel, heading; int clean; } probe_t;

static int probe_click(ctx_t *c, int dx, int dy, probe_t *out) {
    float a[3], b[3]; double secs=0;
    if (!rest_click(c,dx,dy,a,b,&secs,0)) return 0;
    out->travel=dist_of(a,b);
    out->heading=heading_of(a,b);
    // A zero-length walk has no direction: heading_of on two identical points
    // returns 0.0 deg, and v9 took that as the camera yaw ("heading at centre right
    // now = 0.0 deg", from a probe that moved 0.00 yd).
    out->clean=c->lastRestClean && out->travel>=0.5;
    fprintf(c->log,"  probe dx=%+4d dy=%+4d -> %6.2f yd at heading %7.1f deg (%5.2f s, %4.1f yd/s)%s\n",
        dx,dy,out->travel,out->heading,secs,out->travel/(secs>0?secs:1),
        out->clean?"":"  [did not come to rest - heading not trusted]");
    return 1;
}

int main(int argc, char **argv) {
    DWORD pid=0;
    int useSendInput=0, dry=0, doWalk=0, doGoto=0, doProbe=0;
    double walkDist=0, walkDeg=0, goalx=0, goaly=0;
    int probeDx=0, probeDy=-140;
    int dyMin=-200, dyMax=-20, farDy=-140;
    int prefDy=PREF_DY, nearDy=NEAR_DY, quick=0, forceDrag=0, forceKey=0;
    int stopEnabled=1; double capture=DEFAULT_CAPTURE;
    int useSavedCal=0, dyEvidence=0;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--sendinput")) { useSendInput=1; continue; }
        if (!strcmp(argv[i],"--dry")) { dry=1; continue; }
        if (!strcmp(argv[i],"--walk") && i+1<argc) { doWalk=1; walkDist=atof(argv[++i]); if (i+1<argc && argv[i+1][0]!='-') walkDeg=atof(argv[++i]); continue; }
        if (!strcmp(argv[i],"--goto") && i+2<argc) { doGoto=1; goalx=atof(argv[++i]); goaly=atof(argv[++i]); continue; }
        if (!strcmp(argv[i],"--probe") && i+2<argc) { doProbe=1; probeDx=atoi(argv[++i]); probeDy=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--far") && i+1<argc) { farDy=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--dymin") && i+1<argc) { dyMin=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--dymax") && i+1<argc) { dyMax=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--prefdy") && i+1<argc) { prefDy=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--neardy") && i+1<argc) { nearDy=atoi(argv[++i]); continue; }
        if (!strcmp(argv[i],"--quick")) { quick=1; continue; }
        if (!strcmp(argv[i],"--drag")) { forceDrag=1; continue; }
        if (!strcmp(argv[i],"--key")) { forceKey=1; continue; }
        if (!strcmp(argv[i],"--nostop")) { stopEnabled=0; continue; }
        if (!strcmp(argv[i],"--load")) { useSavedCal=1; continue; }
        if (!strcmp(argv[i],"--dyevidence")) { dyEvidence=1; continue; }
        if (!strcmp(argv[i],"--capture") && i+1<argc) { capture=atof(argv[++i]); continue; }
        puts("Usage: WowBClickAim.exe [--dry] [--sendinput] [--pid N]");
        puts("         [--walk DIST [HEADING_DEG]]   closed-loop walk (relative heading)");
        puts("         [--goto X Y]                  closed-loop walk to a world point");
        puts("         [--probe DX DY]               one measured click");
        puts("         [--far DY] [--dymin DY] [--dymax DY] [--prefdy DY] [--neardy DY]");
        puts("         [--quick]                     one click probe, no held-out check, no dy spread");
        puts("         [--dyevidence]                two extra clicks proving dy is not a distance");
        puts("                                       control (costs 2 walks; off by default)");
        puts("         [--load]                      reuse the slopes saved by the last run (one click");
        puts("                                       to find the camera, then walk: ~10 s instead of 4 min)");
        puts("         [--drag|--key]                force which channel turns the camera");
        puts("         [--nostop] [--capture YD]     cancel the walk on arrival (default 1.0 yd)");
        puts("  default: calibrate and report the model, no walking");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBClickAim-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }

    int rc=1; ctx_t c; memset(&c,0,sizeof(c)); c.log=log; c.useSendInput=useSendInput;
    // First guess at the coast. Deliberately a little large: stopping early costs
    // one short extra leg, stopping late puts the character past the goal. The
    // first stop measures the real value and replaces this.
    c.stopEnabled=stopEnabled; c.stopDistance=0.8;
    fprintf(log,"WOWB CLICK-TO-MOVE AIM %s (rest-based measurement, closed loop, detours)\n",AIM_VERSION);
    fprintf(log,"Every click is measured from standstill to standstill, so the number reported is\n");
    fprintf(log,"what that click actually did. Reads only the confirmed position field.\n\n");
    fflush(log);

    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe.\n"); goto done; }
    c.process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid);
    if (!c.process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); goto done; }
    if (WaitForSingleObject(c.process,0)!=WAIT_TIMEOUT) { fprintf(log,"Not a live process.\n"); goto done; }
    FILETIME create,exitT,kernel,user;
    GetProcessTimes(c.process,&create,&exitT,&kernel,&user);
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,&imageSize);
    if (!imageBase || !valid_identity(c.process,imageBase)) { fprintf(log,"Identity check failed.\n"); goto done; }
    player_anchor player;
    if (!player_find(player_read,c.process,imageBase,&player)) { fprintf(log,"Player record unavailable.\n"); goto done; }
    c.record=player.record;
    fprintf(log,"pid=%lu creation=0x%llX imageBase=0x%llX record=0x%llX\n",(unsigned long)pid,
        (unsigned long long)filetime64(create),(unsigned long long)imageBase,
        (unsigned long long)c.record);

    // ---- window: the largest visible client area owned by the pid
    wf_t wf; wf.pid=pid; wf.best=NULL; wf.area=0;
    EnumWindows(enum_win,(LPARAM)&wf);
    if (!wf.best) { fprintf(log,"No visible game window found.\n"); goto done; }
    c.hwnd=wf.best;
    RECT cr; GetClientRect(c.hwnd,&cr);
    c.cw=cr.right-cr.left; c.ch=cr.bottom-cr.top;
    ClientToScreen(c.hwnd,&c.origin);
    fprintf(log,"window=0x%llX client=%dx%d at (%ld,%ld) centre=(%d,%d) mode=%s\n",
        (unsigned long long)ui(c.hwnd),c.cw,c.ch,(long)c.origin.x,(long)c.origin.y,
        c.cw/2,c.ch/2,useSendInput?"SendInput":"PostMessage");

    float p0[3]; if (!pos_now(&c,p0)) { fprintf(log,"position unreadable\n"); goto done; }
    fprintf(log,"position=(%.3f, %.3f, %.3f)\n",p0[0],p0[1],p0[2]);
    if (dry) {
        fprintf(log,"\nDRY RUN. Would click around client (%d,%d) with dy in [%d,%d], then probe\n"
                    "a %d px drag and a %d ms key hold to find a working turn channel.\n",
            c.cw/2,c.ch/2,dyMin,dyMax,TURN_PROBE_PX,TURN_PROBE_MS);
        rc=0; goto done;
    }
    if (GetForegroundWindow()!=c.hwnd) {
        fprintf(log,"NOTE: game window not foreground; PostMessage may still work, SendInput will not.\n");
        SetForegroundWindow(c.hwnd);
        Sleep(400);
    }

    if (doProbe) {
        float a[3], b[3]; double secs=0;
        printf("PROBE: one click at dx=%+d dy=%+d\n",probeDx,probeDy);
        if (!rest_click(&c,probeDx,probeDy,a,b,&secs,0)) { fprintf(log,"probe failed\n"); goto done; }
        double d=dist_of(a,b);
        fprintf(log,"probe dx=%+d dy=%+d -> moved %.2f yd in %.2f s (%.2f yd/s) heading %.1f deg\n",
            probeDx,probeDy,d,secs,d/(secs>0?secs:1),heading_of(a,b));
        fprintf(log,"from (%.2f, %.2f) to (%.2f, %.2f)\n",a[0],a[1],b[0],b[1]);
        rc=0; goto done;
    }

    // ------------------------------------------------------------ calibration
    printf("CALIBRATING: %s. Do not touch the mouse.\n",
        quick?"one click probe only (--quick)":"6 click probes plus two drags (expect ~2 minutes)");
    fflush(stdout);
    // Every field the planner reads is set here. v3 left coneHalfDeg, preferredDy
    // and nearDy at zero and the walker could not plan a single leg.
    const int calDy=CAL_PROBE_DY;
    aim_params P; memset(&P,0,sizeof(P));
    P.halfWidth=c.cw/2; P.tol=2.0;
    P.preferredDy=prefDy; P.nearDy=nearDy; P.minLearnMove=MIN_LEARN_MOVE;
    P.maxLearnMove=MAX_LEARN_MOVE; P.nearGoal=NEAR_GOAL;
    P.maxTurnMs=1500.0;

    // ------------------------------------------------------------ calibration
    // WHY THIS IS A SEQUENCE AND NOT A FIT THROUGH ONE ORIGIN.
    //
    // v7 fitted four click offsets against the heading measured at the start and
    // both held-out offsets were rejected: the probes gave -0.086, -0.070, -0.142,
    // -0.039, -0.106 and -0.085 deg/px, but three of the six had the wrong SIGN
    // against that fixed origin. The client did not change between those probes.
    // What changed is the camera: WoW's follow camera swings back behind the
    // character while it runs, so the yaw at the moment of probe k is the heading
    // of the walk that probe k-1 produced - not the heading of the first click.
    //
    // Measured against the predecessor, all six agree in sign and in rough size
    // (median -0.0857), which is also what the two runs before v6 measured
    // (-0.0936, -0.1029, -0.10983). So the estimator is one slope per probe and a
    // median with outlier rejection, and the walker now carries the same rule into
    // the loop: the camera yaw for the next leg is the heading the last leg
    // walked. Nothing here assumes the response is a straight line across the
    // whole range, because it is not: the ±225 and ±300 probes buy about the same
    // angle, i.e. the response saturates near the edge of the client.
    static const int calDx[4]={+150,-150,+225,-225};
    static const int holdDx[2]={+300,-300};
    double hRef=0;                       // heading of the walk that just finished
    // A slope is a property of the client and the camera settings, not of this
    // run, and the calibration costs about four minutes of walking. So the values
    // are written to C:\ghidra-scripts\clickaim-cal.txt and --load reuses them.
    // The CAMERA YAW is never reused: one centre click measures it every run.
    const wchar_t *calPath=L"C:\\ghidra-scripts\\clickaim-cal.txt";
    const char *calFile="C:\\ghidra-scripts\\clickaim-cal.txt";
    double savedClick=0, savedDrag=0, savedKey=0;
    int haveSaved=0;
    if (useSavedCal) {
        FILE *f=fopen(calFile,"r");
        fprintf(log,"--load: reading %s\n",calFile);
        if (!f) {
            fprintf(log,"  no file there yet. A successful calibration creates it; this run\n"
                        "  measures everything instead.\n");
        } else {
            double pc[32], pd_[32], pk[32];
            int n=0; double a=0,b=0,cc=0;
            while (n<32 && fscanf(f,"degPerPx=%lf degPerDragPx=%lf degPerKeyMs=%lf",&a,&b,&cc)==3) {
                pc[n]=a; pd_[n]=b; pk[n]=cc; n++;
                int ch; while ((ch=fgetc(f))!='\n' && ch!=EOF) { }
            }
            fclose(f);
            double sc=0, sd=0, sk=0;
            int kc=pool_channel(pc,n,&sc), kd=pool_channel(pd_,n,&sd), kk=pool_channel(pk,n,&sk);
            if (kc) {
                savedClick=sc; savedDrag=sd; savedKey=sk;
                haveSaved=1;
                fprintf(log,"  pooled from %d saved calibration%s: click %.5f (%d sample%s),\n"
                            "  drag %.5f (%d sample%s), key %.5f (%d)\n",
                    n,n==1?"":"s",sc,kc,kc==1?"":"s",sd,kd,kd==1?"":"s",sk,kk);
                double lo=pc[0],hi=pc[0];
                for (int i=1;i<n;i++) { if (pc[i]<lo) lo=pc[i]; if (pc[i]>hi) hi=pc[i]; }
                if (n>1 && hi-lo > 0.25*fabs(sc))
                    fprintf(log,"  the saved click slopes span %.5f to %.5f (%.0f%% of each other):\n"
                                "  the pooled median is used and the loop corrects the rest per leg.\n",
                        lo,hi,100.0*(hi-lo)/fabs(sc));
            } else {
                fprintf(log,"  no usable line in it; calibrating instead.\n");
            }
        }
        if (!haveSaved) {
            // The branch above has already said whether the file was absent or
            // unusable; repeating it here printed two different messages in a row.
            useSavedCal=0;
        } else {
            fprintf(log,"CALIBRATION FROM FILE: click %+.5f deg/px, drag %+.5f deg/px, key %+.5f deg/ms\n",
                savedClick,savedDrag,savedKey);
            fprintf(log,"  (only the camera yaw is measured now - one click - so this run spends\n"
                        "   its time walking instead of calibrating. Delete the file to force a\n"
                        "   full recalibration.)\n");
        }
    }
    {
        probe_t p;
        if (!probe_click(&c,0,calDy,&p)) { fprintf(log,"calibration click 1 failed\n"); goto done; }
        if (!p.clean || p.travel<CAL_MIN_WALK) {
            fprintf(log,"The centre click walked %.2f yd and %s. That is not a usable\n",
                p.travel,p.clean?"is too short to trust":"never came to rest");
            fprintf(log,"heading, so nothing is calibrated and nothing will be walked. Stand on\n"
                        "open ground with room in front of the camera; --sendinput if clicks are\n"
                        "being ignored.\n");
            goto done;
        }
        hRef=p.heading;
        fprintf(log,"cal[0] centre click dy=%d -> %.2f yd at heading %.1f deg (camera yaw now)\n",
            calDy,p.travel,hRef);
    }
    int needTurnProbe=1;
    if (haveSaved) {
        P.degPerPx=savedClick; P.degPerPxRef=savedClick;
        P.degPerDragPx=savedDrag; P.degPerDragPxRef=savedDrag;
        P.degPerKeyMs=savedKey; P.degPerKeyMsRef=savedKey;
        if (fabs(savedKey)>1e-6) P.turnKey=1;
        if (fabs(savedClick)<1e-4) { fprintf(log,"The saved click slope is zero; delete the file.\n"); goto done; }
        // A file written by a run that could not measure a turn channel holds zeros
        // for it, and loading those zeros is exactly what made the --load run of
        // 2026-10-29 refuse to plan a single leg. Zero means UNKNOWN: probe it.
        needTurnProbe = (fabs(savedDrag)<1e-9 && fabs(savedKey)<1e-9);
        if (needTurnProbe)
            fprintf(log,"  the saved file has no turn channel (drag 0, key 0): probing those now.\n");
    } else
    {
        double drv[6]={0}, trn[6]={0};
        int ns=0, verified=0, bad=0, deadProbes=0;
        for (int k=0;k<4;k++) {
            int dx=calDx[k];
            probe_t p;
            if (!probe_click(&c,dx,calDy,&p)) { fprintf(log,"calibration probe dx=%+d failed\n",dx); goto done; }
            // The reference moves with every probe, so a probe that cannot be
            // measured must not become the next reference: keep hRef on the last
            // heading that was actually a walk.
            if (p.travel<2.0) {
                deadProbes++;
                if (deadProbes>=2) {
                    fprintf(log,"\nTHE CHARACTER IS NOT WALKING: two probes in a row moved it %.2f yd.\n"
                                "No camera can be measured from that, and a model fitted to it would be\n"
                                "fiction. Usual causes, in order: it is dead, it is rooted or in combat,\n"
                                "it is inside geometry (a slope, a wall, a doorway), or the click point\n"
                                "is under a roof. Move it by hand, then re-run.\n",p.travel);
                    goto done;
                }
            } else deadProbes=0;
            if (!p.clean || p.travel<CAL_MIN_WALK) {
                fprintf(log,"  dx=%+4d discarded (%s): it does not update the reference\n",dx,
                    !p.clean?"never came to rest":"walk under 6 yd");
                continue;
            }
            double turn=am_wrap_deg(p.heading-hRef);
            drv[ns]=dx; trn[ns]=turn; ns++;
            fprintf(log,"  dx=%+4d turn %+7.2f deg from the previous walk's heading -> %+.5f deg/px\n",
                dx,turn,turn/(double)dx);
            hRef=p.heading;
        }
        double slope=0;
        int kept=am_median_slope(ns,drv,trn,2.0,&slope);
        if (kept<2) {
            fprintf(log,"\n%d usable click probe(s). One or none is not a camera model - that is\n",kept);
            fprintf(log,"exactly how v6 came out 3.3x wrong. Nothing is walked.\n");
            goto done;
        }
        P.degPerPx=slope;
        fprintf(log,"\nclick fit: median of %d of %d probes, %+.5f deg/px (%.2f deg per 100 px)\n",
            kept,ns,P.degPerPx,P.degPerPx*100.0);
        for (int k=0;k<ns;k++)
            fprintf(log,"  probe dx=%+4d: measured %+7.2f, model %+7.2f, error %+6.2f deg\n",
                (int)drv[k],trn[k],P.degPerPx*drv[k],trn[k]-P.degPerPx*drv[k]);
        {
            double span=P.degPerPx*600.0;
            if (fabs(span)<CAL_MIN_SPAN || fabs(span)>CAL_MAX_SPAN) {
                fprintf(log,"\n600 px of click offset implies %.1f deg of camera turn, outside the\n"
                            "%.0f-%.0f deg a client of this width can actually do. The probes are not\n"
                            "measuring the camera. Nothing is walked.\n",
                    span,CAL_MIN_SPAN,CAL_MAX_SPAN);
                goto done;
            }
            fprintf(log,"  +/-300 px spans %.1f deg of camera turn (plausible %.0f-%.0f)\n",
                fabs(span),CAL_MIN_SPAN,CAL_MAX_SPAN);
        }
        for (int k=0;k<2;k++) {
            int dx=holdDx[k];
            probe_t p;
            if (!probe_click(&c,dx,calDy,&p)) { fprintf(log,"holdout probe failed\n"); goto done; }
            if (!p.clean || p.travel<CAL_MIN_WALK) {
                fprintf(log,"  holdout dx=%+4d unusable (%s) - counted neither way\n",dx,
                    !p.clean?"did not rest":"walk under 10 yd");
                continue;
            }
            double turn=am_wrap_deg(p.heading-hRef), pred=P.degPerPx*dx;
            fprintf(log,"  holdout dx=%+4d: measured %+7.2f deg, model %+7.2f, error %+6.2f%s\n",
                dx,turn,pred,turn-pred,
                am_prediction_ok(turn,pred,0.35,6.0)?"":"  OUTSIDE TOLERANCE");
            if (!am_prediction_ok(turn,pred,0.35,6.0)) bad++;
            hRef=p.heading;
        }
        verified = (bad==0);
        if (verified)
            fprintf(log,"  both held-out offsets are predicted: click sensitivity VERIFIED\n");
        else
            fprintf(log,"\n  Held-out offsets miss, so this is an UNVERIFIED slope: the response is not\n"
                        "  linear over +/-300 px (it saturates), and the walker will correct it leg by\n"
                        "  leg instead of trusting it. That is a weaker claim than v7 made, and a\n"
                        "  stronger one than v6 made - the tool walks, and says which it is.\n");
        {
            double edge=0; int ne=0;
            for (int k=0;k<ns;k++)
                if (fabs(drv[k])>=220.0) { edge+=fabs(trn[k])/fabs(drv[k]); ne++; }
            if (ne) {
                edge/=ne;
                double mid=fabs(P.degPerPx);
                if (fabs(edge-mid) > 0.20*fabs(mid))
                    fprintf(log,"  but the edge of the range is steeper than the middle: %.2f vs %.2f\n"
                                "  deg per 100 px, so this is a local slope and not a straight line.\n"
                                "  Plans stay inside %d px and every leg re-measures, which is why\n"
                                "  the loop still converges when the model is a little wrong.\n",
                        edge*100.0,mid*100.0,CLICK_PLAN_MAX_PX);
            }
        }
        if (fabs(P.degPerPx)<1e-4) { fprintf(log,"Heading barely responds to dx; cannot aim.\n"); goto done; }
        P.degPerPxRef=P.degPerPx;   // drift is reported against the calibration value
    }
    // dy spread: measured only to REPORT that dy does not control distance. Two
    // field runs fitted "dist = a + b*dy" and got negative predictions, because a
    // single dy produced 0.09 to 12.35 yd depending on the terrain under the
    // cursor. So this is evidence, not a model - nothing is driven from it.
    double spreadMin=1e9, spreadMax=-1e9;
    for (int k=0;k<((quick||!dyEvidence)?0:2);k++) {
        int dy = (k==0) ? dyMin : dyMax;
        float a[3], b[3]; double secs=0;
        if (!rest_click(&c,0,dy,a,b,&secs,0)) { fprintf(log,"calibration click failed\n"); goto done; }
        double d=dist_of(a,b);
        if (d<spreadMin) spreadMin=d;
        if (d>spreadMax) spreadMax=d;
        fprintf(log,"cal[%d] click dx=0 dy=%+d -> %.2f yd (%.2f s, %.2f yd/s)\n",
            3+k,dy,d,secs,d/(secs>0?secs:1));
    }
    if (dyEvidence && !quick) {
        fprintf(log,"dy spread: %.2f to %.2f yd for the same click offset - dy is a POSITION hint,\n",
            spreadMin,spreadMax);
        fprintf(log,"not a distance control. Distance is closed by iteration instead.\n");
        if (spreadMax>2.5*spreadMin && spreadMin>0.5)
            fprintf(log,"(spread above 2.5x: the terrain under the cursor decides, as measured.)\n");
    }

    // ------------------------------------------------------- turn channels
    // A click only reaches headings inside the camera cone, so anything behind
    // the camera has to be turned to first. Two channels exist: a horizontal
    // right-drag (the client rotates from successive mouse deltas) and a held
    // turn key. Both are probed, because which one works depends on whether the
    // window is receiving real mouse input - a background window may not be.
    printf("PROBING TURN CHANNELS: %d px drag, then a %d ms key hold.\n",TURN_PROBE_PX,TURN_PROBE_MS);
    fflush(stdout);
    // The last probe above ended in a walk, and the camera has re-centred to that
    // walk's heading - the same rule the click fit is built on. So no extra
    // baseline walk is needed here; adding one would be one more rotation to
    // account for, which is precisely what went wrong in v7.
    double hPrev=hRef;
    WORD vkPos=0, vkNeg=0;
    fprintf(log,"turn baseline: heading %.1f deg (from the last calibration walk)\n",hPrev);
    if (!forceKey && needTurnProbe) {
        // The drag channel gets the same treatment as the click channel, because
        // it produced the same failure: v8 measured ONE direction (-200 px probe
        // walked only 4.78 yd and could not be measured), accepted -0.19706 deg/px
        // from a single +200 px sample, and then asked for +532 px per leg. Earlier
        // runs had the single-sample drag channel at -0.36373, -0.38165, -0.22653,
        // -0.22762 and +0.04202 deg/px. Four probes, both directions, two sizes,
        // then a median - and the SAME moving reference as the click fit, because
        // a drag is judged against the heading of the walk that preceded it.
        static const int dragPx[4]={+200,-200,+300,-300};
        double drv[4]={0}, trn[4]={0};
        int ns=0, unmeasured=0, poisonNext=0;
        for (int k=0;k<4;k++) {
            int dpx=dragPx[k];
            double hbase=hPrev;
            rest_drag(&c,dpx);
            probe_t p;
            if (!probe_click(&c,0,calDy,&p)) { fprintf(log,"drag probe click failed\n"); goto done; }
            if (!p.clean || p.travel<CAL_MIN_TURN_WALK) {
                unmeasured++;
                fprintf(log,"cal[drag] %+5d px: the click after the drag walked %.2f yd%s, under the\n"
                            "  2.5 yd a heading needs (probe skipped).\n",
                    dpx,p.travel,p.clean?"":" and never rested");
                // The camera DID rotate, so leaving hPrev where it was would make the
                // next probe's turn include this one. Re-anchor with one more click at
                // a different height, where the ground is more likely to be walkable.
                probe_t re;
                if (probe_click(&c,0,-200,&re) && re.clean && re.travel>=CAL_MIN_TURN_WALK) {
                    hPrev=re.heading;
                    fprintf(log,"      re-anchored the heading on that walk: %.1f deg\n",hPrev);
                } else {
                    fprintf(log,"      could not re-anchor the heading; the next drag probe's turn\n"
                                "      will include this drag as well, so it is discarded below.\n");
                    poisonNext=1;
                }
                continue;
            }
            double turn=am_wrap_deg(p.heading-hbase);
            if (poisonNext) {
                poisonNext=0;
                fprintf(log,"      discarded: its reference heading was lost after the previous\n"
                            "      probe could not be measured\n");
                hPrev=p.heading;
                continue;
            }
            drv[ns]=dpx; trn[ns]=turn; ns++;
            fprintf(log,"cal[drag] %+5d px then click -> heading %.1f deg, turn %+.1f deg over %.2f yd\n",
                dpx,p.heading,turn,p.travel);
            fprintf(log,"      %+5d px -> %+.5f deg/px\n",dpx,turn/(double)dpx);
            hPrev=p.heading;
        }
        double slope=0;
        int kept=am_median_slope(ns,drv,trn,2.0,&slope);
        if (kept>=2) {
            P.degPerDragPx=slope;
            double bias=0; for (int k=0;k<ns;k++) bias+=trn[k]-slope*drv[k];
            bias/= (ns?ns:1);
            fprintf(log,"      drag fit: median of %d of %d probes, %+.5f deg per pixel (%+.2f per 100 px)\n",
                kept,ns,P.degPerDragPx,P.degPerDragPx*100.0);
            fprintf(log,"      leftover %+.1f deg that the drag does not explain (camera drift or a\n"
                        "      non-linear drag); anything above ~10 deg means the channel is loose.\n",bias);
            if (unmeasured)
                fprintf(log,"      %d of 4 drag probes could not be measured; the slope is from the rest.\n",
                    unmeasured);
            if (fabs(P.degPerDragPx)>=TURN_PROBE_MIN_DEG/(double)TURN_PROBE_PX)
                P.degPerDragPxRef=P.degPerDragPx;
            else {
                fprintf(log,"      net turn per pixel is below the noise floor; drag treated as unproven.\n");
                P.degPerDragPx=0;
            }
        } else if (kept==1) {
            // One direction only. Keep it, because a wedged bot is worse than a
            // rough slope, but say so and cap how far a leg may ask it to turn.
            P.degPerDragPx=slope;
            P.degPerDragPxRef=slope;
            fprintf(log,"      ONLY ONE DRAG DIRECTION WAS MEASURABLE (%+.5f deg/px). The drag\n"
                        "      channel is unverified; per-leg drags are capped at %d px.\n",
                P.degPerDragPx,DRAG_MAX_PX);
        } else {
            fprintf(log,"      not one drag probe could be measured; the drag channel is unproven.\n");
        }
    }
    if (needTurnProbe && !forceDrag && (forceKey || fabs(P.degPerDragPx)<1e-5)) {
        // Try both arrows: which way each turns is a keybinding question, and a
        // rebound client need not match the defaults.
        WORD order[2]={VK_RIGHT,VK_LEFT};
        for (int k=0;k<2 && !vkPos;k++) {
            rest_key(&c,order[k],TURN_PROBE_MS);
            probe_t p;
            if (!probe_click(&c,0,calDy,&p)) { fprintf(log,"key probe click failed\n"); goto done; }
            if (!p.clean || p.travel<CAL_MIN_TURN_WALK) {
                fprintf(log,"cal[key] held %s %d ms -> the walk afterwards was %.2f yd%s, under\n"
                            "  the 2.5 yd a heading needs; unmeasurable\n",
                    order[k]==VK_RIGHT?"RIGHT":"LEFT",TURN_PROBE_MS,p.travel,p.clean?"":" and never rested");
                continue;
            }
            double h=p.heading;
            double turn=am_wrap_deg(h-hPrev);
            fprintf(log,"cal[key] held %s %d ms then click -> heading %.1f deg, turn %+.1f deg\n",
                order[k]==VK_RIGHT?"RIGHT":"LEFT",TURN_PROBE_MS,h,turn);
            hPrev=h;
            if (fabs(turn)>=TURN_PROBE_MIN_DEG) {
                P.degPerKeyMs=turn/(double)TURN_PROBE_MS;
                P.degPerKeyMsRef=P.degPerKeyMs;
                P.turnKey=1;
                if (turn>0) { vkPos=order[k]; vkNeg=(order[k]==VK_RIGHT)?VK_LEFT:VK_RIGHT; }
                else        { vkNeg=order[k]; vkPos=(order[k]==VK_RIGHT)?VK_LEFT:VK_RIGHT; }
                fprintf(log,"      key channel: %+.5f deg per ms (%+.2f deg per 100 ms);\n"
                            "      %s turns the heading up, %s turns it down\n",
                    P.degPerKeyMs,P.degPerKeyMs*100.0,
                    vkPos==VK_RIGHT?"RIGHT":"LEFT",vkNeg==VK_RIGHT?"RIGHT":"LEFT");
            } else {
                fprintf(log,"      that arrow did NOT turn the camera (%+.1f deg for %d ms).\n",
                    turn,TURN_PROBE_MS);
            }
        }
    }
    if (fabs(P.degPerDragPx)<1e-5 && fabs(P.degPerKeyMs)<1e-5) {
        fprintf(log,"\nWARNING: neither turn channel moved the camera. Only goals already inside\n"
                    "the click cone can be reached. If the window is in the background, try\n"
                    "--sendinput with the client in front, or bind turn keys in game.\n");
    }
    if (!haveSaved && !(fabs(P.degPerPx)>1e-4 && (fabs(P.degPerDragPx)>1e-5 || fabs(P.degPerKeyMs)>1e-6))) {
        fprintf(log,"\nNOT SAVING THIS CALIBRATION: it has %s. A saved file with a zero turn\n"
                    "channel is what stopped the next --load run from planning at all, so the\n"
                    "old file is left alone and the next run calibrates again.\n",
            fabs(P.degPerPx)<=1e-4 ? "no click slope" : "no usable turn channel");
        haveSaved=1;               // suppress the write without pretending the model is fine
    }
    if (!haveSaved) {
        FILE *f=_wfopen(calPath,L"a");
        if (f) {
            fprintf(f,"degPerPx=%.6f degPerDragPx=%.6f degPerKeyMs=%.6f\n",
                P.degPerPx,P.degPerDragPx,P.degPerKeyMs);
            fclose(f);
            fprintf(log,"calibration saved to %ls - next run with --load skips the probing\n",calPath);
        }
    }
    // The planner's origin: the heading a centre click walks RIGHT NOW. Every
    // probe above ends with a centre click, so the last measured heading is it.
    // v3 never set this at all, so the first leg planned against heading 0.
    P.headingAtZero=hPrev;
    P.coneHalfDeg=am_cone_half_deg(P.degPerPx,c.cw/2);
    fprintf(log,"heading at centre right now = %.1f deg; clicks reach +/-%.1f deg around it\n",
        P.headingAtZero,P.coneHalfDeg);

    if (!doWalk && !doGoto) {
        fprintf(log,"\nMODEL\n  heading at centre = %.1f deg, %.5f deg/px click\n"
                    "  turn: %+.5f deg/px drag, %+.5f deg/ms key (using %s)\n"
                    "  cone +/-%.1f deg, preferred dy=%d (short hop %d), learn above %.1f yd\n",
            P.headingAtZero,P.degPerPx,P.degPerDragPx,P.degPerKeyMs,
            P.turnKey?"keys":"drag",P.coneHalfDeg,P.preferredDy,P.nearDy,P.minLearnMove);
        fprintf(log,"\nNothing walked. Re-run with --walk DIST [DEG] or --goto X Y to use it.\n");
        rc=0; goto done;
    }

    // ------------------------------------------------------------------ goal
    float start[3];
    if (!wait_rest(&c,start,"pre-walk")) { fprintf(log,"rest failed\n"); goto done; }
    double gx, gy;
    if (doGoto) { gx=goalx; gy=goaly; }
    else {
        double rad=walkDeg*AM_D2R;
        gx=start[0]+walkDist*cos(rad);
        gy=start[1]+walkDist*sin(rad);
    }
    fprintf(log,"\nWALK: from (%.2f, %.2f) to (%.2f, %.2f), %.1f yd, tolerance %.1f yd\n",
        start[0],start[1],gx,gy,am_len(gx-start[0],gy-start[1]),P.tol);
    if (c.stopEnabled)
        fprintf(log,"capture: cancel the walk within %.1f yd of the goal (tap %c, %d ms);\n"
                    "  stop distance starts at %.2f yd and is measured from the first stop\n",
            capture,STOP_KEY,STOP_HOLD_MS,c.stopDistance);
    else
        fprintf(log,"capture: DISABLED (--nostop); every leg runs to wherever the click lands\n");

    printf("WALKING to (%.1f, %.1f). Hands off.\n",gx,gy);
    fflush(stdout);
    int converged=0, legs=0, noProgress=0, stuck=0, nudges=0, discontinuities=0;
    int passbys=0, neverStopped=0, detours=0, reaches=0, runaways=0, stagnant=0;
    double bestRemaining=1e9;
    // Nudging to a different dy changes the terrain under the cursor, which is the
    // point. It must NOT change the distance much: dy=-200 once walked 99.35 yd
    // from a 30 yd goal and had to be walked back (2026-10-29 16:0x). Short hops.
    int dyCycle[3]={P.preferredDy,-70,-90};
    for (legs=1; legs<=MAX_LEGS; legs++) {
        float cur[3];
        if (!wait_rest(&c,cur,"between legs")) { fprintf(log,"rest failed\n"); goto done; }
        if (c.lastRestTimedOut) {
            runaways++;
            fprintf(log,"leg %2d: RUNAWAY before the leg even started - the character was still\n"
                        "        moving after %d ms of waiting.\n",legs,REST_TIMEOUT_MS);
            panic_stop(&c,"still moving between legs");
            if (runaways>=2) {
                fprintf(log,"\nSTOPPING: two runaways, so the input state is not staying released.\n"
                            "Try again; if it keeps happening the drag is leaving a button down in\n"
                            "this client and --sendinput is the way to find out.\n");
                break;
            }
            if (!wait_rest(&c,cur,"after panic stop")) { fprintf(log,"rest failed\n"); goto done; }
        }
        double remx=gx-cur[0], remy=gy-cur[1];
        double remaining=am_len(remx,remy);
        leg_t leg;
        int pr=am_plan_leg(&P,remx,remy,&leg);
        if (pr==0) { converged=1; fprintf(log,"\nREACHED: remaining %.2f yd after %d legs\n",remaining,legs-1); break; }
        // A goal behind the camera with no turn channel is not a dead end. The
        // camera follows the character, so walking at the edge of the cone leaves
        // the next leg starting from a heading closer to the goal: the bot can turn
        // by walking. v9 broke out with "cannot plan a leg" and moved 0.00 yd in
        // both 2026-10-29 runs because the walk's own heading was 90 deg off.
        int isReach=0;
        if (pr<0 && reaches<MAX_REACH) {
            double wanted=am_heading_deg(remx,remy);
            double delta=am_wrap_deg(wanted-P.headingAtZero);
            double edge=((delta>=0)?1.0:-1.0)*P.coneHalfDeg*0.9;
            double rad=am_wrap_deg(P.headingAtZero+edge)*AM_D2R;
            double rx=remaining*cos(rad), ry=remaining*sin(rad);
            if (am_plan_leg(&P,rx,ry,&leg)==1) {
                isReach=1; reaches++;
                fprintf(log,"      REACH-AROUND %d/%d: the goal is %+.1f deg off the centre heading\n"
                            "      and there is no turn channel, so this leg walks to the edge of the\n"
                            "      cone (%+.1f deg). The camera re-centres on the walk, and the next\n"
                            "      leg aims again from there.\n",reaches,MAX_REACH,delta,edge);
            }
        }
        if (pr<0 && !isReach) {
            double wanted=am_heading_deg(remx,remy);
            fprintf(log,"\nCannot plan a leg: the goal heading is %.1f deg, heading at centre is\n"
                        "%.1f deg, the cone is +/-%.1f deg, no turn channel was calibrated, and\n"
                        "%d reach-around legs did not bring the goal into the cone.\n",
                wanted,P.headingAtZero,P.coneHalfDeg,reaches);
            break;
        }
        // ---------------------------------------------------------- detour
        // The straight line is not the only way, and on this client it is often
        // not a way at all: hills, rocks, trees, fences and monsters sit between
        // any two points. v8 answered a blocked leg with "no progress: trying
        // dy=..." - the same heading, a slightly different click point, straight
        // into the same obstacle - and gave up after four of them. That is not a
        // blocked goal, it is a blocked heading.
        //
        // So when two legs in a row come out short, this leg goes SIDEWAYS: turn
        // DETOUR_DEG off the goal bearing, click far, walk the tangent, and let
        // the next leg plan from the new position. The side alternates, so two
        // attempts are not two pushes at the same rock.
        int isDetour=0;
        // Two signals mean "this heading is not the way there": legs that hardly
        // move, and legs that move fine but leave the goal no closer. The 100 yd
        // run only ever saw the first, so it walked 5-13 yd at a time through a
        // dozen legs and gained nothing (43.09 -> 43.00 -> 43.50 -> 41.78 ...).
        if ((stuck>=2 || stagnant>=3) && detours<MAX_DETOURS) {
            int side=am_detour_side(detours+1);
            double turnDeg=side*DETOUR_DEG;
            isDetour=1; detours++; stuck=0; stagnant=0;
            leg.clickPx=0; leg.dy=farDy;
            if (fabs(P.degPerDragPx)>1e-5) {
                int px=(int)(turnDeg/P.degPerDragPx);
                if (px> DRAG_MAX_PX) px= DRAG_MAX_PX;
                if (px<-DRAG_MAX_PX) px=-DRAG_MAX_PX;
                leg.turn=AM_TURN_DRAG; leg.dragPx=px;
            } else if (fabs(P.degPerKeyMs)>1e-6) {
                int ms=(int)(turnDeg/P.degPerKeyMs);
                if (ms> P.maxTurnMs) ms=(int)P.maxTurnMs;
                if (ms<-(int)P.maxTurnMs) ms=-(int)P.maxTurnMs;
                leg.turn=AM_TURN_KEY; leg.keyMs=ms;
            } else {
                // No turn channel at all: the only detour left is a different
                // click point, which is worth trying rather than giving up.
                leg.turn=AM_TURN_NONE;
            }
            fprintf(log,"      DETOUR %d/%d: %s %.0f deg to walk around whatever is in the way,\n"
                        "      then a far click (dy=%d).\n",
                detours,MAX_DETOURS,side>0?"right":"left",DETOUR_DEG,leg.dy);
        }
        int preClean=c.lastRestClean;
        if (leg.turn==AM_TURN_DRAG) {
            // Never ask for a drag outside the range the probes covered: v8 asked
            // for +532 px when every measurement it had was at +/-200, and a
            // saturated drag is free to be anything.
            if (leg.dragPx> DRAG_MAX_PX) leg.dragPx= DRAG_MAX_PX;
            if (leg.dragPx<-DRAG_MAX_PX) leg.dragPx=-DRAG_MAX_PX;
            if (!isDetour && fabs(P.degPerDragPx)>1e-9) {
                double capPx=TURN_MAX_DEG/fabs(P.degPerDragPx);
                if (fabs((double)leg.dragPx)>capPx) {
                    int sign=(leg.dragPx>0)?1:-1;
                    leg.dragPx=sign*(int)capPx;
                    fprintf(log,"      (turn capped to %.0f deg: one drag that big overshoots -\n"
                                "       the rest of the turn comes from the next leg)\n",TURN_MAX_DEG);
                }
            }
            rest_drag(&c,leg.dragPx);
        }
        else if (leg.turn==AM_TURN_KEY) {
            WORD vk=(leg.keyMs>=0)?vkPos:vkNeg;
            if (!vk) { fprintf(log,"\nPlanner asked for a key turn but no arrow was calibrated.\n"); break; }
            rest_key(&c,vk,leg.keyMs);
        }
        // The click is measured from the very position this leg was planned from,
        // so a stale reading cannot make the plan and the measurement disagree.
        if (leg.clickPx> CLICK_PLAN_MAX_PX) leg.clickPx= CLICK_PLAN_MAX_PX;
        if (leg.clickPx<-CLICK_PLAN_MAX_PX) leg.clickPx=-CLICK_PLAN_MAX_PX;
        float a[3]; memcpy(a,cur,sizeof(a));
        float b[3]; double secs=0; legcap_t cap;
        if (!click_and_capture(&c,leg.clickPx,leg.dy,gx,gy,capture,a,b,&secs,&cap)) {
            fprintf(log,"leg click failed\n"); goto done;
        }
        int postClean=c.lastRestClean;
        double moved=dist_of(a,b);
        double newRemaining=am_len(gx-b[0],gy-b[1]);
        // The closest approach is the only place a lateral miss is visible: a leg
        // can end 20 yd out having passed 4 yd from the goal, and no other number
        // in this log distinguishes that from a leg that never went near it.
        double closest = cap.polled ? cap.closest : newRemaining;
        if (cap.stopped) {
            // How far it kept running after the key; the next stop is placed that
            // much earlier. Only a coast long enough to be the client's reaction
            // time counts - a character already slowing coasts centimetres and
            // measures the stop, not the latency (v6 learned 0.07 yd and moved
            // the stop distance 0.80 -> 0.58 on that alone).
            double coast=cap.stopAt-newRemaining;
            double before=c.stopDistance;
            double learned=am_learn_coast(c.stopDistance,coast);
            if (learned!=before)
                fprintf(log,"      (%s stop at %.2f yd, coasted %.2f yd; stop distance %.2f -> %.2f yd)\n",
                    cap.passby?"pass-by":"capture",cap.stopAt,coast,before,learned);
            else
                fprintf(log,"      (%s stop at %.2f yd, coasted %.2f yd; too little to learn a stop\n"
                            "       distance from - the character was already slowing - keeping %.2f yd)\n",
                    cap.passby?"pass-by":"capture",cap.stopAt,coast,before);
            c.stopDistance=learned;
        }
        if (cap.passby) {
            passbys++;
            fprintf(log,"      (passed the goal at %.2f yd; cancelled the walk rather than run on)\n",
                closest);
        }
        if (!cap.stopped) neverStopped++;

        // ------------------------------------------------------------ runaway
        // A click moves a character at walking or running speed for as long as it
        // took to reach the point. A leg that covers 171.52 yd in 25.00 s, or a
        // character still moving when the rest wait gives up, is not a click being
        // followed - it is a button left down. v9 carried on from wherever it had
        // got to, spent 15 legs walking back, and ended 93 yd from a 30 yd goal.
        // A long leg is not a runaway: a far click legitimately walks as far as the
        // point it was given. v12 treated any leg over 40 yd as a stuck button and
        // panicked on a planned dy=-200 click that had walked 99.35 yd correctly.
        // The real signature is a walk that is still going when the rest wait gives
        // up, or a near hop that travels like a far click.
        double plausible = (leg.dy>=-80) ? 10.0 : 60.0;
        int runaway = c.lastRestTimedOut || (moved>RUNAWAY_MOVE && moved>3.0*plausible);
        if (runaway && !isReach) {
            runaways++;
            fprintf(log,"      RUNAWAY: a click at dy=%d should walk about %.0f yd; this leg took\n"
                        "      %.2f yd in %.2f s%s.\n",
                leg.dy,plausible,moved,secs,c.lastRestTimedOut?" and was still moving when the rest wait gave up":"");
            panic_stop(&c,runaways==1?"a leg ran far past what a click means":"the runaway repeated");
        }
        if (runaways>=2 || newRemaining > RUNAWAY_FACTOR*am_len(gx-start[0],gy-start[1])) {
            fprintf(log,"\nSTOPPING: %.0f yd from a goal that started %.0f yd away, after %d runaway\n"
                        "leg(s). This is not a path problem - the client is being told to keep\n"
                        "walking. Run again; if it repeats, the same spot needs --sendinput,\n"
                        "which drives the real cursor and cannot leave a synthetic button down.\n",
                newRemaining,am_len(gx-start[0],gy-start[1]),runaways);
            break;
        }

        // For a fixed goal the distance can grow by at most the distance walked,
        // and no single step can exceed run speed. The v4 field run broke both
        // (remaining 2.04 -> 30.83 after "moving" 5.45 yd, then 30.83 -> 110.23
        // after "moving" 0.63 yd). That leg is not a measurement of anything.
        if (!am_leg_consistent(remaining,newRemaining,moved,0.5) || !am_plausible_leg(moved,secs)) {
            discontinuities++;
            fprintf(log,"leg %2d: IMPOSSIBLE GEOMETRY - moved %.2f yd in %.2f s but remaining went\n"
                        "        %.2f -> %.2f yd. The position read is discontinuous; re-planning\n"
                        "        from a fresh read and taking no learning from this leg.\n",
                legs,moved,secs,remaining,newRemaining);
            if (discontinuities>4) { fprintf(log,"Too many discontinuous reads; stopping.\n"); break; }
            continue;
        }
        fprintf(log,"leg %2d: %s=%+5d click=%+5d dy=%+4d -> moved %5.2f yd in %5.2f s, remaining %6.2f -> %6.2f, closest=%5.2f yd (%s)\n",
            legs,leg.turn==AM_TURN_KEY?"keyms":"drag",leg.turn==AM_TURN_KEY?leg.keyMs:leg.dragPx,
            leg.clickPx,leg.dy,moved,secs,remaining,newRemaining,closest,
            cap.stopped ? (cap.passby?"pass-by stop":"stop") : "no stop");
        fflush(log);

        // Learn only from legs that actually travelled: a 0.09 yd bump reports a
        // meaningless heading, and feeding that in flipped the model's sign twice
        // in the field run.
        if (moved > 0.5 && preClean && postClean) {
            stuck=0;              // only CONSECUTIVE non-moves mean blocked
            double walkedHeading=heading_of(a,b);
            double turn=am_wrap_deg(walkedHeading-P.headingAtZero);
            if (leg.turn==AM_TURN_DRAG && leg.dragPx) {
                if (am_learn_slope(&P.degPerDragPx,P.degPerDragPxRef,turn,(double)leg.dragPx,
                                   P.minLearnMove,P.maxLearnMove,moved,0.5))
                    fprintf(log,"      (drag slope -> %.5f deg/px)\n",P.degPerDragPx);
            }
            if (leg.turn==AM_TURN_KEY && leg.keyMs) {
                if (am_learn_slope(&P.degPerKeyMs,P.degPerKeyMsRef,turn,(double)leg.keyMs,
                                   P.minLearnMove,P.maxLearnMove,moved,0.5))
                    fprintf(log,"      (key slope -> %.5f deg/ms)\n",P.degPerKeyMs);
            }
            if (abs(leg.clickPx)>=40) {
                if (am_learn_slope(&P.degPerPx,P.degPerPxRef,turn,(double)leg.clickPx,
                                   P.minLearnMove,P.maxLearnMove,moved,0.35))
                    fprintf(log,"      (click slope -> %.5f deg/px)\n",P.degPerPx);
            }
            // v5 and v6 back-solved the camera yaw from the click model
            // (walkedHeading - degPerPx*clickPx), which imports the model's own
            // error into the next leg's reference. The follow camera swings back
            // behind the character while it runs, so the measured heading of this
            // walk IS the yaw the next click will use - measured, not inferred.
            // A 0.6 yd walk reports a heading with tens of degrees of noise, and
            // v13 re-aimed itself from every one of them: the 100 yd run re-fitted
            // its yaw from 0.63-1.35 yd legs and then circled for a dozen legs at
            // 40 yd from the goal, alternating 45 deg drags. The yaw is where the
            // character is facing; it needs a walk long enough to have one.
            if (leg.turn!=AM_TURN_NONE) {
                // A turn leg does NOT walk along the camera it turned to. The right
                // button is held down during the drag, so the character runs along
                // its OLD facing, and only the click after it walks along the new
                // one - the displacement is the sum of the two and its heading
                // belongs to neither camera. Using it as the next yaw is what made
                // the 15:56 and 15:57 runs ask for drag after drag: the estimate
                // swung 172 deg across a 25 yd goal, the goal never re-entered the
                // click cone, and 24 legs went nowhere.
                double pred=(leg.turn==AM_TURN_DRAG) ? P.degPerDragPx*(double)leg.dragPx
                                                     : P.degPerKeyMs*(double)leg.keyMs;
                P.headingAtZero=am_wrap_deg(P.headingAtZero+pred);
                fprintf(log,"      (turn leg: yaw advanced by the model %+.1f deg to %.1f deg -\n"
                            "       the walk heading is not the camera here, the drag runs the\n"
                            "       character along its old facing)\n",pred,P.headingAtZero);
            } else if (moved>=HEADING_MIN_WALK)
                P.headingAtZero=am_wrap_deg(walkedHeading);
            else
                fprintf(log,"      (walked %.2f yd: too short to re-measure the camera yaw,\n"
                            "       keeping %.1f deg)\n",moved,P.headingAtZero);
        } else if (moved < DETOUR_MOVE) {
            // Short could mean "blocked" or "the click landed on a wall face".
            // Either way the next attempt gets a different point and, if this
            // happens twice running, a different heading.
            stuck++;
            if (isDetour)
                fprintf(log,"      (the detour barely moved either: %.2f yd - something is all\n"
                            "       around this spot, not just ahead)\n",moved);
            else
                fprintf(log,"      (barely moved: that click hit something; no learning taken)\n");
        }

        // A leg that ends further away is a lateral miss, not slow progress. v6
        // treated these as "no progress" and answered with dy nudges, which moved
        // the click point to different terrain and made the next leg miss the
        // other way. Credit the closest approach instead: if it came near, the
        // goal IS reachable from here and the next leg plans from the new spot.
        if (closest <= remaining - 0.5) noProgress = (newRemaining > remaining-0.2);
        else if (newRemaining > remaining-0.2) noProgress++;
        else noProgress=0;
        if (newRemaining > remaining-1.0) stagnant++; else stagnant=0;
        if (newRemaining<bestRemaining) bestRemaining=newRemaining;
        if (stuck>=2 && detours>=MAX_DETOURS) {
            fprintf(log,"\nBLOCKED: %d detours and the legs still move under %.1f yd. This is not a\n"
                        "heading problem any more - the goal is walled in, under water, or up a\n"
                        "slope this client will not walk. Best remaining %.2f yd.\n",
                detours,DETOUR_MOVE,bestRemaining);
            break;
        }
        if (stuck>=6) {
            fprintf(log,"\nBLOCKED: six legs in a row moved under %.1f yd even with detours.\n"
                        "Best remaining %.2f yd.\n",DETOUR_MOVE,bestRemaining);
            break;
        }
        if (noProgress>=2 && nudges<6) {
            // Change the click point: a different dy lands on different terrain.
            nudges++;
            P.preferredDy=dyCycle[nudges%3];
            fprintf(log,"      (no progress: trying dy=%d to move the click point)\n",P.preferredDy);
            noProgress=0;
        }
    }
    // A converged walk is itself a measurement of the same camera: it arrived, so
    // the slopes it learned on the way are worth keeping. One saved line per run
    // is what turns the file from "the last calibration" into a population, and
    // the 2026-10-29 pair showed why that matters - --load reused a single
    // -0.08631 sample while the walk measured -0.0934 then -0.10017 (+16 %).
    if (converged && fabs(P.degPerPx)>1e-4 &&
        (fabs(P.degPerDragPx)>1e-5 || fabs(P.degPerKeyMs)>1e-6)) {
        FILE *f=_wfopen(calPath,L"a");
        if (f) {
            fprintf(f,"degPerPx=%.6f degPerDragPx=%.6f degPerKeyMs=%.6f\n",
                P.degPerPx,P.degPerDragPx,P.degPerKeyMs);
            fclose(f);
            fprintf(log,"model refined by this walk appended to %ls; --load uses the\n"
                        "robust median of every line in that file.\n",calPath);
        }
    }
    {
        float fin[3];
        if (pos_now(&c,fin)) {
            fprintf(log,"\nfinal position (%.2f, %.2f) remaining %.2f yd, legs=%d converged=%d\n",
                fin[0],fin[1],am_len(gx-fin[0],gy-fin[1]),
                legs-(converged?1:0),converged);
        }
    }
    fprintf(log,"\nmodel at exit: headingAtZero=%.1f degPerPx=%.5f degPerDragPx=%.5f degPerKeyMs=%.5f\n",
        P.headingAtZero,P.degPerPx,P.degPerDragPx,P.degPerKeyMs);
    fprintf(log,"pass-by cancellations: %d of %d legs; legs that never stopped: %d\n",
        passbys, legs-1, neverStopped);
    fprintf(log,"drift vs calibration: click %+.0f%%, drag %+.0f%%; discontinuous legs skipped %d\n",
        P.degPerPxRef!=0.0 ? 100.0*(P.degPerPx-P.degPerPxRef)/P.degPerPxRef : 0.0,
        P.degPerDragPxRef!=0.0 ? 100.0*(P.degPerDragPx-P.degPerDragPxRef)/P.degPerDragPxRef : 0.0,
        discontinuities);
    rc=0;
done:
    if (c.process) CloseHandle(c.process);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    puts("No client memory was written; input was synthesised at the window/OS level.");
    return rc;
}

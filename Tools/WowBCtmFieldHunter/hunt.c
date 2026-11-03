// WowBCtmFieldHunter v2 - read-only search for the click-to-move point.
//
// v1 looked for a value that ends up where the character stopped. It found
// nothing in 128 KiB, and there is a good reason to expect that: on the modern
// classic client the right-click handler (CameraOrSelectOrMove) is believed to
// derive the move point from the cursor ray EVERY frame rather than storing one
// destination. So v2 tests both shapes at once:
//
//   A) a stored destination  -> constant during the walk, ahead of the player,
//                               getting closer, and equal to the stopping point;
//   B) a cursor-derived point -> follows the mouse while the player stands still
//                               (calibration phase), so it can be identified
//                               even though it never freezes.
//
// Phases: baseline (hands off) -> cursor sweep (move mouse, no click) -> one
// right-click -> during walk -> after walk. Reads memory only: no writes, no
// injection, no protection changes, no remote threads.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include <math.h>
#include "player_lookup.h"
#include "huntmath.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define PAGE 4096u
#define SNAP_COUNT 5
#define SNAP_BASE 0
#define SNAP_CURSOR 1
#define SNAP_WALK1 2
#define SNAP_WALK2 3
#define SNAP_END 4
#define MAX_SPAN (64u*1024u*1024u)
#define SCAN_CHUNK (8u*1024u*1024u)
#define MAX_CANDS 400000u
#define MAX_FOLLOWERS 4096u

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
static uint64_t module_base(DWORD pid, const wchar_t *name, DWORD *size) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); uint64_t found=0;
    if (Module32FirstW(snap,&e)) do {
        if (!name || _wcsicmp(e.szModule,name)==0) { found=ui(e.modBaseAddr); if (size) *size=e.modBaseSize; break; }
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return found;
}
static int player_read(void *context, uint64_t address, void *output, size_t size) {
    return read_exact((HANDLE)context,address,output,size);
}
static int valid_identity(HANDLE process, uint64_t imageBase) {
    unsigned char dos[64], pe[6], sample[16];
    static const unsigned char expected[16]={0x48,0x8b,0xc4,0x55,0x41,0x57,0x48,0x8d,0x68,0xa1,0x48,0x81,0xec,0xb8,0,0};
    if (!read_exact(process,imageBase,dos,sizeof(dos)) || dos[0]!='M' || dos[1]!='Z') return 0;
    uint32_t peoff=le32(dos+0x3c); if (peoff<64 || peoff>0x100000) return 0;
    if (!read_exact(process,imageBase+peoff,pe,6) || memcmp(pe,"PE\0\0",4) || pe[4]!=0x64 || pe[5]!=0x86) return 0;
    return read_exact(process,imageBase+HEALTH_RVA,sample,16) && memcmp(sample,expected,16)==0;
}

// ---------------------------------------------------------------- snapshots
static void read_span(HANDLE process, uint64_t base, unsigned span,
                      unsigned char *buf, unsigned char *valid) {
    for (unsigned off=0; off<span; off+=PAGE) {
        unsigned chunk = (span-off < PAGE) ? span-off : PAGE;
        SIZE_T got=0;
        int ok = ReadProcessMemory(process,(void*)(uintptr_t)(base+off),buf+off,chunk,&got) && got==chunk;
        memset(valid+off, ok ? 1 : 0, chunk);
        if (!ok) memset(buf+off,0,chunk);
    }
}
static int slot_ok(const unsigned char *valid, unsigned off) {
    return valid[off] && valid[off+1] && valid[off+2] && valid[off+3];
}

// --------------------------------------------------------------- candidates
typedef struct { uint64_t addr; float v[3]; } cand_t;
typedef struct { uint64_t addr; float base[3]; float cursor[3]; unsigned runFloats; } follower_t;

typedef struct {
    HANDLE process;
    player_anchor player;
    uint64_t spanBase;
    unsigned span;
    cand_t *cands; unsigned ncands;
    follower_t *followers; unsigned nfollowers;
    uint64_t bytesScanned;
    int truncated;
} hunt_ctx;

static void add_cand(hunt_ctx *c, uint64_t addr, const float *v) {
    if (c->ncands>=MAX_CANDS) { c->truncated=1; return; }
    cand_t *x=&c->cands[c->ncands++];
    x->addr=addr; x->v[0]=v[0]; x->v[1]=v[1]; x->v[2]=v[2];
}
// Collects every plausible triple whose horizontal distance to the player is
// inside the click range. Geometry is refined later, once the walk direction is
// known from two player positions.
static void scan_buffer(hunt_ctx *c, const unsigned char *buf, unsigned span,
                        uint64_t base, const float *playerPos) {
    for (unsigned off=0; off+12<=span; off+=4) {
        const float *v=(const float*)(buf+off);
        if (!fh_plausible(v)) continue;
        double d=fh_dist2(v,playerPos);
        if (d<4.0 || d>200.0) continue;
        add_cand(c,base+off,v);
    }
}
static void scan_region(hunt_ctx *c, const unsigned char *buf, unsigned size,
                        uint64_t base, const float *playerPos) {
    for (unsigned off=0; off+12<=size; off+=4) {
        const float *v=(const float*)(buf+off);
        if (!fh_plausible(v)) continue;
        double d=fh_dist2(v,playerPos);
        if (d<4.0 || d>200.0) continue;
        add_cand(c,base+off,v);
    }
}

// ------------------------------------------------------- process-wide scan
static void scan_process(hunt_ctx *c, const float *playerPos, unsigned budgetMs,
                         unsigned char *buf, unsigned chunkCap) {
    uint64_t start=GetTickCount64();
    uint64_t addr=0x10000; MEMORY_BASIC_INFORMATION mbi;
    while (addr<MAX_USER) {
        if (VirtualQueryEx(c->process,(void*)(uintptr_t)addr,&mbi,sizeof(mbi))!=sizeof(mbi)) break;
        uint64_t base=ui(mbi.BaseAddress);
        uint64_t size=mbi.RegionSize;
        if (!size) break;
        uint64_t next=base+size;
        int usable = mbi.State==MEM_COMMIT && !(mbi.Protect & (PAGE_GUARD|PAGE_NOACCESS)) &&
            ((mbi.Protect & 0xFF)==PAGE_READONLY || (mbi.Protect & 0xFF)==PAGE_READWRITE ||
             (mbi.Protect & 0xFF)==PAGE_WRITECOPY || (mbi.Protect & 0xFF)==PAGE_EXECUTE_READ ||
             (mbi.Protect & 0xFF)==PAGE_EXECUTE_READWRITE);
        if (usable) {
            for (uint64_t off=0; off<size; off+=chunkCap) {
                unsigned want=(unsigned)((size-off<chunkCap)?(size-off):chunkCap);
                SIZE_T got=0;
                if (ReadProcessMemory(c->process,(void*)(uintptr_t)(base+off),buf,want,&got) && got>=16) {
                    scan_region(c,buf,(unsigned)got,base+off,playerPos);
                    c->bytesScanned+=got;
                }
                if (GetTickCount64()-start>budgetMs) { c->truncated=1; return; }
            }
        }
        if (next<=addr) break;
        addr=next;
    }
}

// ------------------------------------------------------------------ helpers
static float g_end[3];
static int cmp_end(const void *a, const void *b) {
    double da=fh_dist2(((const cand_t*)a)->v,g_end), db=fh_dist2(((const cand_t*)b)->v,g_end);
    return (da<db)? -1 : (da>db)? 1 : 0;
}
static int right_held(int *prev, DWORD pid) {
    DWORD fgPid=0; HWND fg=GetForegroundWindow();
    if (fg) GetWindowThreadProcessId(fg,&fgPid);
    if (fgPid!=pid) return 0;
    int down=(GetAsyncKeyState(VK_RBUTTON)&0x8000)!=0;
    int was=*prev; *prev=down;
    return down && !was;
}
// Waits for a fresh right-button press, or expires.
static int wait_for_click(DWORD pid, unsigned ms, uint64_t begin, FILE *log, const char *phase) {
    int prev=0; uint64_t until=GetTickCount64()+ms;
    while (GetTickCount64()<until) {
        if (right_held(&prev,pid)) {
            fprintf(log,"RIGHT_DOWN t=%llums (%s)\n",
                (unsigned long long)(GetTickCount64()-begin),phase);
            return 1;
        }
        Sleep(10);
    }
    return 0;
}

int main(int argc, char **argv) {
    DWORD pid=0; unsigned span=0x800000;
    unsigned beforeMs=2500, cursorMs=2500, walkMs=10000, budgetMs=6000;
    int full=0;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--span") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,16);
            if (v>=0x1000 && v<=MAX_SPAN) span=(unsigned)v; continue;
        }
        if (!strcmp(argv[i],"--before") && i+1<argc) { beforeMs=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--cursor") && i+1<argc) { cursorMs=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--walk") && i+1<argc) { walkMs=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--budget") && i+1<argc) { budgetMs=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--full")) { full=1; continue; }
        puts("Usage: WowBCtmFieldHunter.exe [--pid PID] [--span hex] [--full]");
        puts("       [--before ms] [--cursor ms] [--walk ms] [--budget ms]");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBCtmFieldHunter-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB READ-ONLY CLICK-TO-MOVE HUNTER v2\n");
    fprintf(log,"Phases: baseline (hands off) -> cursor sweep (move mouse, no click) -> one\n");
    fprintf(log,"right-click -> during walk -> after walk. A stored destination must stay\n");
    fprintf(log,"constant during the walk, sit ahead of the player and get closer; a cursor-driven\n");
    fprintf(log,"point is found instead by the cursor sweep, where it follows the mouse while the\n");
    fprintf(log,"player stands still. Reads memory only: no writes, no injection, no threads.\n\n");
    fflush(log);

    int result=1;
    HANDLE process=NULL;
    unsigned char *snap[SNAP_COUNT]={0}; unsigned char *valid=NULL;
    hunt_ctx ctx; memset(&ctx,0,sizeof(ctx));
    unsigned char *scanBuf=NULL;
    player_anchor player; memset(&player,0,sizeof(player));
    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; pass --pid PID.\n"); goto done; }
    process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); goto done; }
    if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"Not a live process.\n"); goto done; }
    FILETIME create,exitT,kernel,user;
    if (!GetProcessTimes(process,&create,&exitT,&kernel,&user)) { fprintf(log,"GetProcessTimes failed\n"); goto done; }
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,L"wowb.exe",&imageSize);
    if (!imageBase || !valid_identity(process,imageBase)) {
        fprintf(log,"AMD64 PE or identity sample mismatch; refusing.\n"); goto done;
    }
    if (!player_find(player_read,process,imageBase,&player)) { fprintf(log,"Player record unavailable.\n"); goto done; }

    ctx.process=process; ctx.player=player;
    ctx.spanBase = (player.record>span/2) ? (player.record-span/2) : player.record;
    ctx.span=span;
    ctx.cands=(cand_t*)malloc(sizeof(cand_t)*MAX_CANDS);
    ctx.followers=(follower_t*)malloc(sizeof(follower_t)*MAX_FOLLOWERS);
    scanBuf=(unsigned char*)malloc(SCAN_CHUNK);
    for (int i=0;i<SNAP_COUNT;i++) snap[i]=(unsigned char*)malloc(span);
    valid=(unsigned char*)malloc(span);
    if (!ctx.cands||!ctx.followers||!scanBuf||!valid) { fprintf(log,"host allocation failed\n"); goto done; }
    for (int i=0;i<SNAP_COUNT;i++) if (!snap[i]) { fprintf(log,"host allocation failed\n"); goto done; }

    float pStart[3],pCursor[3],pWalk1[3],pWalk2[3],pEnd[3];
    fprintf(log,"pid=%lu creation=0x%llX imageBase=0x%llX\n",(unsigned long)pid,
        (unsigned long long)filetime64(create),(unsigned long long)imageBase);
    fprintf(log,"record=0x%llX spanBase=0x%llX span=0x%X mode=%s\n",
        (unsigned long long)player.record,(unsigned long long)ctx.spanBase,span,
        full?"full-process (window for cursor phase)":"window");
    fflush(log);

    uint64_t begin=GetTickCount64();
    // ---- phase 0: baseline
    printf("PHASE 1/4  BASELINE %u ms - hands off, do not touch the mouse.\n",beforeMs);
    fflush(stdout);
    Sleep(beforeMs);
    if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"client exited\n"); goto done; }
    read_span(process,ctx.spanBase,span,snap[SNAP_BASE],valid);
    if (!read_exact(process,player.record+0x60,pStart,12)) { fprintf(log,"position unreadable\n"); goto done; }

    // ---- phase 1: cursor sweep, no clicking
    printf("PHASE 2/4  CURSOR SWEEP %u ms - move the mouse slowly over the ground, NO clicking.\n",cursorMs);
    fflush(stdout);
    { int prev=0; uint64_t until=GetTickCount64()+cursorMs;
      while (GetTickCount64()<until) { right_held(&prev,pid); Sleep(20); } }
    read_span(process,ctx.spanBase,span,snap[SNAP_CURSOR],valid);
    if (!read_exact(process,player.record+0x60,pCursor,12)) { fprintf(log,"position unreadable\n"); goto done; }
    int playerIdleDuringCursor = fh_dist3(pStart,pCursor) < 0.5;
    // Cursor followers: plausible triples that changed while the player stood still.
    for (unsigned off=0; off+12<=span; off+=4) {
        if (!slot_ok(valid,off)) continue;
        if (memcmp(snap[SNAP_BASE]+off,snap[SNAP_CURSOR]+off,4)==0) continue;
        // Only the START of a changed run of floats counts. Without this every
        // shifted window of one matrix row was reported as its own "follower".
        if (off>=4 && memcmp(snap[SNAP_BASE]+off-4,snap[SNAP_CURSOR]+off-4,4)!=0) continue;
        if (memcmp(snap[SNAP_BASE]+off,snap[SNAP_CURSOR]+off,12)==0) continue;
        const float *v=(const float*)(snap[SNAP_CURSOR]+off);
        if (!fh_plausible(v)) continue;
        unsigned run=0;
        while (off+4*run+4<=span && memcmp(snap[SNAP_BASE]+off+4*run,snap[SNAP_CURSOR]+off+4*run,4)!=0) run++;
        // A single changed float next to unchanged data is noise, not a coordinate.
        if (run<2) continue;
        if (fh_dist2(v,pCursor)<3.0) continue;
        if (ctx.nfollowers>=MAX_FOLLOWERS) break;
        follower_t *f=&ctx.followers[ctx.nfollowers++];
        f->addr=ctx.spanBase+off;
        f->runFloats=run;
        memcpy(f->base,snap[SNAP_BASE]+off,12);
        memcpy(f->cursor,v,12);
    }
    fprintf(log,"position baseline=(%.3f, %.3f, %.3f) cursor=(%.3f, %.3f, %.3f) playerIdle=%d\n",
        pStart[0],pStart[1],pStart[2],pCursor[0],pCursor[1],pCursor[2],playerIdleDuringCursor);
    fprintf(log,"cursorFollowers=%u (run starts of plausible triples that changed while idle)\n",
        ctx.nfollowers);
    fflush(log);

    // ---- phase 2: one right-click, then scan during the walk
    printf("PHASE 3/4  NOW right-click ONE distant ground spot (50-100 yd), then hands off.\n");
    fflush(stdout);
    int clicked=wait_for_click(pid,120000,begin,log,"click phase");
    if (!clicked) { fprintf(log,"No right-click seen; nothing to correlate.\n"); goto done; }
    Sleep(200);
    if (!read_exact(process,player.record+0x60,pWalk1,12)) { fprintf(log,"position unreadable\n"); goto done; }
    if (full) {
        scan_process(&ctx,pWalk1,budgetMs,scanBuf,SCAN_CHUNK);
        /* also keep the window snapshot so cursor followers can be re-tested */
        read_span(process,ctx.spanBase,span,snap[SNAP_WALK1],valid);
    } else {
        read_span(process,ctx.spanBase,span,snap[SNAP_WALK1],valid);
        scan_buffer(&ctx,snap[SNAP_WALK1],span,ctx.spanBase,pWalk1);
    }
    fprintf(log,"scan1 candidates=%u bytesScanned=%llu truncated=%d\n",ctx.ncands,
        (unsigned long long)ctx.bytesScanned,ctx.truncated);
    fflush(log);

    // ---- phase 3: second sample during the walk
    printf("PHASE 3/4  walking - hands off.\n");
    fflush(stdout);
    Sleep(700);
    if (!read_exact(process,player.record+0x60,pWalk2,12)) { fprintf(log,"position unreadable\n"); goto done; }
    read_span(process,ctx.spanBase,span,snap[SNAP_WALK2],valid);
    double step=fh_dist3(pWalk1,pWalk2);
    fprintf(log,"position walk1=(%.3f, %.3f, %.3f) walk2=(%.3f, %.3f, %.3f) movedBetween=%.2fyd\n",
        pWalk1[0],pWalk1[1],pWalk1[2],pWalk2[0],pWalk2[1],pWalk2[2],step);

    // Re-read candidates and keep the ones that behave like a destination.
    cand_t *keep=(cand_t*)malloc(sizeof(cand_t)*(ctx.ncands?ctx.ncands:1));
    unsigned nkeep=0;
    unsigned tested=0;
    double walkCosFloor = (step>0.5) ? 0.90 : -1.0;
    for (unsigned i=0;i<ctx.ncands;i++) {
        cand_t *c=&ctx.cands[i];
        float now[3];
        if (!read_exact(process,c->addr,now,12)) continue;
        tested++;
        int stable = memcmp(now,c->v,12)==0;
        if (!fh_destination_like(c->v,pWalk1,pWalk2,stable,0.20)) continue;
        keep[nkeep++]=*c;
    }
    fprintf(log,"retestedCandidates=%u keep=%u (cosFloor=%.2f)\n",tested,nkeep,walkCosFloor);
    fflush(log);

    // ---- phase 4: end of walk
    printf("PHASE 4/4  waiting %u ms for the walk to finish - hands off.\n",walkMs);
    fflush(stdout);
    Sleep(walkMs);
    if (!read_exact(process,player.record+0x60,pEnd,12)) { fprintf(log,"position unreadable\n"); goto done; }
    read_span(process,ctx.spanBase,span,snap[SNAP_END],valid);
    double walked=fh_dist3(pWalk1,pEnd);
    fprintf(log,"position end=(%.3f, %.3f, %.3f) walked=%.2fyd\n",pEnd[0],pEnd[1],pEnd[2],walked);

    memcpy(g_end,pEnd,sizeof(g_end));
    if (nkeep) qsort(keep,nkeep,sizeof(cand_t),cmp_end);

    fprintf(log,"\n=== A) STORED-DESTINATION CANDIDATES (stable during walk, ahead, approaching) ===\n");
    if (!nkeep) {
        fprintf(log,"none.\n");
    } else {
        unsigned shown=0;
        for (unsigned i=0;i<nkeep && shown<40;i++) {
            cand_t *c=&keep[i];
            float endv[3];
            int stillSame = read_exact(process,c->addr,endv,12) && memcmp(endv,c->v,12)==0;
            double dEnd=fh_dist2(c->v,pEnd);
            unsigned off=(unsigned)(c->addr-ctx.spanBase);
            int isFollower=0;
            for (unsigned k=0;k<ctx.nfollowers;k++) if (ctx.followers[k].addr==c->addr) { isFollower=1; break; }
            fprintf(log,"record%+lld (span+0x%X) = (%.3f, %.3f, %.3f) dWalk1=%.2f dEnd=%.2f held=%d cursorFollower=%d\n",
                (long long)((int64_t)c->addr-(int64_t)player.record),off,
                c->v[0],c->v[1],c->v[2],fh_dist2(c->v,pWalk1),dEnd,stillSame,isFollower);
            shown++;
        }
        fprintf(log,"A destination should have dEnd small if the character stopped on it, and held=1.\n");
    }

    fprintf(log,"\n=== B) CURSOR-SWEEP FOLLOWERS (where the cursor ray is stored) ===\n");
    if (!ctx.nfollowers) {
        fprintf(log,"none: nothing in the window changed while the mouse moved and the player stood still.\n");
    } else if (!playerIdleDuringCursor) {
        fprintf(log,"player moved during the sweep (%u followers), so this list mixes player and cursor data.\n",
            ctx.nfollowers);
    } else {
        unsigned shown=0, frozen=0;
        for (unsigned i=0;i<ctx.nfollowers && shown<25;i++) {
            follower_t *f=&ctx.followers[i];
            float now[3];
            int held = read_exact(process,f->addr,now,12) && memcmp(now,f->cursor,12)==0;
            if (held) frozen++;   // stopped tracking the cursor: possibly consumed as a destination
            unsigned off=(unsigned)(f->addr-ctx.spanBase);
            double norm=sqrt((double)f->cursor[0]*f->cursor[0]+(double)f->cursor[1]*f->cursor[1]+
                             (double)f->cursor[2]*f->cursor[2]);
            fprintf(log,"record%+lld (span+0x%X) run=%u floats norm=%.3f base=(%.3f, %.3f, %.3f) afterSweep=(%.3f, %.3f, %.3f) frozen=%d\n",
                (long long)((int64_t)f->addr-(int64_t)player.record),off,f->runFloats,norm,
                f->base[0],f->base[1],f->base[2],f->cursor[0],f->cursor[1],f->cursor[2],held);
            shown++;
        }
        fprintf(log,"frozenAfterSweep=%u of %u followers\n",frozen,ctx.nfollowers);
        fprintf(log,"A terrain pick under the cursor appears here; for a bot it is the useful value,\n");
        fprintf(log,"because projecting a world point to the screen and reading it back closes the loop.\n");
        fprintf(log,"frozen=1 means the value stopped following the cursor after the click - the best\n");
        fprintf(log,"single indicator that it was adopted as the move point.\n");
    }

    if (!nkeep && !ctx.nfollowers) {
        fprintf(log,"\nNeither shape was found in this window.\n");
        if (!full) fprintf(log,"Re-run with --full to scan the whole process instead of %u bytes.\n",span);
    }
    result=0;
done:
    free(scanBuf); free(valid); free(ctx.cands); free(ctx.followers);
    for (int i=0;i<SNAP_COUNT;i++) free(snap[i]);
    if (process) CloseHandle(process);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    puts("Read-only: no client changes, no restart needed.");
    return result;
}

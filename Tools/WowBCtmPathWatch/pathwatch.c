// WowBCtmPathWatch - read-only staged probe for click-to-move state.
//
// The field diff found four position-like float triples inside the player
// record (0x60, 0x150, 0x6A0, 0x7D0, each mirrored 0x44 bytes later) sitting
// 50-96 yards apart. To command movement from the engine we must know which is
// the live position and which, if any, is the click destination.
//
// This tool runs two stages:
//   1. BASELINE - you do nothing. Records how much each triple drifts anyway.
//   2. CLICK    - you right-click ONE distant ground spot and then keep your
//                 hands off. The player triple will move smoothly toward it; a
//                 destination triple jumps at the click and then holds still.
//
// It only reads memory: no writes, no injection, no protection changes, no
// remote threads, no debugger. The client is not modified.
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
#include "pathmath.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define MAX_WATCH 8
#define MIRROR_DELTA 0x44u
#define LOG_CAP 400

typedef struct {
    uint32_t offset;
    int usable;
    float first[3], last[3], prev[3];
    unsigned samples, changes, changesA, changesB;
    uint64_t firstTick;
    float pathB;            // distance walked during the click stage
    float maxJump;          // largest single-sample move, yards
    uint64_t maxJumpTick;
    float jumpFrom[3], jumpTo[3];
    uint64_t lastClickTick; // most recent right-button edge before a change
} watcher;

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
int main(int argc, char **argv) {
    DWORD pid=0; unsigned baselineMs=4000, clickMs=8000, intervalMs=20;
    unsigned offsets[MAX_WATCH]={0x60,0x150,0x6A0,0x7D0}; unsigned offsetCount=4;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--baseline") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,10);
            if (v>=1000 && v<=15000) baselineMs=(unsigned)v; continue;
        }
        if (!strcmp(argv[i],"--click") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,10);
            if (v>=2000 && v<=30000) clickMs=(unsigned)v; continue;
        }
        if (!strcmp(argv[i],"--offsets") && i+1<argc) {
            offsetCount=0;
            char *list=argv[++i];
            for (char *tok=strtok(list,","); tok && offsetCount<MAX_WATCH; tok=strtok(NULL,",")) {
                unsigned long v=strtoul(tok,NULL,16);
                if (v<0x800) offsets[offsetCount++]=(unsigned)v;
            }
            if (!offsetCount) { puts("No usable offsets."); return 2; }
            continue;
        }
        puts("Usage: WowBCtmPathWatch.exe [--pid PID] [--baseline ms] [--click ms] [--offsets hex,...]");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBCtmPathWatch-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB READ-ONLY CLICK-TO-MOVE PATH WATCH\n");
    fprintf(log,"Reads memory only: no writes, no injection, no protection changes, no remote threads.\n");
    fprintf(log,"Stages: BASELINE (hands off) then CLICK (one right-click, then hands off).\n");
    fprintf(log,"Right-button lines are sampled physical input, not proof the game accepted the click.\n");
    fflush(log);

    int result=1; HANDLE process=NULL;
    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; pass --pid PID.\n"); goto done; }
    process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); goto done; }
    FILETIME create,exitT,kernel,user; wchar_t image[MAX_PATH]; DWORD imageLen=MAX_PATH;
    if (!GetProcessTimes(process,&create,&exitT,&kernel,&user) || !QueryFullProcessImageNameW(process,0,image,&imageLen)) {
        fprintf(log,"Process identity query failed=%lu\n",(unsigned long)GetLastError()); goto done;
    }
    wchar_t *base=wcsrchr(image,L'\\'); base=base ? base+1 : image;
    if (_wcsicmp(base,L"wowb.exe") || WaitForSingleObject(process,0)!=WAIT_TIMEOUT) {
        fprintf(log,"Not a live wowb.exe; refusing.\n"); goto done;
    }
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,L"wowb.exe",&imageSize);
    if (!imageBase || imageSize<=PLAYER_GUID_RVA+16 || !valid_identity(process,imageBase)) {
        fprintf(log,"AMD64 PE or identity sample mismatch/unreadable; refusing.\n"); goto done;
    }
    player_anchor player;
    if (!player_find(player_read,process,imageBase,&player)) {
        fprintf(log,"Unique checked local-player record unavailable; nothing sampled.\n"); goto done;
    }
    fprintf(log,"pid=%lu creation=0x%llX imageBase=0x%llX\n",(unsigned long)pid,
        (unsigned long long)filetime64(create),(unsigned long long)imageBase);
    fprintf(log,"playerAnchor family=%u slot=%u record=0x%llX\n",player.family,player.index,
        (unsigned long long)player.record);
    fprintf(log,"baseline=%ums clickStage=%ums interval=%ums offsets=%u\n",baselineMs,clickMs,intervalMs,offsetCount);
    fflush(log);

    watcher w[MAX_WATCH]; memset(w,0,sizeof(w));
    unsigned live=0;
    for (unsigned i=0;i<offsetCount;i++) {
        w[i].offset=offsets[i];
        if (read_exact(process,player.record+offsets[i],w[i].first,12) && pm_plausible(w[i].first)) {
            memcpy(w[i].last,w[i].first,12); memcpy(w[i].prev,w[i].first,12);
            w[i].usable=1; live++;
            float mirror[3];
            int hasMirror = read_exact(process,player.record+offsets[i]+MIRROR_DELTA,mirror,12) &&
                            memcmp(mirror,w[i].first,12)==0;
            unsigned char raw[12]; memcpy(raw,w[i].first,12);
            fprintf(log,"watch +0x%03X = %08lX %08lX %08lX  float=(%.3f, %.3f, %.3f) mirrorAt+0x%03X=%s\n",
                offsets[i],(unsigned long)le32(raw),(unsigned long)le32(raw+4),(unsigned long)le32(raw+8),
                w[i].first[0],w[i].first[1],w[i].first[2],offsets[i]+MIRROR_DELTA,hasMirror ? "identical" : "differs");
        } else {
            fprintf(log,"watch +0x%03X unreadable or implausible; ignored\n",offsets[i]);
        }
    }
    if (!live) { fprintf(log,"No usable offsets.\n"); goto done; }
    fflush(log);
    printf("BASELINE %u ms: keep your hands off the mouse and keyboard now.\n",baselineMs); fflush(stdout);

    uint64_t begin=GetTickCount64();
    uint64_t phaseBTick=begin+baselineMs, endTick=phaseBTick+clickMs;
    int rightDown=0, announced=0; unsigned logged=0;
    uint64_t lastClickTick=0;
    for (;;) {
        uint64_t now=GetTickCount64();
        if (now>=endTick) break;
        if (!announced && now>=phaseBTick) {
            announced=1;
            puts("CLICK STAGE: right-click ONE distant ground spot now (~40-80 yards), then keep hands off."); fflush(stdout);
            fprintf(log,"PHASE_BEGIN t=%llums\n",(unsigned long long)(now-begin)); fflush(log);
        }
        if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"client exited during observation\n"); break; }
        DWORD fgPid=0; HWND fg=GetForegroundWindow();
        if (fg) GetWindowThreadProcessId(fg,&fgPid);
        if (fgPid==pid) {
            int down=(GetAsyncKeyState(VK_RBUTTON)&0x8000)!=0;
            if (down!=rightDown) {
                fprintf(log,"RIGHT_%s t=%llums stage=%s\n",down ? "DOWN" : "UP",
                    (unsigned long long)(now-begin), now<phaseBTick ? "baseline" : "click");
                rightDown=down;
                if (down) lastClickTick=now;
            }
        } else if (rightDown) {
            fprintf(log,"RIGHT_TRACKING_LOST_FOCUS t=%llums\n",(unsigned long long)(now-begin));
            rightDown=0;
        }
        int inB = now>=phaseBTick;
        for (unsigned i=0;i<offsetCount;i++) {
            if (!w[i].usable) continue;
            float cur[3];
            if (!read_exact(process,player.record+w[i].offset,cur,12)) continue;
            w[i].samples++;
            if (memcmp(cur,w[i].prev,12)==0) continue;
            float step=pm_dist3(cur,w[i].prev);
            w[i].changes++;
            if (w[i].firstTick==0) w[i].firstTick=now-begin;
            if (inB) { w[i].changesB++; w[i].pathB+=step; } else w[i].changesA++;
            if (step>=w[i].maxJump) {
                w[i].maxJump=step; w[i].maxJumpTick=now-begin;
                memcpy(w[i].jumpFrom,w[i].prev,12); memcpy(w[i].jumpTo,cur,12);
            }
            if (step>=0.05f && logged<LOG_CAP) {
                fprintf(log,"CH t=%llums stage=%s +0x%03X step=%.3fyd (%.3f,%.3f,%.3f) lastClick=%lldms\n",
                    (unsigned long long)(now-begin), inB ? "click" : "base", w[i].offset, step,
                    cur[0],cur[1],cur[2], lastClickTick ? (long long)(now-lastClickTick) : -1);
                logged++;
            }
            memcpy(w[i].prev,cur,12); memcpy(w[i].last,cur,12);
        }
        Sleep(intervalMs);
    }
    fprintf(log,"OBSERVATION_ENDED t=%llums loggedChangeLines=%u\n",
        (unsigned long long)(GetTickCount64()-begin),logged);
    fprintf(log,"loggedChangeLines are capped at %u; counters below are complete.\n",LOG_CAP);

    fprintf(log,"\nSUMMARY (baseline %ums then click stage %ums)\n",baselineMs,clickMs);
    for (unsigned i=0;i<offsetCount;i++) {
        if (!w[i].usable) continue;
        fprintf(log,"+0x%03X samples=%u changes=%u (baseline=%u click=%u) firstChange=%llums pathInClickStage=%.2fyd maxStep=%.3fyd at %llums\n",
            w[i].offset,w[i].samples,w[i].changes,w[i].changesA,w[i].changesB,
            (unsigned long long)w[i].firstTick,(double)w[i].pathB,(double)w[i].maxJump,
            (unsigned long long)w[i].maxJumpTick);
        if (w[i].maxJump>0.0f)
            fprintf(log,"      biggest step went (%.3f,%.3f,%.3f) -> (%.3f,%.3f,%.3f)\n",
                w[i].jumpFrom[0],w[i].jumpFrom[1],w[i].jumpFrom[2],
                w[i].jumpTo[0],w[i].jumpTo[1],w[i].jumpTo[2]);
        fprintf(log,"      start=(%.3f,%.3f,%.3f) end=(%.3f,%.3f,%.3f) net=%.2fyd\n",
            w[i].first[0],w[i].first[1],w[i].first[2],
            w[i].last[0],w[i].last[1],w[i].last[2],(double)pm_dist3(w[i].first,w[i].last));
    }

    fprintf(log,"\nVERDICTS\n");
    for (unsigned i=0;i<offsetCount;i++) {
        if (!w[i].usable) continue;
        float net=pm_dist3(w[i].first,w[i].last);
        const char *what="unclear";
        if (w[i].maxJump>=3.0f && w[i].changesB>0 && net<1.0f)
            what="DESTINATION candidate (one large jump, then holds)";
        else if (w[i].pathB>=0.5f && w[i].maxJump<2.0f && w[i].changesB>=5)
            what="POSITION candidate (many small steps during the click stage)";
        else if (w[i].changes==0)
            what="static (nothing changed at all)";
        fprintf(log,"+0x%03X %s | net=%.2fyd pathInClick=%.2fyd changes=%u maxStep=%.3fyd\n",
            w[i].offset,what,(double)net,(double)w[i].pathB,w[i].changes,(double)w[i].maxJump);
    }
    unsigned moving=0;
    for (unsigned i=0;i<offsetCount;i++) if (w[i].usable && w[i].pathB>=0.5f) moving++;
    if (!moving) {
        fprintf(log,"\nNo watched triple moved at all. If you did click, the client did not act on it;\n");
        fprintf(log,"that is an input/delivery problem, not an offset problem, and it must be settled first.\n");
    }
    fprintf(log,"\nNext step: the POSITION candidate is the value to command; the DESTINATION candidate is\n");
    fprintf(log,"the field a native move call would populate. Neither claim is proven by this file alone.\n");
    result=0;
done:
    if (process) CloseHandle(process);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    puts("No client changes were made; no restart needed for this tool.");
    return result;
}

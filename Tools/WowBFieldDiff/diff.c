// Read-only field-change sampler for the user's local wowb.exe 70291 client.
//
// Why this exists: the injected PAGE_GUARD probe (v7) ran cleanly but saw one
// read and zero writes to the page holding the player record in ten seconds of
// clicking. So the anchor we watch is not demonstrably the live player state.
// This tool writes nothing, injects nothing, allocates nothing in the client and
// changes no protection: it only reads a window of memory at intervals and
// reports which words actually change while the character walks.
//
// Output: C:\ghidra-scripts\WowBFieldDiff-<timestamp>.txt
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include "player_lookup.h"
#include "field_diff.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define DEFAULT_WINDOW 0x800u
#define DEFAULT_SECONDS 10u
#define POLL_MS 25u
#define EVENT_CAP 600

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
static void describe(FILE *log, unsigned offset, uint32_t value, int first) {
    float f; memcpy(&f,&value,4);
    int32_t i=(int32_t)value;
    const char *tag="";
    if (offset>=0x60 && offset<=0x68) tag=" (record position component)";
    else if (offset>=0x1E0 && offset<=0x1F0) tag=" (near the actor-pointer region)";
    char fs[32]; snprintf(fs,sizeof(fs),"%.6g",(double)f);
    fprintf(log,"   %s +0x%03X = 0x%08lX  float=%s  int=%ld%s\n",
        first ? "first" : "last ", offset,(unsigned long)value,fs,(long)i,tag);
}
static int window_watch(FILE *log, HANDLE process, const char *label, uint64_t base,
                        unsigned size, unsigned seconds, int rightClickReporting,
                        DWORD pid, uint64_t *pollFailures) {
    unsigned char *first=(unsigned char*)malloc(size), *latest=(unsigned char*)malloc(size);
    fd_state *state=(fd_state*)malloc(sizeof(fd_state));
    if (!first || !latest || !state) { free(first); free(latest); free(state); return 0; }
    if (!read_exact(process,base,first,size)) {
        fprintf(log,"%s at 0x%llX unreadable; skipped\n",label,(unsigned long long)base);
        free(first); free(latest); free(state); return 0;
    }
    fd_init(state,first,size);
    uint64_t begin=GetTickCount64(), deadline=begin+(uint64_t)seconds*1000;
    unsigned events=0; int rightDown=0; int truncated=0;
    for (;;) {
        uint64_t now=GetTickCount64();
        if (now>=deadline) break;
        if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"client exited during observation\n"); break; }
        if (rightClickReporting) {
            DWORD fgPid=0; HWND fg=GetForegroundWindow();
            if (fg) GetWindowThreadProcessId(fg,&fgPid);
            if (fgPid==pid) {
                int down=(GetAsyncKeyState(VK_RBUTTON)&0x8000)!=0;
                if (down!=rightDown) {
                    fprintf(log,"RIGHT_%s t=%llums\n",down ? "DOWN" : "UP",(unsigned long long)(now-begin));
                    rightDown=down;
                }
            }
        }
        if (!read_exact(process,base,latest,size)) { (*pollFailures)++; Sleep(POLL_MS); continue; }
        unsigned changed=fd_compare(state,latest,(uint64_t)(now-begin));
        if (changed && events<EVENT_CAP) {
            fprintf(log,"POLL t=%llums changedWords=%u\n",(unsigned long long)(now-begin),changed);
            events++;
            unsigned printed=0;
            for (unsigned i=0;i<size/4 && i<FD_MAX_WORDS && printed<16;i++) {
                uint32_t cur=((const uint32_t*)latest)[i], prev=((const uint32_t*)first)[i];
                if (cur!=prev && fd_count(state,i*4)==1) {
                    fprintf(log,"   +0x%03X became 0x%08lX (was 0x%08lX)\n",i*4,
                        (unsigned long)cur,(unsigned long)prev);
                    printed++;
                }
            }
        } else if (changed) truncated=1;
        Sleep(POLL_MS);
    }
    fd_sort(state);
    fprintf(log,"%s summary: base=0x%llX window=0x%X polls=%llu changedWordsTotal=%llu distinctOffsets=%u dropped=%u%s\n",
        label,(unsigned long long)base,size,(unsigned long long)state->polls,
        (unsigned long long)state->changes,state->tracked,state->dropped,
        truncated ? " (event lines truncated)" : "");
    for (unsigned k=0;k<state->tracked && k<40;k++) {
        fd_entry *e=&state->entry[k];
        fprintf(log,"  offset +0x%03X changes=%lu firstAt=%llums\n",e->offset,
            (unsigned long)e->changes,(unsigned long long)e->first_tick);
        describe(log,e->offset,e->first_old,1);
        describe(log,e->offset,e->last_new,0);
    }
    if (!state->tracked) fprintf(log,"  no word in this window changed at all\n");
    if (fd_count(state,0x60)==0 && fd_count(state,0x64)==0 && fd_count(state,0x68)==0)
        fprintf(log,"  record+0x60/+0x64/+0x68 unchanged during this window\n");
    fflush(log);
    free(first); free(latest); free(state);
    return 1;
}

int main(int argc, char **argv) {
    DWORD pid=0; unsigned seconds=DEFAULT_SECONDS, size=DEFAULT_WINDOW;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--seconds") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,10);
            if (v>=1 && v<=60) seconds=(unsigned)v;
            continue;
        }
        if (!strcmp(argv[i],"--window") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,16);
            if (v>=0x100 && v<=FD_MAX_WORDS*4) size=(unsigned)v;
            continue;
        }
        puts("Usage: WowBFieldDiff.exe [--pid PID] [--seconds 1..60] [--window hexbytes]");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBFieldDiff-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB READ-ONLY FIELD-CHANGE SAMPLER\n");
    fprintf(log,"Reads memory only: no writes, no injection, no protection changes, no remote threads.\n");
    fprintf(log,"Finds which 4-byte words in the player record actually change while the character moves.\n");
    fprintf(log,"Right-button lines are sampled physical input, not proof the game accepted a click.\n");
    fprintf(log,"Exit code is 0 when a report was written, regardless of how many fields changed.\n");
    fflush(log);
    int result=1; HANDLE process=NULL;
    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; pass --pid PID if there are several.\n"); goto done; }
    fprintf(log,"pid=%lu seconds=%u window=0x%X pollInterval=%ums\n",(unsigned long)pid,seconds,size,POLL_MS);
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
    fprintf(log,"creation=0x%llX imageBase=0x%llX imageSize=0x%lX identitySample=match (partial only)\n",
        (unsigned long long)filetime64(create),(unsigned long long)imageBase,(unsigned long)imageSize);
    player_anchor player;
    if (!player_find(player_read,process,imageBase,&player)) {
        fprintf(log,"Unique checked local-player record unavailable; nothing sampled.\n"); goto done;
    }
    fprintf(log,"playerAnchor family=%u slot=%u record=0x%llX\n",player.family,player.index,
        (unsigned long long)player.record);
    fflush(log);
    puts("ACTIVE: move the character now for the whole window (right-click to walk, and hold W for a couple of seconds).");
    fflush(stdout);
    uint64_t failures=0;
    window_watch(log,process,"recordWindow",player.record,size,seconds,1,pid,&failures);
    uint64_t pollFailures=failures;
    // A second window one page later often holds the rest of the unit state and
    // costs nothing extra to sample.
    window_watch(log,process,"recordWindow+0x1000",player.record+0x1000,size,seconds,0,pid,&failures);
    fprintf(log,"readFailures=%llu\n",(unsigned long long)failures);
    fprintf(log,"Interpretation: an offset with many changes is live state. If the position words\n");
    fprintf(log,"(+0x60/+0x64/+0x68) never change while you are walking, that record is not the live\n");
    fprintf(log,"position and the next step is to find which window does change.\n");
    (void)pollFailures;
    result=0;
done:
    if (process) CloseHandle(process);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    puts("No client changes were made; a restart is not required for this tool.");
    return result;
}

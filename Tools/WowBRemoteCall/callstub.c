// WowBRemoteCall - executes one game function per request through the APC route.
//
// Context: the OS loader is unusable from injected code in this client
// (LoadLibraryW returns 87 for every path; LdrLoadDll returns
// STATUS_INVALID_THREAD), but APC-delivered stubs on alertable client threads do
// run, and manually mapped images work. This tool uses that capability to call a
// single function at a known image RVA with up to four integer arguments, and to
// read the player position before and after so the effect is measurable.
//
// Default mode only READS: it resolves the image, the player record, and dumps
// the code bytes at the addresses you name. Calling requires --go.
//
// Risk: calling an unrecovered function can crash the client. Addresses from
// research notes are registrations, not validated ABIs.
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
#include "call_stubs.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define MAX_THREADS 24
#define APC_WAIT_MS 12000
#define MOVE_FORWARD_START 0x23C8BE0ULL
#define MOVE_FORWARD_STOP  0x23C9000ULL

static uint64_t ui(const void *p) { return (uint64_t)(uintptr_t)p; }
static int read_exact(HANDLE p, uint64_t addr, void *out, SIZE_T n) {
    SIZE_T got=0;
    return n && addr>=0x10000 && addr<MAX_USER && n<=MAX_USER-addr &&
        ReadProcessMemory(p,(void*)(uintptr_t)addr,out,n,&got) && got==n;
}
static int write_exact(HANDLE p, uint64_t addr, const void *in, SIZE_T n) {
    SIZE_T got=0; return WriteProcessMemory(p,(void*)(uintptr_t)addr,in,n,&got) && got==n;
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
static unsigned pick_threads(DWORD pid, DWORD *out, unsigned capacity) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); unsigned n=0;
    if (Thread32First(snap,&e)) do {
        if (e.th32OwnerProcessID!=pid || n==capacity) continue;
        out[n++]=e.th32ThreadID;
    } while (Thread32Next(snap,&e));
    CloseHandle(snap); return n;
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
static float dist3(const float *a, const float *b) {
    double dx=(double)a[0]-b[0], dy=(double)a[1]-b[1], dz=(double)a[2]-b[2];
    return (float)sqrt(dx*dx+dy*dy+dz*dz);
}

typedef struct {
    HANDLE process; DWORD pid; FILE *log;
    uint64_t codePage, dataPage;
    DWORD tid;
    int haveThread;
} ctx_t;

static int queue_one(ctx_t *c, uint64_t code, uint64_t data) {
    HANDLE t=OpenThread(THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,c->tid);
    if (!t) { fprintf(c->log,"OpenThread(%lu) failed=%lu\n",(unsigned long)c->tid,(unsigned long)GetLastError()); return 0; }
    int ok=QueueUserAPC((PAPCFUNC)(uintptr_t)code,t,(ULONG_PTR)data)?1:0;
    if (!ok) fprintf(c->log,"QueueUserAPC failed=%lu\n",(unsigned long)GetLastError());
    CloseHandle(t);
    return ok;
}
// Runs one stub on the selected thread and waits for phase to leave 0.
static int run_stub(ctx_t *c, uint64_t code, call_data *want, call_data *out, DWORD waitMs) {
    if (!write_exact(c->process,c->dataPage,want,sizeof(*want))) return 0;
    call_data check;
    if (!read_exact(c->process,c->dataPage,&check,sizeof(check)) || check.fn!=want->fn) return 0;
    if (!queue_one(c,code,c->dataPage)) return 0;
    uint64_t begin=GetTickCount64();
    for (;;) {
        if (read_exact(c->process,c->dataPage,out,sizeof(*out)) && (out->phase==2 || out->phase==42)) return 1;
        if (GetTickCount64()-begin>=waitMs) return 0;
        Sleep(10);
    }
}

int main(int argc, char **argv) {
    DWORD pid=0; int go=0, moves=0; uint64_t callRva=0, readRva=0; unsigned readLen=32;
    uint64_t args[4]={0,0,0,0}; unsigned argCount=0;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--read") && i+1<argc) { readRva=strtoull(argv[++i],NULL,16); continue; }
        if (!strcmp(argv[i],"--len") && i+1<argc) {
            unsigned long v=strtoul(argv[++i],NULL,16);
            if (v>=16 && v<=256) readLen=(unsigned)v;
            continue;
        }
        if (!strcmp(argv[i],"--call") && i+1<argc) {
            callRva=strtoull(argv[++i],NULL,16);
            // optional :arg1,arg2,arg3,arg4
            char *colon=strchr(argv[i],':');
            if (colon) {
                char *list=colon+1;
                for (char *tok=strtok(list,","); tok && argCount<4; tok=strtok(NULL,","))
                    args[argCount++]=strtoull(tok,NULL,16);
            }
            continue;
        }
        if (!strcmp(argv[i],"--moves")) { moves=1; continue; }
        if (!strcmp(argv[i],"--go")) { go=1; continue; }
        puts("Usage: WowBRemoteCall.exe [--pid PID] [--read RVA [--len HEX]]");
        puts("       [--call RVA[:arg1,arg2,arg3,arg4]] [--moves] [--go]");
        puts("  Without --go the tool only reads memory and code bytes.");
        puts("  --moves calls MoveForwardStart then (after 1.5 s) MoveForwardStop and measures the");
        puts("  player position change; needs --go.");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBRemoteCall-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB REMOTE CALL HARNESS (APC route, manually placed stubs)\n");
    fprintf(log,"Default mode is read-only. --go allocates remote memory, queues APCs and calls game code;\n");
    fprintf(log,"an unvalidated address can crash the client. Restart the client after a --go run.\n");
    fflush(log);

    ctx_t c; memset(&c,0,sizeof(c)); c.log=log;
    int result=1; HANDLE process=NULL;
    player_anchor player;
    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; pass --pid PID.\n"); goto done; }
    process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_VM_OPERATION|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); goto done; }
    c.process=process; c.pid=pid;
    FILETIME create,exitT,kernel,user; wchar_t image[MAX_PATH]; DWORD imageLen=MAX_PATH;
    if (!GetProcessTimes(process,&create,&exitT,&kernel,&user) || !QueryFullProcessImageNameW(process,0,image,&imageLen)) {
        fprintf(log,"Process identity query failed=%lu\n",(unsigned long)GetLastError()); goto done;
    }
    wchar_t *baseName=wcsrchr(image,L'\\'); baseName=baseName ? baseName+1 : image;
    if (_wcsicmp(baseName,L"wowb.exe") || WaitForSingleObject(process,0)!=WAIT_TIMEOUT) {
        fprintf(log,"Not a live wowb.exe; refusing.\n"); goto done;
    }
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,L"wowb.exe",&imageSize);
    if (!imageBase || imageSize<=PLAYER_GUID_RVA+16 || !valid_identity(process,imageBase)) {
        fprintf(log,"AMD64 PE or identity sample mismatch/unreadable; refusing.\n"); goto done;
    }
    fprintf(log,"pid=%lu creation=0x%llX imageBase=0x%llX imageSize=0x%lX\n",(unsigned long)pid,
        (unsigned long long)filetime64(create),(unsigned long long)imageBase,(unsigned long)imageSize);
    if (!player_find(player_read,process,imageBase,&player)) {
        fprintf(log,"Player record unavailable; refusing.\n"); goto done;
    }
    fprintf(log,"record=0x%llX\n",(unsigned long long)player.record);
    float position[3]={0,0,0};
    int havePosition = read_exact(process,player.record+0x60,position,12);
    if (havePosition)
        fprintf(log,"position now = (%.3f, %.3f, %.3f)\n",position[0],position[1],position[2]);

    if (readRva) {
        uint64_t address=imageBase+readRva;
        unsigned char bytes[256];
        unsigned len=readLen;
        if (read_exact(process,address,bytes,len)) {
            fprintf(log,"bytes at +0x%llX (0x%llX):\n",(unsigned long long)readRva,(unsigned long long)address);
            for (unsigned i=0;i<len;i+=16) {
                fprintf(log,"  %04X:",i);
                for (unsigned k=0;k<16 && i+k<len;k++) fprintf(log," %02X",bytes[i+k]);
                fprintf(log,"\n");
            }
        } else fprintf(log,"bytes at +0x%llX unreadable\n",(unsigned long long)readRva);
        fflush(log);
    }

    // Candidate prologues are dumped even without --go so ABI questions can be
    // studied before anything is executed.
    const struct { const char *name; uint64_t rva; } candidates[] = {
        {"MoveForwardStart",MOVE_FORWARD_START},
        {"MoveForwardStop", MOVE_FORWARD_STOP},
        {"UnitHealth",0x1D63190ULL},
        {"UnitReaction",0x1D89A00ULL}
    };
    if (!callRva && !moves) {
        for (unsigned i=0;i<sizeof(candidates)/sizeof(candidates[0]);i++) {
            unsigned char bytes[24];
            if (!read_exact(process,imageBase+candidates[i].rva,bytes,sizeof(bytes))) continue;
            fprintf(log,"prologue %-16s +0x%llX:",candidates[i].name,(unsigned long long)candidates[i].rva);
            for (unsigned k=0;k<sizeof(bytes);k++) fprintf(log," %02X",bytes[k]);
            fprintf(log,"\n");
        }
    }

    if ((callRva || moves) && !go) {
        fprintf(log,"Call requested but --go was not passed; nothing was executed.\n");
        result=0; goto done;
    }

    if (callRva || moves) {
        uint64_t code=ui(VirtualAllocEx(process,NULL,0x100,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
        c.dataPage=ui(VirtualAllocEx(process,NULL,sizeof(call_data),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
        if (!code || !c.dataPage) { fprintf(log,"Remote allocation failed=%lu\n",(unsigned long)GetLastError()); goto done; }
        unsigned char block[0x100]; memset(block,0xCC,sizeof(block));
        memcpy(block,call_minimal_code,sizeof(call_minimal_code));
        memcpy(block+0x40,call_four_code,sizeof(call_four_code));
        DWORD oldProtect=0; unsigned char back[0x100];
        if (!write_exact(process,code,block,sizeof(block)) ||
            !VirtualProtectEx(process,(void*)code,sizeof(block),PAGE_EXECUTE_READ,&oldProtect) ||
            !FlushInstructionCache(process,(void*)code,sizeof(block)) ||
            !read_exact(process,code,back,sizeof(back)) || memcmp(back,block,sizeof(back))) {
            fprintf(log,"Stub write/protect/verify failed=%lu\n",(unsigned long)GetLastError()); goto done;
        }
        c.codePage=code;
        fprintf(log,"stubs at 0x%llX minimal=+0 call=+0x40 protect=0x%lX readback=match\n",
            (unsigned long long)code,(unsigned long)oldProtect); fflush(log);

        // Find one thread that accepts an APC (the only reliable route observed).
        DWORD tids[MAX_THREADS]; unsigned count=pick_threads(pid,tids,MAX_THREADS);
        fprintf(log,"looking for an AP-capable thread among %u candidates\n",count);
        for (unsigned i=0;i<count && !c.haveThread;i++) {
            c.tid=tids[i];
            call_data want; memset(&want,0,sizeof(want));
            call_data got;
            int ok=run_stub(&c,code,&want,&got,8000);
            fprintf(log,"  tid=%lu executed=%d phase=%lu\n",(unsigned long)tids[i],ok,(unsigned long)got.phase);
            if (ok && got.phase==42) c.haveThread=1;
            else if (got.phase==0) {
                // One retry: a busy thread may consume the APC later.
                ok=run_stub(&c,code,&want,&got,4000);
                fprintf(log,"  tid=%lu retry executed=%d phase=%lu\n",(unsigned long)tids[i],ok,(unsigned long)got.phase);
                if (ok && got.phase==42) c.haveThread=1;
            }
        }
        if (!c.haveThread) { fprintf(log,"No AP-capable thread found; nothing called.\n"); goto done; }
        fprintf(log,"executionThread=%lu\n",(unsigned long)c.tid); fflush(log);

        int did=0;
        if (callRva) {
            call_data want; memset(&want,0,sizeof(want));
            want.fn=imageBase+callRva;
            want.a1=args[0]; want.a2=args[1]; want.a3=args[2]; want.a4=args[3];
            fprintf(log,"calling +0x%llX (0x%llX) args=%llX,%llX,%llX,%llX\n",
                (unsigned long long)callRva,(unsigned long long)want.fn,
                (unsigned long long)args[0],(unsigned long long)args[1],
                (unsigned long long)args[2],(unsigned long long)args[3]);
            fflush(log);
            call_data got;
            int ok=run_stub(&c,c.codePage+0x40,&want,&got,APC_WAIT_MS);
            fprintf(log,"call observed=%d phase=%lu result=0x%llX\n",ok,(unsigned long)got.phase,
                (unsigned long long)got.result);
            if (ok && got.phase==2) did=1;
            fflush(log);
        }
        if (moves) {
            float before[3], after[3];
            int haveBefore=read_exact(process,player.record+0x60,before,12);
            call_data start; memset(&start,0,sizeof(start));
            start.fn=imageBase+MOVE_FORWARD_START;
            fprintf(log,"MoveForwardStart +0x%llX\n",(unsigned long long)MOVE_FORWARD_START); fflush(log);
            call_data got;
            int ok=run_stub(&c,c.codePage+0x40,&start,&got,APC_WAIT_MS);
            fprintf(log,"start observed=%d phase=%lu result=0x%llX\n",ok,(unsigned long)got.phase,
                (unsigned long long)got.result);
            Sleep(1500);
            int haveAfter=read_exact(process,player.record+0x60,after,12);
            call_data stop; memset(&stop,0,sizeof(stop));
            stop.fn=imageBase+MOVE_FORWARD_STOP;
            call_data got2;
            int ok2=run_stub(&c,c.codePage+0x40,&stop,&got2,APC_WAIT_MS);
            fprintf(log,"stop observed=%d phase=%lu result=0x%llX\n",ok2,(unsigned long)got2.phase,
                (unsigned long long)got2.result);
            if (haveBefore && haveAfter) {
                float moved=dist3(before,after);
                fprintf(log,"position before=(%.3f,%.3f,%.3f) after=(%.3f,%.3f,%.3f) moved=%.2fyd\n",
                    before[0],before[1],before[2],after[0],after[1],after[2],(double)moved);
                fprintf(log,"MOVEMENT %s\n", moved>=1.0f ? "CONFIRMED" : "NOT OBSERVED");
            } else fprintf(log,"position unreadable before/after\n");
            if (ok && ok2) did=1;
            fflush(log);
        }
        if (did) result=0;
    } else {
        result=0;
    }
done:
    if (process) CloseHandle(process);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    if (go) puts("Restart the client after a --go run.");
    return result;
}

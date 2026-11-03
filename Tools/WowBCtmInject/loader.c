#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "probe_shared.h"

static uint64_t ui(const void *p) { return (uint64_t)(uintptr_t)p; }
static int read_exact(HANDLE p, uint64_t addr, void *out, SIZE_T n) {
    SIZE_T got=0; return ReadProcessMemory(p,(void*)(uintptr_t)addr,out,n,&got) && got==n;
}
static int write_exact(HANDLE p, uint64_t addr, const void *in, SIZE_T n) {
    SIZE_T got=0; return WriteProcessMemory(p,(void*)(uintptr_t)addr,in,n,&got) && got==n;
}
static uint64_t filetime64(FILETIME f) { return ((uint64_t)f.dwHighDateTime<<32)|f.dwLowDateTime; }
static uint64_t module_base(DWORD pid, const wchar_t *name, DWORD *size) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); uint64_t found=0;
    if (Module32FirstW(snap,&e)) do {
        if (!name || _wcsicmp(e.szModule,name)==0 || _wcsicmp(e.szExePath,name)==0) {
            found=ui(e.modBaseAddr); if (size) *size=e.modBaseSize; break;
        }
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return found;
}
static uint64_t remote_function(DWORD pid, FARPROC local) {
    HMODULE module=NULL; wchar_t full[MAX_PATH];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(uintptr_t)local,&module) ||
        !GetModuleFileNameW(module,full,MAX_PATH)) return 0;
    wchar_t *base=wcsrchr(full,L'\\'); base=base ? base+1 : full;
    uint64_t remote=module_base(pid,base,NULL);
    return remote ? remote+(ui((void*)local)-ui(module)) : 0;
}
static int call_remote(HANDLE p, uint64_t function, uint64_t arg, DWORD *result) {
    HANDLE t=CreateRemoteThread(p,NULL,0,(LPTHREAD_START_ROUTINE)(uintptr_t)function,(void*)(uintptr_t)arg,0,NULL);
    if (!t) { printf("CreateRemoteThread failed: %lu\n",GetLastError()); return 0; }
    DWORD wait=WaitForSingleObject(t,10000);
    if (wait != WAIT_OBJECT_0) { printf("Remote thread did not finish (wait=%lu); restart client.\n",wait); CloseHandle(t); return 0; }
    int ok=GetExitCodeThread(t,result); CloseHandle(t); return ok;
}
static void print_addr(FILE *f, uint64_t v, uint64_t base, DWORD size) {
    fprintf(f,"0x%llX",(unsigned long long)v);
    if (v>=base && v-base<size) fprintf(f,"(wowb+rva=0x%llX)",(unsigned long long)(v-base));
}
typedef struct module_record { uint64_t base; DWORD size; char name[128]; } module_record;
static unsigned snapshot_modules(DWORD pid, module_record *out, unsigned capacity) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); unsigned n=0;
    if (Module32FirstW(snap,&e)) do {
        if (n==capacity) break;
        out[n].base=ui(e.modBaseAddr); out[n].size=e.modBaseSize;
        if (!WideCharToMultiByte(CP_UTF8,0,e.szModule,-1,out[n].name,sizeof(out[n].name),NULL,NULL))
            snprintf(out[n].name,sizeof(out[n].name),"unknown");
        n++;
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return n;
}
int main(int argc, char **argv) {
    if (argc < 4 || argc > 7) { puts("Usage: WowBCtmInject PID PROCESS_CREATION_HEX TARGET_HEX [TARGET_HEX ...] (one to four)"); return 2; }
    char *end; unsigned long pid=strtoul(argv[1],&end,10); if (*end || !pid) return 2;
    uint64_t expected=strtoull(argv[2],&end,16); if (*end || !expected) return 2;
    probe_config *buf=(probe_config*)calloc(1,sizeof(*buf)); if (!buf) return 1;
    buf->magic=PROBE_MAGIC; buf->size=sizeof(*buf); buf->target_count=(uint32_t)(argc-3);
    for (unsigned i=0;i<buf->target_count;i++) {
        buf->targets[i]=strtoull(argv[3+i],&end,16);
        if (*end || buf->targets[i]<0x10000 || buf->targets[i]>=0x7fffffffffffULL) { puts("Invalid target address."); free(buf); return 2; }
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME t; GetLocalTime(&t); wchar_t logpath[MAX_PATH];
    swprintf(logpath,MAX_PATH,L"C:\\ghidra-scripts\\WowBCtmInjected-%04u%02u%02u-%02u%02u%02u-%03u.txt",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);
    FILE *log=_wfopen(logpath,L"w"); if (!log) { puts("Cannot open report path."); free(buf); return 1; }
    fprintf(log,"WOWB INJECTED PAGE-GUARD WRITER PROBE v1 -- EXPERIMENTAL\n");
    fprintf(log,"GUARD faults are BEFORE access. Watch is page-level, non-atomic, racy, and misses writes; no CTM identity/ABI asserted.\n");
    fprintf(log,"DLL/VEH/TLS/config deliberately remain in process after stop. Restart the client before further testing.\n");
    fprintf(log,"pid=%lu creation=0x%llX targetCount=%u\n",pid,(unsigned long long)expected,buf->target_count);
    for (unsigned i=0;i<buf->target_count;i++) fprintf(log,"target%u=0x%llX\n",i,(unsigned long long)buf->targets[i]); fflush(log);
    int result=1, loaded=0, started=0, stopped=0; uint64_t remoteConfig=0, remotePath=0;
    HANDLE process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_VM_OPERATION|PROCESS_CREATE_THREAD|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",GetLastError()); goto done; }
    FILETIME create,exit,kernel,user; wchar_t image[MAX_PATH]; DWORD imageLen=MAX_PATH;
    if (!GetProcessTimes(process,&create,&exit,&kernel,&user) || filetime64(create)!=expected ||
        !QueryFullProcessImageNameW(process,0,image,&imageLen)) { fprintf(log,"Process identity/creation check failed\n"); goto done; }
    wchar_t *basename=wcsrchr(image,L'\\'); basename=basename ? basename+1 : image;
    if (_wcsicmp(basename,L"wowb.exe") != 0 || WaitForSingleObject(process,0) != WAIT_TIMEOUT) { fprintf(log,"Not a live wowb.exe; refusing\n"); goto done; }
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,L"wowb.exe",&imageSize);
    if (!imageBase) { fprintf(log,"Cannot find wowb image\n"); goto done; }
    fprintf(log,"imageBase=0x%llX imageSize=0x%lX\n",(unsigned long long)imageBase,(unsigned long)imageSize);
    wchar_t dll[MAX_PATH]; DWORD n=GetModuleFileNameW(NULL,dll,MAX_PATH);
    wchar_t *slash=wcsrchr(dll,L'\\'); if (!n || n>=MAX_PATH || !slash) { fprintf(log,"Cannot find injector directory\n"); goto done; }
    *(slash+1)=0; if (wcslen(dll)+wcslen(L"WowBCtmProbe.dll")>=MAX_PATH) goto done;
    wcscat(dll,L"WowBCtmProbe.dll");
    HMODULE local=LoadLibraryExW(dll,NULL,DONT_RESOLVE_DLL_REFERENCES);
    if (!local) { fprintf(log,"Cannot map probe DLL=%lu\n",GetLastError()); goto done; }
    FARPROC localStart=GetProcAddress(local,"ProbeStart"), localStop=GetProcAddress(local,"ProbeStop");
    if (!localStart || !localStop) { fprintf(log,"Missing DLL exports\n"); FreeLibrary(local); goto done; }
    uint64_t startRva=ui((void*)localStart)-ui(local), stopRva=ui((void*)localStop)-ui(local); FreeLibrary(local);
    FARPROC localLoad=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
    uint64_t remoteLoad=localLoad ? remote_function(pid,localLoad) : 0;
    if (!remoteLoad) { fprintf(log,"Cannot resolve remote LoadLibraryW\n"); goto done; }
    SIZE_T pathBytes=(wcslen(dll)+1)*sizeof(wchar_t);
    remotePath=ui(VirtualAllocEx(process,NULL,pathBytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    remoteConfig=ui(VirtualAllocEx(process,NULL,sizeof(*buf),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!remotePath || !remoteConfig || !write_exact(process,remotePath,dll,pathBytes) || !write_exact(process,remoteConfig,buf,sizeof(*buf))) {
        fprintf(log,"Remote allocation/write failed=%lu\n",GetLastError()); goto done;
    }
    DWORD remoteRet=0;
    if (!call_remote(process,remoteLoad,remotePath,&remoteRet)) { fprintf(log,"LoadLibrary remote call failed or timed out\n"); goto done; }
    uint64_t remoteDll=module_base(pid,dll,NULL);
    if (!remoteDll) { fprintf(log,"Remote DLL not found after LoadLibrary; return=0x%lX\n",(unsigned long)remoteRet); goto done; }
    loaded=1; fprintf(log,"remoteDll=0x%llX\n",(unsigned long long)remoteDll); fflush(log);
    if (!call_remote(process,remoteDll+startRva,remoteConfig,&remoteRet)) { fprintf(log,"ProbeStart remote call failed or timed out\n"); goto done; }
    if (remoteRet != 0) { fprintf(log,"ProbeStart refused with code=%lu\n",remoteRet); goto done; }
    started=1;
    puts("INJECTED WATCH ACTIVE for 25 seconds. Switch to WoW and click several DIFFERENT ground destinations now.");
    fflush(stdout); Sleep(25000);
    if (!call_remote(process,remoteDll+stopRva,remoteConfig,&remoteRet)) { fprintf(log,"ProbeStop call failed or timed out\n"); goto done; }
    stopped=1; fprintf(log,"ProbeStop status=%lu\n",remoteRet);
    if (!read_exact(process,remoteConfig,buf,sizeof(*buf))) { fprintf(log,"Reading probe data failed=%lu\n",GetLastError()); goto done; }
    fprintf(log,"active=%ld pages=%ld guardFaults=%ld writeFaults=%ld nearWrites=%ld events=%ld rearmErrors=%ld restoreErrors=%ld\n",
        buf->active,buf->page_count,buf->guard_faults,buf->write_faults,buf->nearby_writes,buf->event_count,buf->rearm_errors,buf->restore_errors);
    for (int i=0;i<buf->page_count && i<MAX_TARGETS;i++) fprintf(log,"page%d=0x%llX allocation=0x%llX oldProtect=0x%lX\n",i,
        (unsigned long long)buf->pages[i],(unsigned long long)buf->allocations[i],(unsigned long)buf->protections[i]);
    module_record modules[512]; unsigned moduleCount=snapshot_modules(pid,modules,512);
    fprintf(log,"moduleSnapshotAtEnd=%u (not necessarily module state at fault time)\n",moduleCount);
    LONG limit=buf->event_count; if (limit>MAX_EVENTS) limit=MAX_EVENTS; if (limit<0) limit=0;
    for (LONG i=0;i<limit;i++) {
        probe_event *e=&buf->events[i]; if (e->sequence!=(uint64_t)i+1) { fprintf(log,"event%ld incomplete\n",i); continue; }
        fprintf(log,"EVENT %ld tid=%llu address=0x%llX RIP-before-write=",i+1,(unsigned long long)e->tid,(unsigned long long)e->address);
        print_addr(log,e->rip,imageBase,imageSize);
        for (unsigned j=0;j<moduleCount;j++) if (e->rip>=modules[j].base && e->rip-modules[j].base<modules[j].size) {
            fprintf(log," moduleAtEnd=%s+rva:0x%llX",modules[j].name,(unsigned long long)(e->rip-modules[j].base)); break;
        }
        fprintf(log," RSP=0x%llX RAX=0x%llX RCX=0x%llX RDX=0x%llX RBX=0x%llX RBP=0x%llX RSI=0x%llX RDI=0x%llX\n",
            (unsigned long long)e->rsp,(unsigned long long)e->rax,(unsigned long long)e->rcx,(unsigned long long)e->rdx,(unsigned long long)e->rbx,
            (unsigned long long)e->rbp,(unsigned long long)e->rsi,(unsigned long long)e->rdi);
        fprintf(log,"  R8=0x%llX R9=0x%llX R10=0x%llX R11=0x%llX R12=0x%llX R13=0x%llX R14=0x%llX R15=0x%llX\n",
            (unsigned long long)e->r8,(unsigned long long)e->r9,(unsigned long long)e->r10,(unsigned long long)e->r11,
            (unsigned long long)e->r12,(unsigned long long)e->r13,(unsigned long long)e->r14,(unsigned long long)e->r15);
        unsigned char bytes[32];
        if (e->rip>=16 && read_exact(process,e->rip-16,bytes,sizeof(bytes))) {
            fprintf(log,"  bytesAtEnd[RIP-16..+15]="); for (unsigned j=0;j<sizeof(bytes);j++) fprintf(log,"%02X",bytes[j]); fprintf(log,"\n");
        } else fprintf(log,"  bytesAtEnd=unreadable\n");
    }
    if (buf->event_count>MAX_EVENTS) fprintf(log,"eventsCapped=true\n");
    result=(remoteRet==0 && !buf->rearm_errors && !buf->restore_errors) ? 0 : 1;
done:
    // Do not free remote memory after any remote thread is launched: on timeout or
    // after VEH registration target code may still reference it. Restart target.
    if (process) CloseHandle(process);
    fprintf(log,"loaded=%d started=%d stopped=%d status=%d. Restart target before reuse.\n",loaded,started,stopped,result);
    fclose(log); wprintf(L"Injected trace report: %ls\n",logpath); puts("Restart the client after this experiment."); free(buf); return result;
}

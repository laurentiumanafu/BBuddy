// Standalone, experimental 10-second instrumentation for the user's local client.
// No debugger, scan snapshots, remote gameplay calls, or client code patches.
//
// v4 rationale (from v1-v3 evidence):
//  * CreateRemoteThread threads are created, signalled as finished with exit
//    code 0, and do not run their start routine (three independent runs).
//  * An APC-delivered stub DOES execute on an existing client thread.
//  * v3 queued 74 identical APCs, so LoadLibraryW was re-entered concurrently
//    and its recorded errors (87, 570) are not attributable.
// v4 therefore: measures the thread-start anomaly with a timed kernel32!Sleep
// start routine, proves execution with a single minimal stub, then runs EVERY
// later step exactly once on a dedicated state slot - first a benign control
// DLL, then the probe DLL - and only then ProbeStart/ProbeStop. No repeated or
// overlapping executions are issued, and a still-pending APC blocks later ones.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include <stddef.h>
#include "probe_shared.h"
#include "player_lookup.h"
#include "remote_stubs.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define SLOT_COUNT 16
#define MAX_THREADS 4
#define APC_WAIT_MS 12000
#define LOAD_WAIT_MS 25000
_Static_assert(offsetof(probe_event,tick_ms)==8, "event ABI");
_Static_assert(offsetof(probe_config,events)%8==0, "event alignment");
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
    DWORD pid=0, count=0;
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
        if (!name || _wcsicmp(e.szModule,name)==0 || _wcsicmp(e.szExePath,name)==0) {
            found=ui(e.modBaseAddr); if (size) *size=e.modBaseSize; break;
        }
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return found;
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
static uint64_t remote_function(DWORD pid, FARPROC local) {
    HMODULE module=NULL; wchar_t full[MAX_PATH];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(uintptr_t)local,&module) ||
        !GetModuleFileNameW(module,full,MAX_PATH)) return 0;
    wchar_t *base=wcsrchr(full,L'\\'); base=base ? base+1 : full;
    uint64_t remote=module_base(pid,base,NULL);
    return remote ? remote+(ui((void*)local)-ui(module)) : 0;
}
static uint64_t resolve_remote(DWORD pid, const wchar_t *module, const char *name) {
    HMODULE local=GetModuleHandleW(module);
    FARPROC address=local ? GetProcAddress(local,name) : NULL;
    return address ? remote_function(pid,address) : 0;
}

// A previous injection into the same process instance leaves remote pages,
// pending APCs and possibly a half-loaded DLL behind. Refuse to reuse it.
#define LAST_TARGET L"C:\\ghidra-scripts\\WowBCtmQuickProbe-last-target.txt"
static int same_instance_as_last_run(DWORD pid, uint64_t creation) {
    FILE *f=_wfopen(LAST_TARGET,L"r");
    if (!f) return 0;
    unsigned long lastPid=0; unsigned long long lastCreation=0;
    int matched=fscanf(f,"%lu %llu",&lastPid,&lastCreation)==2;
    fclose(f);
    return matched && lastPid==pid && lastCreation==creation;
}
static void remember_target(DWORD pid, uint64_t creation) {
    FILE *f=_wfopen(LAST_TARGET,L"w");
    if (f) { fprintf(f,"%lu %llu\n",pid,(unsigned long long)creation); fclose(f); }
}

// ---- read-only loader-hook inspection -----------------------------------------
// Compares live code bytes in the target with the same function in this process.
// Both run on the same OS, so identical images must match byte for byte; any
// difference means the target's copy was patched.
static void compare_code(FILE *log, HANDLE process, DWORD pid, const wchar_t *module,
                         const char *name, unsigned count) {
    HMODULE local=GetModuleHandleW(module);
    if (!local) { fprintf(log,"codeDiff %ls!%s localModuleMissing\n",module,name); return; }
    FARPROC function=GetProcAddress(local,name);
    if (!function) { fprintf(log,"codeDiff %ls!%s localExportMissing\n",module,name); return; }
    uint64_t remote=remote_function(pid,function);
    unsigned char mine[32], theirs[32];
    if (count>sizeof(mine)) count=sizeof(mine);
    memcpy(mine,(const void*)function,count);
    if (!remote || !read_exact(process,remote,theirs,count)) {
        fprintf(log,"codeDiff %ls!%s remoteUnreadable remote=0x%llX\n",module,name,(unsigned long long)remote); return;
    }
    int same=memcmp(mine,theirs,count)==0;
    fprintf(log,"codeDiff %ls!%s patched=%s remote=0x%llX\n",module,name,same ? "false" : "true",
        (unsigned long long)remote);
    if (!same) {
        fprintf(log,"  local =");
        for (unsigned i=0;i<count;i++) fprintf(log,"%02X",mine[i]);
        fprintf(log,"\n  target=");
        for (unsigned i=0;i<count;i++) fprintf(log,"%02X",theirs[i]);
        fprintf(log,"\n");
    }
    fflush(log);
}

// ---- manual image mapping (no OS loader involved) ------------------------------
// LdrLoadDll answered STATUS_INVALID_THREAD and LoadLibraryW refused every path,
// so the loader is not usable from injected code here. Map the probe DLL by hand:
// copy sections, apply base relocations, resolve imports against modules that are
// already loaded, protect the pages, then call the image entry point and the
// exported functions directly.
typedef struct pe_image {
    unsigned char *data;
    size_t size;
    uint64_t preferred;
    uint32_t entry;
    uint32_t optAt;      // offset of the optional header inside data
    unsigned sections;
    struct { uint32_t va, vsize, raw, rawsize, chars; } section[24];
} pe_image;

static int read_whole_file(const wchar_t *path, unsigned char **out, size_t *outSize) {
    FILE *f=_wfopen(path,L"rb");
    if (!f) return 0;
    if (fseek(f,0,SEEK_END)) { fclose(f); return 0; }
    long length=ftell(f);
    if (length<=0 || length>64*1024*1024) { fclose(f); return 0; }
    if (fseek(f,0,SEEK_SET)) { fclose(f); return 0; }
    unsigned char *buffer=(unsigned char*)malloc((size_t)length);
    if (!buffer) { fclose(f); return 0; }
    size_t got=fread(buffer,1,(size_t)length,f);
    fclose(f);
    if (got!=(size_t)length) { free(buffer); return 0; }
    *out=buffer; *outSize=got; return 1;
}

static int pe_prepare(const unsigned char *file, size_t fileSize, pe_image *image, FILE *log) {
    if (fileSize<0x100) { fprintf(log,"map: file too small\n"); return 0; }
    uint32_t peoff=le32(file+0x3c);
    if (peoff+0xf8>fileSize || memcmp(file+peoff,"PE\0\0",4)) { fprintf(log,"map: bad PE signature\n"); return 0; }
    const unsigned char *coff=file+peoff+4, *opt=coff+20;
    uint16_t machine=*(const uint16_t*)coff;
    uint16_t sectionCount=*(const uint16_t*)(coff+2);
    uint16_t optSize=*(const uint16_t*)(coff+16);
    if (machine!=0x8664) { fprintf(log,"map: not AMD64 (machine=0x%X)\n",machine); return 0; }
    if (optSize<0xf0 || *(const uint16_t*)opt!=0x20b) { fprintf(log,"map: not PE32+\n"); return 0; }
    uint32_t sizeOfImage=*(const uint32_t*)(opt+56);
    uint32_t sizeOfHeaders=*(const uint32_t*)(opt+60);
    if (sizeOfImage==0 || sizeOfImage>256u*1024*1024 || sizeOfHeaders>sizeOfImage) {
        fprintf(log,"map: implausible image size=%u headers=%u\n",sizeOfImage,sizeOfHeaders); return 0;
    }
    image->optAt=(uint32_t)(opt-file);
    if (image->optAt<0x40 || image->optAt+112+16*8>fileSize) { fprintf(log,"map: optional header offset implausible\n"); return 0; }
    image->preferred=*(const uint64_t*)(opt+24);
    image->entry=*(const uint32_t*)(opt+16);
    image->size=sizeOfImage;
    if (sectionCount>24) { fprintf(log,"map: too many sections (%u)\n",sectionCount); return 0; }
    image->sections=sectionCount;
    const unsigned char *sec=opt+optSize;
    for (unsigned i=0;i<sectionCount;i++,sec+=40) {
        image->section[i].vsize=*(const uint32_t*)(sec+8);
        image->section[i].va=*(const uint32_t*)(sec+12);
        image->section[i].rawsize=*(const uint32_t*)(sec+16);
        image->section[i].raw=*(const uint32_t*)(sec+20);
        image->section[i].chars=*(const uint32_t*)(sec+36);
    }
    image->data=(unsigned char*)calloc(1,sizeOfImage);
    if (!image->data) { fprintf(log,"map: host allocation failed\n"); return 0; }
    memcpy(image->data,file,sizeOfHeaders<=fileSize ? sizeOfHeaders : fileSize);
    for (unsigned i=0;i<sectionCount;i++) {
        uint32_t copy=image->section[i].rawsize;
        if (image->section[i].raw > fileSize) continue;
        if (copy > fileSize-image->section[i].raw) copy=(uint32_t)(fileSize-image->section[i].raw);
        if (image->section[i].va >= sizeOfImage || copy > sizeOfImage-image->section[i].va) continue;
        memcpy(image->data+image->section[i].va,file+image->section[i].raw,copy);
    }
    fprintf(log,"map: image size=0x%lX preferred=0x%llX sections=%u entryRva=0x%lX\n",
        (unsigned long)sizeOfImage,(unsigned long long)image->preferred,sectionCount,
        (unsigned long)image->entry);
    return 1;
}

static int pe_relocate(pe_image *image, uint64_t remoteBase, FILE *log) {
    const unsigned char *opt=image->data+image->optAt;
    uint32_t relocRva=*(const uint32_t*)(opt+112+5*8);
    uint32_t relocSize=*(const uint32_t*)(opt+112+5*8+4);
    if (!relocRva || !relocSize) {
        fprintf(log,"map: no relocation table (x64 image is position independent); mapping at preferred base is still attempted first\n");
        return 1;
    }
    if (relocRva>=image->size || relocSize>image->size-relocRva) { fprintf(log,"map: reloc table out of range\n"); return 0; }
    int64_t delta=(int64_t)remoteBase-(int64_t)image->preferred;
    unsigned applied=0, skipped=0;
    uint32_t off=0;
    while (off+8<=relocSize) {
        const unsigned char *block=image->data+relocRva+off;
        uint32_t pageRva=*(const uint32_t*)block, blockSize=*(const uint32_t*)(block+4);
        if (blockSize<8 || blockSize>relocSize-off) break;
        unsigned entries=(blockSize-8)/2;
        for (unsigned i=0;i<entries;i++) {
            uint16_t entry=*(const uint16_t*)(block+8+i*2);
            unsigned type=entry>>12; uint32_t at=pageRva+(entry&0xfff);
            if (type==0) continue;
            if (type!=10) { skipped++; continue; }
            if (at+8>image->size) { skipped++; continue; }
            uint64_t *slot=(uint64_t*)(image->data+at);
            *slot=(uint64_t)((int64_t)*slot+delta);
            applied++;
        }
        off+=blockSize;
    }
    fprintf(log,"map: relocations applied=%u otherTypesSkipped=%u delta=%lld\n",
        applied,skipped,(long long)delta);
    if (skipped) { fprintf(log,"map: unsupported relocation types present; refusing\n"); return 0; }
    return 1;
}

static int pe_imports(pe_image *image, DWORD pid, FILE *log,
                      unsigned *resolvedOut, unsigned *unresolvedOut) {
    const unsigned char *opt=image->data+image->optAt;
    uint32_t importRva=*(const uint32_t*)(opt+112+1*8);
    uint32_t importSize=*(const uint32_t*)(opt+112+1*8+4);
    unsigned resolved=0, unresolved=0;
    if (!importRva || !importSize) { fprintf(log,"map: no import table\n"); *resolvedOut=0; *unresolvedOut=0; return 1; }
    if (importRva>=image->size) { fprintf(log,"map: import table out of range\n"); return 0; }
    for (uint32_t d=0;;d++) {
        uint64_t at=(uint64_t)importRva+d*20;
        if (at+20>image->size) break;
        const unsigned char *desc=image->data+at;
        uint32_t nameRva=*(const uint32_t*)(desc+12);
        uint32_t thunkRva=*(const uint32_t*)(desc+16);
        uint32_t lookupRva=*(const uint32_t*)(desc+0);
        if (!nameRva && !thunkRva && !lookupRva) break;
        if (nameRva>=image->size) { fprintf(log,"map: import descriptor name out of range\n"); return 0; }
        const char *moduleName=(const char*)(image->data+nameRva);
        wchar_t wide[128]; size_t i=0;
        for (;moduleName[i] && i<127;i++) wide[i]=(wchar_t)(unsigned char)moduleName[i];
        wide[i]=0;
        HMODULE localModule=GetModuleHandleW(wide);
        if (!localModule) localModule=LoadLibraryW(wide);
        uint64_t remoteModule=localModule ? module_base(pid,wide,NULL) : 0;
        fprintf(log,"map: import module %s local=%s remote=0x%llX\n",moduleName,
            localModule ? "yes" : "no",(unsigned long long)remoteModule);
        if (!localModule || !remoteModule) { unresolved++; continue; }
        uint32_t walk=(lookupRva ? lookupRva : thunkRva);
        for (unsigned index=0;;index++) {
            uint64_t entryAt=(uint64_t)walk+(uint64_t)index*8;
            if (entryAt+8>image->size) break;
            uint64_t thunk=*(const uint64_t*)(image->data+entryAt);
            if (!thunk) break;
            FARPROC localAddress=NULL;
            if (thunk & 0x8000000000000000ULL) {
                localAddress=GetProcAddress(localModule,(LPCSTR)(uintptr_t)(thunk & 0xffff));
            } else {
                uint32_t hintAt=(uint32_t)thunk;
                if (hintAt+2<image->size) {
                    const char *symbol=(const char*)(image->data+hintAt+2);
                    localAddress=GetProcAddress(localModule,symbol);
                }
            }
            uint64_t slotAt=(uint64_t)thunkRva+(uint64_t)index*8;
            if (slotAt+8>image->size) break;
            if (!localAddress) { unresolved++; *(uint64_t*)(image->data+slotAt)=0; continue; }
            uint64_t offset=(uint64_t)(uintptr_t)localAddress-(uint64_t)(uintptr_t)localModule;
            *(uint64_t*)(image->data+slotAt)=remoteModule+offset;
            resolved++;
        }
    }
    *resolvedOut=resolved; *unresolvedOut=unresolved;
    fprintf(log,"map: imports resolved=%u unresolved=%u\n",resolved,unresolved);
    return 1;
}

static void pe_release(pe_image *image) {
    if (image->data) free(image->data);
    image->data=NULL;
}

// ---- read-only mitigation report -------------------------------------------------
typedef BOOL (WINAPI *mitigation_query_fn)(HANDLE,DWORD,PVOID,SIZE_T);
static void report_mitigations(FILE *log, HANDLE process) {
    HMODULE kernel=GetModuleHandleW(L"kernel32.dll");
    mitigation_query_fn query=kernel ? (mitigation_query_fn)(uintptr_t)GetProcAddress(kernel,"GetProcessMitigationPolicy") : NULL;
    if (!query) { fprintf(log,"mitigationQuery=unavailable\n"); fflush(log); return; }
    // Policy 0 (ProcessDEPPolicy) is not queryable on every build; DEP is always
    // enforced for x64 images, so a failure there is reported but not fatal.
    static const struct { DWORD id; const char *name; } policies[] = {
        {0,"DEP"},{2,"DynamicCode"},{4,"SystemCallDisable"},{6,"ExtensionPointDisable"},
        {7,"ControlFlowGuard"},{8,"Signature"},{10,"ImageLoad"},{11,"SystemCallFilter"}
    };
    for (unsigned i=0;i<sizeof(policies)/sizeof(policies[0]);i++) {
        DWORD value=0;
        if (query(process,policies[i].id,&value,sizeof(value)))
            fprintf(log,"mitigation %s=0x%08lX\n",policies[i].name,(unsigned long)value);
        else
            fprintf(log,"mitigation %s=queryFailed(%lu)\n",policies[i].name,(unsigned long)GetLastError());
    }
    fflush(log);
}

// ---- remote execution helpers ----------------------------------------------------
typedef struct remote_ctx {
    HANDLE process; DWORD pid; FILE *log;
    uint64_t slots;          // array of SLOT_COUNT remote_loader_data
    uint64_t codeExec, codeLoad, codeCall, codeLdr, codeDllMain;
    int method;              // 1 = CreateRemoteThread, 2 = APC on tid
    DWORD tid;               // winning APC thread
    unsigned slotsUsed, apcsPending;
    DWORD lastThreadWait; int lastExitAvailable; DWORD lastExitCode; int lastPollTimedOut;
} remote_ctx;

static uint64_t slot_addr(remote_ctx *ctx, unsigned index) {
    return ctx->slots + (uint64_t)index*sizeof(remote_loader_data);
}
// Prepares a never-reused slot from a fully initialised record and verifies it.
static uint64_t arm_slot_full(remote_ctx *ctx, const remote_loader_data *want) {
    if (ctx->slotsUsed>=SLOT_COUNT) { fprintf(ctx->log,"slot budget exhausted\n"); return 0; }
    uint64_t addr=slot_addr(ctx,ctx->slotsUsed);
    if (!write_exact(ctx->process,addr,want,sizeof(*want))) {
        fprintf(ctx->log,"slot write failed=%lu\n",(unsigned long)GetLastError()); return 0;
    }
    remote_loader_data check;
    if (!read_exact(ctx->process,addr,&check,sizeof(check)) || memcmp(&check,want,sizeof(*want))) {
        fprintf(ctx->log,"slot readback mismatch\n"); return 0;
    }
    ctx->slotsUsed++;
    return addr;
}
// Prepares a never-reused slot for one function call.
static uint64_t arm_slot(remote_ctx *ctx, uint64_t function, uint64_t getError, uint64_t arg) {
    remote_loader_data want; memset(&want,0,sizeof(want));
    want.function=function; want.get_error=getError; want.arg=arg;
    return arm_slot_full(ctx,&want);
}
static int execute_once(remote_ctx *ctx, uint64_t code, uint64_t slot, DWORD waitMs, uint64_t *elapsedOut) {
    uint64_t begin=GetTickCount64();
    DWORD wait=0, threadWait=0; HANDLE thread=NULL; DWORD exitCode=0; int exitAvailable=0;
    if (ctx->method==1) {
        thread=CreateRemoteThread(ctx->process,NULL,0,(LPTHREAD_START_ROUTINE)(uintptr_t)code,
                                  (void*)(uintptr_t)slot,0,NULL);
        if (!thread) { fprintf(ctx->log,"crt createFailed=%lu\n",(unsigned long)GetLastError()); return 0; }
        threadWait=WaitForSingleObject(thread,waitMs); wait=threadWait;
        exitAvailable = threadWait==WAIT_OBJECT_0 && GetExitCodeThread(thread,&exitCode);
        CloseHandle(thread);
    } else {
        HANDLE target=OpenThread(THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,ctx->tid);
        if (!target) { fprintf(ctx->log,"apc openThread failed=%lu\n",(unsigned long)GetLastError()); return 0; }
        if (!QueueUserAPC((PAPCFUNC)(uintptr_t)code,target,(ULONG_PTR)slot)) {
            fprintf(ctx->log,"apc queue failed=%lu\n",(unsigned long)GetLastError()); CloseHandle(target); return 0;
        }
        CloseHandle(target);
    }
    // Wait for completion (phase==2) or the minimal-stub marker (phase==42).
    // If a CreateRemoteThread thread already exited, the slot gets one immediate
    // read: an untouched slot means nothing further will happen on that thread.
    int immediate=ctx->method==1 && wait==WAIT_OBJECT_0;
    for (;;) {
        remote_loader_data state;
        if (read_exact(ctx->process,slot,&state,sizeof(state)) && (state.phase==2 || state.phase==42)) {
            wait=WAIT_OBJECT_0; break;
        }
        if (immediate) { wait=WAIT_TIMEOUT; break; }
        if (GetTickCount64()-begin>=waitMs) { wait=WAIT_TIMEOUT; break; }
        Sleep(10);
    }
    uint64_t elapsed=GetTickCount64()-begin;
    if (elapsedOut) *elapsedOut=elapsed;
    ctx->lastThreadWait=threadWait; ctx->lastExitAvailable=exitAvailable;
    ctx->lastExitCode=exitCode; ctx->lastPollTimedOut=(wait!=WAIT_OBJECT_0 && ctx->method==1);
    if (ctx->method==1)
        fprintf(ctx->log,"crt threadWait=0x%lX exitAvailable=%d exit=0x%lX pollTimeout=%s\n",
            (unsigned long)threadWait,exitAvailable,(unsigned long)exitCode,
            ctx->lastPollTimedOut ? "true" : "false");
    return wait==WAIT_OBJECT_0;
}
static int state_of(remote_ctx *ctx, uint64_t slot, remote_loader_data *out) {
    return read_exact(ctx->process,slot,out,sizeof(*out));
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
static void print_event(FILE *log, HANDLE process, const probe_event *e, uint64_t start,
                        uint64_t imageBase, DWORD imageSize, const module_record *modules, unsigned moduleCount) {
    long long delta=e->tick_ms >= start ? (long long)(e->tick_ms-start) : -(long long)(start-e->tick_ms);
    fprintf(log,"EVENT %llu t=%lldms tid=%llu accessed=0x%llX RIP-before-write=0x%llX",
        (unsigned long long)e->sequence,delta,(unsigned long long)e->tid,(unsigned long long)e->address,(unsigned long long)e->rip);
    if (e->rip>=imageBase && e->rip-imageBase<imageSize)
        fprintf(log,"(wowb+rva=0x%llX)",(unsigned long long)(e->rip-imageBase));
    for (unsigned j=0;j<moduleCount;j++) if (e->rip>=modules[j].base && e->rip-modules[j].base<modules[j].size) {
        fprintf(log," moduleAtStart=%s+rva:0x%llX",modules[j].name,(unsigned long long)(e->rip-modules[j].base)); break;
    }
    fprintf(log,"\n RSP=0x%llX RAX=0x%llX RCX=0x%llX RDX=0x%llX RBX=0x%llX RBP=0x%llX RSI=0x%llX RDI=0x%llX\n",
        (unsigned long long)e->rsp,(unsigned long long)e->rax,(unsigned long long)e->rcx,(unsigned long long)e->rdx,(unsigned long long)e->rbx,
        (unsigned long long)e->rbp,(unsigned long long)e->rsi,(unsigned long long)e->rdi);
    fprintf(log," R8=0x%llX R9=0x%llX R10=0x%llX R11=0x%llX R12=0x%llX R13=0x%llX R14=0x%llX R15=0x%llX\n",
        (unsigned long long)e->r8,(unsigned long long)e->r9,(unsigned long long)e->r10,(unsigned long long)e->r11,
        (unsigned long long)e->r12,(unsigned long long)e->r13,(unsigned long long)e->r14,(unsigned long long)e->r15);
    unsigned char bytes[32];
    if (e->rip>=16 && read_exact(process,e->rip-16,bytes,sizeof(bytes))) {
        fprintf(log," bytesAtSample[RIP-16..+15]="); for (unsigned j=0;j<sizeof(bytes);j++) fprintf(log,"%02X",bytes[j]); fprintf(log,"\n");
    } else fprintf(log," bytesAtSample=unreadable\n");
}

int main(int argc, char **argv) {
    DWORD pid=0; int force=0;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--force")) { force=1; continue; }
        if (!strcmp(argv[i],"--pid") && i+1<argc) {
            char *end; unsigned long v=strtoul(argv[++i],&end,10);
            if (*end || !v || v>0xffffffffUL) { puts("Invalid PID."); return 2; }
            pid=(DWORD)v; continue;
        }
        puts("Usage: WowBCtmQuickProbe.exe [--pid PID] [--force]"); return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME time; GetLocalTime(&time); wchar_t logpath[MAX_PATH];
    swprintf(logpath,MAX_PATH,L"C:\\ghidra-scripts\\WowBCtmQuickProbe-%04u%02u%02u-%02u%02u%02u-%03u.txt",
        time.wYear,time.wMonth,time.wDay,time.wHour,time.wMinute,time.wSecond,time.wMilliseconds);
    FILE *log=_wfopen(logpath,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB STANDALONE INJECTED 10-SECOND PLAYER-POSITION WRITER PROBE v7 (loader-hook inspection, manual mapping)\n");
    fprintf(log,"No debugger/snapshot capture. Setup uses read-only GUID/slot lookup; injection writes target process memory and changes page protections.\n");
    fprintf(log,"This watches near player record position, NOT a known CTM destination. PAGE_GUARD may miss accesses; events are pre-write and don't prove CTM.\n");
    fprintf(log,"Right-button events are sampled physical input when WoW is foreground, not proof the game accepted a ground click.\n");
    fprintf(log,"Every remote execution in this build is issued once, on its own state slot. DLL/VEH/TLS/config/APCs remain until client exit; restart client afterward.\n");
    fprintf(log,"A run against a process instance that a previous run already touched is refused, because leftovers make results meaningless.\n");
    fflush(log);
    int result=1, loaded=0, started=0, stopped=0; LONG readFailures=0, logged=0;
    uint64_t imageBase=0, codesPage=0, configPage=0, remoteDll=0, controlPath=0, loaderErrorSlot=0;
    DWORD imageSize=0;
    HANDLE process=NULL;
    probe_config *buf=(probe_config*)calloc(1,sizeof(*buf));
    if (!buf) { fprintf(log,"allocation failed\n"); goto done; }
    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; use --pid PID if multiple.\n"); goto done; }
    fprintf(log,"pid=%lu\n",pid); fflush(log);
    process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|PROCESS_VM_WRITE|PROCESS_VM_OPERATION|PROCESS_CREATE_THREAD|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); goto done; }
    FILETIME create,exit,kernel,user; wchar_t image[MAX_PATH]; DWORD imageLen=MAX_PATH;
    if (!GetProcessTimes(process,&create,&exit,&kernel,&user) || !QueryFullProcessImageNameW(process,0,image,&imageLen)) {
        fprintf(log,"Process identity query failed=%lu\n",(unsigned long)GetLastError()); goto done;
    }
    wchar_t *basename=wcsrchr(image,L'\\'); basename=basename ? basename+1 : image;
    if (_wcsicmp(basename,L"wowb.exe") || WaitForSingleObject(process,0)!=WAIT_TIMEOUT) {
        fprintf(log,"Not a live wowb.exe; refusing. A live process may still be crash-reporting.\n"); goto done;
    }
    imageBase=module_base(pid,L"wowb.exe",&imageSize);
    if (!imageBase || imageSize <= PLAYER_GUID_RVA + 16 || !valid_identity(process,imageBase)) {
        fprintf(log,"AMD64 PE or observed UnitHealth identity sample mismatch/unreadable; refusing. This check is partial.\n"); goto done;
    }
    uint64_t creation=filetime64(create);
    fprintf(log,"creation=0x%llX imageBase=0x%llX imageSize=0x%lX identitySample=match (partial only)\n",
        (unsigned long long)creation,(unsigned long long)imageBase,(unsigned long)imageSize);
    if (!force && same_instance_as_last_run(pid,creation)) {
        fprintf(log,"REFUSING: this exact process instance (pid=%lu creation=0x%llX) was already used by a previous probe run.\n",
            (unsigned long)pid,(unsigned long long)creation);
        fprintf(log,"A reused client still holds the earlier remote pages and queued APCs, which makes the results meaningless.\n");
        fprintf(log,"Fully close and restart wowb.exe, then run again. Use --force only if you accept contaminated results.\n");
        goto done;
    }
    remember_target(pid,creation);
    report_mitigations(log,process);
    compare_code(log,process,pid,L"kernel32.dll","LoadLibraryW",24);
    compare_code(log,process,pid,L"kernelbase.dll","LoadLibraryW",24);
    compare_code(log,process,pid,L"kernel32.dll","LoadLibraryExW",24);
    compare_code(log,process,pid,L"ntdll.dll","LdrLoadDll",24);
    compare_code(log,process,pid,L"ntdll.dll","LdrGetProcedureAddress",24);
    compare_code(log,process,pid,L"kernel32.dll","GetCurrentProcessId",16);
    player_anchor player;
    if (!player_find(player_read,process,imageBase,&player)) { fprintf(log,"Unique checked local-player record unavailable; injection skipped.\n"); goto done; }
    fprintf(log,"playerAnchor=GUID-slot-checked-nonatomic family=%u slot=%u record=0x%llX\n",
        player.family,player.index,(unsigned long long)player.record);
    buf->magic=PROBE_MAGIC; buf->size=sizeof(*buf); buf->target_count=3;
    for (unsigned i=0;i<3;i++) { buf->targets[i]=player.record+0x60+4*i;
        fprintf(log,"target%u=0x%llX positionComponent=%c\n",i,(unsigned long long)buf->targets[i],"XYZ"[i]); }
    fflush(log);
    module_record modules[512]; unsigned moduleCount=snapshot_modules(pid,modules,512);
    fprintf(log,"moduleSnapshotBeforeInjection=%u (may not describe modules at fault time)\n",moduleCount); fflush(log);

    wchar_t dll[MAX_PATH]; DWORD n=GetModuleFileNameW(NULL,dll,MAX_PATH);
    wchar_t *slash=wcsrchr(dll,L'\\');
    if (!n || n>=MAX_PATH || !slash) { fprintf(log,"Cannot find probe directory\n"); goto done; }
    *(slash+1)=0;
    if (wcslen(dll)+wcslen(L"WowBCtmQuickProbe.dll")>=MAX_PATH) { fprintf(log,"Probe path too long\n"); goto done; }
    wcscat(dll,L"WowBCtmQuickProbe.dll");
    HMODULE local=LoadLibraryExW(dll,NULL,DONT_RESOLVE_DLL_REFERENCES);
    if (!local) { fprintf(log,"Cannot map local probe DLL=%lu\n",(unsigned long)GetLastError()); goto done; }
    FARPROC localStart=GetProcAddress(local,"ProbeStart"), localStop=GetProcAddress(local,"ProbeStop");
    if (!localStart || !localStop) { fprintf(log,"Missing probe DLL exports\n"); FreeLibrary(local); goto done; }
    uint64_t startRva=ui((void*)localStart)-ui(local), stopRva=ui((void*)localStop)-ui(local); FreeLibrary(local);
    uint64_t remoteLoad=resolve_remote(pid,L"kernel32.dll","LoadLibraryW");
    uint64_t remoteError=resolve_remote(pid,L"kernel32.dll","GetLastError");
    uint64_t remoteSleep=resolve_remote(pid,L"kernelbase.dll","Sleep");
    uint64_t remoteLdr=resolve_remote(pid,L"ntdll.dll","LdrLoadDll");
    uint64_t remoteGetPid=resolve_remote(pid,L"kernel32.dll","GetCurrentProcessId");
    if (!remoteSleep) remoteSleep=resolve_remote(pid,L"kernel32.dll","Sleep");
    if (!remoteLoad || !remoteError || !remoteSleep || !remoteLdr || !remoteGetPid) {
        fprintf(log,"Cannot resolve remote LoadLibraryW/GetLastError/Sleep/LdrLoadDll/GetCurrentProcessId\n"); goto done;
    }
    fprintf(log,"resolved remote LoadLibraryW=0x%llX GetLastError=0x%llX Sleep=0x%llX LdrLoadDll=0x%llX GetCurrentProcessId=0x%llX\n",
        (unsigned long long)remoteLoad,(unsigned long long)remoteError,(unsigned long long)remoteSleep,
        (unsigned long long)remoteLdr,(unsigned long long)remoteGetPid);
    fflush(log);

    remote_ctx ctx; ZeroMemory(&ctx,sizeof(ctx)); ctx.process=process; ctx.pid=pid; ctx.log=log;
    ctx.slots=ui(VirtualAllocEx(process,NULL,sizeof(remote_loader_data)*SLOT_COUNT,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    codesPage=ui(VirtualAllocEx(process,NULL,512,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    configPage=ui(VirtualAllocEx(process,NULL,sizeof(*buf),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    controlPath=ui(VirtualAllocEx(process,NULL,64*sizeof(wchar_t),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    uint64_t bogusPath=ui(VirtualAllocEx(process,NULL,64*sizeof(wchar_t),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!ctx.slots || !codesPage || !configPage || !controlPath || !bogusPath ||
        !write_exact(process,configPage,buf,sizeof(*buf))) {
        fprintf(log,"Remote allocation/write failed=%lu\n",(unsigned long)GetLastError()); goto done;
    }
    fprintf(log,"remote slots=0x%llX codePage=0x%llX config=0x%llX\n",
        (unsigned long long)ctx.slots,(unsigned long long)codesPage,(unsigned long long)configPage); fflush(log);

    // ---- code block: minimal stub at +0, loader stub at +64, call stub at +128 ----
    {
        unsigned char block[512]; memset(block,0xCC,sizeof(block));
        memcpy(block,remote_minimal_code,sizeof(remote_minimal_code));
        memcpy(block+64,remote_loader_code,sizeof(remote_loader_code));
        memcpy(block+128,remote_call_code,sizeof(remote_call_code));
        memcpy(block+192,remote_ldr_code,sizeof(remote_ldr_code));
        memcpy(block+256,remote_dllmain_code,sizeof(remote_dllmain_code));
        DWORD oldProtect=0, liveProtect=0; unsigned char back[512];
        MEMORY_BASIC_INFORMATION info;
        if (!write_exact(process,codesPage,block,sizeof(block)) ||
            !VirtualProtectEx(process,(void*)(uintptr_t)codesPage,sizeof(block),PAGE_EXECUTE_READ,&oldProtect) ||
            !FlushInstructionCache(process,(void*)(uintptr_t)codesPage,sizeof(block)) ||
            !read_exact(process,codesPage,back,sizeof(back)) || memcmp(back,block,sizeof(block))) {
            fprintf(log,"code block write/protect/verify failed=%lu\n",(unsigned long)GetLastError()); goto done;
        }
        if (VirtualQueryEx(process,(void*)(uintptr_t)codesPage,&info,sizeof(info))) liveProtect=info.Protect;
        ctx.codeExec=codesPage; ctx.codeLoad=codesPage+64; ctx.codeCall=codesPage+128;
        ctx.codeLdr=codesPage+192; ctx.codeDllMain=codesPage+256;
        fprintf(log,"codeBlock page=0x%llX minimal=+0 loader=+64 call=+128 ldr=+192 dllmain=+256 protectNow=0x%lX (was 0x%lX) readback=match\n",
            (unsigned long long)codesPage,(unsigned long)liveProtect,(unsigned long)oldProtect); fflush(log);
    }

    // ---- Stage 1: is a CreateRemoteThread start routine executed at all? ----
    {
        uint64_t begin=GetTickCount64();
        HANDLE thread=CreateRemoteThread(process,NULL,0,(LPTHREAD_START_ROUTINE)(uintptr_t)remoteSleep,
                                         (void*)(uintptr_t)1500,0,NULL);
        if (!thread) {
            fprintf(log,"sleepTest createFailed=%lu\n",(unsigned long)GetLastError());
        } else {
            DWORD wait=WaitForSingleObject(thread,5000), exitCode=0;
            int exitAvailable=wait==WAIT_OBJECT_0 && GetExitCodeThread(thread,&exitCode);
            CloseHandle(thread);
            uint64_t elapsed=GetTickCount64()-begin;
            fprintf(log,"sleepTest startRoutine=kernelbase!Sleep arg=1500 elapsed=%llums wait=0x%lX exitAvailable=%d exit=0x%lX expected~1500\n",
                (unsigned long long)elapsed,(unsigned long)wait,exitAvailable,(unsigned long)exitCode);
            fprintf(log,"sleepTest verdict=%s\n", elapsed<200 ? "startRoutineNotExecuted" : "routineExecuted");
        }
        fflush(log);
    }

    // ---- Stage 2: prove a stub can execute, one execution per slot ----
    int executed=0;
    ctx.method=1;
    {
        uint64_t slot=arm_slot(&ctx,0,0,0);
        if (!slot) goto done;
        int observed=execute_once(&ctx,ctx.codeExec,slot,8000,NULL);
        remote_loader_data state;
        int read=state_of(&ctx,slot,&state);
        fprintf(log,"crt minimalStub observed=%d readback=%d phase=%lu\n",
            observed,read,(unsigned long)(read ? state.phase : 0)); fflush(log);
        if (read && state.phase==42) executed=1;
    }
    if (!executed) {
        DWORD tids[MAX_THREADS]; unsigned count=pick_threads(pid,tids,MAX_THREADS);
        fprintf(log,"apc testing %u candidate thread(s) one at a time; each gets its own slot and a %ums wait\n",
            count,(unsigned)APC_WAIT_MS);
        for (unsigned i=0;i<count && !executed;i++) {
            ctx.method=2; ctx.tid=tids[i];
            uint64_t slot=arm_slot(&ctx,0,0,0);
            if (!slot) break;
            uint64_t elapsed=0;
            int observed=execute_once(&ctx,ctx.codeExec,slot,APC_WAIT_MS,&elapsed);
            remote_loader_data state;
            int read=state_of(&ctx,slot,&state);
            fprintf(log,"apc minimalStub tid=%lu observed=%d elapsed=%llums readback=%d phase=%lu\n",
                (unsigned long)tids[i],observed,(unsigned long long)elapsed,read,
                (unsigned long)(read ? state.phase : 0)); fflush(log);
            if (read && state.phase==42) executed=1;
            else if (read && state.phase==0) ctx.apcsPending++;
        }
    }
    if (!executed) {
        fprintf(log,"No stub executed on any route; watcher not started.\n");
        fprintf(log,"Note: sleepTest above shows whether CreateRemoteThread start routines run at all.\n");
        fprintf(log,"Unconsumed APCs stay queued in the client; close and restart it before any retry.\n");
        goto done;
    }
    if (ctx.method==1) fprintf(log,"executionRoute=CreateRemoteThread\n");
    else fprintf(log,"executionRoute=QueueUserAPC tid=%lu\n",(unsigned long)ctx.tid);
    fflush(log);

    // ---- Stage 2b: prove the call stubs actually reach the API ----
    {
        uint64_t slot=arm_slot(&ctx,remoteGetPid,0,0);
        remote_loader_data state; memset(&state,0,sizeof(state));
        int observed=slot && execute_once(&ctx,ctx.codeCall,slot,LOAD_WAIT_MS,NULL);
        int read=slot && state_of(&ctx,slot,&state);
        unsigned long gotPid=read ? (unsigned long)(uint32_t)state.result : 0;
        fprintf(log,"callVerify GetCurrentProcessId observed=%d phase=%lu returned=%lu expected=%lu %s\n",
            observed,(unsigned long)(read ? state.phase : 0),gotPid,(unsigned long)pid,
            (read && state.phase==2 && gotPid==pid) ? "matches" : "MISMATCH");
        fflush(log);
        if (!read || state.phase!=2 || gotPid!=pid) {
            fprintf(log,"The call stub did not return the expected value, so later loader errors cannot be trusted. Stopping.\n");
            goto done;
        }
    }

    // ---- Stage 2c: missing path should give 126, which tests whether the
    // error we read is really produced by this call ----
    {
        const wchar_t *missing=L"C:\\__wowb_probe_missing__.dll";
        if (write_exact(process,bogusPath,missing,(wcslen(missing)+1)*sizeof(wchar_t))) {
            uint64_t slot=arm_slot(&ctx,remoteLoad,remoteError,bogusPath);
            remote_loader_data state; memset(&state,0,sizeof(state));
            int observed=slot && execute_once(&ctx,ctx.codeLoad,slot,LOAD_WAIT_MS,NULL);
            int read=slot && state_of(&ctx,slot,&state);
            fprintf(log,"bogusLoad observed=%d phase=%lu module=0x%llX error=%lu (126=ERROR_MOD_NOT_FOUND expected)\n",
                observed,(unsigned long)(read ? state.phase : 0),
                (unsigned long long)(read ? state.result : 0),(unsigned long)(read ? state.error : 0));
            if (read && state.phase==2 && state.error!=126)
                fprintf(log,"bogusLoad error is not 126, so the reported code does not come from path resolution.\n");
            fflush(log);
        } else fprintf(log,"bogusPath write failed=%lu\n",(unsigned long)GetLastError());
    }

    // ---- Stage 3a: control load of a benign system DLL, once ----
    uint64_t controlDll=0; int controlIncomplete=0;
    {
        wchar_t control[MAX_PATH];
        UINT got=GetSystemDirectoryW(control,MAX_PATH);
        if (got && got+12<MAX_PATH) {
            wcscat(control,L"\\version.dll");
            if (write_exact(process,controlPath,control,(wcslen(control)+1)*sizeof(wchar_t))) {
                wchar_t back[MAX_PATH];
                if (read_exact(process,controlPath,back,sizeof(back)) && !wcscmp(back,control)) {
                    fprintf(log,"controlPath=%ls verified\n",control);
                    uint64_t slot=arm_slot(&ctx,remoteLoad,remoteError,controlPath);
                    remote_loader_data state;
                    int observed=slot && execute_once(&ctx,ctx.codeLoad,slot,LOAD_WAIT_MS,NULL);
                    if (slot && state_of(&ctx,slot,&state))
                        fprintf(log,"controlLoad observed=%d phase=%lu module=0x%llX error=%lu\n",
                            observed,(unsigned long)state.phase,(unsigned long long)state.result,
                            (unsigned long)state.error);
                    else { fprintf(log,"controlLoad state unreadable\n"); controlIncomplete=1; }
                    remote_loader_data s2;
                    if (slot && state_of(&ctx,slot,&s2)) {
                        controlDll=s2.result;
                        if (s2.phase==0) { controlIncomplete=1;
                            fprintf(log,"controlLoad phase=0: the APC was never delivered to that thread.\n"); }
                        else if (s2.phase==1) { controlIncomplete=1;
                            fprintf(log,"controlLoad phase=1: LoadLibraryW was entered and never returned on that thread (possible loader-lock deadlock).\n"); }
                    }
                    fflush(log);
                } else fprintf(log,"controlPath readback mismatch\n");
            } else fprintf(log,"controlPath write failed=%lu\n",(unsigned long)GetLastError());
        }
    }
    (void)controlDll;

    if (controlIncomplete) {
        fprintf(log,"Control APC never executed; stopping before the probe load so no second call can overlap it.\n");
        goto done;
    }

    // ---- Stage 3b: map the probe DLL by hand (no OS loader) ----
    {
        unsigned char *fileBytes=NULL; size_t fileSize=0; pe_image image; memset(&image,0,sizeof(image));
        uint64_t mapBase=0;
        if (!read_whole_file(dll,&fileBytes,&fileSize)) { fprintf(log,"map: cannot read %ls\n",dll); goto done; }
        int ok=pe_prepare(fileBytes,fileSize,&image,log);
        free(fileBytes);
        if (!ok) { pe_release(&image); goto done; }
        mapBase=ui(VirtualAllocEx(process,(void*)(uintptr_t)image.preferred,image.size,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
        int atPreferred=mapBase!=0;
        if (!mapBase) {
            mapBase=ui(VirtualAllocEx(process,NULL,image.size,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
            fprintf(log,"map: preferred base 0x%llX unavailable (%lu); using another address, relocations required\n",
                (unsigned long long)image.preferred,(unsigned long)GetLastError());
        }
        if (!mapBase) { fprintf(log,"map: VirtualAllocEx failed=%lu\n",(unsigned long)GetLastError()); pe_release(&image); goto done; }
        fprintf(log,"map: allocation atPreferredBase=%s base=0x%llX\n",atPreferred ? "true" : "false",
            (unsigned long long)mapBase);
        if (!pe_relocate(&image,mapBase,log)) { fprintf(log,"map: relocation stage failed\n"); pe_release(&image); goto done; }
        unsigned resolved=0, unresolved=0;
        if (!pe_imports(&image,pid,log,&resolved,&unresolved)) {
            fprintf(log,"map: import stage failed\n"); pe_release(&image); goto done;
        }
        if (unresolved) { fprintf(log,"map: %u imports unresolved; refusing to run a partial image\n",unresolved); pe_release(&image); goto done; }
        if (!write_exact(process,mapBase,image.data,image.size)) {
            fprintf(log,"map: image write failed=%lu\n",(unsigned long)GetLastError()); pe_release(&image); goto done;
        }
        unsigned badProtect=0;
        for (unsigned i=0;i<image.sections;i++) {
            if (!image.section[i].vsize) continue;
            DWORD want=(image.section[i].chars & 0x20000000u) ? PAGE_EXECUTE_READ :
                       ((image.section[i].chars & 0x80000000u) ? PAGE_READWRITE : PAGE_READONLY);
            DWORD old=0;
            if (!VirtualProtectEx(process,(void*)(uintptr_t)(mapBase+image.section[i].va),
                                  image.section[i].vsize,want,&old)) badProtect++;
        }
        FlushInstructionCache(process,(void*)(uintptr_t)mapBase,image.size);
        uint64_t startAddress=mapBase+startRva, stopAddress=mapBase+stopRva;
        unsigned char verify[16]; int entryRead=read_exact(process,mapBase+image.entry,verify,sizeof(verify));
        fprintf(log,"map: base=0x%llX writes=ok protectFailures=%u entryRva=0x%lX entryReadable=%d ProbeStartRva=0x%llX\n",
            (unsigned long long)mapBase,badProtect,(unsigned long)image.entry,entryRead,
            (unsigned long long)startRva);
        fflush(log);
        pe_release(&image);
        // The image entry point is the CRT wrapper; the probe needs neither it nor
        // DllMain, but run it once so a broken image shows up here rather than in
        // ProbeStart. A timeout means the image is wedged: stop.
        remote_loader_data entry; memset(&entry,0,sizeof(entry));
        entry.function=mapBase+image.entry; entry.arg=mapBase; entry.arg2=1; entry.arg3=0;
        uint64_t slot=arm_slot_full(&ctx,&entry);
        if (slot) {
            remote_loader_data state; memset(&state,0,sizeof(state));
            int observed=execute_once(&ctx,ctx.codeDllMain,slot,LOAD_WAIT_MS,NULL);
            int read=state_of(&ctx,slot,&state);
            fprintf(log,"mapEntry observed=%d phase=%lu returned=%lld\n",observed,
                (unsigned long)(read ? state.phase : 0),(long long)(read ? state.result : 0));
            fflush(log);
            if (read && state.phase==1) {
                fprintf(log,"mapEntry did not return; refusing to continue on a wedged image.\n");
                goto done;
            }
        }
        remoteDll=mapBase; loaded=1;
        startRva=startAddress-mapBase; stopRva=stopAddress-mapBase;
    }
    if (!loaded) { fprintf(log,"manual map failed; watcher not started.\n"); goto done; }
    fprintf(log,"mappedImage=0x%llX (manually mapped; not registered with the loader)\n",
        (unsigned long long)remoteDll);
    (void)loaderErrorSlot;
    fflush(log);
    if (!player_anchor_current(player_read,process,imageBase,&player)) { fprintf(log,"Player anchor changed before probe start; refusing.\n"); goto done; }

    // ---- Stage 4: ProbeStart, once ----
    {
        uint64_t slot=arm_slot(&ctx,remoteDll+startRva,0,configPage);
        if (!slot) goto done;
        int observed=execute_once(&ctx,ctx.codeCall,slot,LOAD_WAIT_MS,NULL);
        remote_loader_data state;
        int read=state_of(&ctx,slot,&state);
        fprintf(log,"probeStart observed=%d readback=%d phase=%lu result=%lu\n",
            observed,read,(unsigned long)(read ? state.phase : 0),(unsigned long)(read ? (uint32_t)state.result : 0));
        fflush(log);
        if (!read || state.phase!=2) { fprintf(log,"ProbeStart did not complete; watcher not started.\n"); goto done; }
        if ((uint32_t)state.result!=0) { fprintf(log,"ProbeStart refused with code=%lu\n",(unsigned long)(uint32_t)state.result); goto done; }
        started=1;
    }

    puts("ACTIVE: return to the WoW window and right-click ground destinations NOW. Waiting 10 seconds..."); fflush(stdout);
    uint64_t begin=GetTickCount64(); fprintf(log,"ACTIVE tick=%llu window=10000ms\n",(unsigned long long)begin); fflush(log);
    int rightDown=0;
    for (;;) {
        uint64_t tick=GetTickCount64();
        if (tick-begin>=10000) break;
        if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"Target process exited before 10 seconds elapsed.\n"); break; }
        DWORD fgPid=0; HWND foreground=GetForegroundWindow();
        if (foreground) GetWindowThreadProcessId(foreground,&fgPid);
        if (fgPid==pid) {
            int isDown=(GetAsyncKeyState(VK_RBUTTON)&0x8000)!=0;
            if (isDown!=rightDown) {
                fprintf(log,"RIGHT_%s t=%llums (sampled physical state, WoW foreground)\n",isDown ? "DOWN" : "UP",
                    (unsigned long long)(tick-begin));
                rightDown=isDown;
            }
        } else if (rightDown) {
            fprintf(log,"RIGHT_TRACKING_LOST_FOCUS t=%llums (no release observed)\n",(unsigned long long)(tick-begin));
            rightDown=0;
        }
        if (read_exact(process,configPage,buf,sizeof(*buf))) {
            LONG limit=buf->event_count; if (limit>MAX_EVENTS) limit=MAX_EVENTS; if (limit<0) limit=0;
            while (logged<limit) {
                probe_event *e=&buf->events[logged];
                if (e->sequence!=(uint64_t)logged+1) break; // possibly still being written
                print_event(log,process,e,begin,imageBase,imageSize,modules,moduleCount); logged++;
            }
        } else readFailures++;
        fflush(log);
        Sleep(10);
    }
    fprintf(log,"OBSERVATION_ENDED elapsed=%llums logged=%ld readFailures=%ld\n",
        (unsigned long long)(GetTickCount64()-begin),logged,readFailures); fflush(log);
    if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"Client exited; no stop call possible.\n"); goto done; }
    {
        uint64_t slot=arm_slot(&ctx,remoteDll+stopRva,0,configPage);
        remote_loader_data state;
        slot && execute_once(&ctx,ctx.codeCall,slot,LOAD_WAIT_MS,NULL);
        int read=slot && state_of(&ctx,slot,&state);
        if (!read || state.phase!=2) { fprintf(log,"ProbeStop failed or timed out\n"); goto done; }
        stopped=1;
        fprintf(log,"ProbeStop status=%lu\n",(unsigned long)(uint32_t)state.result); fflush(log);
        if (!read_exact(process,configPage,buf,sizeof(*buf))) { fprintf(log,"Final probe data unreadable=%lu\n",(unsigned long)GetLastError()); goto done; }
        LONG limit=buf->event_count; if (limit>MAX_EVENTS) limit=MAX_EVENTS; if (limit<0) limit=0;
        while (logged<limit) {
            probe_event *e=&buf->events[logged];
            if (e->sequence!=(uint64_t)logged+1) { fprintf(log,"event%ld incomplete at stop\n",logged); break; }
            print_event(log,process,e,begin,imageBase,imageSize,modules,moduleCount); logged++;
        }
        fprintf(log,"active=%ld pages=%ld guardFaults=%ld writeFaults=%ld nearWrites=%ld events=%ld logged=%ld capped=%s rearmErrors=%ld restoreErrors=%ld\n",
            buf->active,buf->page_count,buf->guard_faults,buf->write_faults,buf->nearby_writes,buf->event_count,logged,
            buf->event_count>MAX_EVENTS ? "true" : "false",buf->rearm_errors,buf->restore_errors);
        result=((uint32_t)state.result==0 && !buf->rearm_errors && !buf->restore_errors) ? 0 : 1;
    }
done:
    // Deliberately do not free target memory or unload DLL. Handlers/single-step
    // or pending APCs may reference them after a timeout or during cleanup.
    if (process) CloseHandle(process);
    fprintf(log,"loaded=%d started=%d stopped=%d status=%d route=%s slotsUsed=%u pendingApcs=%u. Restart target before reuse.\n",
        loaded,started,stopped,result,
        ctx.method==1 ? "CreateRemoteThread" : (ctx.method==2 ? "QueueUserAPC" : "none"),
        ctx.slotsUsed,ctx.apcsPending);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,logpath,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    else puts("Report is in C:\\ghidra-scripts (timestamped WowBCtmQuickProbe file).");
    puts("Restart the client after this experiment.");
    free(buf); return result;
}

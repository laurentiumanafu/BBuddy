// Safe loader diagnostic: operates on itself and a short-lived child it owns.
// Never opens, debugs, or injects into WoW. Does NOT call ProbeStart.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

static uint64_t ui(const void *p) { return (uint64_t)(uintptr_t)p; }
static uint64_t module_base(DWORD pid, const wchar_t *name, DWORD *err) {
    HANDLE snap=INVALID_HANDLE_VALUE;
    for (unsigned i=0;i<3;i++) {
        snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
        if (snap!=INVALID_HANDLE_VALUE || GetLastError()!=ERROR_BAD_LENGTH) break;
        Sleep(20);
    }
    if (snap==INVALID_HANDLE_VALUE) { if(err) *err=GetLastError(); return 0; }
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); uint64_t found=0;
    if (Module32FirstW(snap,&e)) do {
        if (_wcsicmp(e.szModule,name)==0 || _wcsicmp(e.szExePath,name)==0) { found=ui(e.modBaseAddr); break; }
    } while (Module32NextW(snap,&e));
    if (err) *err=found ? 0 : GetLastError(); CloseHandle(snap); return found;
}
static uint64_t remote_function(DWORD pid, FARPROC local, wchar_t *moduleName, size_t capacity, DWORD *error) {
    HMODULE module=NULL; wchar_t full[MAX_PATH];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(uintptr_t)local,&module)) { *error=GetLastError(); return 0; }
    if (!GetModuleFileNameW(module,full,MAX_PATH)) { *error=GetLastError(); return 0; }
    wchar_t *base=wcsrchr(full,L'\\'); base=base ? base+1 : full;
    if (wcslen(base)+1>capacity) { *error=ERROR_INSUFFICIENT_BUFFER; return 0; }
    wcscpy(moduleName,base);
    uint64_t remote=module_base(pid,base,error);
    return remote ? remote+(ui((void*)local)-ui(module)) : 0;
}
static void show_report(const wchar_t *path) {
    FILE *in=_wfopen(path,L"rb");
    if (!in) { puts("Could not reopen diagnostic report; check C:\\ghidra-scripts."); return; }
    char buf[4096]; size_t n;
    while ((n=fread(buf,1,sizeof(buf),in))>0) fwrite(buf,1,n,stdout);
    fclose(in);
}
int main(int argc, char **argv) {
    if (argc==3 && strcmp(argv[1],"--helper")==0) {
        HANDLE ready=OpenEventA(EVENT_MODIFY_STATE,FALSE,argv[2]);
        if (!ready) return 3;
        SetEvent(ready); CloseHandle(ready); Sleep(15000); return 0;
    }
    if (argc!=1) { puts("Usage: WowBCtmQuickProbeDoctor.exe"); return 2; }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME t; GetLocalTime(&t); wchar_t report[MAX_PATH];
    swprintf(report,MAX_PATH,L"C:\\ghidra-scripts\\WowBCtmQuickDoctor-%04u%02u%02u-%02u%02u%02u-%03u.txt",
        t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);
    FILE *log=_wfopen(report,L"w"); if (!log) { puts("Cannot create report in C:\\ghidra-scripts."); return 1; }
    fprintf(log,"WOWB QUICK PROBE LOADER DIAGNOSTIC v1\n");
    fprintf(log,"Runs only in this process and its temporary helper. Does not attach to WoW or call ProbeStart.\n");
    fprintf(log,"Parent PID=%lu bitness=%zu\n",GetCurrentProcessId(),sizeof(void*)*8); fflush(log);
    int result=1; HANDLE ready=NULL, thread=NULL, child=NULL, childThread=NULL; void *remotePath=NULL;
    wchar_t exe[MAX_PATH], dll[MAX_PATH], eventName[128], cmd[MAX_PATH*2+180];
    DWORD n=GetModuleFileNameW(NULL,exe,MAX_PATH);
    if (!n || n>=MAX_PATH) { fprintf(log,"FAIL GetModuleFileNameW=%lu\n",GetLastError()); goto done; }
    wcscpy(dll,exe); wchar_t *slash=wcsrchr(dll,L'\\');
    if (!slash) { fprintf(log,"FAIL no program directory\n"); goto done; }
    *(slash+1)=0;
    if (wcslen(dll)+wcslen(L"WowBCtmQuickProbe.dll")>=MAX_PATH) { fprintf(log,"FAIL DLL path too long\n"); goto done; }
    wcscat(dll,L"WowBCtmQuickProbe.dll");
    WIN32_FILE_ATTRIBUTE_DATA info;
    if (!GetFileAttributesExW(dll,GetFileExInfoStandard,&info)) {
        fprintf(log,"FAIL dll_missing_or_unreadable win32=%lu\n",GetLastError()); goto done;
    }
    fprintf(log,"dll_file_present=true size=%llu\n",((unsigned long long)info.nFileSizeHigh<<32)|info.nFileSizeLow);
    HMODULE mapped=LoadLibraryExW(dll,NULL,DONT_RESOLVE_DLL_REFERENCES);
    if (!mapped) { fprintf(log,"metadata_map=false win32=%lu\n",GetLastError()); goto done; }
    FARPROC metaStart=GetProcAddress(mapped,"ProbeStart"), metaStop=GetProcAddress(mapped,"ProbeStop");
    fprintf(log,"metadata_map=true export_ProbeStart=%s export_ProbeStop=%s\n",metaStart?"present":"missing",metaStop?"present":"missing");
    FreeLibrary(mapped);
    if (!metaStart || !metaStop) goto done;
    // Unlike DONT_RESOLVE, this does normal import resolution and DllMain in
    // the diagnostic process only. The probe's DllMain does not start a guard.
    HMODULE local=LoadLibraryW(dll);
    if (!local) { DWORD e=GetLastError(); fprintf(log,"local_normal_load=false win32=%lu\n",e); goto done; }
    fprintf(log,"local_normal_load=true\n"); FreeLibrary(local); fflush(log);
    swprintf(eventName,128,L"Local\\WowBQuickDoctor-%lu-%llu",GetCurrentProcessId(),(unsigned long long)GetTickCount64());
    ready=CreateEventW(NULL,TRUE,FALSE,eventName);
    if (!ready) { fprintf(log,"FAIL readiness_event win32=%lu\n",GetLastError()); goto done; }
    int len=swprintf(cmd,sizeof(cmd)/sizeof(cmd[0]),L"\"%ls\" --helper \"%ls\"",exe,eventName);
    if (len<0 || (size_t)len>=sizeof(cmd)/sizeof(cmd[0])) { fprintf(log,"FAIL helper command too long\n"); goto done; }
    STARTUPINFOW si; PROCESS_INFORMATION pi; ZeroMemory(&si,sizeof(si)); ZeroMemory(&pi,sizeof(pi)); si.cb=sizeof(si);
    if (!CreateProcessW(exe,cmd,NULL,NULL,FALSE,CREATE_NO_WINDOW,NULL,NULL,&si,&pi)) {
        fprintf(log,"helper_create=false win32=%lu\n",GetLastError()); goto done;
    }
    child=pi.hProcess; childThread=pi.hThread;
    DWORD wait=WaitForSingleObject(ready,10000);
    if (wait!=WAIT_OBJECT_0) { fprintf(log,"helper_ready=false wait=%lu\n",wait); goto done; }
    fprintf(log,"helper_ready=true helper_pid=%lu\n",pi.dwProcessId); fflush(log);
    FARPROC localLoad=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
    wchar_t module[128]=L""; DWORD error=0;
    uint64_t remoteLoad=localLoad ? remote_function(pi.dwProcessId,localLoad,module,128,&error) : 0;
    if (!remoteLoad) { fprintf(log,"remote_load_resolve=false win32=%lu\n",error); goto done; }
    fprintf(log,"remote_load_resolve=true local_export_module=%ls\n",module);
    SIZE_T pathBytes=(wcslen(dll)+1)*sizeof(wchar_t), wrote=0, got=0;
    remotePath=VirtualAllocEx(child,NULL,pathBytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if (!remotePath || !WriteProcessMemory(child,remotePath,dll,pathBytes,&wrote) || wrote!=pathBytes) {
        fprintf(log,"helper_path_write=false win32=%lu bytes=%zu/%zu\n",GetLastError(),(size_t)wrote,(size_t)pathBytes); goto done;
    }
    wchar_t *copy=(wchar_t*)malloc(pathBytes);
    if (!copy) { fprintf(log,"FAIL path_verify_allocation\n"); goto done; }
    BOOL read=ReadProcessMemory(child,remotePath,copy,pathBytes,&got);
    int equal=read && got==pathBytes && memcmp(copy,dll,pathBytes)==0;
    free(copy);
    fprintf(log,"helper_path_write=true helper_path_readback=%s\n",equal?"match":"failure");
    if (!equal) goto done;
    thread=CreateRemoteThread(child,NULL,0,(LPTHREAD_START_ROUTINE)(uintptr_t)remoteLoad,remotePath,0,NULL);
    if (!thread) { fprintf(log,"helper_remote_thread=false win32=%lu\n",GetLastError()); goto done; }
    wait=WaitForSingleObject(thread,10000);
    if (wait!=WAIT_OBJECT_0) { fprintf(log,"helper_remote_thread_complete=false wait=%lu\n",wait); goto done; }
    DWORD ret=0; BOOL gotExit=GetExitCodeThread(thread,&ret);
    fprintf(log,"helper_remote_thread_complete=true exitCodeAvailable=%s low32=0x%lX (not full x64 module address)\n",
        gotExit?"true":"false",(unsigned long)ret);
    uint64_t remoteDll=module_base(pi.dwProcessId,dll,&error);
    fprintf(log,"helper_remote_dll_found=%s module_scan_error=%lu\n",remoteDll?"true":"false",error);
    if (remoteDll) result=0;
done:
    if (thread) CloseHandle(thread);
    if (child) {
        // Only our disposable helper is terminated; never touch the game.
        // Terminate before freeing its memory: on a timeout its loader thread
        // could still be reading the DLL path. Process exit releases memory.
        TerminateProcess(child,0); WaitForSingleObject(child,5000); CloseHandle(child);
    }
    if (childThread) CloseHandle(childThread);
    if (ready) CloseHandle(ready);
    fprintf(log,"diagnostic_status=%d; helper was disposable; no WoW process was accessed.\n",result);
    fclose(log); show_report(report);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,report,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    return result;
}

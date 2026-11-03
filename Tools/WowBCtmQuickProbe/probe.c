// Intrusive one-shot injected research probe. The DLL intentionally remains loaded.
// PAGE_GUARD observes page-level accesses, not every actual access, and is racy.
#define WIN32_LEAN_AND_MEAN
#include "probe_shared.h"

static probe_config *g;
static PVOID veh;
static DWORD tls = TLS_OUT_OF_INDEXES;

static int is_near(uint64_t addr) {
    for (uint32_t i=0; i<g->target_count; i++) {
        uint64_t x=g->targets[i];
        if ((addr >= x ? addr-x : x-addr) <= NEAR_RANGE) return 1;
    }
    return 0;
}
static LONG CALLBACK on_exception(EXCEPTION_POINTERS *ep) {
    if (!g || !ep || !ep->ExceptionRecord || !ep->ContextRecord) return EXCEPTION_CONTINUE_SEARCH;
    DWORD code=ep->ExceptionRecord->ExceptionCode;
    CONTEXT *c=ep->ContextRecord;
    if (code == EXCEPTION_SINGLE_STEP) {
        uintptr_t marker=(uintptr_t)TlsGetValue(tls);
        if (!marker) return EXCEPTION_CONTINUE_SEARCH;
        TlsSetValue(tls, NULL);
        unsigned index=(unsigned)((marker & 7u)-1u);
        if (index < (unsigned)g->page_count && InterlockedCompareExchange(&g->active,0,0)) {
            MEMORY_BASIC_INFORMATION m; DWORD old;
            if (!VirtualQuery((void*)(uintptr_t)g->pages[index], &m, sizeof(m)) ||
                m.State != MEM_COMMIT || (uint64_t)(uintptr_t)m.AllocationBase != g->allocations[index] ||
                (m.Protect & ~PAGE_GUARD) != g->protections[index] ||
                !VirtualProtect((void*)(uintptr_t)g->pages[index], 4096, g->protections[index] | PAGE_GUARD, &old))
                InterlockedIncrement(&g->rearm_errors);
        }
        if (!(marker & 8u)) c->EFlags &= ~0x100u;
        return (marker & 8u) ? EXCEPTION_CONTINUE_SEARCH : EXCEPTION_CONTINUE_EXECUTION;
    }
    if (code != STATUS_GUARD_PAGE_VIOLATION || ep->ExceptionRecord->NumberParameters < 2)
        return EXCEPTION_CONTINUE_SEARCH;
    uint64_t address=(uint64_t)ep->ExceptionRecord->ExceptionInformation[1];
    unsigned index;
    for (index=0; index<(unsigned)g->page_count; index++)
        if (address >= g->pages[index] && address-g->pages[index] < 4096) break;
    if (index == (unsigned)g->page_count) return EXCEPTION_CONTINUE_SEARCH;
    InterlockedIncrement(&g->guard_faults);
    if (!InterlockedCompareExchange(&g->active,0,0)) return EXCEPTION_CONTINUE_EXECUTION;
    if (ep->ExceptionRecord->ExceptionInformation[0] == 1) {
        InterlockedIncrement(&g->write_faults);
        if (is_near(address)) {
            InterlockedIncrement(&g->nearby_writes);
            LONG n=InterlockedIncrement(&g->event_count)-1;
            if (n >= 0 && n < MAX_EVENTS) {
                probe_event *e=&g->events[n];
                e->tick_ms=GetTickCount64(); e->tid=GetCurrentThreadId(); e->address=address; e->rip=c->Rip; e->rsp=c->Rsp;
                e->rax=c->Rax; e->rcx=c->Rcx; e->rdx=c->Rdx; e->rbx=c->Rbx; e->rbp=c->Rbp; e->rsi=c->Rsi; e->rdi=c->Rdi;
                e->r8=c->R8; e->r9=c->R9; e->r10=c->R10; e->r11=c->R11; e->r12=c->R12; e->r13=c->R13; e->r14=c->R14; e->r15=c->R15;
                InterlockedExchange64((volatile LONG64*)&e->sequence, (LONG64)n+1);
            }
        }
    }
    // A per-thread marker identifies our own re-arm single-step. Preserve preexisting TF.
    if (TlsGetValue(tls) != NULL) { InterlockedIncrement(&g->rearm_errors); return EXCEPTION_CONTINUE_EXECUTION; }
    uintptr_t marker=(index+1u) | ((c->EFlags & 0x100u) ? 8u : 0u);
    if (!TlsSetValue(tls, (void*)marker)) { InterlockedIncrement(&g->rearm_errors); return EXCEPTION_CONTINUE_EXECUTION; }
    c->EFlags |= 0x100u;
    return EXCEPTION_CONTINUE_EXECUTION;
}

__declspec(dllexport) DWORD WINAPI ProbeStart(void *arg) {
    probe_config *p=(probe_config*)arg;
    if (!p || p->magic != PROBE_MAGIC || p->size != sizeof(*p) || p->target_count == 0 || p->target_count > MAX_TARGETS || g)
        return 1;
    SYSTEM_INFO si; GetSystemInfo(&si);
    if (si.dwPageSize != 4096) return 2;
    tls=TlsAlloc(); if (tls == TLS_OUT_OF_INDEXES) return 3;
    // Validate all pages before modifying any of them.
    for (unsigned i=0; i<p->target_count; i++) {
        uint64_t page=p->targets[i]&~4095ULL; int seen=0;
        for (int j=0; j<p->page_count; j++) if (p->pages[j] == page) seen=1;
        if (seen) continue;
        MEMORY_BASIC_INFORMATION m;
        if (!VirtualQuery((void*)(uintptr_t)page, &m, sizeof(m)) || m.State != MEM_COMMIT ||
            (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || (m.Protect & 0xff) == PAGE_EXECUTE ||
            (uint64_t)(uintptr_t)m.BaseAddress > page || m.RegionSize < 4096 ||
            page-(uint64_t)(uintptr_t)m.BaseAddress > m.RegionSize-4096) { TlsFree(tls); tls=TLS_OUT_OF_INDEXES; return 4; }
        int n=p->page_count++; p->pages[n]=page; p->allocations[n]=(uint64_t)(uintptr_t)m.AllocationBase; p->protections[n]=m.Protect;
    }
    g=p;
    veh=AddVectoredExceptionHandler(1,on_exception);
    if (!veh) { g=NULL; TlsFree(tls); tls=TLS_OUT_OF_INDEXES; return 5; }
    InterlockedExchange(&p->active, 1);
    for (int i=0; i<p->page_count; i++) {
        DWORD old;
        BOOL changed=VirtualProtect((void*)(uintptr_t)p->pages[i], 4096, p->protections[i] | PAGE_GUARD, &old);
        if (!changed || old != p->protections[i]) {
            if (changed) { DWORD ignored; VirtualProtect((void*)(uintptr_t)p->pages[i],4096,old,&ignored); }
            InterlockedIncrement(&p->rearm_errors);
            InterlockedExchange(&p->active,0);
            for (int j=0; j<i; j++) { MEMORY_BASIC_INFORMATION m; if (VirtualQuery((void*)(uintptr_t)p->pages[j], &m, sizeof(m)) &&
                (uint64_t)(uintptr_t)m.AllocationBase == p->allocations[j] && m.Protect == (p->protections[j] | PAGE_GUARD))
                VirtualProtect((void*)(uintptr_t)p->pages[j],4096,p->protections[j],&old); }
            // Leave VEH + DLL + TLS + config alive; a racing handler may be using them.
            return 6;
        }
    }
    return 0;
}

__declspec(dllexport) DWORD WINAPI ProbeStop(void *arg) {
    probe_config *p=(probe_config*)arg;
    if (!p || p != g || p->magic != PROBE_MAGIC) return 1;
    InterlockedExchange(&p->active,0);
    for (int i=0; i<p->page_count; i++) {
        MEMORY_BASIC_INFORMATION m; DWORD old;
        if (!VirtualQuery((void*)(uintptr_t)p->pages[i], &m, sizeof(m)) || m.State != MEM_COMMIT ||
            (uint64_t)(uintptr_t)m.AllocationBase != p->allocations[i]) { InterlockedIncrement(&p->restore_errors); continue; }
        if (m.Protect == (p->protections[i] | PAGE_GUARD)) {
            if (!VirtualProtect((void*)(uintptr_t)p->pages[i], 4096, p->protections[i], &old)) InterlockedIncrement(&p->restore_errors);
        } else if (m.Protect != p->protections[i]) InterlockedIncrement(&p->restore_errors);
    }
    // Deliberately keep handler, TLS, DLL and config until process exit: any
    // in-flight re-arm single-step must still reach valid code and storage.
    Sleep(250);
    return p->restore_errors ? 2 : 0;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)instance; (void)reason; (void)reserved; return TRUE;
}

// Linux/x86-64 offline test of the actual Windows-ABI stub bytes; not a Windows integration test.
#define _GNU_SOURCE
#include <sys/mman.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "remote_stubs.h"

static int loadCalls, errorCalls, callCalls;
static uint64_t loadResult;
static uint64_t expectArg;

static uint64_t __attribute__((ms_abi)) fake_load(uint64_t path) {
    ++loadCalls; return path==expectArg ? loadResult : 0;
}
static uint32_t __attribute__((ms_abi)) fake_error(void) { ++errorCalls; return 126; }
static uint64_t __attribute__((ms_abi)) fake_call(uint64_t arg) { ++callCalls; return arg==expectArg ? 0x1234 : 0xBAD; }

static uint32_t ldrCalls, ldrMode; static uint64_t ldrBase;
static uint32_t dmCalls; static uint64_t dmHinst, dmReason, dmReserved;
static uint64_t __attribute__((ms_abi)) fake_dllmain(uint64_t hinst, uint64_t reason, uint64_t reserved) {
    ++dmCalls; dmHinst=hinst; dmReason=reason; dmReserved=reserved; return 1;
}
// NTSTATUS LdrLoadDll(PWSTR, PULONG, PUNICODE_STRING, PVOID*)
static uint32_t __attribute__((ms_abi)) fake_ldr(uint64_t search, uint64_t flags, uint64_t us, uint64_t baseOut) {
    ++ldrCalls;
    if (search || flags || !us || !baseOut) return 0xC000000D;
    const unsigned char *raw=(const unsigned char *)(uintptr_t)us;
    if (*(const uint16_t*)(raw) != 6 || *(const uint16_t*)(raw+2) != 8) return 0xC000000D;
    if (*(const uint64_t*)(raw+8) != 0x1234ULL) return 0xC000000D;
    if (ldrMode==0) { *(uint64_t*)(uintptr_t)baseOut=ldrBase; return 0; }
    return 0xC0000135; // STATUS_DLL_NOT_FOUND, leaves base untouched
}

typedef uint64_t (__attribute__((ms_abi)) *stub_fn)(remote_loader_data*);
static stub_fn map(const unsigned char *code, size_t n) {
    void *memory=mmap(NULL,4096,PROT_READ|PROT_WRITE,MAP_PRIVATE|MAP_ANONYMOUS,-1,0);
    if (memory==MAP_FAILED) return NULL;
    memcpy(memory,code,n);
    if (mprotect(memory,4096,PROT_READ|PROT_EXEC)) return NULL;
    return (stub_fn)memory;
}

int main(void) {
    stub_fn minimal=map(remote_minimal_code,sizeof(remote_minimal_code));
    stub_fn call=map(remote_call_code,sizeof(remote_call_code));
    stub_fn load=map(remote_loader_code,sizeof(remote_loader_code));
    if (!minimal || !call || !load) return 1;

    remote_loader_data d={0};
    if (minimal(&d)!=0 || d.phase!=42) return 2;

    memset(&d,0,sizeof(d)); expectArg=0x55; d.function=(uint64_t)(uintptr_t)fake_call; d.arg=expectArg;
    if (call(&d)!=0 || d.phase!=2 || (uint32_t)d.result!=0x1234 || callCalls!=1) return 3;

    memset(&d,0,sizeof(d)); expectArg=0xABCDEF; d.function=(uint64_t)(uintptr_t)fake_load;
    d.get_error=(uint64_t)(uintptr_t)fake_error; d.arg=expectArg; loadResult=0x7FFA12340000ULL;
    if (load(&d)!=0 || d.phase!=2 || d.result!=loadResult || d.error || loadCalls!=1 || errorCalls) return 4;

    memset(&d,0,sizeof(d)); d.function=(uint64_t)(uintptr_t)fake_load;
    d.get_error=(uint64_t)(uintptr_t)fake_error; d.arg=expectArg; loadResult=0;
    if (load(&d)!=0 || d.phase!=2 || d.result || d.error!=126 || loadCalls!=2 || errorCalls!=1) return 5;

    stub_fn ldr=map(remote_ldr_code,sizeof(remote_ldr_code));
    if (!ldr) return 7;
    memset(&d,0,sizeof(d)); ldrCalls=0; ldrMode=0; ldrBase=0x7FFB12340000ULL;
    d.function=(uint64_t)(uintptr_t)fake_ldr; d.unicode_length=6; d.unicode_max=8;
    d.unicode_buffer=0x1234ULL; d.base_out=0xDEADBEEFULL;
    if (ldr(&d)!=0 || d.phase!=2 || d.error || d.result!=ldrBase || ldrCalls!=1) return 8;
    memset(&d,0,sizeof(d)); ldrMode=1; d.function=(uint64_t)(uintptr_t)fake_ldr;
    d.unicode_length=6; d.unicode_max=8; d.unicode_buffer=0x1234ULL;
    if (ldr(&d)!=0 || d.phase!=2 || d.error!=0xC0000135u || d.result!=0 || ldrCalls!=2) return 9;

    stub_fn dm=map(remote_dllmain_code,sizeof(remote_dllmain_code));
    if (!dm) return 10;
    memset(&d,0,sizeof(d)); dmCalls=0;
    d.function=(uint64_t)(uintptr_t)fake_dllmain; d.arg=0x7FFA00010000ULL; d.arg2=1; d.arg3=0;
    if (dm(&d)!=0 || d.phase!=2 || d.result!=1 || dmCalls!=1) return 11;
    if (dmHinst!=0x7FFA00010000ULL || dmReason!=1 || dmReserved!=0) return 12;

    // Calling stub must also record a zero return without treating it as failure.
    memset(&d,0,sizeof(d)); expectArg=0; d.function=(uint64_t)(uintptr_t)fake_call; d.arg=0;
    if (call(&d)!=0 || d.phase!=2 || (uint32_t)d.result!=0x1234 || callCalls!=2) return 6;
    puts("remote stubs (minimal/call/loader/ldr/dllmain success+error) passed (offline)");
    return 0;
}

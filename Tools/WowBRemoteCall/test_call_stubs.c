// Offline test of the call stubs through an ms_abi harness: verifies argument
// register assignment (RCX,RDX,R8,R9), result capture and the 16-byte stack
// alignment the Windows ABI requires at the call site.
#define _GNU_SOURCE
#include <sys/mman.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "call_stubs.h"

typedef uint64_t (__attribute__((ms_abi)) *stub_fn)(call_data *);

static stub_fn map(const unsigned char *code, size_t n) {
    void *m=mmap(NULL,4096,PROT_READ|PROT_WRITE,MAP_PRIVATE|MAP_ANONYMOUS,-1,0);
    if (m==MAP_FAILED) return NULL;
    memcpy(m,code,n);
    if (mprotect(m,4096,PROT_READ|PROT_EXEC)) return NULL;
    return (stub_fn)m;
}

static int calls; static uint64_t seen[4]; static int aligned;
static uint64_t __attribute__((ms_abi)) target(uint64_t a,uint64_t b,uint64_t c,uint64_t d) {
    ++calls;
    seen[0]=a; seen[1]=b; seen[2]=c; seen[3]=d;
    uintptr_t sp; __asm__ volatile("mov %%rsp,%0":"=r"(sp));
    aligned = ((sp + 8) % 16) == 0;   // +8 for the return address pushed by the call
    return a ^ (b<<8) ^ (c<<16) ^ (d<<24);
}

int main(void) {
    stub_fn minimal=map(call_minimal_code,sizeof(call_minimal_code));
    stub_fn four=map(call_four_code,sizeof(call_four_code));
    if (!minimal || !four) return 1;
    call_data d; memset(&d,0,sizeof(d));
    if (minimal(&d)!=0 || d.phase!=42) return 2;
    memset(&d,0,sizeof(d));
    d.fn=(uint64_t)(uintptr_t)target; d.a1=0x11; d.a2=0x22; d.a3=0x33; d.a4=0x44;
    uint64_t expect=0x11u ^ (0x22u<<8) ^ (0x33u<<16) ^ (0x44u<<24);
    uint64_t ret=four(&d);
    if (ret!=0 || d.phase!=2) return 3;
    if (d.result!=expect) { printf("result %llX != %llX\n",(unsigned long long)d.result,(unsigned long long)expect); return 4; }
    if (seen[0]!=0x11||seen[1]!=0x22||seen[2]!=0x33||seen[3]!=0x44) { printf("args %llX %llX %llX %llX\n",
        (unsigned long long)seen[0],(unsigned long long)seen[1],(unsigned long long)seen[2],(unsigned long long)seen[3]); return 5; }
    if (!aligned) { puts("stack misaligned at call site"); return 6; }
    if (calls!=1) return 7;
    puts("call stubs passed (args, result, alignment, single invocation)");
    return 0;
}

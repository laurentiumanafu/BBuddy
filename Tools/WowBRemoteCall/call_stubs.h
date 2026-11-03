#ifndef CALL_STUBS_H
#define CALL_STUBS_H
// Stubs executed inside the target through the APC route (the only route that
// has ever executed in this client). Windows x64 ABI: RBX preserved, shadow
// space reserved and RSP 16-byte aligned at the call site.
#include <stdint.h>
#include <stddef.h>

typedef struct call_data {
    uint64_t fn;        // +0  target function address
    uint64_t a1;        // +8  RCX
    uint64_t a2;        // +16 RDX
    uint64_t a3;        // +24 R8
    uint64_t a4;        // +32 R9
    uint64_t result;    // +40 RAX after the call
    uint32_t phase;     // +48 0 = not entered, 1 = inside call, 2 = completed
    uint32_t reserved;  // +52
} call_data;

_Static_assert(offsetof(call_data, a1) == 8, "arg1");
_Static_assert(offsetof(call_data, result) == 40, "result");
_Static_assert(offsetof(call_data, phase) == 48, "phase");
_Static_assert(sizeof(call_data) == 56, "size");

// write 42 to phase then return - proves the stub executed at all.
static const unsigned char call_minimal_code[] = {
    0xc7,0x41,0x30,0x2a,0,0,0, 0xc3
};

// push rbx ; sub rsp,0x30 ; rbx=rcx ; phase=1
// 0x30 keeps RSP 16-byte aligned at the call: push(8)+0x30 makes RSP%16==0
// before the call, so the callee sees the ABI-required RSP%16==8.
// rcx=a1 ; rdx=a2 ; r8=a3 ; r9=a4 ; call [rbx] ; result=rax ; phase=2 ; ret 0
static const unsigned char call_four_code[] = {
    0x53,
    0x48,0x83,0xec,0x30,
    0x48,0x89,0xcb,
    0xc7,0x43,0x30,1,0,0,0,
    0x48,0x8b,0x4b,0x08,
    0x48,0x8b,0x53,0x10,
    0x4c,0x8b,0x43,0x18,
    0x4c,0x8b,0x4b,0x20,
    0xff,0x13,
    0x48,0x89,0x43,0x28,
    0xc7,0x43,0x30,2,0,0,0,
    0x31,0xc0,
    0x48,0x83,0xc4,0x30,
    0x5b,
    0xc3
};
#endif

#ifndef REMOTE_STUBS_H
#define REMOTE_STUBS_H
// x64 remote stubs, executed in the target by CreateRemoteThread or QueueUserAPC.
//
// All stubs follow the Windows x64 ABI: they preserve RBX, reserve 32 bytes of
// shadow space and keep RSP 16-byte aligned at every call site. The single
// argument (RCX) is the address of a remote_loader_data record.
//
//   remote_minimal_code : writes phase = 42 and returns. Proves only that the
//                         stub executed at all.
//   remote_call_code    : module = function(arg); phase = 2. Used for exports
//                         with a single pointer argument (ProbeStart/Stop).
//   remote_loader_code  : module = LoadLibraryW(path); on NULL, error =
//                         GetLastError() on the same thread; phase = 2.
#include <stdint.h>
#include <stddef.h>

typedef struct remote_loader_data {
    uint64_t function;      // +0  entry point / LoadLibraryW / LdrLoadDll
    uint64_t get_error;     // +8  GetLastError (loader stub only)
    uint64_t arg;           // +16 argument (wide path for the loader stub)
    uint64_t result;        // +24 return value / HMODULE / loaded base
    uint32_t error;         // +32 Win32 error, or NTSTATUS for LdrLoadDll
    uint32_t phase;         // +36 0 = not entered, 1 = inside call, 2 = completed
    uint16_t unicode_length;// +40 UNICODE_STRING.Length  (bytes, no NUL)
    uint16_t unicode_max;   // +42 UNICODE_STRING.MaximumLength
    uint32_t reserved0;     // +44
    uint64_t unicode_buffer;// +48 UNICODE_STRING.Buffer (wide path)
    uint64_t base_out;      // +56 LdrLoadDll BaseAddress out-parameter
    uint64_t arg2;          // +64 second argument (DllMain reason)
    uint64_t arg3;          // +72 third argument (DllMain reserved)
} remote_loader_data;

_Static_assert(offsetof(remote_loader_data, arg) == 16, "stub ABI arg");
_Static_assert(offsetof(remote_loader_data, result) == 24, "stub ABI result");
_Static_assert(offsetof(remote_loader_data, error) == 32, "stub ABI error");
_Static_assert(offsetof(remote_loader_data, phase) == 36, "stub ABI phase");
_Static_assert(offsetof(remote_loader_data, unicode_length) == 40, "UNICODE_STRING.Length");
_Static_assert(offsetof(remote_loader_data, unicode_max) == 42, "UNICODE_STRING.MaximumLength");
_Static_assert(offsetof(remote_loader_data, unicode_buffer) == 48, "UNICODE_STRING.Buffer");
_Static_assert(offsetof(remote_loader_data, base_out) == 56, "LdrLoadDll out pointer");
_Static_assert(offsetof(remote_loader_data, arg2) == 64, "arg2");
_Static_assert(offsetof(remote_loader_data, arg3) == 72, "arg3");
_Static_assert(sizeof(remote_loader_data) == 80, "slot size");

// mov dword [rcx+0x24], 42 ; ret
static const unsigned char remote_minimal_code[] = {
    0xc7,0x41,0x24,0x2a,0,0,0, 0xc3
};

// push rbx ; sub rsp,32 ; mov rbx,rcx
// phase=1 ; rcx=[rbx+16] ; call [rbx] ; result=rax ; phase=2 ; return 0
static const unsigned char remote_call_code[] = {
    0x53,
    0x48,0x83,0xec,0x20,
    0x48,0x89,0xcb,
    0xc7,0x43,0x24,1,0,0,0,
    0x48,0x8b,0x4b,0x10,
    0xff,0x13,
    0x48,0x89,0x43,0x18,
    0xc7,0x43,0x24,2,0,0,0,
    0x31,0xc0,
    0x48,0x83,0xc4,0x20,0x5b,0xc3
};

// push rbx ; sub rsp,32 ; mov rbx,rcx
// phase=1 ; rcx=[rbx+16] ; call [rbx] ; result=rax
// if rax==0: error=GetLastError() ; phase=2 ; return 0
static const unsigned char remote_loader_code[] = {
    0x53,
    0x48,0x83,0xec,0x20,
    0x48,0x89,0xcb,
    0xc7,0x43,0x24,1,0,0,0,
    0x48,0x8b,0x4b,0x10,
    0xff,0x13,
    0x48,0x89,0x43,0x18,
    0x48,0x85,0xc0,
    0x75,0x06,
    0xff,0x53,0x08,
    0x89,0x43,0x20,
    0xc7,0x43,0x24,2,0,0,0,
    0x31,0xc0,
    0x48,0x83,0xc4,0x20,0x5b,0xc3
};

// LdrLoadDll(NULL, NULL, &UNICODE_STRING(+40), &base_out(+56)) - bypasses the
// kernel32/kernelbase wrappers, so a kernel32-level hook cannot fake it.
// push rbx ; sub rsp,0x30 ; mov rbx,rcx
// phase=1 ; rcx=0 ; rdx=0 ; r8=&us ; r9=&base ; call [rbx]
// status=eax ; result=[rbx+56] ; phase=2 ; return 0
static const unsigned char remote_ldr_code[] = {
    0x53,
    0x48,0x83,0xec,0x30,
    0x48,0x89,0xcb,
    0xc7,0x43,0x24,1,0,0,0,
    0x31,0xc9,
    0x31,0xd2,
    0x4c,0x8d,0x43,0x28,
    0x4c,0x8d,0x4b,0x38,
    0xff,0x13,
    0x89,0x43,0x20,
    0x48,0x8b,0x43,0x38,
    0x48,0x89,0x43,0x18,
    0xc7,0x43,0x24,2,0,0,0,
    0x31,0xc0,
    0x48,0x83,0xc4,0x30,
    0x5b,
    0xc3
};

// DllMain(base, DLL_PROCESS_ATTACH, NULL) on a manually mapped image:
// push rbx ; sub rsp,0x20 ; mov rbx,rcx
// phase=1 ; rcx=[+16] rdx=[+64] r8=[+72] ; call [rbx] ; result=rax ; phase=2
static const unsigned char remote_dllmain_code[] = {
    0x53,
    0x48,0x83,0xec,0x20,
    0x48,0x89,0xcb,
    0xc7,0x43,0x24,1,0,0,0,
    0x48,0x8b,0x4b,0x10,
    0x48,0x8b,0x53,0x40,
    0x4c,0x8b,0x43,0x48,
    0xff,0x13,
    0x48,0x89,0x43,0x18,
    0xc7,0x43,0x24,2,0,0,0,
    0x31,0xc0,
    0x48,0x83,0xc4,0x20,
    0x5b,
    0xc3
};
#endif

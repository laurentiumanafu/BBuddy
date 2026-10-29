#pragma once
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#define PROBE_MAGIC 0x43544D31u
#define MAX_TARGETS 4
#define MAX_EVENTS 512
#define NEAR_RANGE 32

typedef struct probe_event {
    uint64_t sequence, tick_ms, tid, address, rip, rsp;
    uint64_t rax, rcx, rdx, rbx, rbp, rsi, rdi;
    uint64_t r8, r9, r10, r11, r12, r13, r14, r15;
} probe_event;
typedef struct probe_config {
    uint32_t magic, size, target_count, reserved;
    uint64_t targets[MAX_TARGETS];
    volatile LONG active, page_count, guard_faults, write_faults;
    volatile LONG nearby_writes, event_count, rearm_errors, restore_errors;
    uint64_t pages[MAX_TARGETS], allocations[MAX_TARGETS];
    uint32_t protections[MAX_TARGETS], reserved2[MAX_TARGETS];
    probe_event events[MAX_EVENTS];
} probe_config;

_Static_assert(sizeof(probe_event) == 168, "shared event ABI mismatch");
_Static_assert(offsetof(probe_config, events) == 176, "shared buffer offset mismatch");
_Static_assert(sizeof(probe_config) == 86192, "shared configuration ABI mismatch");

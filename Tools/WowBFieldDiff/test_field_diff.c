// Offline test of the word-diff tracker used by WowBFieldDiff.
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include "field_diff.h"

static int failures;
#define CHECK(cond) do { if (!(cond)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #cond); failures++; } } while (0)

int main(void) {
    static uint32_t window[64];
    memset(window, 0, sizeof(window));
    fd_state state;
    fd_init(&state, window, sizeof(window));
    CHECK(state.words == 64);
    CHECK(fd_compare(&state, window, 10) == 0);

    // One position component changes at offset 0x20.
    window[8] = 0x42F60000;
    CHECK(fd_compare(&state, window, 70) == 1);
    CHECK(state.tracked == 1);
    CHECK(state.entry[0].offset == 0x20);
    CHECK(state.entry[0].changes == 1);
    CHECK(state.entry[0].first_old == 0);
    CHECK(state.entry[0].first_new == 0x42F60000);

    // Same word changes again; entry is updated, not duplicated.
    window[8] = 0x42F68000;
    CHECK(fd_compare(&state, window, 130) == 1);
    CHECK(state.tracked == 1);
    CHECK(state.entry[0].changes == 2);
    CHECK(state.entry[0].last_new == 0x42F68000);
    CHECK(state.entry[0].last_tick == 130);
    CHECK(state.entry[0].first_new == 0x42F60000);

    // Two more words move in one poll.
    window[1] = 7; window[63] = 9;
    CHECK(fd_compare(&state, window, 190) == 2);
    CHECK(state.tracked == 3);
    CHECK(fd_count(&state, 4) == 1);
    CHECK(fd_count(&state, 0xFC) == 1);

    // Static word stays unknown.
    CHECK(fd_count(&state, 0x40) == 0);

    // Sorting puts the busiest offset first.
    fd_sort(&state);
    CHECK(state.entry[0].offset == 0x20);
    CHECK(state.entry[0].changes == 2);

    // An unchanged poll reports nothing.
    CHECK(fd_compare(&state, window, 250) == 0);
    CHECK(state.polls == 5);

    // Oversized window is clamped rather than overflowing.
    static uint32_t big[FD_MAX_WORDS + 32];
    memset(big, 0, sizeof(big));
    fd_state clamp;
    fd_init(&clamp, big, sizeof(big));
    CHECK(clamp.words == FD_MAX_WORDS);

    // Tracking table overflow is counted, not written past the end.
    fd_state many;
    static uint32_t wide[FD_MAX_TRACKED * 4 / 4 + 64];
    memset(wide, 0, sizeof(wide));
    fd_init(&many, wide, sizeof(wide));
    for (unsigned i = 0; i < many.words; i++) wide[i] = i + 1;
    fd_compare(&many, wide, 1);
    CHECK(many.tracked == FD_MAX_TRACKED);
    CHECK(many.changes == many.words);
    CHECK(many.dropped > 0);

    if (!failures) puts("field diff tracker tests passed");
    return failures ? 1 : 0;
}

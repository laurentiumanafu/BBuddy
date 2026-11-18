# 70938 Maelstrom Weapon candidate and Lightning Bolt

## Evidence

The user's player-only external Combat Research recordings are
`20261115-084719-252c82aba43048ad90da42c96b112b1a.txt` (described as one
stack) and `20261115-084747-629c6f2a8292478e811a97655ea1d118.txt` (described
as two through five). They belong to the same player/session; recordings are
not checked in because raw snapshots contain character/world identifiers.

With the inherited 0x128-stride external layout, ID **408505** appeared in
slot index 5; its `+0x104` word was 1 at 3.250 s in the first trace and was
already 3 at the second baseline, then changed 3 → 4 at 10.517 s and 4 → 5
at 12.770 s. The user describes two stacks during the second recording, but
that value or a 2 → 3 transition is not in the recorded external candidate.
The associated duration word `+0xF4` was 30000; `+0xF8` minus the sampled
player `+0x1C10` clock was approximately 30 seconds. At the 5 transition
that difference was 30082, demonstrating non-atomic sampling and why a single
trace sample must not be treated as exact simultaneous state. The known
candidate count and unique nonzero IDs agree in those samples.

The later five-stack recording `20261115-210942-2c935aed971c436b97e1a3cf8a971c8d.txt`
reported an external header of length 32 and capacity 36 in all 100 samples.
The old 70938 limit of 30 rejected every external read, so this recording
contains no external buffer bytes and cannot establish that the candidate layout,
count, ID, stack or expiry are valid at that size. The 70938 reader now allows
up to the observed length 32 (capacity remains at most 36), with its existing
readability and stability checks and consumer validation. Ghost Wolf remains
limited to 29 slots because a mistaken absence can toggle the form off. These
changes do not establish that the Bolt or macro input reaches the game.

This correlation does not prove an aura API, spell rank mapping, atomicity,
stack lifetime, successful cast or consumption. Another rank, inline storage,
changed offsets, incomplete/unreadable data or clock behavior may withhold the
spell. `Tools/analyze_maelstrom_candidate.py <trace>` reconstructs the recorded
candidate offline; it invalidates inconsistent history rather than interpreting
unknown as absence.

## Experimental policy

For 70938, a Lightning Bolt explicitly configured as Damage is separated from
the usual filler/opener selection and attempted only with an experimentally
validated five-stack candidate. Other roles do not bypass this gate; Ignore
still prevents use. Other builds' rotations are unchanged. Shock, Attack and
Stormstrike remain available while the candidate is absent or unknown. Five
stacks may be used during melee without waiting for the Shock cooldown. This
also affects Gather's shared defensive fight logic. The macro contents remain
opaque; an independent manually configured macro can do anything the user puts
in it and is not inspected by this gate.

The external reader must find the observed ID exactly once, validate the
entire bounded buffer and header, unique IDs and matching stable active count,
read a plausible unexpired 30000-duration candidate against the sampled clock,
and see exactly 5. Inline or unknown/unreadable/unstable data fail closed.
The fight takes a second fresh guarded reading after stopping and facing,
checking the same local player, map, attachment, window, action slot and role,
health, resource type and estimated mana, live selected enemy, range, faction,
profile, STOP and session. A five-second attempt backoff persists across
fights; it is not a verified cooldown. Reads remain non-atomic. A key attempt
is not a successful cast and the polling/verification may delay other actions.

Rank mana floors use the published [Forever Shaman spell listing](https://warcraftforever.games/classes/shaman/spells)
(15, 30, 45, 60, 85, 110, 135, 160, 190, 220), combined with the existing
higher learned estimate. This is not a live cost reader; client changes or
talents can differ. The candidate and action must be tested locally in game.
Synthetic fixtures do not prove the actual fields or casting behavior.

## 2026-11-18: why Bolt almost never fired, and the four-stack change

The 2026-11-18 08:00 log (70959) shows 266 "Lightning Bolt withheld" against
6 attempts. The candidate was developed only from external-mode recordings
(length 18-33, capacity 24-36). With few auras the slots live **inline** in
the unit struct at player+0x608 with the capacity word tagged
`0x8000000000000010`; `ReadExternalCandidate` rejected that signature on the
capacity cap and the window-overlap guard, so the read returned null and Bolt
was withheld. `WowBTotemAuraCandidate` already tried the inline layout first;
the Maelstrom read did not. `ReadExternalCandidate` now accepts the exact
inline signature (pointer == player+0x608, capacity tag, length 1-16) and
reads it from the same pointer with the same stability checks; the slot
stride and +0xE8 ID position are shared, so parsing is unchanged.

Two further defects were fixed in the same pass. The expiry check required
`remaining <= 30000`, but the recordings show 30082 at the five-stack
transition (non-atomic sample/clock), so a just-refreshed buff was withheld in
exactly that window; it now allows `duration + 2000` ms of slack. The spend
threshold is now four stacks (`MinimumStacks = 4`) instead of five, at the
user's request. The withheld log line now appends a `Diagnose` readout
(storage mode, count, unique IDs, duplicates, found stack/duration/remaining)
so a future silent withhold is visible. None of this proves a cast in game;
the user still needs to watch Bolt fire at four and five stacks.

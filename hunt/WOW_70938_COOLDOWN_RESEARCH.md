# 70938 cooldown static research

This is **research only**, not a runtime reader. No Lua bridge, game function
calls, injection, writes, or gameplay behavior are added. All RVAs below refer to
the decoded 1.60.1.70938 x64 image with SHA-256
`436a1b0dc17006895d46b1619dff45c84b683d37e2b24a458cdec333f2accb3a`.
Run `python Tools/check_wow70938_image.py <decoded-exe>` to verify selected
instruction and relative-reference anchors. The verifier does not prove live
semantics. Registration handlers include obfuscated/overlapping instructions;
linear disassembly of those blocks is not reliable.

## Static leads

- A registration sequence at RVA `0x15F96A9`/`0x15F96B5` associates a
  handler at `0x15DE750` with the name `GetSpellCooldown`. The similarly named
  output wrapper at `0x15DEF80` calls `0x28A2390` at `0x15DF033` with a
  32-bit value from its input in EDX and an output pointer in R8. This is not
  a license to call either function; the prior Lua bridge crashed the client.
- In helper `0x28A2390`, the value is retained in EDI. A preliminary path
  calls `0x277D6F0`; if it does not return a result, another lookup returns a
  byte used to select `image base + 0x48EC7E0 + index * 0xB8`. That candidate
  state object is passed as RCX to `0x1F50E20` at `0x28A2480`, with the input
  value in EDX and output pointers on the stack/in R9. The wrapper later
  converts two returned integers to doubles for its output. The returned
  lookup byte has not been established as always 0 or 1 at runtime.
- On one normal path in `0x1F50E20`, `[state+0x10]` is the first candidate
  link, link bit 0 is tested as a tag, and the next link is read from
  `[node+0x08]`. At `0x1F51133` the node's dword `+0x10` is compared to the
  input spell value and `+0x14` to another argument. When its conditions hold,
  dwords `+0x1C` and `+0x20` feed a start/duration-like calculation. Other
  fields at `+0x24`, `+0x28`, `+0x2C`, `+0x30`, `+0x34`, `+0x38`, `+0x3C`
  and `+0x40` participate in other branches. These are static descriptions,
  **not** verified layout, time units, readiness, or reset semantics.
- The same calculation also consults category-like data, an additional
  helper at `0x27BEE40`, and other conditions. The earlier `0x277D6F0` path
  may independently return a result. Therefore an absent matching linked-list
  node is **not proof of readiness**, and a node value alone is not proof of
  the effective cooldown or that it is distinct from the global cooldown.
  Clock wrapping, concurrent mutation, pointer tags, player/session changes,
  rank IDs and category/charge behavior are also unresolved.

## Validation needed before any gameplay use

The Combat research tab has a default-off **Also record bounded cooldown
candidate (70938 only)** option. It samples the two hypothesized array entries
0 and 1, follows at most 64 nodes per list, checks readable committed ranges,
cycle/alignment/address limits and a 100 ms per-sample budget checked between
reads, and compares two complete observations. Unreadable, changing, cancelled or incomplete
observations are `unknown`, not empty or ready. The capture is stopped-bot only,
requires 70938 and retains the existing attachment, identity and bar checks.
The values are raw research evidence: even a stable observation is not a live
cooldown result or proof the list is exhaustive. Reads are not atomic and the
number of list states and structure layout remain unverified. The normal
player window is also captured; raw memory should not be uploaded without review.

Compare separate stopped-bot observations on the same character and bar:
ready, immediately after one successful Stormstrike, natural expiry, and a
confirmed Improved Stormstrike reset before natural expiry; also trigger an
unrelated global cooldown while Stormstrike is ready and while it is cooling
down. Record the visible UI result and approximate event times for each. Repeat
across several uses and a fresh session. A reset must be visibly confirmed, not
assumed merely because a key was pressed. Test unreadable/changed-session
handling separately. Do not substitute a guessed timer or an incomplete list
for live data. Existing eight-second attempt estimates remain in the rotation
and **cannot detect early resets**; until validated, the requested live cooldown
behavior is not implemented.

## Four supplied recordings (2026-11-14)

All four completed with 100 samples and no reported candidate-read failures.
`Tools/analyze_cooldown_candidate.py <trace>` summarizes changes without
printing process addresses; it is a raw research parser, not a reader used by
combat. The entries below are correlations, not independently verified
semantics or proof that every client cooldown path is covered.

- Ready control: both sampled candidate lists contained no nodes throughout.
- Normal Stormstrike: the initial state contained ID 8045 (the local name
  table identifies this as Earth Shock), so it was not a clean all-idle
  baseline. At 7.758 seconds an ID 17364 node appeared with `+0x20=8000`
  and another with `+0x38=133`, `+0x3C=1500`. Both had the same `+0x1C`
  start-like value. Their last sampled presence was 16.770 seconds; both
  were gone at 17.020 seconds.
- GCD-ready: ID 8045 appeared at 6.258 seconds, including a node with
  `+0x38=133` and `+0x3C=1500`, plus a different node with `+0x24=19` and
  `+0x2C=6000`. No ID 17364 node was sampled. The 1500-unit entry is a
  **global-cooldown candidate**, not proof of an active GCD for every spell.
- GCD during Stormstrike: a 17364 `+0x3C=1500` node appeared at 6.260 s;
  the 17364 `+0x20=8000` node appeared at 6.510 s. At 8.262 s ID 8045
  appeared with its own `+0x3C=1500` node, while the 17364 8000-unit node
  remained. These simultaneous distinct records are useful evidence that
  the short category-like field and spell-specific candidate differ. Both
  spell IDs were last seen at 17.770 s and gone by 18.019 s.

The candidate player `+0x1C10` word advanced near real-time in these traces;
compared to the Stormstrike `+0x1C` value, it had advanced 9175 units at
16.770 s in the normal trace and 11601 at 17.770 s in the mixed trace, even
though the 8000-unit node remained present. This strongly cautions that node
presence is **not** equivalent to an active cooldown. The field's clock source,
units, wrap behavior, stale nodes, update races, alternate client paths and
actual UI readiness remain unverified. No reset test was supplied, and no
rotation change is justified by these four recordings alone.

## Manual recording procedure

Rebuild the Windows app, attach to the 70938 character, stop all bot/route
activity, open Combat research, check **Player only** and **Also record bounded
cooldown candidate (70938 only)**, and leave the external aura option off.
Keep the same character and action bar. For each separate recording enter the
label below, click **Record 25 seconds**, wait for **Recording**, then return to
the game and wait approximately five seconds. Allow the recording to finish;
its status gives the path in `Logs/CombatResearch`. Avoid incidental casts,
actions or equipment/bar changes; use a safe, durable target for attacks.

1. `ready control`: with Stormstrike ready at the start, do not cast anything.
2. `normal Stormstrike`: start ready; at about five seconds successfully cast
   Stormstrike once. Do not cast anything else; observe the visible cooldown
   until it expires. Report whether it appeared and its approximate duration.
3. `GCD ready`: start with Stormstrike ready; cast one unrelated spell which
   produces a visible global cooldown, and do not use Stormstrike. Report its
   name and whether the Stormstrike icon briefly shows the GCD.
4. `GCD during Stormstrike`: start ready, use Stormstrike around five seconds;
   after its global cooldown permits, use the same unrelated spell once while
   Stormstrike's own cooldown remains visibly active. Report approximate times.
5. `confirmed early reset`: start ready, use Stormstrike and perform the
   normal in-game actions that can trigger the talent. If its visible cooldown
   genuinely clears early, report the approximate first-cast and reset times,
   what was visible, and whether Stormstrike was actually usable. Do not assume
   every talent proc resets it. If no reset is visibly observed, mark the trace
   `no confirmed reset` and repeat when convenient; never fabricate one.

Each trace can contain character identifiers, raw process addresses and
other sensitive memory. Review before sharing. In particular, label unclear
casts, target deaths, other actions, failed attempts, and recordings cut short;
the recorder may return `unknown` or miss the data despite stable samples.

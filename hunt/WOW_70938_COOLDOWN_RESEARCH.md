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

A bounded, read-only research recorder could sample the candidate array and
nodes, with exact 70938 gating, attachment/player identity checks, readable
committed-page checks, strict node/byte/time limits, cycle detection, tagged
pointer handling, and stable pre/post reads. An unstable or incomplete
snapshot must be marked unknown, not empty or ready. Its feasibility and actual
layout must be established locally first; this document does not add that
recorder and raw memory should not be uploaded without review.

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

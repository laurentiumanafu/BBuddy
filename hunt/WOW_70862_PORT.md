# Experimental 1.60.1.70862 port

## Input and status

The user's Drive ID `1VM3hp_fLx5u6BvnLvhO7zlWW14p-OMo_` was reused: the current `Wow-decoded.zip` (version 8) is 34,440,529 bytes, MD5 `b61f8f7477fda93821ecfa67350d6337`. Its one executable is 80,627,408 bytes, AMD64 PE32+, image base `0x140000000`, resource version **1.60.1.70862**, SHA-256 `b332526a904931e81dcbcf1f95a42d64e842177b37ab5af52ab1f27b875fd0af`. Neither executable nor archive is committed. The older 70790 executable was no longer available through that ID; direct comparison used the available 70291 decoded reference (`8f864e73e73ad67319765063a7c491a420dbe2d0714ce427106e39cd9f109192`), the documented 70790 anchors, bounded masked instruction matches and Capstone disassembly. The new image was not executed.

70862 is the default registered build and routes to the existing experimental 70707-family gameplay, settings and safeguards with its own image-relative candidates. Earlier registered builds remain supported. This is a **port of shared implementation, not proof that every feature or structure works**. No Windows/WPF compile or live client validation has been done here. Unknown versions are not assumed compatible; no Lua is used. Any new action or layout should be supervised, particularly combat, healing, vendoring, corpse recovery and skinning.

## Static candidates (RVAs)

| Candidate | 70862 RVA | Static basis |
|---|---:|---|
| Player GUID / name | `0x4C1A770` / `0x4C1A780` | Three corresponding 70291 references to the GUID; the adjacent byte-write sequence at `0x1C712DB` targets the name candidate. Name semantics still need a live comparison. |
| Target GUID | `0x6A32478` | 35 corresponding 70291 xrefs voted for this candidate; `0x5ABB58` and `0x23C76D` provide separate references. |
| Mouseover GUID | `0x4CD5F98` | Two corresponding 70291 xrefs, including `0x21C900A`; live cursor behavior unverified. |
| Object manager / pools | `0x6C349F8` / `0x4728BE0` | Three and two corresponding 70291 references respectively; inherited pool record layout remains a hypothesis. |
| Map root | `0x6C34978` | Three corresponding 70291 references; actual map offset remains inherited and unverified. |
| Camera frame | `0x4A51EB0` | Fourteen matching 70291 references; frame/camera member offsets are inherited and unverified. |
| Action array | `0x6C53140` | 36 corresponding references and indexed array use at `0xA2B9FD`. Key bindings and packing remain experimental. |
| Player / unit / game object vtables | `0x40E6498` / `0x40FA510` / `0x40EECB0` | Correlated 70291 virtual-method slots (player two, game object five); the unit candidate shares 47 of the first 60 player slots and has plausible corresponding initial methods. Other similar tables exist. Especially the unit identification is an inference, not a live observation. |
| Inventory handler / lookup | `0x25D4E10` / `0x2074700` | `0x25D4EF9` uses `+0x9460` and calls lookup at `0x25D4F00`; observed `+0x90` after the call. Semantics are still unverified. |
| Container identity table | `0x6C56180` | Helper `0x2473D60`, called at `0xD0FD5C` and `0x247584B`, computes this RIP-relative table with index ×16. Completeness is unproven. |

Gameplay code marker sites are faction `0x1F9298C`, health `0x1FBCA47`, power `0x27997E5`, action `0xA2B9FD`. Their selected bytes are checked independently, with the existing minimum-two-matches/no-readable-mismatch policy. The container loop at `0x2475876` accesses `+0xAC0`, `+0x490`, strides 16 and tests high bits; player-range markers `0x24758C6`, `0x24758D9`, `0x24758E4` match inherited `+0x9480`, `+0x7D04`, `0x23` behavior. They are partial static evidence, not proof of the slot model or safety.

`Tools/check_wow70862_image.py` checks the exact PE hash, marker bytes, RIP-relative targets and selected table slots; a successful run does not validate behavior. Unit vtable, health/power/faction semantics, ghost, aura, input, pool, object and interaction fields, and complete bag enumeration remain unverified for 70862.

## Bags and live verification

The existing ordinary-bag policy, two-zero monitor, startup priority and read-only stopped research are routed to 70862. The 70790 free-memory/neighbor-record handling is reused with the same conservative checks. Saved classifications are keyed by exact player and bag GUID in `Settings/clickaim-bag-kinds-70755.txt`; a changed character or bag remains unknown. Confirm existing classifications still correspond to ordinary/specialty bags on this client. Zero identities cannot prove a complete inventory. Keep auto-selling disabled until stopped snapshots match visible ordinary slots and a controlled non-stacking item changes the subtotal correctly; supervise the first zero-trigger trip and check the sale independently.

Build Windows x64 with `dotnet build CopilotBuddy.csproj -c Release -p:Platform=x64`. In a safe area start stopped, inspect attach/build, character, coordinates, target, map, action bar and nearby scan; check STOP and short mesh movement before supervised combat/gather/loot/buffs/death recovery. Test individual modes separately and send a log if a guard refuses or values disagree. A logged action is not proof it succeeded. See `HANDOFF_FUTURE_WOW_BUILDS.md` for the full live validation procedure and limitations.

## Checks performed

The supplied PE passed `Tools/check_wow70862_image.py`; the old 70291 reference was rejected by its exact-hash check. The production-linked Roslyn synthetic reader suite passed for 70707, 70755, 70790 and 70862, including 70862 marker mismatches, bag classifications, unreadable alternatives and mapping changes. Gather radius/interaction, memory-region, bag-sell, remote-defence, buff, Shock/Attack and power fixtures passed; eight Python inventory and two build-default checks passed. C# lint scripts completed with the existing heuristic at `GreenMagic/LuaBridge64.cs:1752`; changed C# parsed, project/XAML XML parsed, and `git diff --check` passed. These are not a full Windows application build or live validation.

# 1.60.1.70755 experimental port

70755 is the preferred registered build. The 70707 experimental Grind, GatherBuddy, combat, corpse recovery, interaction, buff and candidate-subtotal selling paths are shared with 70755; 12340, 70291 and 70707 remain registered. An unreadable or unregistered version is no longer accepted solely because of its process name: doing so could apply another build's offsets. The historical `IsExperimental70707` property currently denotes the **shared 70707-style feature family**, not an exact version test; use `IsExperimental70755` or `ExperimentalBuildNumber` to identify the actual build.

**Status: experimental implementation, not proven parity.** No 70755 live client session or Windows/WPF project build has been run here. Many object, unit, position, flags, power, action, aura and corpse layouts are inherited from 70707; correspondence of a few instructions does not prove those layouts. The 70755 unit vtable is particularly weak: inferred from shared method alignment and table order, not independently identified. If a layout is wrong, reads, movement, clicks, targeting, selling and other bot actions can be wrong. Partial signature checks are not full executable authentication. User accepted experimental bot operation, but none of these observations is proof of functional parity.

## Static candidates

Source: supplied decoded x64 1.60.1.70755 executable, SHA-256 `c9a02b3449b0cedae584022605a18f3b9c9b6be4487f3401ff1a216cb24c058c`; image base `0x140000000`. Addresses below are RVAs, not absolute addresses. Masked instruction matches to a 70291 comparison image and candidate vtable-method matches are evidence, not live validation.

| Candidate | 70755 RVA | Evidence / caveat |
|---|---:|---|
| Player GUID / name | `5017E50` / `5017E60` | three / two matching reference sites |
| Selected GUID | `6A29BB8` | 32 matching sites |
| Object manager / pool array | `6C2C138` / `4723BE0` | three / two matching sites |
| Map root | `6C2C0B8` | five matching sites; root+0x20 remains a candidate |
| Mouseover GUID | `50D3678` | three matching sites |
| Stored actions | `6C4A880` | 37 matching sites; action representation inherited |
| Player / unit / game-object vtable | `40E15D8` / `40F55D8` / `40EA0F0` | player and game object method matches; unit inferred, not independently confirmed |
| Camera frame | `4E4F640` | GetCameraZoom handler `273C760` uses frame+0x480 then +0x274 |
| Bag identity table | `6C4D8C0` | function `2471A20`, index stride 16 |
| Inventory handler / lookup | `25D2A70` / `20733B0` | handler uses context+0x9460, result+0x90 |

Code markers for faction (`1F9163C`), health (`1FBB6F7`), power (`2797405`), and action array (`A2BA4D`) are used by the shared experimental startup guard. At least two readable matching markers and no readable mismatch are required; unreadable markers do not count as matches. The checks additionally require a stable local player, map candidate, action bar, fields and ghost candidate. These checks do not establish map, ghost, power, faction, or movement semantics.

The container helper `24734C0` uses candidate offsets `+0xAC0`, `+0x490`, `+0x7D04`, `+0x9480`, and start index `0x23`. They resemble the 70707 candidates but the subtotal is not live-validated on 70755. Code-page unreadability in inventory probing is allowed under the existing independent build/player guards; readable mismatches fail closed. Two consecutive fresh zero observations may trigger automatic selling; unknown does not mean zero. Candidate enumeration may omit or overlap bags, special bags can restrict use, and a recorded click replay does not verify a sale. Disable automatic selling until the subtotal has been checked on the new client if a wrong vendor trip would be unacceptable.

## Checks and first local validation

Synthetic reader fixtures exercise both 70707 and 70755 address routing, the 70755 marker set, gameplay-session startup/revocation, action-bar changes, bag marker mismatch, changed subtotal, and legacy reader behavior. Fixtures establish code wiring only, not client correctness. An isolated Roslyn runtime compiled these components; it is not a complete Windows project build.

On the actual client, build the Windows project first. Attach while stationary and inspect the 70755 log for player name/GUID, snapshot, map candidate and signature status. With the bot stopped, compare the bag-research subtotal to visible slots and a controlled single-item change; do not infer success from an unreadable marker. Then, if choosing to operate experimentally, test small monitored steps (STOP, movement, a known target, combat/loot, gather, death recovery, buffs and vendor plan), stopping immediately if observations or actions disagree. Logs may contain GUIDs and addresses. Existing 70707 logs and confirmations do not validate 70755.

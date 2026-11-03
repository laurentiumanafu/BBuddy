# The missing offsets — what the 70291 port still needs, per core feature

Written 2026-10-13 (build 1.60.1.70291, beta build 70291, `wowb.exe`, native x64).
Companion docs: `X64_PORT_PLAN.md` (phases), `hunt/WOWB_DECODED_MODEL.md` (what was
actually decoded), `LUA_BRIDGE_64.md` (the execution vector), `FUTURE_PLAN.md`
(what to do next), `RESUME.md` (how to pick this up after a break).

Method: every core action of the bot was read at its call site (memory read/write,
`Lua.DoString`, executor JIT, CTM call) and compared against what
`Styx/Offsets/WowBOffsets.cs` knows **for 70291**. Rows marked `= 0` below are
literally `public const ulong X = 0;` in that file today.

---

## Finding 0 — the structural one: the whole state layer is 3.3.5a-shaped

```csharp
// Styx/WoWInternals/WoWObjects/WoWUnit.cs
internal T GetDescriptor<T>(UnitFields field) where T : struct
    => GetDescriptorField<T>((int)field * 4);

// Styx/WoWInternals/WoWObjects/WoWObject.cs
uint descriptorPtr = Memory.Read<uint>(BaseAddress + DescriptorOffset);  // DescriptorOffset = 0x8
return Memory.Read<T>(descriptorPtr + offset);
```

That is the **3.3.5a descriptor model**: `[object + 0x8]` points at a flat array and
field *N* lives at `array + N*4`. The bot reads **76 distinct `UnitFields` /
`ObjectFields` / `PlayerFields` members** through this one method (health, power,
level, flags, flags2, npc flags, dynamic flags, faction template, bytes0/1/2,
target, channel spell, display id, combat reach, bounding radius, attack times,
damage, happiness, auras, …) plus every write path (`WriteDescriptor`).

**70291 has no such array.** The modern client keeps unit state as *direct fields on
the unit object* — `record (+0xD8) → unit object`, with the fields the port already
proved live: health `+0x1E30/+0x1E38`, power container `+0x1BC8`, unit flags
`+0x1E60`, level `+0x1E40`, name pointer `+0x238`, npc dynamic flags `+0x94`,
auto-attack `+0xA20` (2592), loot deadline `+0x4DC`.

**Consequence:** every one of those 76 fields currently reads garbage (the pointer at
`+0x8` is a back-pointer, not a descriptor array — see the Phase-3 notes in
`WowBOffsets.cs`). Targeting, combat decisions, quest logic and the UI wrappers are
all blind until this is mapped. This is the single highest-value artifact left.

**How to close it — `hunt49_unitfields`** (value-diff hunt, same method that produced
`+0x1E30`):

1. Pick the local player and one NPC; read 0x2400 bytes of each unit object.
2. Bring a *known* state change about (take damage → health; switch target → target
   guid; cast → casting spell; kill → dynamic flags; change a hostile/friendly
   target → reaction) and diff the two snapshots.
3. Score each candidate offset by (invariant) plausible ranges + (variant) the
   expected delta shape. Emit a table `UnitFields → actor offset + width + source`.
4. Accept only offsets confirmed by **two** independent value changes.

The fields the core actually *needs* first (in order): `Health` (done), `MaxHealth`
(done), `Power/MaxPower` (done, container), `Level` (done), `Flags`
(`ActorUnitFlags` done), `DynamicFlags`, `NpcFlags`, `FactionTemplate`, `Reaction`
(hostility — **not found yet**), `Target`, `DisplayId`, `ChannelSpell` /
`CastingSpell`, `Bytes0/1/2` (power type / stand state), `CombatReach`,
`BoundingRadius`, `AuraState`. The rest can follow lazily.

---

## Finding 1 — the action functions are placeholders

`WowBOffsets.cs` declares them, all as 0. Legacy 3.3.5a values are given so the
same call site can be recognised in Ghidra/rea output.

| Feature (bot call site) | Legacy 3.3.5a | 70291 today | How to find it |
|---|---|---|---|
| **Set target** (`WoWUnit.Target()`, executor `call 0x525070`) | `0x525070` | `CGGameUITarget = 0` | Warm the Lua handler `TargetUnit` (`0x1B46C60`, known) then find its inner call; or find the **writers of `PlayerTargetGuid` (0x7BDFF38)** — the write that stores a new guid *is* the setter |
| **Auto attack** (`Lua.DoString("StartAttack()")`) | Lua `StartAttack` | handler RVA only, unmapped | The Lua C function table is walkable live (it gave all movement RVAs) — read `StartAttack`'s handler from it, then its inner call |
| **Cast spell** (`Spell_C::CastSpell`) | `SpellCCastSpell` | `SpellCCastSpell = 0` | Inner call of the warm handler `CastSpellByName` (`0x2C24040`); cross-check with the action-bar route (`UseAction`, actionbar `0x590CB20`) |
| **Spell cooldown / GCD** | `SpellCGetSpellCooldown`, `CooldownList 0xD3F5AC` | both 0 | Prefer the **spellbook record** (`0x5909500`, stride 0x10): find the `start/duration` pair by value-diff while a cooldown ticks |
| **Click to move** (`WoWMovement.CallClickToMove`) | `CGPlayer_C__ClickToMove 0x727400`, stop `0x72B3A0`, base `0xCA11D8` (Type+0, TurnSpeed+4, dest +0x8C/+0x90/+0x94, guid +0x20, action +0x1C) | `ClickToMove = 0` | **Highest gameplay value.** (a) find the live CTM struct by scanning for the destination triple after clicking the ground in-game; (b) find who writes it (that is `CGPlayer_C__ClickToMove`) via an execute-watch; (c) the `enableClickToMove` CVar string xref leads to the click handler that reads the CVar |
| **Movement bits / input control** | `CGInputControl_ToggleControlBit 0x5FBE10`, `ActiveInputControl_Ptr 0xC24954` | both 0 | From the known movement Lua handlers (`0x23C8BE0`…) → their shared inner function (`0x23C550D`) → the bit setter |
| **TraceLine (LOS)** | `CGWorldFrame::Intersect 0x77F310` | 0 | Signature/xref hunt on the camera/ray struct usage; needed before casting through walls |
| **Object manager helpers** | `ClntObjMgrGetActivePlayerObj`, `…ObjectPtr`, `…EnumVisibleObjects` | `= 0` | Optional — the port enumerates via the pool hash + `FnEntityGetGuid` (`0x2BBB7F0`) already |
| **DX11 / hardware event** | — | `SwapChainPtr = 0`, `HardwareEventPtr = 0` | Later phases (overlay hook, and only if the taint gate turns out to need a hardware-event stamp) |

### The code gap behind them: a native-call stage
The legacy bot made C calls through an **executor that pushes arguments and calls**
(`executor.AddLine("push …"); executor.AddLine("call 5393392"); executor.Execute();`).
The x64 payload executes **Lua** calls only. CTM (3 floats + guid + enum), SetTarget
(2×u32) and CastSpell (spell id) all need a **native call stage** in the payload:

```
RCX, RDX, R8, R9  + XMM0..XMM3 (float args) + 32-byte shadow space
call qword ptr [target]      ; return value and RAX/RCX write-back to ctrl
```

That stage is ~40 payload bytes and unblocks every Class-B entry above **without the
Lua thread at all** (movement/CTM/target/cast are engine calls, no FrameScript
assert) — while the Lua-thread hijack remains the route for the *script-level*
functions (`GetTime`, `RunScript`, quest frames).

---

## Finding 2 — questing, looting, vendors are Lua-frame driven

The questing/loot/vendor code paths are all `Lua.DoString(...)` against Blizzard
frames:

| Feature | Legacy call | 70291 status |
|---|---|---|
| Accept quest | `AcceptQuest()` / `QuestFrameAcceptButton:Click()` | protected Lua → **taint gate** |
| Complete / turn in | `CompleteQuest()` / `QuestFrameCompleteQuestButton:Click()` | protected Lua → taint gate (`QuestSelectedId` `0x6305B88` is known = the C side's selected quest) |
| Loot | `LootSlot(i)` loop | protected Lua; read side known (`NpcDynamicFlags 0x94` loot bit, `ActorLootDeadline 0x4DC`) |
| Vendor / repair | `BuyMerchantItem`, `RepairAllItems` | protected Lua |
| Gossip / interaction | `SelectGossipOption`, CTM `NpcInteract` | protected Lua; CTM route covers the *interaction* itself |
| Quest data (log, objectives) | `GetQuestLog*` Lua reads | unprotected — usable through the bridge today |

Two routes, and the 0.8.2 `GetTime`→`MoveForwardStart` pair decides which:
1. **Hijacked Lua thread** — a C-level call may bypass the script-taint gate the chat
   `/run` path hits (`FORCETAINT_STRONG`, see the model doc). If yes, all of the
   above works as-is through the bridge.
2. **C-level equivalents** — the CMSG senders behind accept/turn-in/gossip/loot/buy.
   Found via the known globals (`QuestSelectedId`), the opcode constants, or the
   inner calls of the warm Lua handlers. This route is immune to taint forever.

---

## What is *not* missing (for peace of mind)

Object enumeration (`ObjectManager 0x7B7B738`, pool hash `0x27FBD0`, `FnEntityGetGuid
0x2BBB7F0`), object→unit-object chain (`*(record+0xD8)`), the global pointers
(player/target/name/zone/area/registry/spellbook/actionbar), the camera, the Lua ABI
(`lua_pcall 0x471B5C0`, `getfield`, `pushlstring`, `gettop`, stub `0x471A983`), the
**whole movement Lua-handler table** (28 handlers, RVAs live==static), the warm-up
list (`RunScript 0x136FFF0`, `TargetUnit 0x1B46C60`, `CastSpellByName 0x2C24040`,
`RunMacro 0x2CC2DB0`), and the two hook surfaces (our bridge page + the
`DX11PresentHook64` plan).

## Priority order (agreed with what the bot actually does first)

1. **Prove the Lua-thread call** — 0.8.2 `--call GetTime` (vector) then
   `MoveForwardStart` (protected-function question). Everything in Finding 2 hangs on
   the answer.
2. **Native-call stage** in the payload (code, no RE) — unblocks every Class-B row.
3. **CTM** (function + struct + stop) — movement *and* all world interaction
   (quest giver, vendor, loot, corpse, mailbox, flight master).
4. **SetTarget + auto-attack + CastSpell** — combat.
5. **`UnitFields` mapping table** (`hunt49`) — makes decisions (hostility, level,
   cast state) possible; needed by every combat/quest behaviour.
6. **Cooldowns/GCD, TraceLine** — polish before the bot is left unattended.
7. **HardwareEventPtr / taint** — only if route 1 is blocked and route 2 is not
   enough (quest frames).

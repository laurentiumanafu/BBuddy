# WowB Offset Hunting Runbook (build 70291)

Pipeline: **Ghidra 12.1.x** (analysis engine) + **REA** (`rea-agents`, the MCP
bridge) + **Gemini CLI** (the agent, flash-lite model) hunting the offsets that
`Styx/Offsets/WowBOffsets.cs` needs for `WowB.exe` (WoW Classic Beta 1.60.1,
build 70291).

Target binary: `F:\World of Warcraft\_classic_beta_\WowB.exe`
(adjust to your install; it must sit on a local NTFS volume).

---

## 1. Toolchain setup (one-time, Windows/PowerShell)

### 1.1 Prerequisites

| Component | Requirement | Notes |
|---|---|---|
| Node.js | 22.x (≥22.19), 24.x (≥24.11), or 26+ | `node --version` |
| JDK | 21+ **full JDK** (not a JRE) | for Ghidra headless |
| Ghidra | 12.1.x (verified with 12.1.4) | zip distribution, unzip to e.g. `C:\tools\ghidra_12.1.4_PUBLIC` |
| Gemini CLI | current | `npm install --global @google/gemini-cli` |

### 1.2 Install REA and point it at Ghidra

```powershell
npm install --global rea-agents

# persistent environment (adjust paths)
setx GHIDRA_INSTALL_DIR "C:\tools\ghidra_12.1.4_PUBLIC"
setx JAVA_HOME          "C:\tools\jdk-21"
setx REA_ANALYSIS_PROVIDER "ghidra"
# open a NEW terminal so the vars apply, then verify:

rea doctor --json        # Ghidra release, headless launcher, JDK, x64 host
rea providers --json     # ghidra must list the read-only operations
```

`rea doctor` separates *installation* problems from *target* problems — if it
fails, read which one it names before going further.

### 1.3 Register REA with Gemini CLI

```powershell
npx rea-agents@latest setup        # choose "Gemini CLI" when asked
```

Setup writes the REA MCP server entry into `~/.gemini/settings.json`
(`mcpServers.rea`) plus workflow instructions, backing up existing config.
It pins the registration to the exact REA version. Restart any running
Gemini CLI afterwards. Sanity check: `rea doctor --client gemini_cli --json`.

### 1.4 Pick the model

Start `gemini`, then inside the REPL run `/model` and select the
flash-lite model you want (the user's choice: Gemini Flash 3.5 Lite — pick
whatever exact ID your account lists). REA's tool schemas are large, so if
requests get rejected, add `"REA_MCP_INPUT_SCHEMA_PROFILE": "compact"` to the
`env` block of the `mcpServers.rea` entry in `~/.gemini/settings.json` and
restart.

---

## 2. Big-binary survival notes (READ BEFORE STARTING)

WowB.exe is a huge x64 PE. Two consequences:

1. **Every REA provider session imports the target into a fresh temporary
   Ghidra project.** First import + auto-analysis can take a very long time
   (tens of minutes to hours) and exceeds default client timeouts — REA's
   provider deadline is 330 s, after which you *reopen* and it continues.
   Plan the first run overnight; don't kill Ghidra processes while they chew.
2. **Work in batches.** Keep one Gemini CLI session open and issue all
   queries for one offset cluster together — results stay warm in the
   session. Use REA snapshots to keep them across CLI invocations:
   `rea <cmd> ... --snapshot C:\rea-snapshots\wowb.json`.

Recommended warm-up: point REA at any small native exe first
(`rea inspect C:\Windows\System32\cmd.exe --provider ghidra --json`) to
prove the Ghidra → REA chain works before burning hours on WowB.

Fallback lane: do the *string search* steps below in the Ghidra GUI yourself
(Window → Navigation; Search → For Strings / For Program Text), then hand the
candidate addresses to the agent for decompilation/xref work.

---

## 3. The offsets to find

All values in `WowBOffsets.cs` are **image-relative RVAs**. Ghidra shows
absolute VAs with the PE image base (normally `0x140000000`):

```
RVA = VA_in_Ghidra - ImageBase          (verify ImageBase in Ghidra: Window → Memory Map)
```

### Priority 1 — unlocks the read-only snapshot (IsComplete needs these five)

| Offset | Field | Best clue |
|---|---|---|
| GameState | `GameState` | Lua API `IsPlayerInWorld` implementation; game-state globals |
| Object manager ptr | `ObjectManager` | string **`objects waiting to be freed`** → ClntObjMgr code → `s_curMgr` global |
| Player GUID | `PlayerGuid` | Lua API `UnitGUID("player")` / player-guid global next to game state |
| Target GUIDs | `PlayerTargetGuid` (+0x10 = last target) | targeting code / `UnitGUID("target")`; two consecutive 128-bit GUIDs |
| Lua entry | `FrameBase`, `FrameScriptExecuteBuffer` | strings `FrameScript`, `ExecuteBuffer`, Lua API registration tables |

### Priority 2 — world awareness

| Offset | Field | Best clue |
|---|---|---|
| Zone/area IDs | `ZoneId`, `AreaId`, `SubAreaId` | `GetRealZoneText`/`GetAreaID`-family Lua APIs; zone-struct readers |
| Zone text ptr | `ZoneText` | string **`GetZoneText`** → function reading the 3-string block (+0/+8/+10) |
| Camera | `CameraManager`, verify `CameraArrayOffset` (0x3A58 from 1.15.x), `CameraPosOffset` | strings `camera`, `SetView`, `CameraZoomIn`, `NextView` |
| Mouseover | `MouseoverGuid` | string `mouseover` (UnitID handling) |

### Priority 3 — execution & movement (later milestones)

| Offset | Field | Best clue |
|---|---|---|
| SwapChain | `SwapChainPtr` | imports `D3D11CreateDeviceAndSwapChain` / `CreateSwapChain*` xrefs; where the returned `IDXGISwapChain*` is stored |
| Lua helpers | `FrameScriptGetTop`, `FrameScriptPCall`, `FrameScriptToLString`, `FrameScriptLoad` | siblings of ExecuteBuffer in the FrameScript export/registration area |
| ObjMgr functions | `ClntObjMgrEnumVisibleObjects`, `...GetActivePlayerObj`, `...ObjectPtr` | callers around the s_curMgr global; strings `EnumVisibleObjects` |
| Targeting | `CGGameUITarget` | `TargetUnit` Lua API; xrefs to target-guid global write |
| Spells | `SpellCCastSpell`, `SpellCGetSpellCooldown` | strings `Spell_C`, cooldown error messages |
| Movement | `ClickToMove` | string `ClickToMove` (it is also a CVar) |
| LoS | `TraceLine` | `CGWorldFrame::Intersect`, strings `TraceLine` |
| Taint | `HardwareEventPtr` | hardware-event timestamp read by FrameScript security checks |

Struct constants already encoded (from 1.15.x research, verify as you go):
ClntObjMgr +0x8 entity array, +0x10 count, +0x48 hash array, +0x50 hash count;
entity entry stride ~0x30 with GUID @+0x10 and CGObject* @+0x28; CGObject type
byte @+0x8, GUID @+0x18; Lua chain FrameBase→+0xF18→+0x148.

---

## 4. Working method with the agent

Paste cluster-by-cluster. Template (adjust offsets/strings as findings accrue):

> Using REA with the Ghidra provider, open
> `F:\World of Warcraft\_classic_beta_\WowB.exe` (use snapshot
> `C:\rea-snapshots\wowb.json` if present). I need the image-relative RVA of
> the client's object-manager global for WoW Classic build 70291. Method:
> search for the string "objects waiting to be freed", list its xrefs,
> decompile the functions referencing it, identify the global pointer they
> load/store (the ClntObjMgr singleton), and give me:
> 1. the global's VA and its RVA (image base 0x140000000 unless the map says otherwise),
> 2. the function(s) and instructions that prove it (quote the pseudocode lines),
> 3. any uncertainty and how to resolve it.
> Show evidence for every claim; if the string is absent, say so and try
> "ClntObjMgr" or object-manager-related symbols instead.

Repeat per cluster, substituting the clue from §3. Rules that keep results usable:

- Always ask for **VA → RVA conversion with the observed image base**.
- Always ask for the **evidence** (function name/address + quoted pseudocode).
- When the agent proposes a candidate global, ask it to also show *writers*
  (what stores into it) — globals that are only read in one place are often
  copies, not the canonical location.
- Save confirmed RVAs into the results table below as you go.

### Results table (fill in, then paste back to CopilotBuddy development)

| Field | RVA (hex) | VA (hex) | Evidence (function @ addr) | Verified live? |
|---|---|---|---|---|
| GameState | | | | |
| ObjectManager | | | | |
| PlayerGuid | | | | |
| PlayerTargetGuid | | | | |
| MouseoverGuid | | | | |
| ZoneId / AreaId / SubAreaId | | | | |
| ZoneText | | | | |
| CameraManager / CameraPosOffset | | | | |
| FrameBase | | | | |
| FrameScriptExecuteBuffer | | | | |
| SwapChainPtr | | | | |

---

## 5. Live verification loop (back in CopilotBuddy)

1. Enter found RVAs in `Styx/Offsets/WowBOffsets.cs` (image-relative, hex).
2. `dotnet build -c Release -p:Platform=x64 .` and run the `bin\x64\...` exe
   with WowB running and a character logged in.
3. The attach log prints `WowB snapshot: ...` — check against reality:
   - `inWorld=True` while in the world, `False` at the login screen;
   - `localGuid` ≠ 0 and stable; changes character → changes GUID;
   - `zoneText` matches what you see in-game;
   - `entities` in the hundreds/thousands while in a populated area;
   - camera ≈ your character's coordinates (compare with `/dump GetPlayerMapPosition`-style
     addons or map coordinates × map size).
4. Wrong value → take the symptom back to the agent with the candidate's
   evidence and iterate.

Note: `IsComplete` flips true once `ObjectManager`, `PlayerGuid`,
`PlayerTargetGuid`, `GameState`, `FrameBase`, `FrameScriptExecuteBuffer` are
all non-zero — that is the milestone that unlocks full attach (currently the
bot then continues in read-only mode by design until the executor milestones
land; see `X64_PORT_PLAN.md` §6).

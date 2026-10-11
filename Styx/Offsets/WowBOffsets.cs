namespace Styx.Offsets
{
	/// <summary>
	/// Memory offsets for the modern Classic engine — WowB.exe game version
	/// 1.60.1, build 70291 (World of Warcraft Classic Beta/PTR client).
	///
	/// IMPORTANT: this client is NOT the 3.3.5a engine. The differences
	/// that matter for the bot:
	///
	///  * Native x64 — all pointers are 8 bytes; read them through the
	///    ulong-addressed Memory API (ReadPointer64 / ReadChain64).
<<<<<<< ours
	///  * The object manager is a HASH TABLE, not the 3.3.5a doubly linked
	///    list. Layout constants (verified stable across recent Classic
	///    builds 1.15.x .. 1.60.x):
	///
	///      ClntObjMgr (s_curMgr)
	///        +0x08  entity array (TEntityEntry*[])
	///        +0x10  entity count
	///        +0x40  hash array capacity
	///        +0x48  hash array (entity index buckets)
	///        +0x50  hash array count
	///
	///      TEntityEntry (one per live object)
	///        +0x10  GUID (128-bit: low qword, high qword)
	///        +0x28  pointer to the CGObject-derived instance
	///
	///      CGObject (base of every object)
	///        +0x08  object type (byte: 1=item, 3=container, 4=unit/NPC,
	///               5=player, 6=local player (some builds: 4/5/6/7 vary —
	///               verify on 70291), 7=gameobject, 8=dynamic)
	///        +0x18  GUID (128-bit)
	///
	///  * GUIDs are 128-bit. TargetGuid and LastTargetGuid are two
	///    consecutive qword pairs (last_target = target + 0x10).
=======
	///  * The on-disk executable has an ENCRYPTED/scrambled .text section.
	///    Static analysis must use a live-memory image (see hunt6/hunt8
	///    scripts and WOWB_70334_forum_reference_distilled.md). All RVAs
	///    below were re-derived for 70291 via masked signature scans of a
	///    decrypted live dump and validated against the running process.
	///  * The object manager is a HASH TABLE, not the 3.3.5a doubly linked
	///    list. Layout constants below are engine-level and still need
	///    confirmation against a live entity on 70291.
	///  * GUIDs are 128-bit (two qwords: low, high). The high qword's top
	///    byte encodes the GUID type (0x08 = player observed).
>>>>>>> theirs
	///  * Rendering is DX11/DX12 — there is no d3d9.dll!EndScene to hook;
	///    the Executor needs a new hook point (see X64_PORT_PLAN.md §6).
	///  * Lua taint: many FrameScript paths require a recent hardware
	///    event; HardwareEventPtr feeds that check.
	///
<<<<<<< ours
	/// All ADDRESSES below are image-relative offsets from the WowB.exe
	/// base address and are still TO BE REVERSED for build 70291 —
	/// placeholders are 0. The STRUCTURE constants are engine-level and
	/// already filled in.
=======
	/// All ADDRESSES below are image-relative offsets (RVAs) from the
	/// WowB.exe module base at runtime (ASLR applies — add the live base).
	///
	/// Verification status (2026-10-11, live process, logged-in char):
	///   [V] = read back from the running client and semantically correct
	///         (character name, GUIDs, target switch, map id, camera
	///         position/orientation/FOV all confirmed).
	///   [S] = extracted by masked-signature scan, structurally plausible,
	///         not yet semantically verified live.
	///   [C] = candidate, needs discovery or verification.
>>>>>>> theirs
	/// </summary>
	public static class WowBOffsets
	{
		// ==================== Client Info ====================
		public const int GameBuild = 70291;
		public const string GameVersion = "1.60.1";

		// ==================== Object Manager structure ====================
		// Engine-level layout constants (stable across recent Classic builds).
		public const int ObjMgrEntityArray = 0x8;
		public const int ObjMgrEntityCount = 0x10;
		public const int ObjMgrHashArrayMax = 0x40;
		public const int ObjMgrHashArray = 0x48;
		public const int ObjMgrHashArrayCount = 0x50;
<<<<<<< ours
		public const int ObjMgrEntryStride = 0x30;   // size of one entity entry; verify on 70291

		public const int EntityGuidOffset = 0x10;    // 128-bit GUID in entity entry
		public const int EntityObjectOffset = 0x28;  // CGObject* in entity entry

		public const int ObjectTypeOffset = 0x8;     // byte in CGObject
		public const int ObjectGuidOffset = 0x18;    // 128-bit GUID in CGObject

		// ==================== Global pointers (TO FIND for 70291) ====================
		// Image-relative offsets; 0 = not reversed yet.
		// How to find: see X64_PORT_PLAN.md §6 ("Offset checklist").

		/// <summary>Pointer to the ClntObjMgr structure.</summary>
		public const ulong ObjectManager = 0;        // "objects waiting to be freed" string ref

		/// <summary>Local player GUID (128-bit) — global, not via object manager.</summary>
		public const ulong PlayerGuid = 0;

		/// <summary>Current target GUID (128-bit).</summary>
		public const ulong PlayerTargetGuid = 0;

		/// <summary>Last target GUID — PlayerTargetGuid + 0x10.</summary>
		public const ulong LastTargetGuid = PlayerTargetGuid == 0 ? 0 : PlayerTargetGuid + 0x10;

		/// <summary>Mouseover GUID (128-bit).</summary>
		public const ulong MouseoverGuid = 0;

		/// <summary>Non-zero when the player is in the world.</summary>
		public const ulong GameState = 0;            // Script_IsPlayerInWorld data ref

		/// <summary>Current zone/map ID.</summary>
		public const ulong ZoneId = 0;

		/// <summary>Current area ID.</summary>
		public const ulong AreaId = 0;

		/// <summary>Current sub-area ID.</summary>
		public const ulong SubAreaId = 0;

		/// <summary>Pointer to the zone text string.</summary>
		public const ulong ZoneText = 0;             // subzone/minimap at +0x8/+0x10

		/// <summary>Pointer to the camera manager.</summary>
		public const ulong CameraManager = 0;

		/// <summary>
		/// Offset from the camera manager to the active-camera pointer.
		/// 0x3A58 on recent Classic builds (1.15.x dumps) — VERIFY on 70291.
		/// </summary>
		public const ulong CameraArrayOffset = 0x3A58;

		/// <summary>
		/// Offset of the position floats (x, y, z) inside the camera
		/// structure. TO FIND for 70291.
		/// </summary>
		public const ulong CameraPosOffset = 0;

		/// <summary>Hardware event timestamp pointer (feeds Lua taint checks).</summary>
		public const ulong HardwareEventPtr = 0;

		// ==================== Graphics (DX11 Present hook) ====================
		/// <summary>
		/// Pointer (or short pointer chain) to the live IDXGISwapChain.
		/// Needed by DX11PresentHook64 — the x64 replacement for the
		/// d3d9 EndScene hook. TO FIND for 70291 (usually reachable from
		/// the graphics/device manager near WorldFrame).
		/// </summary>
		public const ulong SwapChainPtr = 0;

		// ==================== Lua (FrameScript) ====================
		/// <summary>Frame base pointer; the Lua state hangs off a chain from it.</summary>
=======
		public const int ObjMgrEntryStride = 0x30;   // size of one entity entry; [C] verify on 70291

		public const int EntityGuidOffset = 0x10;    // 128-bit GUID in entity entry [C]
		public const int EntityObjectOffset = 0x28;  // CGObject* in entity entry [C]

		public const int ObjectTypeOffset = 0x8;     // byte in CGObject [C]
		public const int ObjectGuidOffset = 0x18;    // 128-bit GUID in CGObject [C]

		// ==================== Global pointers (70291 RVAs) ====================

		/// <summary>[V] Pointer to the ClntObjMgr structure (heap).
		/// Accessor function at RVA 0x1FC09C0. The instance contains
		/// pointers + counters (0x5C / 0x1388 observed). Enumeration
		/// layout still to be confirmed on a live entity.</summary>
		public const ulong ObjectManager = 0x7B7B738;

		/// <summary>[V] Local player GUID (128-bit) — global, not via
		/// object manager. Verified nonzero with type byte 0x08.</summary>
		public const ulong PlayerGuid = 0x61C72F0;

		/// <summary>[V] Local player name (null-terminated ASCII,
		/// ~48-byte field). Verified: read back the character name.</summary>
		public const ulong PlayerNamePrimary = 0x61C7300;

		/// <summary>[V] Secondary name component (realm suffix).</summary>
		public const ulong PlayerNameSecondary = 0x61C7331;

		/// <summary>[V] Current target GUID (128-bit). Verified: zero
		/// with no target, valid GUID128 after targeting.</summary>
		public const ulong PlayerTargetGuid = 0x7BDFF38;

		/// <summary>[C] Last target GUID — assumed PlayerTargetGuid + 0x10
		/// (GUID128 size). Unverified.</summary>
		public const ulong LastTargetGuid = PlayerTargetGuid == 0 ? 0 : PlayerTargetGuid + 0x10;

		/// <summary>[S] Target-context state word. Verified 0 with no
		/// target and 0x900 while targeting — treat as opaque state,
		/// not a small enum.</summary>
		public const ulong TargetContextState = 0x61A7497;

		/// <summary>[C] Mouseover GUID (128-bit). Candidate: cursor-mode
		/// byte root verified at 0x6283890; GUID expected at +0x8.</summary>
		public const ulong MouseoverGuid = 0;
		public const ulong CursorMode = 0x6283890;          // [V] u8, 0 observed idle
		public const ulong MouseoverGuidCandidate = 0x6283898; // [C]

		/// <summary>[C] Non-zero when the player is in the world.
		/// Needs hunt7/Ghidra (IsPlayerInWorld references).</summary>
		public const ulong GameState = 0;

		/// <summary>[V] Map root pointer. Dereference, then map id is
		/// int32 at resolved+0x28. Verified: 0 (Eastern Kingdoms).</summary>
		public const ulong MapRoot = 0x7B7B4F8;
		public const ulong MapIdOffset = 0x28;

		/// <summary>[C] Current zone text string pointers (TO FIND —
		/// GetZoneText/GetRealZoneText call sites known from live scan,
		/// e.g. around RVAs 0x1E7C811/0x1E7BE7E; decompile in Ghidra).</summary>
		public const ulong ZoneId = 0;
		public const ulong AreaId = 0;
		public const ulong SubAreaId = 0;
		public const ulong ZoneText = 0;

		// ==================== Camera ====================
		// [V] FULLY VERIFIED live (map 0 world coords, orthonormal basis,
		// FOV 90 deg):
		//   [base + CameraWorldFrameGlobal] -> world frame object
		//   [frame + CameraPointerOffset]   -> camera object
		//   camera + CameraOriginOffset     -> x,y,z floats (vec3)
		/// <summary>[V] Global read by GetCameraZoom (fn 0x312CD70).</summary>
		public const ulong CameraWorldFrameGlobal = 0x5FF99F0;
		public const ulong CameraPointerOffset = 0x490;
		public const ulong CameraOriginOffset = 0x10;    // vec3 float
		public const ulong CameraForwardOffset = 0x1C;   // vec3 float
		public const ulong CameraLeftOffset = 0x28;      // vec3 float
		public const ulong CameraUpOffset = 0x34;        // vec3 float
		public const ulong CameraFovOffset = 0x40;       // horizontal FOV, radians
		public const ulong CameraZoomOffset = 0x74;      // zoom factor (GetCameraZoom)

		/// <summary>Legacy aliases used by the camera reader.</summary>
		public const ulong CameraManager = CameraWorldFrameGlobal;
		public const ulong CameraArrayOffset = CameraPointerOffset;
		public const ulong CameraPosOffset = CameraOriginOffset;

		// Camera rig/view-state objects (contain zoom distance ~13.7,
		// pitch/yaw parameters), reached via frame root 0x7B7B978 slots
		// +0xF0/+0xF8/+0x110/+0x118/+0x130/+0x138 and alt frame root
		// 0x5FF19F8 slot +0xED8. [S] — not needed for world-to-screen yet.
		public const ulong CameraRigFrameRoot = 0x7B7B978;

		// ==================== Handle pools / entities ====================
		/// <summary>[V] Handle-pool table root (generation-checked handle
		/// resolution; pool index = handle >> 0x38). qword[1] observed
		/// equal to the ObjectManager instance pointer.</summary>
		public const ulong HandlePools = 0x582D150;
		public const ulong HandlePoolIndexShift = 0x38;

		// ==================== Quest / action-bar globals ====================
		public const ulong QuestSelectedId = 0x6305B88;    // [V] u32, 0 idle
		public const ulong StoredActionArray = 0x7DFF4E0;  // [V] 0x198 u64 slots
		public const ulong QuestV2Table = 0x7B538C0;       // [S]
		public const ulong QuestV2Provider = 0x5DF5F50;    // [S]

		// ==================== Functions (70291 RVAs) [S] ====================
		public const ulong FnObjectManagerAccessor = 0x1FC09C0;
		public const ulong FnQuestV2PrimaryLookup = 0x676F30;
		public const ulong FnQuestHistoryPerfectHash = 0x686200;
		public const ulong FnHealthScale = 0x26E1CB0;
		public const ulong FnPowerIndex = 0x281FE80;
		public const ulong FnPowerAccessor = 0x2841300;
		public const ulong FnGetCameraZoom = 0x312CD70;

		// ==================== Actor fields (from unit object) [S] ====================
		// Extracted via signatures; identical to reference build; pending
		// live confirmation on an entity object (needs OM enumeration).
		public const int ActorPredictedHealth = 0x1BC0;  // int64
		public const int ActorHealthCurrent = 0x1E30;    // int64
		public const int ActorHealthMax = 0x1E38;        // uint64
		public const int ActorPowerContainer = 0x1BC8;   // class-specific
		public const int ActorUnitFlags = 0x1E60;        // combat bit 0x80000
		public const int ActorCombatFlagMask = 0x80000;
		public const int ActorNamePointer = 0x238;
		public const int ActorNameAltSelector = 0x1D3C;
		public const int ActorLootDeadline = 0x4DC;
		public const int NpcDynamicFlags = 0x94;         // loot 0x4, tap 0x10

		// Transform (position) fields on the world-reference object [S]:
		public const int TransformState = 0x20;          // dirty 0x40, parented 0x2
		public const int TransformLocalXY = 0x38;
		public const int TransformLocalZ = 0x40;
		public const int TransformWorldXY = 0x7C;        // +0x7C x, +0x80 y
		public const int TransformWorldZ = 0x84;

		// ==================== Graphics (DX11 Present hook) ====================
		/// <summary>
		/// [C] Pointer (or short pointer chain) to the live IDXGISwapChain.
		/// Needed by DX11PresentHook64. TO FIND for 70291 (strings
		/// "CreateSwapChainForHwnd failed" / "pm_dx11::Device::..."
		/// exist in the image; find their live references via Ghidra).
		/// </summary>
		public const ulong SwapChainPtr = 0;

		/// <summary>[C] Hardware event timestamp pointer (Lua taint).</summary>
		public const ulong HardwareEventPtr = 0;

		// ==================== Lua (FrameScript) ====================
		/// <summary>[C] Frame base pointer; the Lua state hangs off a
		/// chain from it. Registration table (luaL_Reg pairs) confirmed
		/// in .data — e.g. CameraZoomIn entry observed; handlers live in
		/// decrypted .text. Full FrameScript globals TO FIND.</summary>
>>>>>>> theirs
		public const ulong FrameBase = 0;

		// Classic-era chain (verify on 70291): FrameBase -> +0xF18 -> +0x148 -> Lua state
		public const ulong FrameOffset1 = 0xF18;
		public const ulong FrameOffset2 = 0x148;

		/// <summary>FrameScript_ExecuteBuffer — runs a Lua string.</summary>
		public const ulong FrameScriptExecuteBuffer = 0;

		/// <summary>FrameScript_GetTop.</summary>
		public const ulong FrameScriptGetTop = 0;

		/// <summary>FrameScript_PCall.</summary>
		public const ulong FrameScriptPCall = 0;

		/// <summary>FrameScript_ToLString.</summary>
		public const ulong FrameScriptToLString = 0;

		/// <summary>FrameScript_Load.</summary>
		public const ulong FrameScriptLoad = 0;

		// ==================== Object manager functions ====================
		/// <summary>ClntObjMgrEnumVisibleObjects(callback, filter).</summary>
		public const ulong ClntObjMgrEnumVisibleObjects = 0;

		/// <summary>ClntObjMgrGetActivePlayerObj — returns the local player object.</summary>
		public const ulong ClntObjMgrGetActivePlayerObj = 0;

		/// <summary>ClntObjMgrObjectPtr(mapId, guidLow) — object lookup.</summary>
		public const ulong ClntObjMgrObjectPtr = 0;

		// ==================== UI / interaction ====================
		/// <summary>CGGameUI::Target(guid).</summary>
		public const ulong CGGameUITarget = 0;

		// ==================== Spells / movement ====================
		/// <summary>Spell_C::CastSpell (caller-side signature varies by build).</summary>
		public const ulong SpellCCastSpell = 0;

		/// <summary>Spell_C::GetSpellCooldown.</summary>
		public const ulong SpellCGetSpellCooldown = 0;

		/// <summary>CGPlayer_C::ClickToMove.</summary>
		public const ulong ClickToMove = 0;

		// ==================== Line of sight ====================
		/// <summary>CGWorldFrame::Intersect (TraceLine).</summary>
		public const ulong TraceLine = 0;

		/// <summary>
		/// True once every required offset above has been filled in for
		/// build <see cref="GameBuild"/>. Until then the bot must refuse
		/// to attach to this client with a clear message.
		/// </summary>
		public static bool IsComplete =>
			ObjectManager != 0 && PlayerGuid != 0 && PlayerTargetGuid != 0 &&
			GameState != 0 && FrameBase != 0 && FrameScriptExecuteBuffer != 0;
	}
}

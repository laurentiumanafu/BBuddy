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
	///  * Rendering is DX11/DX12 — there is no d3d9.dll!EndScene to hook;
	///    the Executor needs a new hook point (see X64_PORT_PLAN.md §6).
	///  * Lua taint: many FrameScript paths require a recent hardware
	///    event; HardwareEventPtr feeds that check.
	///
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
		public const ulong CameraZoomOffset = 0x274;     // zoom factor (GetCameraZoom)
		// NOTE: decompiled GetCameraZoom (fn 0x312CD70) pushes *(float*)(camera+0x274).
		// An earlier live probe read 1.0 at +0x74 by coincidence (default zoom is 1.0);
		// +0x274 is authoritative per the decompilation.

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
		/// <summary>[C] Frame base pointer. On 70291 the FrameScript
		/// subsystem is GLOBAL-based, not FrameBase-chain-based:
		/// see LuaStateGlobal / registration tables below. Left 0 until
		/// hunt8 confirms whether a frame base exists at all.</summary>
		public const ulong FrameBase = 0;

		// Classic-era chain (kept for reference; likely NOT used on 70291)
		public const ulong FrameOffset1 = 0xF18;
		public const ulong FrameOffset2 = 0x148;

		// ---- FrameScript core (hunt7, Ghidra decompilation) ----

		/// <summary>[S] The FrameScript Lua state pointer global. The
		/// API-unregistration/shutdown function (0x2995320) ends by
		/// null-checking this and passing it to the lua_close-like
		/// function FnLuaCloseCandidate. Live-verify: [base+this] should
		/// be a heap pointer whose +0x40/+0x68 fields are stack ptrs
		/// (stride 0x38 per lua value, see FnFrameScriptHandleError).</summary>
		public const ulong LuaStateGlobal = 0x7B56AB8;
		public const ulong FnLuaCloseCandidate = 0x471BD00;

		/// <summary>[S] FrameScript error reporter — tail-called by the
		/// small taint/error stub handlers as FUN(L, 0, "ApiName").
		/// Formats "%s(): %s" from FrameScriptErrorMsgPtr/Flag and pushes
		/// the string to Lua via FnFrameScriptPushErrorString.
		/// NOT the API dispatcher (hunt8 corrected hunt7's guess).</summary>
		public const ulong FnFrameScriptReportError = 0x749800;
		public const ulong FrameScriptErrorMsgPtr = 0x589B720;   // [S] char* buffer
		public const ulong FrameScriptErrorMsgFlag = 0x589B728;  // [S]
		public const ulong FnFrameScriptPushErrorString = 0x471BDD0; // [S]

		/// <summary>[S] FrameScript_RegisterFunction(name, handler).
		/// Walked over (namePtr, handlerPtr) 16-byte tables.
		/// (hunt8: corrected from a bad RVA transcription.)</summary>
		public const ulong FnFrameScriptRegisterFunction = 0x75AF40;

		/// <summary>[S] FrameScript namespace registration
		/// (table, count, "C_XXX").</summary>
		public const ulong FnFrameScriptRegisterNamespace = 0x75A930;

		/// <summary>[S] FrameScript shutdown tail call (called after the
		/// state object's [+0x28]+8 u16 is zeroed).</summary>
		public const ulong FnFrameScriptShutdownTail = 0x4718E50;

		/// <summary>[S] FrameScript_HandleError (string-confirmed).
		/// lua_State layout hints: stack ptrs at L+0x40 / L+0x68,
		/// TValue stride 0x38. Error-halt flag at 0x617C94F,
		/// error count at 0x61A5BD8 (halt at 100).</summary>
		public const ulong FnFrameScriptHandleError = 0x759520;
		public const ulong FrameScriptErrorHaltFlag = 0x617C94F;
		public const ulong FrameScriptErrorCount = 0x61A5BD8;

		/// <summary>[S] FrameScript_GetGlobalString(name) — e.g. used
		/// for "ERROR_SCRIPT_EXECUTION_HALT".</summary>
		public const ulong FnFrameScriptGetGlobalString = 0x7618B0;

		/// <summary>[S] Global zeroed by every API handler prologue
		/// (secure/taint context).</summary>
		public const ulong FrameScriptApiContextGlobal = 0x5AA2BB8;

		// ---- API registration tables in .data (namePtr, handlerPtr) pairs,
		// 16 bytes per entry. FORMAT CONFIRMED by live-image dump of the
		// GetText table (hunt8). Reading these yields handler RVAs for
		// dozens of APIs (movement APIs live in the JumpOrAscendStart table).
		// hunt9 round: addresses corrected (+0x20000 arithmetic fix).
		public const ulong RegTableGetText = 0x5DF7480;              // 14 entries (dumped OK)
		public const ulong RegTableGetNumBindings = 0x58CFBF0;       // 27
		public const ulong RegTableSecureCmdOptionParse = 0x590C2A0; // 21
		public const ulong RegTableJumpOrAscendStart = 0x58CF2B0;    // 58 (movement!)
		public const ulong RegTableCameraZoomIn = 0x5958FE0;         // 22 (camera)
		public const ulong RegTablePlayMusic = 0x58C5D50;            // 16
		public const ulong RegTableSpellIsTargeting = 0x58DE300;     // 25
		public const ulong RegTableGetActionInfo = 0x590CB20;        // 9
		public const ulong RegTableGetNumFlyouts = 0x593B0B0;        // 5
		public const ulong RegTableIsChatAFK = 0x58FA800;            // 66
		public const ulong RegTableSetSpellbookPetAction = 0x59095F0;// 31
		public const ulong RegTableGetInventoryItemsForSlot = 0x5940780; // 29
		public const ulong RegTableSetLootPortrait = 0x5909AB0;      // 23
		public const ulong RegTableItemTextGetItem = 0x5950880;      // 10
		public const ulong RegTableCloseQuest = 0x5944C80;           // 65
		public const ulong RegTableGetQuestLogQuestText = 0x59049F0; // 68
		public const ulong RegTableSetTaxiMap = 0x5946160;           // 16
		public const ulong RegTableGetTrainerServiceInfo = 0x5947280;// 7
		public const ulong RegTableOpenTrainer = 0x59473C0;          // 15
		public const ulong RegTableCountMax = 68;

		// Known handlers harvested from the GetText table (hunt8 dump):
		// GetText 0x4572780, EnumerateFrames 0x4573010, CreateFont 0x4573D90,
		// CreateFrame 0x4575E10, CreateForbiddenFrame 0x4575E20,
		// DoesTemplateExist 0x4575E30, GetFramesRegisteredForEvent 0x4576650,
		// GetCurrentKeyBoardFocus 0x4576F90, StoreSecureReference 0x45777F0,
		// GetFontStringMetatable 0x45786F0, GetEditBoxMetatable 0x4578710,
		// GetFrameMetatable 0x4578730, GetButtonMetatable 0x4578750,
		// GetTextureMetatable 0x4578770.  (Frame/XML API cluster ~0x457xxxx.)
		public const ulong FnCreateFrame = 0x4575E10;        // [S] from table dump
		public const ulong FnEnumerateFrames = 0x4573010;    // [S]
		public const ulong FnGetFrameMetatable = 0x4578730;  // [S]

		// ---- Unit token / object resolution (hunt7) ----

		/// <summary>[S] UnitGUID handler's token -> GUID128 resolver
		/// (returns pointer to 16-byte GUID128). RVA corrected hunt9.</summary>
		public const ulong FnUnitTokenToGuid = 0x26ACAA0;

		/// <summary>[S] FrameScript unit-token argument parser
		/// ("Usage: local result = UnitGUID(unit)").</summary>
		public const ulong FnUnitTokenArgParser = 0x1FDDF40;

		/// <summary>[S] GUID/handle -> unit object resolver (used by the
		/// mouseover emitter FUN_2D61100, which reads the GUID at
		/// param+0x10). Decompiling this should expose the handle-pool /
		/// ObjMgr lookup path. RVA corrected hunt9. Companion emitter
		/// helper (unit -> token string) at 0x2CC2150.</summary>
		public const ulong FnGuidToUnitObject = 0x2CC15A0;
		public const ulong FnUnitObjectToToken = 0x2CC2150;

		/// <summary>[S] Unit token id -> token name switch table fn
		/// (player/target/mouseover/focus/raid1..40/...).</summary>
		public const ulong FnUnitTokenNameMap = 0x414DA10;

		/// <summary>[C] Zone/map state root loaded by GetRealZoneText
		/// handler right before dispatch.</summary>
		public const ulong ZoneStateRoot = 0x7DF8190;

		// Camera rig globals seen in the zoom-handler prologues:
		// DAT 0x1ACF650 = init flag, DAT 0x5FF19F8 = rig/frame pointer
		// checked non-null before camera ops.
		public const ulong CameraRigInitFlag = 0x1ACF650;
		public const ulong CameraRigPointer = 0x5FF19F8;
		// [!] 0x269B450 (tail call of the zoom handlers) decompiles to
		// OBFUSCATED code (pcode errors, self-modifying bytes) — do not
		// rely on static analysis there; use the live-verified chain above.
		public const ulong FnCameraZoomImpl = 0x269B450;

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

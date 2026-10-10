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
	///  * Rendering is DX11/DX12 — there is no d3d9.dll!EndScene to hook;
	///    the Executor needs a new hook point (see X64_PORT_PLAN.md §6).
	///  * Lua taint: many FrameScript paths require a recent hardware
	///    event; HardwareEventPtr feeds that check.
	///
	/// All ADDRESSES below are image-relative offsets from the WowB.exe
	/// base address and are still TO BE REVERSED for build 70291 —
	/// placeholders are 0. The STRUCTURE constants are engine-level and
	/// already filled in.
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

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
<<<<<<< ours
=======
		// hunt15: 0x7B7B978 also read by the player-creation fn (dual role:
		// OM-region global, statically 0 in the decoded image).
>>>>>>> theirs
		public const ulong CameraRigFrameRoot = 0x7B7B978;

		// ==================== Handle pools / entities ====================
		/// <summary>[V] Handle-pool table root (generation-checked handle
		/// resolution; pool index = handle >> 0x38). qword[1] observed
		/// equal to the ObjectManager instance pointer.</summary>
		public const ulong HandlePools = 0x582D150;
		public const ulong HandlePoolIndexShift = 0x38;
<<<<<<< ours
=======
		// hunt24 LIVE (2026-10-13): HandlePools[0] == base+0x5F9FBF0 exactly
		// (static slot prediction proven); HandlePools[1] == the ObjectManager
		// global pointer => the OM global IS a handle-pool object.
		// Pool header layout (live): {q0=init flag, elems@+0x08, cap@+0x10,
		// maxOrCount@+0x18}. Slot1 live: elems=heap, cap=91, q3=5000, 91/91
		// slots occupied. Element (16 B): {dword tag @+0 (bit31 = occupied),
		// dword unknown @+4, record ptr @+8}. Observed tags: 0x80000000
		// (occupied, low30 = 0), 0xC0000000, 0xBFFFFFFF (pool0 slot0).
		// Record strides seen: 0x60 and 0x110 => two record classes.
		// [!] The hunt15 chain "obj = qword[qword[entry+8]+8]" is WRONG as a
		// unit pointer: it returns one SHARED value (e.g. 0x15DCCCDAB20 with
		// typeId 0xFFFFFFFF) for many entries. The real unit pointer lives
		// elsewhere in the record; hunt25 auto-detects it (sane xyz @+0x60
		// AND small typeId @+0xA8).
		public const ulong HandlePoolRecordOffset = 0x8;      // entry+8 -> record
		public const ulong HandlePoolCapacityField = 0x10;    // p[1] used by lookup
		public const ulong HandlePoolMaxOrCount = 0x18;       // q3 (5000 on slot1)
		// hunt25 LIVE: HandlePools[1] IS the ObjectManager (same pointer as
		// global 0x7B7B738) and the OM object starts with THE POOL HEADER at
		// +0x00 {flag=1, elems, cap=92, max=5000}. The two OM maps
		// (OM+0x90, OM+0x1A8) are GUID -> pool-slot tables, stride 0x20,
		// first entry at +0x20 (0x00..0x1F = zero/hash header):
		//   +0x00 u32 id   +0x04 u32 0     +0x08 u64 guid
		//   +0x10 u32 poolSlot   +0x14 u32 flags   +0x18 u64 recordPtr
		// Verified cross-check: (poolSlot, recordPtr) pairs in the maps are
		// byte-identical to the pool's own element array entries
		// (elems + slot*0x10 -> qword+8), e.g. mapSlot 76 -> 0x15D1C73CD30
		// == pool k=76 rec. => Entity enumeration =
		//   for each map entry: guid, slot, rec; read rec fields directly.
		// NOTE: guid has its OWN encoding here, different from the Lua
		// GetGUID string/hi-word form: map guids look like
		// 0x200118000000EC40 / 0x2C01180000201400 / 0x18000D4000000000 /
		// 0x0400000000000000 -- "family+type" nibbles live in the HIGH half,
		// while the Lua-level player guid was {lo=0x2, hi=0x0801180000000000}.
		public const ulong EntityMapEntryStride = 0x20;
		public const ulong EntityMapEntryBase = 0x20;
		public const ulong EntityMapEntryIdOffset = 0x00;
		public const ulong EntityMapEntryGuidOffset = 0x08;
		public const ulong EntityMapEntrySlotOffset = 0x10;
		public const ulong EntityMapEntryFlagsOffset = 0x14;
		public const ulong EntityMapEntryRecordOffset = 0x18;
		// ---- WowBHunt24 v2 log: FnEntityGetGuid 0x2BBB7F0 (46-line clean
		// decompile) proves the whole entity<->GUID path:
		//   idx  = *(u64*)(entity + 0x398) & 0xFFFFFFFF   (packed handle)
		//   fam  = *(u8*)(entity + 0x39F)                 (handle top byte)
		//   pool = *(u64*)(HandlePools + fam*8)
		//   entry= FnPoolHashLookup(pool+8, idx)
		//   rec  = *(u64*)(entry + 8)
		//   chk  = *(u8*)(*(u64*)(rec + 8) + 0x10) & 0x20   (entity valid flag)
		//   GUID128 = { *(u64*)(rec + 0x18), *(u64*)(rec + 0x20) }
		// Guard fn 0x2BBB770 (22 lines) re-runs the same validation.
		// [!] hunt27 LIVE REFUTED "record+0x08 == the unit object". On the
		// player record (slot 16, guid {2,0x0801180000000000})
		// *(rec+0x08) -> 0x15DCD9B37A0 has +0x398 = 0xFFFFFFFFFFFFFFFF and
		// +0x39F = 0xFF (no packed handle) => it is NOT the unit. It still
		// satisfies the guard byte (u8@+0x10 = 0x39 has bit 0x20) and holds
		// a back-reference to the ObjectManager at its own +0x08. The unit
		// object hunt continues in hunt28; do NOT use it as the unit ptr.
		public const ulong EntityHandleOffset = 0x398;      // u64 packed handle
		public const ulong EntityFamilyByteOffset = 0x39F;  // u8 (handle >> 0x38)
		public const ulong EntityValidBoolOffset = 0x08;    // u8 must be non-zero
		public const ulong EntityFlagsOffset = 0x10;        // dword, bit 0x20 = valid
		public const ulong EntityValidFlagMask = 0x20;
		public const ulong EntityAltFlagOffset = 0x3AC;     // u8 checked by guard
		public const ulong RecordEntityPtrOffset = 0x08;    // u64 -> entity object
		public const ulong RecordGuidLoOffset = 0x18;       // u64 GUID low qword
		public const ulong RecordGuidHiOffset = 0x20;       // u64 GUID high qword
		public const ulong RecordSlotOffset = 0x00;         // u32 = own pool index
		public const ulong RecordFlagsOffset = 0x04;        // u32 0x01000000|tagbits
		public const ulong RecordCounterOffset = 0x10;      // u32 sequence
		public const ulong RecordPoisonDword = 0xCECECE00;  // uninitialised marker
		public const ulong FnEntityGuard = 0x2BBB770;       // [S] guard (22 lines)
		// ---- hunt27 LIVE (2026-10-13): the pool RECORD carries the game
		// object's world transform and its own packed handle. Camera
		// cross-check on the player record (rec = 0x15D1C73B1A0, slot 16):
		//   rec+0x60 pos = (-6180.625, 359.458, 398.718); camera origin =
		//   (-6192.04, 369.10, 401.43). camera - pos = -15.186 * forward
		//   + 2.71 * up, i.e. the camera sits exactly BEHIND and slightly
		//   ABOVE the player (horizontal dot with the basis col0 = -0.99999,
		//   residual = the camera's 2.7 yd height) => +0x60 IS the player's
		//   world position.
		//   The basis @+0x80 is a clean orthonormal Z-rotation, col-major:
		//     col0 = ( cos f,  sin f, 0)      f = facing @+0x70
		//     col1 = (-sin f,  cos f, 0)      |col0| = |col1| = 1.000000,
		//     col2 = ( 0,      0,     1)      col0 . col1 = 0.000000
		//   and atan2(-col0.y, col0.x) = 0.705461 == 2*pi - f (f = 5.577724)
		//   to all printed digits => position/facing/rotation PROVEN live.
		//   +0x00 u64 packed handle == {lo32 = pool slot 16, mid24 = 0,
		//          top8 = pool/family 1} - exactly the shape FnEntityGetGuid
		//          consumes, and consistent with the element tag 0x80000000
		//          (generation bits 0).  +0x18/+0x20 = GUID128;
		//   +0x48 u32 flags (0x206 observed);  +0x6C float 1.0;
		//   +0xA4 position copy;  +0xD8 pointer (reference-build "data
		//   block" slot) [C] - chain probed by hunt28.
		//   forward = (cos f, sin f, 0) - use it for navigation maths.
		// ---- hunt28 LIVE (2026-10-13) - the record model is now complete:
		//  * The pool's 16-byte elements point straight at the record; the
		//    records live in two flavours inside arena allocations:
		//      class A: 0x60-stride (non-world/placeholder records - flags
		//               @+0x48 read the 0xCECECECE uninitialised marker and
		//               +0x60 is NOT a position);
		//      class B: 0x110-stride (real world objects - transform at
		//               +0x60/+0x70/+0x80, data block pointer at +0xD8).
		//    Observed live: slots 2..15 = class A, slots 1 and 16..22 =
		//    class B, all inside the SAME pool (family 2 @ that moment).
		//  * rec+0x00 = packed handle (slot | generation<<32 | family<<56):
		//    for every occupied slot the low dword equals the element index
		//    (audit: 82/82 records matched on family byte, generation 0).
		//  * rec+0x04 = family << 24  (0x02000000 for family 2,
		//    0x01000000 for family 1) - second confirmation site of family.
		//  * rec+0x08 = owning arena/zone descriptor (NOT the unit object:
		//    82 records shared only 6 distinct values; its +0x08 points at
		//    the pool object and +0x10 holds flags 0x39/0x3B).
		//  * rec+0x10 = sequence counter, rec+0x14 = 0xCECECE00 poison,
		//    rec+0x40 = EACECECECECECECE poison, rec+0x48 = object flags
		//    (0x206 player, 0x217 npc/other, 0x17 npc slot 1 - and
		//    0xCECECECE when the record was never initialised).
		//  * rec+0xD8 = unit DATA BLOCK: a C++ object whose +0x00/+0x28/
		//    +0xA0/+0xB0 hold module pointers, +0x08 points back at the
		//    record, +0x10/+0x18 mirror GUID lo/hi and +0x38 mirrors the
		//    packed handle. Its stat fields match the reference layout:
		//      data+0x1E30 u32 health      (live: 0x54 = 84)
		//      data+0x1E38 u32 maxHealth   (live: 0x54 = 84)
		//      data+0x1BC8 u32 power       (live: 0x60 = 96)
		//      data+0x1BD0 u32 maxPower    [C] (print in hunt29)
		//      data+0x1E60 u32 unitFlags   (live: 8, next dword 0x800)
		//      data+0x238     name pointer [C] - hunt29 harvests strings
		//    (health/max pair + power are exactly the values a fresh
		//     classic character shows, to be eyeball-confirmed in hunt29.)
		//  * The player NAME is stored INLINE in the module at
		//    PlayerName (0x61C7300) - "Brutallex" visible as raw ASCII,
		//    no pointer chase needed.
		//  * Within +/-0x10000 of the record there is exactly ONE copy of
		//    the packed handle (at rec+0x00) and NO copy of the name string
		//    => the FnEntityGetGuid "entity" object is NOT adjacent to the
		//    record; it is a separate heap object we no longer need.
		public const ulong RecordHandleOffset = 0x00;       // [V] u64 packed handle
		public const ulong RecordFamilyShift = 24;          // [V] dword @+0x04
		public const ulong RecordSequenceOffset = 0x10;     // [V] u32 counter
		public const ulong RecordPoisonDword2 = 0xCECECE00; // [V] @+0x14
		public const ulong RecordPoisonQword = 0xEACECECECECECECEUL; // @+0x40
		public const ulong RecordPositionOffset = 0x60;     // [V] float x,y,z (class B)
		public const ulong RecordFacingOffset = 0x70;       // [V] float radians 0..2pi
		public const ulong RecordMatrixOffset = 0x80;       // [V] 3x3 basis, column-major
		public const ulong RecordPositionCopyOffset = 0xA4; // [V] second copy of x,y,z
		public const ulong RecordObjectFlagsOffset = 0x48;  // [V] u32 (0x206 / 0x217 / 0x17)
		public const ulong RecordClassAStride = 0x60;       // [V] non-world records
		public const ulong RecordClassBStride = 0x110;      // [V] world records (transform)
		public const ulong RecordDataBlockPtrOffset = 0xD8; // [V] -> unit data block
		public const ulong RecordOwnerDescriptorOffset = 0x08; // [V] arena/zone descriptor
		// ---- Unit data block (*(record+0xD8)) stat fields: reference
		// layout survives on 70291 (health pair confirmed numerically).
		public const ulong UnitDataHealthOffset = 0x1E30;    // u32 health
		public const ulong UnitDataMaxHealthOffset = 0x1E38; // u32 max health
		public const ulong UnitDataPowerOffset = 0x1BC8;     // u32 power (mana/rage/...)
		public const ulong UnitDataMaxPowerOffset = 0x1BD0;  // [C] u32 max power
		public const ulong UnitDataUnitFlagsOffset = 0x1E60; // u32 unit flags
		/// <summary>[V-ish] hunt31 §6: level. Player 2 / NPC 5 in the live
		/// sample, and the two neighbouring qwords (+0x1E48/+0x1E50) carry
		/// the same value on the NPC (effective level / scaling level).
		/// Fields are 8-byte spaced (health 0x1E30, maxHealth 0x1E38) —
		/// consistent with the modern int64 descriptor layout.
		/// Still to be eyeballed against the character sheet: hunt32 prints
		/// it for every record it validates as a real unit.</summary>
		public const ulong UnitDataLevelOffset = 0x1E40;     // u32 level [V-ish]
		/// <summary>[V] hunt31: the unit object stores its own PACKED HANDLE
		/// at +0x38 (e.g. 0x0200000000000010 for the player, slot 16).
		/// This is the value FnEntityGetGuid expects a pointer to — and it
		/// is NOT at +0x398 on this object (see the Phase-3 note below).</summary>
		public const ulong UnitDataHandleOffset = 0x38;      // u64 packed handle [V]
		public const ulong UnitDataNamePtrOffset = 0x238;    // [C] char* name / 0
		// CORRECTED map entry semantics (30/30 cross-checked vs the pool):
		//   +0x00 u32 = GUID lo32 (== record+0x18 low32)
		//   +0x08 u64 = GUID hi64 (== record+0x20)
		//   +0x10 u32 = POOL SLOT (== handle low32 == lookup index)
		//   +0x14 u32 = 0x01000000 flags
		//   +0x18 u64 = RECORD pointer
		// Entity enumeration for the reader: walk both OM maps; each entry
		// yields the full GUID, the pool slot and the record; record+0x08 is
		// the unit object. The local player is NOT in the maps - locate it by
		// scanning the pools for record+0x18/+0x20 == the player GUID pair
		// (live-verified: pool slot 16 held {2, 0x0801180000000000}).

		// ---- Entity resolution algorithm (hunt15 decompile of the client's
		// own player-creation fn 0x271F2F + hunt17 clean C bodies of the
		// internals). The GUID -> entity path, verbatim from the C output:
		//   pool  = qword[HandlePools + (guid >> 0x38) * 8]   (null => error)
		//   idx   = guid & 0xFFFFFFFF   (exact derivation being live-checked
		//           by hunt23.ps1 - see HandlePoolIndexCandidates below)
		//   entry = FnPoolHashLookup(pool + 8, idx)
		//   valid iff ((guid >> 32 & 0xFFFFFF) ^ dword[entry]) & 0x3FFFFFFF == 0
		//         and dword[entry] < 0            (bit31 = occupied flag)
		//   obj   = qword[qword[entry + 8] + 8]  (see FnObjectAccessor)
		//   type  = dword[obj + 0xA8]
		// hunt17 CLEAN bodies prove the two internals are trivial wrappers:
		//   FnPoolHashLookup(p, idx): if (p[1] <= idx) grow(0x27FAE0);
		//                             return idx*0x10 + *p;
		//     => p = pool+8 = {elemArray @ +0x00, capacity @ +0x08}; ELEMENT
		//        STRIDE IS 0x10. There is NO hashing inside - the caller
		//        supplies the index. CopilotBuddy can replicate the whole
		//        read-only GUID -> object path with plain memory reads.
		//   FnObjectAccessor(p): if (*p) return *p; else lazy-init via
		//     0x27F520 then return *p. => param is &cachedPtr, never the obj.
		//   The hunt15 "factory" call was RE-CLASSIFIED in hunt17: 0x33CB00
		//     is a LOCKed container insert (std::unordered_map-ish), so the
		//     0x7DE4340 lookup + 0x33CB00 call registers an instance in a
		//     registry map - it does NOT instantiate a class.
		public const ulong HandlePoolHashOffset = 0x8;      // pool+8 = {elems, capacity}
		public const ulong HandlePoolArrayOffset = 0x8;     // qword[pool+8]   = element array
		public const ulong HandlePoolCapacityOffset = 0x10; // qword[pool+0x10] = capacity
		public const ulong HandlePoolElementStride = 0x10;  // entry = elems + idx*0x10
		public const ulong HandleValidMask = 0x3FFFFFFF;    // generation-check mask
		public const ulong HandleOccupiedBit = 0x80000000;  // bit31 of dword[entry]
		public const ulong EntityObjectOffset = 0x8;        // entry+8 -> obj header
		public const ulong EntityTypeIdOffset = 0xA8;       // dword: object type id
		public const ulong TypeFactoryTable = 0x7DE4340;    // [S] qword[type] -> registry slot
		public const ulong FnPoolHashLookup = 0x27FBD0;     // [S] entry = *p + idx*0x10 (+grow)
		public const ulong FnPoolGrow = 0x27FAE0;           // [S] grow/rehash, called by above
		public const ulong FnObjectAccessor = 0x27F600;     // [S] cached-ptr getter
		public const ulong FnObjectAccessorLazyInit = 0x27F520; // [S] called when cache null
		public const ulong FnFactoryDispatch = 0x33CB00;    // [S] LOCKed container insert
		public const ulong FnContainerInsert = 0x33CB00;    // alias (hunt17 semantics)
		/// <summary>[S] Entity GetGUID method (entity-GUID table 0x4DC4968
		/// slot +12). hunt14: clean 46-line decompile; reads HandlePools
		/// and calls FnPoolHashLookup — second confirmation site of the
		/// entity-resolution algorithm.</summary>
		public const ulong FnEntityGetGuid = 0x2BBB7F0;
		/// <summary>[S] targetGUID -> NameCachedData lookup (hunt14, clean
		/// 318 lines). Reads PlayerTargetGuid (0x7BDFF38).</summary>
		public const ulong FnTargetGuidNameLookup = 0x244466;
		/// <summary>[V] live (hunt30): *(record+0xD8)+0x00 for the local
		/// player reads base+0x50A7920 — the NPC/unit vtable for the same
		/// field is CGUnitFnTable below (base+0x50CDDC0). NOTE the value is
		/// 0x050A7920, not 0x050127920 (the earlier constant was a hex
		/// typo; the hunt30 log prints vtable=base+0x50A7920).</summary>
		public const ulong PlayerObjectVtable = 0x50A7920;   // [V] live hunt30
		public const ulong EntityGuidTable = 0x4DC4968;     // [S] 13 slots
		public const ulong CGUnitFnTable = 0x50CDDC0;       // [V] live unit vtable
		/// <summary>[V] hunt31: the unit object's back-pointer to its record.</summary>
		public const ulong UnitObjectBackPtrOffset = 0x08;
		/// <summary>[V] hunt31: the unit object repeats the 128-bit GUID
		/// (record+0x18/+0x20) at +0x10/+0x18.</summary>
		public const ulong UnitObjectGuidLoOffset = 0x10;
		public const ulong UnitObjectGuidHiOffset = 0x18;
		/// <summary>[V] hunt31: third and fourth vtables seen at rec+0xD8.
		/// 0x50B1838 slots have 0x15D at +0x398, 0x50D8258 has float 1.0
		/// there - proof that +0x398 is not a handle field on those shapes.
		/// They are accepted by the structural (back-pointer + GUID) test
		/// only when the record shape actually matches.</summary>
		public const ulong UnitObjectVtable3 = 0x50B1838;
		public const ulong UnitObjectVtable4 = 0x50D8258;
		public const ulong ClientConnectionVtableA = 0x507DAC8; // [S] 12 slots
		public const ulong ClientConnectionVtableC = 0x507DB70; // [S] 11 slots
		/// <summary>[S] Unidentified GUID helper called by the UnitGUID
		/// (0x1D5AD60) and UnitOwnerGUID (0x1D7DF74) impls. NOT the
		/// token->GUID resolver (0x26ACAA0).</summary>
		public const ulong FnGuidHelperUnknown = 0x26CCAA0;
		/// <summary>[S] Player-creation / spawn fn (449-line clean decompile,
		/// hunt15). Reads MapRoot + 0x7B7B978 + SpawnFnGlobal; packs
		/// GUID128 {lo=0x20, hi|0x8000000000000000}. Its tail (where the
		/// created object is stored — the active-player global) is being
		/// re-dumped by hunt16 with maxC=460.</summary>
		public const ulong FnPlayerCreation = 0x271F2F;
		/// <summary>[S] Type-descriptor array read by the spawn fn:
		/// record = SpawnFnGlobal + idx*TypeDescriptorStride,
		/// idx = dword[entry+0x1C]; byte[record+0x1C]==1 checked.
		/// Statically zero-filled (runtime-populated).</summary>
		public const ulong SpawnFnGlobal = 0x5FA1E60;
		public const ulong TypeDescriptorStride = 0xB0;
>>>>>>> theirs

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
<<<<<<< ours
=======
		/// <summary>[X] REFUTED LIVE (hunt23 v2): qword at LuaStateGlobal
		/// reads 0x8 at runtime — a small integer, not a pointer. Not the
		/// lua_State.</summary>
		/// <summary>[V] CONFIRMED as the real lua_State global (WowBHunt24 v2
		/// log, FUN_142724ff0): the code loads [0x7B76AB8] into a local and
		/// passes it straight to gettop (0x471A960) and to
		/// rawgeti-style FUN_1448A2140(L, -10000 /*LUA_REGISTRYINDEX*/, ref).
		/// So lua_State* L == *(void**)(base + LuaStateGlobalCandidate2).
		/// Live value in one run: 0x15D0E37E378.
		/// NOTE: the vanilla Lua-5.1 GCObject fingerprint (byte[+8] == 8)
		/// does NOT hold for this client's state object — do not use it as a
		/// validity test. FnApiRegister receives a lua_State** that points at
		/// a stack local seeded from this global.</summary>
		public const ulong LuaStateGlobalCandidate2 = 0x7B76AB8;
		/// <summary>[V] hunt23 v2 re-confirmed decoded RVA == live RVA:
		/// [0x4DA48A0] and [0x4DD3980] both read exactly base+0x4A11958.</summary>
		public const ulong MappingCheckSlotA = 0x4DA48A0;
		public const ulong MappingCheckSlotB = 0x4DD3980;
		public const ulong MappingCheckTarget = 0x4A11958;
>>>>>>> theirs
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

<<<<<<< ours
=======
		// ---- FrameScript API registration mechanism (hunt15, from the
		// 2961-line batch-registration fn 0x1D98820; hunt17 CLEAN C body
		// of the registrar itself):
		//   FnApiRegister(u64 **pL, const char *name, void *impl, u32, byte flags)
		//     - lua_State is reached through a POINTER-TO-POINTER (*pL).
		//     - flags bit0 = register into GLOBALS table
		//         pushHandler(L, impl, 0); [bit2 => wrap w/ HandleError 0x769510]
		//         setfield(L, -10002 = LUA_GLOBALSINDEX, name)   // 0x471D240
		//     - always:  rawset(L, -10000 = LUA_REGISTRYINDEX, _DAT_1458BB984)
		//     - if top is a TABLE: namespace mode -> table[name] = impl (flags bit1)
		//     - asserts via FUN_14469df90 -> swi(3) on stack imbalance
		//   => ONE helper implements both RegisterFunction and RegisterNamespace
		//      (0x75AF40 / 0x75A930). Lua 5.1 index constants confirmed
		//      (-10002 globals, -10000 registry) => the VM is Lua 5.1.
		//      Phase-3: with a real lua_State we can call 0xAE96A0 ourselves
		//      to publish our own Lua functions (name string + impl RVA).
		//   FnEnumRegister(registry, "Enum", enumName, value, valueName)
		//   FnRegCountCheck(registry) -- count read before/after each
		//     registration; if it didn't grow the fn asserts (swi 3).
		// Known pairs (name str -> impl), decoded hunt16:
		//   "CanEjectPassengerFromSeat" 0x4F9F058 -> 0x1D33A10
		//   "CanSwitchVehicleSeat"      0x4F9F078 -> 0x1D33FA0
		//   "ClosestGameObjectPosition" 0x4F9F0E0 -> 0x1D344A0
		//   "ClosestUnitPosition"       0x4F9F148 -> 0x1D34E40
		//   "CreateUnitHealPredictionCalculator" 0x4F9F160 -> 0x1D357E0
		// (adjacent to UnitGUID str 0x4F9FEE8). Alphabetical Lua C API.
		public const ulong FnApiRegister = 0xAE96A0;          // [S] globals/namespace registrar
		public const ulong FnEnumRegister = 0xB11CB0;         // [S] enum entry registration
		public const ulong FnRegCountCheck = 0x471A960;       // [S] registration count getter
		public const ulong FnApiRegistrationBatch = 0x1D98820; // [S] Enum/API batch registrar

		// FromGUID-family Lua API impls (hunt13-apis registration tables):
		public const ulong FnFromGuidImpl1 = 0x1D5AD60;  // [S] UnitGUID impl (decompile timeout hunt15)
		public const ulong FnFromGuidImpl2 = 0x1D504C1;  // [S] UnitClassFromGUID (timeout)
		public const ulong FnFromGuidImpl3 = 0x1D7C103;  // [S] UnitNameFromGUID (clean 327 lines)
		public const ulong FnFromGuidImpl4 = 0x1D7DF74;  // [S] UnitOwnerGUID (decompile FAILED)
		public const ulong FnFromGuidImpl5 = 0x1D801BA;  // [S] UnitPercentHealthFromGUID (FAILED)
		public const ulong FnFromGuidImpl6 = 0x1D92E80;  // [S] UnitTokenFromGUID (clean 1161 lines)

>>>>>>> theirs
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

		// ---- FrameScript API handler RVAs harvested from the hunt9
		// registration-table dump ([S] = from static table, semantics
		// follow the public API names). FrameScript state object +0xE0
		// is read by FnFrameScriptShutdownTail (simple getter).

		// Movement (JumpOrAscendStart table, cluster ~0x23Cxxxx)
<<<<<<< ours
=======
		// [!] hunt16 REFUTED the thunk theory: MoveForward's first bytes are
		// a normal prologue (40 55 41 57 48 8B EC 48 83 EC 28) and the 11-byte
		// pdata entry is just a truncated unwind record. hunt17 flow-decompiled
		// the whole cluster (after removeFunction) and every one of them either
		// produced garbage (MoveForward 4907 lines, MoveBackward 4139,
		// TurnRight 1106) or failed with Ghidra low-level errors:
		//   TurnLeft/Mouselook/UseAction "Free varnode has multiple descendants",
		//   Jump "Flow exceeded maximum allowable instructions",
		//   CastSpellByName/ByID + CreateFrame "Overlapping input varnodes",
		//   EnumerateFrames "Decompiler process died", 0x1D33A10 timeout.
		// Root cause = overlapping (mis)disassembly created by the pdata-driven
		// auto-import over the shared 0x23C8xxx-0x240Axxx blob - NOT obfuscation.
		// => DECOMPILING THESE BODIES IS A DEAD END; do not retry. The RVAs are
		// exactly what Phase 3 needs (remote-invoke the handler with a real
		// lua_State). If a body is ever required, rebuild the listing from
		// scratch (clearListing(window) + bounded disassemble + createFunction).
>>>>>>> theirs
		public const ulong FnJumpOrAscendStart = 0x23C5560;
		public const ulong FnAscendStop = 0x23C6840;
		public const ulong FnDescendStop = 0x23C7160;
		public const ulong FnToggleRun = 0x23C76C0;
		public const ulong FnToggleAutoRun = 0x23C7F50;
		public const ulong FnStartAutoRun = 0x23C83A0;
		public const ulong FnStopAutoRun = 0x23C87C0;
		public const ulong FnMoveForwardStart = 0x23C8BE0;
		public const ulong FnMoveForwardStop = 0x23C9000;
		public const ulong FnMoveBackwardStart = 0x23C9410;
		public const ulong FnMoveBackwardStop = 0x23C9830;
		public const ulong FnTurnLeftStart = 0x23C9C40;
		public const ulong FnTurnLeftStop = 0x23CA060;
		public const ulong FnTurnRightStart = 0x23CA470;
		public const ulong FnTurnRightStop = 0x23CA890;
		public const ulong FnStrafeLeftStart = 0x23CACA0;
		public const ulong FnStrafeLeftStop = 0x23CB0D0;
		public const ulong FnStrafeRightStart = 0x23CB500;
		public const ulong FnStrafeRightStop = 0x23CB930;
		public const ulong FnPitchUpStart = 0x23CBD60;
		public const ulong FnPitchUpStop = 0x23CC180;
		public const ulong FnPitchDownStart = 0x23CC590;
		public const ulong FnPitchDownStop = 0x23CC9B0;
		public const ulong FnMouselookStart = 0x23D1110;
		public const ulong FnMouselookStop = 0x23D1580;
		public const ulong FnIsMouselooking = 0x23D1A40;
		public const ulong FnVehicleExit = 0x23D2340;
		public const ulong FnSetMoveEnabled = 0x23D9340;
		public const ulong FnSetTurnEnabled = 0x23D97E0;

<<<<<<< ours
=======
		// ---- hunt31: the three registration tables were walked LIVE and the
		// whole extents are now known (hunt31 §5, [V] = verified in the live
		// process, not just in the static image). Every RVA recorded above
		// was re-read from the live table and MATCHED the static dump — the
		// two independent sources agree, so these RVAs are trustworthy.
		//   movement   : table base 0x58CF2B0, 58 entries (0x3A0 bytes),
		//                MoveForwardStart entry @ 0x58CF320 (+0x70).
		//   spellbook  : table base 0x5909500, 31 entries (0x1F0 bytes),
		//                CastSpellByName entry @ 0x59096B0 (+0x1B0).
		//   action bar : table base 0x590CB20,  9 entries (0x90 bytes),
		//                GetActionInfo entry @ 0x590CB20 (+0x00).
		// Entries are {char* name; void* fn} with stride 0x10 and the tables
		// are walked until a name pointer/fn pointer leaves the module, which
		// is how the extents above were measured (bwd/fwd counts).
		// The 18 entries below were missing from the hunt9 static set.
		public const ulong FnSetMouselookOverrideBinding = 0x23D0340; // [V]
		public const ulong FnVehicleAimUpStart = 0x23D3670;           // [V]
		public const ulong FnVehicleAimUpStop = 0x23D3680;            // [V]
		public const ulong FnVehicleAimDownStart = 0x23D3690;         // [V]
		public const ulong FnVehicleAimDownStop = 0x23D36A0;          // [V]
		public const ulong FnVehicleAimIncrement = 0x23D36B0;         // [V]
		public const ulong FnVehicleAimDecrement = 0x23D3B40;         // [V]
		public const ulong FnVehicleAimRequestAngle = 0x23D3FE0;      // [V]
		public const ulong FnVehicleAimSetNormPower = 0x23D5090;      // [V]
		public const ulong FnVehicleAimGetNormPower = 0x23D5C00;      // [V]
		public const ulong FnIsUsingVehicleControls = 0x23D6020;      // [V]
		public const ulong FnCanExitVehicle = 0x23D6470;              // [V]
		public const ulong FnCanCancelScene = 0x23D68E0;              // [V]
		public const ulong FnIsInCinematicScene = 0x23D6D40;          // [V]
		public const ulong FnCancelScene = 0x23D71A0;                 // [V]
		public const ulong FnCanSwitchVehicleSeats = 0x23D7600;       // [V]
		public const ulong FnIsVehicleAimAngleAdjustable = 0x23D7A50; // [V]
		public const ulong FnIsVehicleAimPowerAdjustable = 0x23D8270; // [V]
		public const ulong FnSetSpellbookPetAction = 0x2C23070;       // [V] spellbook table +0x00

		// ---- hunt31 §1: CODE PAGE PROTECTION (Phase-3 blocker) ----
		// ReadProcessMemory over the handler cluster FAILS for a subset of
		// the RVAs above while others read fine. VirtualQueryEx on the
		// failures returns state=MEM_COMMIT(0x1000) with
		// Protect=PAGE_NOACCESS(0x1) and a multi-page region.
		//   UNREADABLE at rest (hunt31): 0x23C8BE0 MoveForwardStart,
		//     0x23C9000 MoveForwardStop, 0x23C76C0 ToggleRun,
		//     0x2C24040 CastSpellByName, 0x2CC2DB0 RunMacro,
		//     0x2CDA340 UseAction, 0x2BBB7F0 FnEntityGetGuid,
		//     0x1D98820 (the 40+ call API registration batch), and in
		//     hunt30 also 0x2C24040 / 0x2CDA340 / 0x2CC2DB0.
		//   READABLE PAGE_EXECUTE_READ (0x20): 0x23C5560 JumpOrAscendStart,
		//     0x312CB70, 0x1FC37F0 FnGuidToUnitChecked, 0x25129E0,
		//     0x2512110, 0xAE96A0 FnApiRegister, 0x27FBD0, 0x471A960.
		// The unreadable set is exactly the set of handlers the client has
		// NOT executed yet (cold: movement/casting/macros/entity-lookup),
		// while every hot handler (called every frame from startup) is RX —
		// i.e. this is DECRYPT-ON-EXECUTE packing, not dead code.
		// hunt31's VirtualProtectEx retry FAILED with
		// EntryPointNotFoundException (the P/Invoke declared
		// "VirtualProtectExRaw", which does not exist in kernel32) so the
		// flip was never actually attempted; hunt32 retries it properly and
		// compares the live bytes against the static image.
		//   => If flipping PAGE_NOACCESS -> PAGE_EXECUTE_READ yields bytes
		//      identical to the decoded dump, Executor64 can remote-CALL the
		//      handler directly (Phase 3 unblocked without any hook).

		// ---- Protected/taint state cluster (hunt17). These globals are read
		// by every protected handler we dumped (Jump, CastSpellByName,
		// CastSpellByID, EnumerateFrames, CreateFrame); all statically zero,
		// runtime-populated. A shared guard fn 0x48A17F0 is called from
		// EnumerateFrames AND both Cast* handlers (ProtectedCallGuard cand.).
		public const ulong ProtectedState08 = 0x5FF0B08;     // [S]
		public const ulong ProtectedState10 = 0x5FF0B10;     // [S]
		public const ulong ProtectedState18 = 0x5FF0B18;     // [S]
		public const ulong ProtectedState30 = 0x5FF0B30;     // [S]
		public const ulong JumpExtraGlobal = 0x5FF4E50;      // [S]
		public const ulong JumpExtraGlobal2 = 0x5FFD0D0;     // [S]
		public const ulong ProtectedCallGuard = 0x48A17F0;   // [S] candidate
		public const ulong SharedErrorObjectSlot = 0x4DA48A0; // [S] holds -> 0x4A11958
		public const ulong SharedErrorObjectSlot2 = 0x4DD3980;// [S] holds -> 0x4A11958
		public const ulong SharedErrorObject = 0x4A11958;    // [S] common error/throw object

>>>>>>> theirs
		// Camera (CameraZoomIn table; matches hunt7 handler RVAs)
		public const ulong FnCameraZoomIn = 0x312CB70;
		public const ulong FnCameraZoomOut = 0x312CC70;
		public const ulong FnMoveViewInStart = 0x312CFA0;
		public const ulong FnSetView = 0x312D060;
		public const ulong FnSaveView = 0x312D0F0;
		public const ulong FnResetView = 0x312D180;
		public const ulong FnNextView = 0x312D200;
		public const ulong FnPrevView = 0x312D250;
		public const ulong FnFlipCameraYaw = 0x312D2A0;
		public const ulong FnCenterCamera = 0x312D320;

		// Spell casting (SetSpellbookPetAction table)
		public const ulong FnCastSpell = 0x2C231E0;
		public const ulong FnCastSpellByName = 0x2C24040;
		public const ulong FnCastSpellByID = 0x2C27AD0;
		public const ulong FnGetShapeshiftForm = 0x2C23A90;
		public const ulong FnGetShapeshiftFormID = 0x2C23AF0;
		public const ulong FnGetSpellBaseCooldown = 0x2C22F00;

		// Spell targeting (SpellIsTargeting table)
		public const ulong FnSpellIsTargeting = 0x25E0E80;
		public const ulong FnSpellTargetUnit = 0x25E3070;
		public const ulong FnSpellStopTargeting = 0x25E7FC0;
		public const ulong FnSpellStopCasting = 0x25E8870;
		public const ulong FnCancelUnitBuff = 0x25E9430;

		// Action bar (GetActionInfo table)
		public const ulong FnGetActionInfo = 0x2CD5E60;
		public const ulong FnUseAction = 0x2CDA340;
		public const ulong FnPickupAction = 0x2CDB4A0;
		public const ulong FnPlaceAction = 0x2CDBEA0;

		// Macros (SecureCmdOptionParse table)
		public const ulong FnSecureCmdOptionParse = 0x2CC2C80;
		public const ulong FnRunMacro = 0x2CC2DB0;
		public const ulong FnCreateMacro = 0x2CC2E60;
		public const ulong FnGetNumMacros = 0x2CC4640;
		public const ulong FnGetMacroBody = 0x2CC4730;
		public const ulong FnGetMacroIndexByName = 0x2CC7170;

		// Loot (SetLootPortrait table)
		public const ulong FnGetNumLootItems = 0x2C3E390;
		public const ulong FnGetLootSlotInfo = 0x2C3E3C0;
		public const ulong FnLootSlot = 0x2C3EC40;
		public const ulong FnCloseLoot = 0x2C3ED90;

		// Quests (CloseQuest + GetQuestLogQuestText tables)
		public const ulong FnGetQuestID = 0x2F8BD80;
		public const ulong FnGetActiveQuestID = 0x2F8C490;
		public const ulong FnAcceptQuest = 0x2F8C9B0;
		public const ulong FnDeclineQuest = 0x2F8C9C0;
		public const ulong FnIsQuestCompletable = 0x2F8C9D0;
		public const ulong FnCompleteQuest = 0x2F8CA20;
		public const ulong FnGetQuestReward = 0x2F8CAC0;
		public const ulong FnGetQuestUiMapID = 0x2B9FA80;

		// Bindings (GetNumBindings table)
		public const ulong FnGetBindingKey = 0x23EA320;
		public const ulong FnGetBindingAction = 0x23EA3B0;
		public const ulong FnRunBinding = 0x23EA630;

		// Inventory / items
		public const ulong FnUseInventoryItem = 0x2F5F600;
		public const ulong FnGetInventoryItemID = 0x2F5F2B0;
		public const ulong FnGetInventoryItemCooldown = 0x2F5ECA0;

		// Taxi
		public const ulong FnNumTaxiNodes = 0x2FCC3C0;
		public const ulong FnTakeTaxiNode = 0x2FCC600;

<<<<<<< ours
		// Lua C-API wrapper cluster (RegisterFunction decompile; [S]
		// semantics guessed from usage — verify before Phase-3 use)
		public const ulong FnLuaPushHandlerCandidate = 0x471B800;
		public const ulong FnLuaSetFieldishCandidate = 0x471D240;
		public const ulong FnLuaRawSetCandidate = 0x471C370;
		public const ulong FnLuaGetTopCandidate = 0x471E1B0;
=======
		// Lua C-API wrapper cluster. WowBHunt29 decompiled FnApiRegister
		// (0xAE96A0) so the roles below are grounded in live CALL SITES,
		// not guesses:
		//   gettop(L)      : 0x471A960 - called before/after every
		//                    registration and compared; the stack-balance
		//                    asserts (swi(3) int3 on mismatch) prove it.
		//   pushcclosure   : 0x471B800 - pushes the handler before setfield.
		//   rawset         : 0x471C370 - rawset(L, -10000 /*REGISTRY*/, key).
		//   setfield       : 0x471D240 - setfield(L, -10002 /*GLOBALS*/, name)
		//                    => globals[name] = fn.
		//   index2addr     : 0x471DA00 - called with (L, -2).
		//   lua_type       : 0x471E1B0 - CORRECTED: called with -1 and the
		//                    result compared to 5 (LUA_TTABLE).
		//   0x471E270      : [C] NOT lua_type (that old label was wrong) -
		//                    remaining unclassified sibling, verify first.
		//   0x48A2140      : registry rawset/rawget helper wrapper.
		// Registration flag bits (param_5): bit0 -> also set globals[name];
		// bit1 -> set it when a table is at stack -1; bit2 -> extra call
		// FUN_140769510(L, gettop(L), 0).
		public const ulong FnLuaGetTop = 0x471A960;
		public const ulong FnLuaPushHandlerCandidate = 0x471B800;
		public const ulong FnLuaSetFieldishCandidate = 0x471D240;
		public const ulong FnLuaRawSetCandidate = 0x471C370;
		public const ulong FnLuaTypeCandidate = 0x471E1B0;
>>>>>>> theirs
		public const ulong FnLuaIndexToAddrCandidate = 0x471DA00;
		public const ulong FnLuaPushNumber = 0x471BD60;
		public const ulong FnLuaPushString = 0x471BDD0;
		public const ulong FnLuaPushLString = 0x471BC20;
<<<<<<< ours
		public const ulong FnLuaType = 0x471E270;
		public const ulong FnLuaArgCheck = 0x471AF10;
		public const ulong FrameScriptRegistryGlobal2 = 0x589B984; // [S]

=======
		/// <summary>[C] sibling of 0x471E1B0 — the old "lua_type" label on
		/// this RVA was wrong (WowBHunt29/FnApiRegister usage).</summary>
		public const ulong FnLuaSibling0x471E270 = 0x471E270;
		public const ulong FnLuaArgCheck = 0x471AF10;
		public const ulong FrameScriptRegistryGlobal2 = 0x589B984; // [S]

		// ================= Lua 5.1 ABI on 70291 (hunt33) =================
		// hunt32 dumped the live Lua cluster 0x4718000..0x4722000 into
		// hunt/hunt32-code.txt; hunt33 disassembled it locally. These
		// RVAs are proven by STRUCTURE (not by guessing):
		//
		//   lua_State layout (all [V] live):
		//     +0x28 top        +0x30 base       +0x38 l_G (global_State*)
		//     +0x40 current CallInfo (ci->top at ci+0x10)
		//     +0x58 stack      (savestack(L,p) == p - L->stack)
		//     +0xE0 ??? (a pointer whose first qword every push helper
		//          copies into the new slot at +0x10)
		//   global_State: +0x70 totalbytes, +0x78 GCthreshold
		//     (the classic `totalbytes >= GCthreshold -> luaC_step` test)
		//   Stack slot = 24 BYTES (lua_gettop divides (top-base) by 24 via
		//     the 0x2AAAAAAAAAAAAAAB magic; index2adr does (idx-1)*3*8).
		//     Slot layout: +0x00 value (lua_Number / pointer),
		//                  +0x08 uint16 type,
		//                  +0x10 an extra 8-byte field the push helpers
		//                       initialise from *(L+0xE0).
		//   Type numbers seen: 0 nil, 1 boolean, 3 number, 6 function
		//     (lua_pushboolean writes 1, lua_pushnumber 3,
		//      lua_pushcclosure 6), 5 = table (lua_settable's check).
		//
		/// <summary>[V] lua_pcall(L, nargs, nresults, errfunc) — proven:
		/// api_check(nargs+1 &lt;= (L->top-L->base)/24) with the message/line
		/// pair at 0x3B6, MULTRET adjust (ci->top = L->top), |
		/// c.func = L->top - (nargs+1)*24, then luaD_pcall(L, f_call, &amp;c,
		/// savestack(c.func), errfunc_offset).</summary>
		public const ulong FnLuaPCall = 0x471B5C0;
		/// <summary>[V] f_call(L, ud) — reads c-&gt;func and c-&gt;nresults and
		/// tail-calls luaD_call.</summary>
		public const ulong FnLuaFCall = 0x47190A0;
		/// <summary>[V] luaD_pcall(L, func, ud, oldtop, ef).</summary>
		public const ulong FnLuaDProtectedCall = 0x471ECE0;
		/// <summary>[V] luaD_call(L, func, nresults).</summary>
		public const ulong FnLuaDCall = 0x471EA50;
		/// <summary>[V] luaD_protectedparser(L, ZIO* z, const char* name) —
		/// the ONLY caller of luaD_pcall inside the dumped window; it passes
		/// f_parser = 0x4718F60. lua_load / lauxlib's loader (which we need
		/// for "load a script string") lives OUTSIDE that window, hence the
		/// wider 256 KB dump in hunt33 (§2).</summary>
		public const ulong FnLuaDProtectedParser = 0x4719A80;
		/// <summary>[V] f_parser(L, void* ud) — pushes the Proto as a
		/// closure (tt = 6).</summary>
		public const ulong FnLuaFParser = 0x4718F60;
		/// <summary>[V] lua_pushcclosure(L, fn, nup).</summary>
		public const ulong FnLuaPushCClosure = 0x471B800;
		/// <summary>[V] lua_pushboolean(L, b).</summary>
		public const ulong FnLuaPushBoolean = 0x471B790;
		/// <summary>[V] the real index2adr(L, idx) — 54 callers in the
		/// cluster (the older FnLuaIndexToAddrCandidate 0x471DA00 is a
		/// different helper: it does NOT both-shift base handling).</summary>
		public const ulong FnLuaIndex2Adr = 0x4719140;
		/// <summary>[C] lua_tolstring/lua_tostring wrapper — index2adr,
		/// type check, luaV_tostring for numbers (0x4722310), returns the
		/// char* (reads *(str+0x18)).</summary>
		public const ulong FnLuaToLString = 0x471B4F0;
		/// <summary>[C] luaV_tostring (number -&gt; string conversion).</summary>
		public const ulong FnLuaVToString = 0x4722310;
		/// <summary>[V] the Lua error/assert helper every API function
		/// calls: (message, file, line) in rcx/rdx/r8 — 190 call sites in
		/// the dumped window alone.</summary>
		public const ulong FnLuaAssertHelper = 0x33C780;
		/// <summary>[V] the unlock/threading epilogue every LUA_API function
		/// tail-calls with L in rcx (63 sites). Clobbers eax, so a caller
		/// that wants the API return value must reload it afterwards.</summary>
		public const ulong FnLuaUnlock = 0x1D8E80;

		// ================= Static extraction (hunt33) =================
		// The SHIPPED WowB.exe is packed only in .text (and .pdata is
		// useless: its function sizes contradict the live code). .rdata and
		// .data are PLAINTEXT, so the whole FrameScript registry can be
		// read offline:
		//   hunt/api_map.txt — 63 tables / 3240 {name -> handler RVA} pairs
		//   extracted from .data (stride 0x10, name ptr -> .rdata string,
		//   fn ptr -> .text, all stored as preferred-base 0x140000000 +
		//   RVA). hunt31's live walk matched 79/79 of these, so the static
		//   values are trustworthy.
		// hunt34: the Unit*/Target*/RunScript/UnitHealth/... names are NOT in
		// any static .data table — they are registered one by one from code.
		// hunt34 parsed all 831 FnApiRegister call sites in the decoded image
		// (lea rdx,[name] / lea r8,[handler] / mov byte [rsp+0x20],flags) and
		// recovered the FULL runtime registration: hunt/api_handlers.tsv
		// (829 entries, flags=3). Their handler RVAs are real — see the
		// FnUnitHealth..FnRunScript block below.
		// New RVAs taken from hunt/api_map.txt (not in the lists above):
		public const ulong FnRunBinding = 0x23EA630;        // [S] movement/binding table
		public const ulong FnAcceptQuest = 0x2F8C9B0;       // [S] quest table
		public const ulong FnConfirmAcceptQuest = 0x2F8D560; // [S]
		public const ulong FnAcknowledgeAutoAcceptQuest = 0x2F8DF80; // [S]
		public const ulong FnSpellStopCasting = 0x25E8870;  // [S]
		public const ulong FnSpellIsTargeting = 0x25E0E80;  // [S]

		// ============ hunt34: decoded image ground truth ============
		// wowb-decoded.exe (103,496,912 B, sha256 8f864e73e73ad673...) is the
		// SHIPPED PE with .text DECRYPTED IN PLACE (a memory image dumped with
		// the original section table, so RVA -> file offset is the normal file
		// mapping). Evidence: 18,474 / 18,755 .text pages differ from the packed
		// file while .rdata/.data are byte-identical, and the live hunt32 Lua
		// dump (hunt32-code.txt / logs/lua_cluster.bin) matches it 40,960/40,960
		// at 0x4718000 -> the dump is byte-faithful.
		//
		// PACKING MODEL (settled):
		//  * The 282 .text pages that are IDENTICAL to the shipped file are pages
		//    the packer never decrypted in that session; their bytes == file bytes,
		//    dense/random-looking (99.5% non-zero). The packed^decoded keystream
		//    has 256 distinct values and no period, and every page has a DIFFERENT
		//    keystream => real stream/block cipher, NOT a recoverable XOR.
		//  * Conclusion: a page is plaintext in memory only AFTER the game itself
		//    executed it. PAGE_NOACCESS (prot=0x1) pages hold CIPHERTEXT.
		//    => Never VirtualProtectEx-flip a cold page and call it directly;
		//       drive the game's own code paths (Lua dispatch) so its
		//       on-execute decrypt does the work, or call only warm functions.
		//
		// Runtime registration (full map: hunt/api_handlers.tsv, 829 entries):
		public const ulong FnUnitHealth = 0x1D63190;   // [S] Lua fn UnitHealth(unit)
		public const ulong FnUnitName   = 0x1D7B840;   // [S] Lua fn UnitName(unit)
		public const ulong FnUnitExists = 0x1D596F0;   // [S] Lua fn UnitExists(unit)
		public const ulong FnUnitGUID   = 0x1D5AD60;   // [S] Lua fn UnitGUID(unit)
		public const ulong FnTargetUnit = 0x1B46C60;   // [S] Lua fn TargetUnit(unit)
		public const ulong FnRunScript  = 0x136FFF0;   // [S] Lua fn RunScript("code")
		// Lua library internals used by the Phase-3 bridge (hunt34):
		public const ulong FnLuaSNewLStr = 0x472BA70;  // [S] (L,s,len) -> TString*
		//   lua_pushlstring 0x471BC20 == FnLuaPushLString (above): proven by body
		//     (luaC_checkGC -> luaS_newlstr -> tt=4 -> top += 0x18 -> ret).
		//   lua_pushnil    0x471BD00 == FnLuaCloseCandidate (above): writes tt=0
		//     at L->top and bumps top; the old "lua_close" label is wrong.
		//   lua_setfield   0x471D240 == FnLuaSetFieldishCandidate (above).
		//
		// PHASE-3 LUA BRIDGE (no loader, no ZIO, no cold-page jumps):
		//   pushcclosure(L, base+handlerRva, 0)   // warm: game registers with it
		//   pushlstring (L, argPtr, argLen)       // warm
		//   pcall       (L, nargs, nresults, 0)   // warm: Lua runs constantly
		//   results at L->top - 24*n; slot: +0x00 value (double / ptr),
		//   +0x08 u16 type (3 number, 4 string), +0x10 extra.
		//   For strings call lua_tolstring(L, -1, NULL) and read the char*.
		//   Always go through lua_pcall (it sets up the CallInfo frame that
		//   index2adr needs for argument index 1); never call a handler raw.
		//   Executed live by hunt/hunt34.ps1 §2.
		//
		// ============ hunt34 RUN verdict (2026-10-13) ============
		//  * [SAN] "live bytes differ" was a SCRIPT bug (16 read bytes compared
		//    against a 13-byte constant). Live 0x471B5C0 == decoded dump 16/16:
		//    the Lua cluster is WARM and reads fine with 0x1F0FFF.
		//  * [1] flip NOACCESS->RX and read gives the SHIPPED FILE bytes for all
		//    four probes (CastSpellByName, RunMacro, FnEntityGetGuid,
		//    GetNumCompanions) => the packer does NOT decrypt on a protection
		//    change, and restoring prot leaves the page cold. A flip-and-call
		//    would therefore execute ciphertext: NEVER do it.
		//  * [2] the remote thread executed NOTHING: WaitForSingleObject
		//    signalled at once, exitCode=0x0 (not the stub's 0x55667788), and the
		//    entire scratch block was still zero while the process stayed alive
		//    (later RPM and CloseHandle succeeded). Either foreign thread starts
		//    are killed before the first instruction, or the payload faulted at
		//    entry. hunt/hunt35.ps1 decides this with a STAGE counter
		//    (scratch+0x50 = 0x1001..0x100B after every step) and retries the
		//    same payload on a HIJACKED existing thread (SuspendThread ->
		//    SetThreadContext(Rip) -> ResumeThread -> restore), which never calls
		//    CreateRemoteThread and so bypasses a thread-start hook.
		//  * wowb-decoded.exe has plaintext for EVERY handler page we care about
		//    (UnitHealth, UnitName, UnitExists, CastSpellByName, RunMacro,
		//    GetNumCompanions, FnEntityGetGuid, MoveForwardStart) while live those
		//    same pages are NOACCESS => it is a long-session / decrypted image.
		//    It is ground truth for BYTES, not for current protection: only a
		//    live VirtualQueryEx (hunt35 [0]) says warm vs cold right now.
		//  * hunt35 [3] tests the decisive architecture question: call a still
		//    cold handler WITHOUT touching its protection. Success => the packer
		//    decrypts on fault (process-wide) and every registered API is
		//    reachable; failure => cold handlers need the game itself to execute
		//    them (hunt36 would then go through a warm dispatcher / input path).

>>>>>>> theirs
		// ---- Unit token / object resolution (hunt7) ----

		/// <summary>[S] UnitGUID handler's token -> GUID128 resolver
		/// (returns pointer to 16-byte GUID128). RVA corrected hunt9.</summary>
		public const ulong FnUnitTokenToGuid = 0x26ACAA0;

		/// <summary>[S] FrameScript unit-token argument parser
<<<<<<< ours
		/// ("Usage: local result = UnitGUID(unit)").</summary>
=======
		/// ("Usage: local result = UnitGUID(unit)"). hunt16: the containing
		/// pdata function actually starts at 0x1FDDE80 (this RVA is
		/// mid-function); its 44-line decompile is SSE math — semantics
		/// doubtful, verify before use.</summary>
>>>>>>> theirs
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
<<<<<<< ours
		/// build <see cref="GameBuild"/>. Until then the bot must refuse
		/// to attach to this client with a clear message.
		/// </summary>
		public static bool IsComplete =>
			ObjectManager != 0 && PlayerGuid != 0 && PlayerTargetGuid != 0 &&
			GameState != 0 && FrameBase != 0 && FrameScriptExecuteBuffer != 0;
=======
		/// build <see cref="GameBuild"/>. 70291 model (post-hunt9): the
		/// legacy 3.3.5a requirements (GameState flag, FrameBase chain,
		/// FrameScript_ExecuteBuffer) do not apply — presence is derived
		/// from verified globals, and script execution goes through the
		/// Lua state global + API handler calls instead. Until complete
		/// the bot must refuse to attach to this client with a clear
		/// message.
		/// </summary>
		public static bool IsComplete =>
			ObjectManager != 0 && PlayerGuid != 0 && PlayerTargetGuid != 0 &&
			LuaStateGlobal != 0 && CameraWorldFrameGlobal != 0 &&
			FnMoveForwardStart != 0 && FnCastSpellByName != 0;

		/// <summary>
		/// Derived "player in world" check for 70291 (no single flag
		/// found; the IsPlayerInWorld handler is obfuscated). Verified
		/// globals that are zero/null at character select and populated
		/// in-world.
		/// </summary>
		public static bool IsPlayerInWorldCheck => PlayerGuid != 0 && MapRoot != 0;

		// ==================================================================
		// PHASE 3 — Lua / FrameScript execution surface (WowBHunt29, live)
		// ==================================================================
		//
		// THE UNIT OBJECT IS *(record + 0xD8) — WowBHunt29 live proof:
		//   * it starts with a VTABLE: CGUnitFnTable (0x50CDDC0) for NPCs,
		//     0x50127920+ for the local player;
		//   * +0x08 points back at the record;
		//   * +0x10/+0x18 = the 128-bit GUID (record has it at +0x18/+0x20);
		//   * the reference stat block is intact: health +0x1E30,
		//     maxHealth +0x1E38, power +0x1BC8, unitFlags +0x1E60.
		//   This is the "entity" FnEntityGetGuid (0x2BBB7F0) takes as its
		//   first argument, which is why it reads the packed handle at
		//   +0x398 / family at +0x39F.
		//   [X] REFUTED by hunt30+hunt31: +0x398 is 0 and +0x39F is 0xFF on
		//   BOTH the player and the NPC unit objects, and the pool slot of
		//   every "unit +0x398" matched the record slot 0 times out of 86.
		//   hunt31 §2/§3: for every occupied record the values seen at
		//   unit+0x398 are 0, 0x15D, 0x3F800000 or 0xDEDEDEDEDEDEDEDE - i.e.
		//   that offset is simply not a handle field on this object.
		//   *(rec+0x08) ("E") DOES have the pair (E+0x398 = -1 = invalid
		//   handle sentinel, E+0x39F = 0xFF = no family) but the check
		//   FnEntityGetGuid runs on it (u8@(E+0x10) & 0x20, E+0x10 = 0x39)
		//   passes by accident. The object the function is meant to receive
		//   is therefore a THIRD shape; hunt32 §4 settles which record shapes
		//   own a valid unit object.
		//
		// hunt31 §2 vtable histogram of rec+0xD8 over the occupied records:
		//   player 0x50A7920 (slot 16), NPC CGUnitFnTable 0x50CDDC0 (~23
		//   slots), 0x50B1838 (~17 slots, whose +0x398 = 0x15D) and
		//   0x50D8258 (1 slot, +0x398 = 0x3F800000 = float 1.0).
		//   ~45% of the occupied records hold a value at +0xD8 that is not
		//   even 8-byte aligned (e.g. 0x15DC2D1C24C, 0x15D35A548C4) and whose
		//   qword at that address is not a vtable - consecutive slots differ
		//   by exactly 0x2F4, i.e. those are strided entries of a packed
		//   array, not objects. => never trust +0xD8 alone; validate with the
		//   back-pointer (*(unit+0x08) == record) plus the GUID pair at
		//   unit+0x10/+0x18 (WowBReader.TryGetUnitObject does this).
		//
		// THE FRAMESCRIPT API REGISTRY IS A STATIC {name, fn} ARRAY
		// (live-verified, stride 0x10, fn stored as an absolute VA):
		//   entry = { char* name; void* fn; }
		// Tables found live in the module data (image-relative):
		public const ulong ApiTableSpellbook = 0x59096B0;   // [V] CastSpellByName entry; -0xC0..+0x120 walked
		public const ulong ApiTableMovement = 0x58CF320;    // [V] MoveForwardStart entry; 58 entries
		public const ulong ApiTableActionBar = 0x590CB20;   // [V] GetActionInfo entry; 9 entries
		// WowBHunt29 dumped ~100 entries and EVERY function RVA matches the
		// handler RVAs already in this file (CastSpellByName 0x2C24040,
		// MoveForwardStart 0x23C8BE0, RunMacro 0x2CC2DB0, UseAction
		// 0x2CDA340, ...) — the RVA library below is now live-corroborated.
		// New RVAs learned from the registry dump (not previously present):
		public const ulong FnCastSpell = 0x2C231E0;          // [V] CastSpell
		public const ulong FnIsSelectedSpellBookItem = 0x2C23330;
		public const ulong FnGetSpecsForSpell = 0x2C233A0;
		public const ulong FnIsSpellClassOrSpec = 0x2C23680;
		public const ulong FnGetNumShapeshiftForms = 0x2C23A60;
		public const ulong FnCancelShapeshiftForm = 0x2C23B50;
		public const ulong FnGetShapeshiftFormInfo = 0x2C23B70;
		public const ulong FnCastShapeshiftForm = 0x2C23CF0;
		public const ulong FnGetShapeshiftFormCooldown = 0x2C23EB0;
		public const ulong FnGetNumCompanions = 0x2C2B5E0;
		public const ulong FnGetCompanionInfo = 0x2C2B9C0;
		public const ulong FnPickupCompanion = 0x2C2BAC0;
		public const ulong FnCallCompanion = 0x2C2BC60;
		public const ulong FnDismissCompanion = 0x2C2BD80;
		public const ulong FnFindSpellBookSlotBySpellID = 0x2C2BED0;
		public const ulong FnSummonRandomCritter = 0x2C2BFA0;
		public const ulong FnGetProfessions = 0x2C2C020;
		public const ulong FnGetProfessionInfo = 0x2C2C330;
		public const ulong FnUnlearnSpecialization = 0x2C2C680;
		public const ulong FnGetCurrentLevelFeatures = 0x2C2C720;
		public const ulong FnGetCallPetSpellInfo = 0x2C2C840;
		public const ulong FnGetWorldMapActionButtonSpellInfo = 0x2C2C920;
		public const ulong FnClickWorldMapActionButton = 0x2C2CB80;
		public const ulong FnUseWorldMapActionButtonSpellOnQuest = 0x2C2D860;
		public const ulong FnRequestBottomLeftActionBar = 0x2C2E980;
		public const ulong FnToggleRunApi = 0x23C76C0;
		public const ulong FnToggleAutoRunApi = 0x23C7F50;
		public const ulong FnStartAutoRunApi = 0x23C83A0;
		public const ulong FnStopAutoRunApi = 0x23C87C0;
		public const ulong FnTurnOrActionStart = 0x23CCDC0;
		public const ulong FnTurnOrActionStop = 0x23CD680;
		public const ulong FnCameraOrSelectOrMoveStart = 0x23CDF80;
		public const ulong FnCameraOrSelectOrMoveStop = 0x23CE840;
		public const ulong FnMoveAndSteerStart = 0x23CF140;
		public const ulong FnMoveAndSteerStop = 0x23CFA00;
		public const ulong FnTaxiRequestEarlyLanding = 0x23D1EA0;
		public const ulong FnVehiclePrevSeat = 0x23D2BF0;
		public const ulong FnVehicleNextSeat = 0x23D3130;
		public const ulong FnDetectWowMouse = 0x23D8A90;
		public const ulong FnToggleWindowed = 0x23D8F30;
		public const ulong FnReleaseAction = 0x2CDAC00;
		public const ulong FnGetActionBarToggles = 0x2CDC6B0;
		public const ulong FnSetActionBarToggles = 0x2CDCB70;
		public const ulong FnGetMultiCastTotemSpells = 0x2CDD020;
		public const ulong FnSetMultiCastSpell = 0x2CDD850;
		// Guid/unit helpers (WowBHunt29 T3 — the target-name path):
		//   unit = 0x1FC37F0(&guid128, 0x20, callerString, line)   [V] used
		//          by the UnitName implementation with &PlayerTargetGuid;
		//   name = 0x25129E0(unitish, &guid128, 0x41, 0, 0);
		//   then 0x2512110(x, buf, 0x132, 0) copies an ANSI name which the
		//   caller converts with MultiByteToWideChar(0xFDE9 /*UTF-8*/, 0,
		//   name, -1, out) via the pointer stored at 0x57E3538.
		public const ulong FnGuidToUnitChecked = 0x1FC37F0;   // [V] (guid,0x20,caller,line) -> unit
		public const ulong FnUnitNameFetch = 0x25129E0;       // [V] (unitish, guid, 0x41, 0, 0)
		public const ulong FnUnitNameCopy = 0x2512110;        // [V] (x, buf, 0x132, 0) -> ANSI name
		public const ulong FnUnitNameFallback = 0x2428F70;    // [V] "Unknown"-ish fallback
		public const ulong FnMultiByteToWideCharPtr = 0x57E3538; // [V] call via [this]
		// FnApiRegister's own xrefs (WowBHunt29 T5) — the registration
		// batches / descriptors:
		public const ulong FnApiRegisterCaller = 0x1D98820;   // [V] ~40 calls to 0xAE96A0
		public const ulong ApiRegisterDescriptorA = 0x54F5488; // [V] DATA ref to 0xAE96A0
		public const ulong ApiRegisterDescriptorB = 0x54F54BC; // [V] DATA ref to 0xAE96A0
		public const ulong ApiRegisterDescriptorC = 0x82A3228; // [V] DATA ref to 0xAE96A0
		// Entity/pool introspection xrefs (WowBHunt29 T6/T7):
		public const ulong EntityGetGuidVtableSlot = 0x8404604; // [V] DATA ref -> 0x2BBB7F0
		public const ulong PoolAccessorVtableSlot = 0x8219BE8;  // [V] DATA ref -> 0x27FBD0
		// NOTE the record flags field (record+0x48) CANNOT classify units:
		// hunt29 read 0x206 on BOTH the player record and the NPC record.
		// Use the unit object's vtable instead (CGUnitFnTable vs player).
>>>>>>> theirs
	}
}

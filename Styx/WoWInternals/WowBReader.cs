using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Offsets;

namespace Styx.WoWInternals
{
    /// <summary>
    /// One entity as tracked by the modern Classic object manager's
    /// entity array (see <see cref="WowBReader.EnumerateEntities"/>).
    /// </summary>
    public readonly struct WowBEntity
    {
        public readonly WowBGuid Guid;
        public readonly ulong ObjectPtr;
        public readonly byte ObjectType;

        public WowBEntity(WowBGuid guid, ulong objectPtr, byte objectType)
        {
            Guid = guid;
            ObjectPtr = objectPtr;
            ObjectType = objectType;
        }

        public override string ToString() => $"[{ObjectType}] {Guid} @ 0x{ObjectPtr:X}";
    }

    /// <summary>
    /// Read-only view of the modern Classic client state (WowB.exe,
    /// build 70291) through the 64-bit Memory API and
    /// <see cref="WowBOffsets"/>. This is the §6.3 milestone-1 path:
    /// pure reads, no injection — it validates the whole x64 read stack
    /// end-to-end and feeds diagnostics while the offset table is being
    /// filled in.
    ///
    /// Every accessor degrades gracefully (returns 0/empty) while its
    /// offset is still a placeholder, so the reader is safe to poll at
    /// any time.
    /// </summary>
    public sealed class WowBReader
    {
        private const int MaxEntities = 262144; // safety cap for entity walks

        private readonly Memory _memory;

        public WowBReader(Memory memory)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));

            if (!memory.Is64BitTarget)
                throw new InvalidOperationException("WowBReader requires a native 64-bit target process.");
        }

        private ulong Abs(ulong imageRelative) => _memory.GetAbsolute64(imageRelative);

        // ------------------------------------------------------------------
        // Game state
        // ------------------------------------------------------------------

        /// <summary>True when the player is in the world (GameState != 0).</summary>
        public bool IsInWorld
        {
            get
            {
                if (WowBOffsets.GameState == 0) return false;
                return _memory.Read64<int>(Abs(WowBOffsets.GameState)) != 0;
            }
        }

        // ------------------------------------------------------------------
        // GUIDs (128-bit)
        // ------------------------------------------------------------------

        /// <summary>Reads a 128-bit GUID (low qword, high qword) at an absolute address.</summary>
        public WowBGuid ReadGuid(ulong absoluteAddress)
        {
            ulong low = _memory.Read64<ulong>(absoluteAddress);
            ulong high = _memory.Read64<ulong>(absoluteAddress + 8);
            return new WowBGuid(low, high);
        }

        public WowBGuid ReadLocalGuid()
            => WowBOffsets.PlayerGuid == 0 ? default : ReadGuid(Abs(WowBOffsets.PlayerGuid));

        public WowBGuid ReadTargetGuid()
            => WowBOffsets.PlayerTargetGuid == 0 ? default : ReadGuid(Abs(WowBOffsets.PlayerTargetGuid));

        public WowBGuid ReadLastTargetGuid()
            => WowBOffsets.LastTargetGuid == 0 ? default : ReadGuid(Abs(WowBOffsets.LastTargetGuid));

        public WowBGuid ReadMouseoverGuid()
            => WowBOffsets.MouseoverGuid == 0 ? default : ReadGuid(Abs(WowBOffsets.MouseoverGuid));

        // ------------------------------------------------------------------
        // Zone / area
        // ------------------------------------------------------------------

        public uint ZoneId => WowBOffsets.ZoneId == 0 ? 0 : _memory.Read64<uint>(Abs(WowBOffsets.ZoneId));
        public uint AreaId => WowBOffsets.AreaId == 0 ? 0 : _memory.Read64<uint>(Abs(WowBOffsets.AreaId));
        public uint SubAreaId => WowBOffsets.SubAreaId == 0 ? 0 : _memory.Read64<uint>(Abs(WowBOffsets.SubAreaId));

        public string ZoneText
            => WowBOffsets.ZoneText == 0 ? string.Empty : _memory.ReadString64(Abs(WowBOffsets.ZoneText), 128) ?? string.Empty;

        public string SubZoneText
            => WowBOffsets.ZoneText == 0 ? string.Empty : _memory.ReadString64(Abs(WowBOffsets.ZoneText) + 0x8, 128) ?? string.Empty;

        public string MinimapZoneText
            => WowBOffsets.ZoneText == 0 ? string.Empty : _memory.ReadString64(Abs(WowBOffsets.ZoneText) + 0x10, 128) ?? string.Empty;

        // ------------------------------------------------------------------
        // Object manager (hash-table entity model)
        // ------------------------------------------------------------------

        /// <summary>Pointer to the ClntObjMgr structure (0 when unknown/not in world).</summary>
        public ulong ObjectManagerPtr
            => WowBOffsets.ObjectManager == 0 ? 0 : _memory.ReadPointer64(Abs(WowBOffsets.ObjectManager));

        /// <summary>Number of entities currently tracked by the client.</summary>
        public uint EntityCount
        {
            get
            {
                ulong mgr = ObjectManagerPtr;
                if (mgr == 0) return 0;
                return _memory.Read64<uint>(mgr + WowBOffsets.ObjMgrEntityCount);
            }
        }

        /// <summary>
        /// Walks the object manager's entity array and returns every live
        /// entity (GUID + object pointer + object type). Returns an empty
        /// list while the offsets are unresolved or the manager is absent.
        /// </summary>
        public List<WowBEntity> EnumerateEntities()
        {
            var result = new List<WowBEntity>();

            ulong mgr = ObjectManagerPtr;
            if (mgr == 0)
                return result;

            uint count = _memory.Read64<uint>(mgr + WowBOffsets.ObjMgrEntityCount);
            if (count == 0 || count > MaxEntities)
                return result;

            ulong array = _memory.ReadPointer64(mgr + WowBOffsets.ObjMgrEntityArray);
            if (array == 0)
                return result;

            int stride = WowBOffsets.ObjMgrEntryStride;
            result.Capacity = (int)count;

            for (uint i = 0; i < count; i++)
            {
                ulong entry = array + (ulong)i * (ulong)stride;

                ulong guidLow = _memory.Read64<ulong>(entry + WowBOffsets.EntityGuidOffset);
                ulong guidHigh = _memory.Read64<ulong>(entry + WowBOffsets.EntityGuidOffset + 8);
                ulong objPtr = _memory.ReadPointer64(entry + WowBOffsets.EntityObjectOffset);

                byte type = 0;
                if (objPtr != 0)
                    type = _memory.Read64<byte>(objPtr + WowBOffsets.ObjectTypeOffset);

                result.Add(new WowBEntity(new WowBGuid(guidLow, guidHigh), objPtr, type));
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Handle pools - the live-verified GUID -> object-record path
        // (WowBHunt25..27 / FnEntityGetGuid 0x2BBB7F0)
        // ------------------------------------------------------------------

        /// <summary>
        /// One occupied handle-pool slot: the 128-bit GUID, the pool slot
        /// and family, and the record pointer that carries the transform.
        /// </summary>
        public readonly struct WowBPoolObject
        {
            public readonly WowBGuid Guid;
            public readonly ulong RecordPtr;
            public readonly int Family;
            public readonly int Slot;
            // Internal scan metadata; +4 is an opaque marker, not a proven
            // generation counter. A match is only a best-effort recheck.
            internal readonly ulong PoolPtr;
            internal readonly ulong SlotArray;
            internal readonly uint SlotTag;
            internal readonly uint SlotMarker;

            public WowBPoolObject(WowBGuid guid, ulong recordPtr, int family, int slot)
                : this(guid, recordPtr, family, slot, 0, 0, 0, 0)
            {
            }

            internal WowBPoolObject(WowBGuid guid, ulong recordPtr, int family, int slot,
                ulong poolPtr, ulong slotArray, uint slotTag, uint slotMarker)
            {
                Guid = guid;
                RecordPtr = recordPtr;
                Family = family;
                Slot = slot;
                PoolPtr = poolPtr;
                SlotArray = slotArray;
                SlotTag = slotTag;
                SlotMarker = slotMarker;
            }

            public bool IsEmpty => RecordPtr == 0;

            public override string ToString() => $"fam{Family}[{Slot}] {Guid} rec=0x{RecordPtr:X}";
        }

        private const int MaxPools = 32;
        private const int MaxPoolSlots = 20000;      // safety cap on live capacity field

        /// <summary>Number of pool slots scanned by the last enumeration.</summary>
        public int LastPoolSlotsScanned { get; private set; }

        private static bool IsUserPointer(ulong p)
            => p > 0x10000UL && p < 0x00007FFFFFFFFFFFUL && (p & 7UL) == 0;

        /// <summary>
        /// Enumerates every occupied slot of every handle pool (the modern
        /// engine's global GUID registry, table at
        /// <see cref="WowBOffsets.HandlePools"/>). Each pool header is
        /// {u32 flag, elems*, cap, max}; each element is 16 bytes
        /// {u32 tag (bit31 = occupied), u32 opaque marker, u64 record*} and the
        /// record stores GUID128 at +0x18/+0x20 (live-verified: pool slot 16
        /// held the player's {2, 0x0801180000000000}).
        /// The local player is NOT in the OM maps, but IS in this registry -
        /// this is the canonical way to resolve any GUID to its record.
        /// </summary>
        public List<WowBPoolObject> EnumeratePoolObjects()
        {
            var result = new List<WowBPoolObject>();
            LastPoolSlotsScanned = 0;

            if (WowBOffsets.HandlePools == 0)
                return result;

            ulong table = Abs(WowBOffsets.HandlePools);

            for (int fam = 0; fam < MaxPools; fam++)
            {
                ulong pool = _memory.ReadPointer64(table + (ulong)fam * 8);
                if (!IsUserPointer(pool))
                    continue;

                ulong elems = _memory.ReadPointer64(pool + WowBOffsets.HandlePoolArrayOffset);
                if (!IsUserPointer(elems))
                    continue;

                uint cap = _memory.Read64<uint>(pool + WowBOffsets.HandlePoolCapacityOffset);
                if (cap == 0 || cap > MaxPoolSlots)
                    continue;

                for (uint slot = 0; slot < cap; slot++)
                {
                    LastPoolSlotsScanned++;

                    ulong elem = elems + (ulong)slot * WowBOffsets.HandlePoolElementStride;

                    byte[]? slotBytes = _memory.ReadBytes64(elem, 16);
                    if (slotBytes == null || slotBytes.Length != 16)
                        continue;
                    uint tag = BitConverter.ToUInt32(slotBytes, 0);
                    if ((tag & (uint)WowBOffsets.HandleOccupiedBit) == 0)
                        continue;
                    uint marker = BitConverter.ToUInt32(slotBytes, 4);
                    ulong rec = BitConverter.ToUInt64(slotBytes, 8);
                    if (!IsUserPointer(rec) ||
                        !TryReadGuid(rec + WowBOffsets.RecordGuidLoOffset, out WowBGuid guid) || guid.IsEmpty)
                        continue;

                    result.Add(new WowBPoolObject(guid, rec, fam, (int)slot, pool, elems, tag, marker));
                }
            }

            return result;
        }

        // Confirm that this scan's family still names the same pool/array and
        // the slot still contains the same tag, opaque marker and record.
        // This reduces stale-slot observations; ABA reuse and post-check
        // changes are still possible and the marker's semantics are unknown.
        private bool IsPoolRecordCurrent(WowBPoolObject record)
        {
            if (record.Family < 0 || record.Family >= MaxPools || record.Slot < 0 ||
                record.Slot >= MaxPoolSlots || !IsUserPointer(record.PoolPtr) || !IsUserPointer(record.SlotArray))
                return false;
            byte[]? familyBytes = _memory.ReadBytes64(Abs(WowBOffsets.HandlePools) + (ulong)record.Family * 8, 8);
            byte[]? arrayBytes = _memory.ReadBytes64(record.PoolPtr + WowBOffsets.HandlePoolArrayOffset, 8);
            byte[]? capacityBytes = _memory.ReadBytes64(record.PoolPtr + WowBOffsets.HandlePoolCapacityOffset, 4);
            if (familyBytes == null || familyBytes.Length != 8 || BitConverter.ToUInt64(familyBytes, 0) != record.PoolPtr ||
                arrayBytes == null || arrayBytes.Length != 8 || BitConverter.ToUInt64(arrayBytes, 0) != record.SlotArray ||
                capacityBytes == null || capacityBytes.Length != 4)
                return false;
            uint cap = BitConverter.ToUInt32(capacityBytes, 0);
            if (cap == 0 || cap > MaxPoolSlots || (uint)record.Slot >= cap)
                return false;
            ulong address = record.SlotArray + (ulong)record.Slot * WowBOffsets.HandlePoolElementStride;
            byte[]? slotBytes = _memory.ReadBytes64(address, 16);
            return slotBytes != null && slotBytes.Length == 16 &&
                BitConverter.ToUInt32(slotBytes, 0) == record.SlotTag &&
                (record.SlotTag & (uint)WowBOffsets.HandleOccupiedBit) != 0 &&
                BitConverter.ToUInt32(slotBytes, 4) == record.SlotMarker &&
                BitConverter.ToUInt64(slotBytes, 8) == record.RecordPtr;
        }

        /// <summary>Resolves a 128-bit GUID to its record via a pool scan.</summary>
        public bool TryFindObjectByGuid(WowBGuid guid, out WowBPoolObject found)
        {
            found = default;
            if (guid.IsEmpty)
                return false;

            foreach (WowBPoolObject o in EnumeratePoolObjects())
            {
                if (o.Guid == guid)
                {
                    found = o;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the record's world transform at the live-verified offsets:
        /// position +0x60 (Vector3) and facing +0x70 (radians 0..2pi).
        /// Returns false when the record is absent or the floats are NaNs.
        /// </summary>
        public bool TryGetObjectTransform(in WowBPoolObject obj, out float x, out float y, out float z, out float facing)
        {
            x = y = z = facing = 0f;
            if (obj.IsEmpty)
                return false;

            ulong rec = obj.RecordPtr;
            x = _memory.Read64<float>(rec + WowBOffsets.RecordPositionOffset);
            y = _memory.Read64<float>(rec + WowBOffsets.RecordPositionOffset + 4);
            z = _memory.Read64<float>(rec + WowBOffsets.RecordPositionOffset + 8);
            facing = _memory.Read64<float>(rec + WowBOffsets.RecordFacingOffset);

            return !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(z);
        }

        /// <summary>The local player's record (via PlayerGuid + pool scan).</summary>
        public bool TryGetPlayerObject(out WowBPoolObject obj)
        {
            obj = default;

            WowBGuid guid = ReadLocalGuid();
            if (guid.IsEmpty)
                return false;

            return TryFindObjectByGuid(guid, out obj);
        }

        /// <summary>
        /// Captures only records with matching back-pointer/GUID and a known
        /// player or CGUnit vtable. The snapshot is non-atomic and read-only;
        /// it cannot establish hostility, targetability, or current health.
        /// False means capture failed or changed during the scan, not an
        /// empty world. Even uncached re-reads cannot detect every race.
        /// </summary>
        public bool TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot)
        {
            // The stability checks below require fresh reads. Memory's cache
            // flag is thread-local; restore the caller's state even on failure.
            // This does not make the cross-process scan atomic.
            using var uncached = _memory.TemporaryCacheState(false);
            snapshot = null;
            if (WowBOffsets.PlayerGuid == 0 || WowBOffsets.PlayerTargetGuid == 0 ||
                !TryReadGuid(Abs(WowBOffsets.PlayerGuid), out WowBGuid local) || local.IsEmpty ||
                !TryReadGuid(Abs(WowBOffsets.PlayerTargetGuid), out WowBGuid target))
                return false;

            List<WowBPoolObject> records = EnumeratePoolObjects();
            var units = new List<WowBObservedUnit>();
            var seen = new HashSet<WowBGuid>();
            bool foundLocal = false;
            WowBPoolObject localRecord = default;
            ulong localUnit = 0, localVtable = 0;

            foreach (WowBPoolObject record in records)
            {
                bool fourByteCandidate = false;
                if (!TryGetUnitObject(record.RecordPtr, out ulong unitPtr, out ulong vtable, out bool playerVtable))
                {
                    // A monster was observed with an otherwise matching header
                    // at a 4-byte-aligned address. Limit this exception to the
                    // read-only snapshot, not the legacy object/stats accessors.
                    if (!TryGetFourByteUnitCandidate(record, out unitPtr, out vtable))
                        continue;
                    playerVtable = false;
                    fourByteCandidate = true;
                }
                ulong vtRva = vtable >= _memory.ImageBase64 ? vtable - _memory.ImageBase64 : 0;
                if (!playerVtable && vtRva != WowBOffsets.CGUnitFnTable)
                    continue;

                // Read the complete transform in one operation. Read64<T>
                // returns zero on a failed read, which would fabricate (0,0,0).
                byte[]? transform = _memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordPositionOffset, 20);
                if (transform == null || transform.Length != 20)
                    continue;
                float x = BitConverter.ToSingle(transform, 0);
                float y = BitConverter.ToSingle(transform, 4);
                float z = BitConverter.ToSingle(transform, 8);
                float facing = BitConverter.ToSingle(transform, 16);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(facing) ||
                    Math.Abs(x) >= 100000f || Math.Abs(y) >= 100000f || Math.Abs(z) >= 100000f)
                    continue;

                // Diagnostic-only data sample for the player and selected
                // target. Preserve the complete raw qwords: actual health
                // width, signedness and general semantics remain unverified.
                // An unreadable sample stays null and does not become zero.
                WowBCandidateHealthWords? candidateHealth = null;
                if (record.Guid == local || (!target.IsEmpty && record.Guid == target))
                {
                    byte[]? words = _memory.ReadBytes64(unitPtr + WowBOffsets.UnitDataHealthOffset, 16);
                    if (words != null && words.Length == 16)
                        candidateHealth = new WowBCandidateHealthWords(BitConverter.ToUInt64(words, 0), BitConverter.ToUInt64(words, 8));
                }

                // The record could have changed since pool enumeration. This
                // reduces, but cannot eliminate, races with the client.
                if (!TryReadGuid(record.RecordPtr + WowBOffsets.RecordGuidLoOffset, out WowBGuid recordAfter) || recordAfter != record.Guid)
                    continue;
                // Re-read the header and pointer after the transform for both
                // supported alignments. Reads for this capture bypass the
                // thread-local cache, but reuse and ABA races remain possible.
                if (fourByteCandidate)
                {
                    if (!TryGetFourByteUnitCandidate(record, out ulong fourUnitAfter, out ulong fourVtableAfter) ||
                        fourUnitAfter != unitPtr || fourVtableAfter != vtable)
                        continue;
                }
                else if (!TryGetUnitObject(record.RecordPtr, out ulong alignedUnitAfter, out ulong alignedVtableAfter, out bool playerAfter) ||
                         alignedUnitAfter != unitPtr || alignedVtableAfter != vtable || playerAfter != playerVtable)
                {
                    continue;
                }

                if (!IsPoolRecordCurrent(record))
                    continue;
                bool isLocal = record.Guid == local;
                if ((isLocal && !playerVtable) || !seen.Add(record.Guid))
                    continue;
                if (isLocal)
                {
                    foundLocal = true;
                    localRecord = record;
                    localUnit = unitPtr;
                    localVtable = vtable;
                }
                units.Add(new WowBObservedUnit(record.Guid, record.RecordPtr, unitPtr,
                    isLocal, playerVtable, new Styx.Logic.Pathing.WoWPoint(x, y, z), facing, candidateHealth));
            }

            // A selected target may change while the pool is being scanned.
            // Fail closed rather than publish a target from the beginning of
            // the scan as current. An ABA change can still hide transitions;
            // the snapshot is not atomic.
            if (!foundLocal || !IsPoolRecordCurrent(localRecord) ||
                !TryReadGuid(localRecord.RecordPtr + WowBOffsets.RecordGuidLoOffset, out WowBGuid localRecordAfter) || localRecordAfter != local ||
                !TryGetUnitObject(localRecord.RecordPtr, out ulong finalLocalUnit, out ulong finalLocalVtable, out bool finalLocalIsPlayer) ||
                finalLocalUnit != localUnit || finalLocalVtable != localVtable || !finalLocalIsPlayer ||
                !TryReadGuid(Abs(WowBOffsets.PlayerGuid), out WowBGuid localAfter) || localAfter != local ||
                !TryReadGuid(Abs(WowBOffsets.PlayerTargetGuid), out WowBGuid targetAfter) || targetAfter != target)
                return false;
            snapshot = new WowBWorldSnapshot(local, target, LastPoolSlotsScanned, units);
            return true;
        }

        // Snapshot-only experiment for the observed 4-mod-8 monster header.
        // A readable pointer alone is never sufficient: demand a known unit
        // vtable, this record's back-pointer and the complete nonempty GUID.
        private bool TryGetFourByteUnitCandidate(WowBPoolObject record, out ulong unit, out ulong vtable)
        {
            unit = 0;
            vtable = 0;
            if (!IsUserPointer(record.RecordPtr) || record.Guid.IsEmpty)
                return false;
            byte[]? pointer = _memory.ReadBytes64(record.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
            if (pointer == null || pointer.Length != 8)
                return false;
            ulong candidate = BitConverter.ToUInt64(pointer, 0);
            if (candidate <= 0x10000UL || candidate >= 0x00007FFFFFFFFFDFUL || (candidate & 7UL) != 4UL)
                return false;
            byte[]? header = _memory.ReadBytes64(candidate, 0x20);
            if (header == null || header.Length != 0x20)
                return false;
            ulong vt = BitConverter.ToUInt64(header, 0);
            if (vt < _memory.ImageBase64 || vt - _memory.ImageBase64 != WowBOffsets.CGUnitFnTable ||
                BitConverter.ToUInt64(header, 8) != record.RecordPtr ||
                new WowBGuid(BitConverter.ToUInt64(header, 0x10), BitConverter.ToUInt64(header, 0x18)) != record.Guid)
                return false;
            unit = candidate;
            vtable = vt;
            return true;
        }

        /// <summary>
        /// Read-only, best-effort explanation of why a selected GUID may be
        /// missing from the validated unit snapshot. This is a new pool scan,
        /// not the same atomic sample; the target may change during the scan.
        /// Never use the result to infer hostility or permission to interact.
        /// </summary>
        public string DiagnoseTarget(WowBGuid expected)
        {
            using var uncached = _memory.TemporaryCacheState(false);
            if (expected.IsEmpty)
                return "no-target";
            if (!TryReadGuid(Abs(WowBOffsets.PlayerTargetGuid), out WowBGuid before) || before != expected)
                return "target-changed-or-unreadable-before-scan";

            List<WowBPoolObject> records = EnumeratePoolObjects();
            int matches = 0;
            WowBPoolObject found = default;
            foreach (WowBPoolObject record in records)
            {
                if (record.Guid != expected)
                    continue;
                if (matches++ == 0)
                    found = record;
            }

            string detail;
            if (matches == 0)
            {
                detail = $"pool-record-not-found slots={LastPoolSlotsScanned}";
            }
            else
            {
                string prefix = $"matches={matches} fam={found.Family} slot={found.Slot} rec=0x{found.RecordPtr:X}";
                byte[]? pointerBytes = _memory.ReadBytes64(found.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
                if (pointerBytes == null || pointerBytes.Length != 8)
                {
                    detail = $"data-pointer-unreadable {prefix}";
                }
                else
                {
                    ulong unit = BitConverter.ToUInt64(pointerBytes, 0);
                    if (!IsUserPointer(unit))
                    {
                        // Misalignment is a filter rejection, not proof the
                        // address is invalid. Some observed +0xD8 values are
                        // 4-byte-aligned packed-array entries, so examine them
                        // without admitting them as units.
                        if (unit > 0x10000UL && unit < 0x00007FFFFFFFFFFFUL && (unit & 7UL) != 0)
                            detail = DescribeUnalignedTarget(found, expected, unit, records, prefix);
                        else
                            detail = $"data-pointer-invalid {prefix} data=0x{unit:X}";
                    }
                    else
                    {
                        // Read only the header: vtable, record back-pointer,
                        // and 128-bit GUID. Do not call any client functions.
                        byte[]? header = _memory.ReadBytes64(unit, 0x20);
                        if (header == null || header.Length != 0x20)
                        {
                            detail = $"unit-header-unreadable {prefix} unit=0x{unit:X}";
                        }
                        else
                        {
                            ulong vt = BitConverter.ToUInt64(header, 0);
                            ulong back = BitConverter.ToUInt64(header, 8);
                            var unitGuid = new WowBGuid(BitConverter.ToUInt64(header, 0x10), BitConverter.ToUInt64(header, 0x18));
                            ulong vtRva = vt >= _memory.ImageBase64 ? vt - _memory.ImageBase64 : 0;
                            string raw = $"{prefix} unit=0x{unit:X} back=0x{back:X} unitGuid={unitGuid} vtable=0x{vt:X} vtableRva=0x{vtRva:X}";
                            if (back != found.RecordPtr)
                                detail = $"back-pointer-mismatch {raw}";
                            else if (unitGuid != expected)
                                detail = $"unit-guid-mismatch {raw}";
                            else if (vtRva != WowBOffsets.PlayerObjectVtable && vtRva != WowBOffsets.CGUnitFnTable)
                                detail = $"vtable-not-in-unit-filter {raw}";
                            else
                            {
                                byte[]? transform = _memory.ReadBytes64(found.RecordPtr + WowBOffsets.RecordPositionOffset, 20);
                                if (transform == null || transform.Length != 20)
                                    detail = $"transform-unreadable {raw}";
                                else
                                {
                                    float x = BitConverter.ToSingle(transform, 0);
                                    float y = BitConverter.ToSingle(transform, 4);
                                    float z = BitConverter.ToSingle(transform, 8);
                                    float facing = BitConverter.ToSingle(transform, 16);
                                    if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(facing) ||
                                        Math.Abs(x) >= 100000f || Math.Abs(y) >= 100000f || Math.Abs(z) >= 100000f)
                                        detail = $"transform-invalid {raw} pos=({x},{y},{z}) facing={facing}";
                                    else
                                        detail = $"passes-filter-on-rescan {raw} pos=({x:F1},{y:F1},{z:F1}) facing={facing:F2}";
                                }
                            }
                        }
                    }
                }
            }

            if (!TryReadGuid(Abs(WowBOffsets.PlayerTargetGuid), out WowBGuid after) || after != expected)
                return "target-changed-or-unreadable-after-scan; discard diagnostic";
            // The record can change even if the selected GUID stays the same.
            return detail;
        }

        /// <summary>
        /// Bounded raw reads for an unaligned +0xD8 value. None of these
        /// observations promotes the target to a unit or proves a layout.
        /// </summary>
        private string DescribeUnalignedTarget(WowBPoolObject target, WowBGuid guid, ulong data,
            List<WowBPoolObject> records, string prefix)
        {
            byte[]? raw = _memory.ReadBytes64(data, 0x20);
            string header = raw != null && raw.Length == 0x20 ? Convert.ToHexString(raw) : "unreadable";
            string headerChecks = "unreadable";
            if (raw != null && raw.Length == 0x20)
            {
                ulong vt = BitConverter.ToUInt64(raw, 0);
                ulong vtRva = vt >= _memory.ImageBase64 ? vt - _memory.ImageBase64 : 0;
                bool backMatches = BitConverter.ToUInt64(raw, 8) == target.RecordPtr;
                var headerGuid = new WowBGuid(BitConverter.ToUInt64(raw, 0x10), BitConverter.ToUInt64(raw, 0x18));
                headerChecks = $"vtableRva=0x{vtRva:X},knownUnitVtable={vtRva == WowBOffsets.CGUnitFnTable},backMatches={backMatches},guidMatches={headerGuid == guid}";
            }
            string position = "unreadable";
            byte[]? transform = _memory.ReadBytes64(target.RecordPtr + WowBOffsets.RecordPositionOffset, 20);
            if (transform != null && transform.Length == 20)
            {
                float x = BitConverter.ToSingle(transform, 0);
                float y = BitConverter.ToSingle(transform, 4);
                float z = BitConverter.ToSingle(transform, 8);
                float facing = BitConverter.ToSingle(transform, 16);
                bool finite = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(facing);
                position = $"({x:F2},{y:F2},{z:F2};f={facing:F2};finite={finite})";
            }

            // Previous surveys observed some unaligned values 0x2F4 apart.
            // Report actual matches rather than assuming this is that array.
            int strideMatches = 0;
            var strideExamples = new List<string>();
            foreach (WowBPoolObject other in records)
            {
                if (other.RecordPtr == target.RecordPtr)
                    continue;
                byte[]? valueBytes = _memory.ReadBytes64(other.RecordPtr + WowBOffsets.RecordDataBlockPtrOffset, 8);
                if (valueBytes == null || valueBytes.Length != 8)
                    continue;
                ulong value = BitConverter.ToUInt64(valueBytes, 0);
                ulong delta = value > data ? value - data : data - value;
                if (delta != 0x2F4UL)
                    continue;
                strideMatches++;
                if (strideExamples.Count < 4)
                    strideExamples.Add($"fam{other.Family}/slot{other.Slot}:0x{value:X}");
            }

            // Check aligned pointer-sized fields in the target record for an
            // alternative object header that repeats BOTH the record back
            // pointer and full GUID. Values are candidates, not accepted units.
            var alternatives = new List<string>();
            for (int offset = 0x28; offset <= 0x108; offset += 8)
            {
                byte[]? valueBytes = _memory.ReadBytes64(target.RecordPtr + (ulong)offset, 8);
                if (valueBytes == null || valueBytes.Length != 8)
                    continue;
                ulong candidate = BitConverter.ToUInt64(valueBytes, 0);
                if (!IsUserPointer(candidate))
                    continue;
                byte[]? candidateHeader = _memory.ReadBytes64(candidate, 0x20);
                if (candidateHeader == null || candidateHeader.Length != 0x20)
                    continue;
                ulong back = BitConverter.ToUInt64(candidateHeader, 8);
                var candidateGuid = new WowBGuid(BitConverter.ToUInt64(candidateHeader, 0x10),
                    BitConverter.ToUInt64(candidateHeader, 0x18));
                if (back != target.RecordPtr || candidateGuid != guid)
                    continue;
                ulong vt = BitConverter.ToUInt64(candidateHeader, 0);
                if (alternatives.Count < 4)
                    alternatives.Add($"+0x{offset:X}:0x{candidate:X}/vt=0x{vt:X}");
            }

            return $"data-pointer-not-8-aligned {prefix} data=0x{data:X} alignment={data & 7UL} " +
                $"raw32={header} headerChecks=({headerChecks}) recordPositionCandidate={position} stride0x2F4Matches={strideMatches} " +
                $"strideExamples=[{string.Join(",", strideExamples)}] alternateHeaders=[{string.Join(",", alternatives)}]";
        }

        private bool TryReadGuid(ulong address, out WowBGuid guid)
        {
            guid = default;
            byte[]? bytes = _memory.ReadBytes64(address, 16);
            if (bytes == null || bytes.Length != 16)
                return false;
            guid = new WowBGuid(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
            return true;
        }


        // ------------------------------------------------------------------
        // Unit fields read without Lua (2026-10-30)
        // ------------------------------------------------------------------
        //
        // The Lua bridge is retired: taking the client's Lua thread over crashed wowb
        // twice in one evening (FrameScript_IsLuaThread on 2026-10-30 14:38, "Lua stack
        // is unbalanced" at Ui/GameUI.cpp:8250 and then a 0xC0000028 unwind fault at
        // 15:34). Everything the grind loop needs from the client has to come from
        // memory it already reads or from the client's own input handling.

        /// <summary>
        /// The fields of a unit's data block that a grind loop needs, all read as raw
        /// 32-bit words. Health/max are the ones the snapshot already prints for the
        /// player (95/95 on 2026-10-30); level is the [V-ish] 0x1E40 word; the rest are
        /// candidates and are logged as raw values, never acted on as if verified.
        /// </summary>
        public readonly struct WowBUnitFields
        {
            public readonly uint Health;
            public readonly uint MaxHealth;
            public readonly uint Level;
            public readonly uint UnitFlags;
            public readonly uint Power;
            public readonly uint MaxPower;
            public readonly ulong DataPtr;

            public WowBUnitFields(uint health, uint maxHealth, uint level, uint unitFlags,
                                  uint power, uint maxPower, ulong dataPtr)
            {
                Health = health;
                MaxHealth = maxHealth;
                Level = level;
                UnitFlags = unitFlags;
                Power = power;
                MaxPower = maxPower;
                DataPtr = dataPtr;
            }

            public override string ToString()
            {
                return $"hp={Health}/{MaxHealth} lvl={Level} uflags=0x{UnitFlags:X} pow={Power}/{MaxPower}";
            }
        }

        /// <summary>
        /// Health/max/level/flags for one unit object, in one read. False when the data
        /// block pointer is not a plausible user pointer or the block does not read.
        /// </summary>
        public bool TryGetUnitFields(ulong unitPtr, out WowBUnitFields fields)
        {
            fields = default;
            // Not IsUserPointer: a monster unit object is legitimately 4-byte aligned on
            // this build (TryGetFourByteUnitCandidate exists for exactly that), and the
            // 8-byte rule would refuse to read the mobs the grind loop has to fight.
            if (unitPtr <= 0x10000UL || unitPtr >= 0x00007FFFFFFFFFFFUL)
                return false;

            // The unit object's data block is the same pointer the health words come
            // from: on 70291 the object at record+0xD8 is the block itself (hunt31).
            ulong data = unitPtr;
            byte[]? head = _memory.ReadBytes64(data, 0x08);
            if (head == null || head.Length != 8)
                return false;

            byte[]? level = _memory.ReadBytes64(data + WowBOffsets.UnitDataLevelOffset, 4);
            byte[]? hp = _memory.ReadBytes64(data + WowBOffsets.UnitDataHealthOffset, 8);
            byte[]? flags = _memory.ReadBytes64(data + WowBOffsets.UnitDataUnitFlagsOffset, 4);
            byte[]? pow = _memory.ReadBytes64(data + WowBOffsets.UnitDataPowerOffset, 8);
            if (level == null || level.Length != 4 || hp == null || hp.Length != 8 ||
                flags == null || flags.Length != 4 || pow == null || pow.Length != 8)
                return false;

            fields = new WowBUnitFields(
                BitConverter.ToUInt32(hp, 0), BitConverter.ToUInt32(hp, 4),
                BitConverter.ToUInt32(level, 0), BitConverter.ToUInt32(flags, 0),
                BitConverter.ToUInt32(pow, 0), BitConverter.ToUInt32(pow, 4), data);
            return true;
        }

        /// <summary>The local player's level from memory, or 0 when it cannot be read.</summary>
        public int ReadPlayerLevel()
        {
            if (!TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null)
                return 0;
            if (!snapshot.TryGetLocalPlayer(out WowBObservedUnit me))
                return 0;
            if (!TryGetUnitFields(me.UnitAddress, out WowBUnitFields fields))
                return 0;
            return fields.Level <= 100 ? (int)fields.Level : 0;
        }

        /// <summary>
        /// A raw window of 32-bit words from a unit's data block, for the offset hunt.
        /// Nothing here is interpreted: the caller prints it and a person decides.
        /// </summary>
        public string DumpWords(ulong address, int count)
        {
            if (!IsUserPointer(address) || count <= 0 || count > 128)
                return "";
            byte[]? bytes = _memory.ReadBytes64(address, count * 4);
            if (bytes == null || bytes.Length != count * 4)
                return "<unreadable>";
            var sb = new System.Text.StringBuilder(count * 8);
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && (i % 8) == 0) sb.Append(Environment.NewLine).Append("      ");
                else if (i > 0) sb.Append(' ');
                sb.Append(BitConverter.ToUInt32(bytes, i * 4).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>How many action-bar slots the stored array holds (0x198 / 8 = 51).</summary>
        public const int ActionSlotCount = 0x198 / 8;

        /// <summary>
        /// One stored action-bar slot, exactly as the client keeps it.
        ///
        /// The array is [V] (hunt31 dumped it at 0x7DFF4E0, 0x198 bytes), but the packing
        /// of a slot is a candidate: the modern client keeps a 64-bit value per slot and
        /// GetActionInfo decodes it. Both readings a person would try are printed rather
        /// than one being asserted.
        /// </summary>
        public readonly struct WowBActionSlot
        {
            public readonly int Index;        // 1-based, the number UseAction takes
            public readonly ulong Raw;

            public WowBActionSlot(int index, ulong raw)
            {
                Index = index;
                Raw = raw;
            }

            public bool IsEmpty { get { return Raw == 0; } }

            /// <summary>Candidate A: low 24 bits are the spell/item/macro id, bits 24-31 the type.</summary>
            public int IdLow24 { get { return (int)(Raw & 0xFFFFFFUL); } }
            public int TypeHigh8 { get { return (int)((Raw >> 24) & 0xFFUL); } }

            /// <summary>Candidate B: low 32 bits are the id, the next 32 bits the type.</summary>
            public int IdLow32 { get { return unchecked((int)(Raw & 0xFFFFFFFFUL)); } }
            public int TypeHigh32 { get { return unchecked((int)(Raw >> 32)); } }

            public override string ToString()
            {
                return $"slot {Index}: raw=0x{Raw:X16} (low24id={IdLow24} type8={TypeHigh8}; low32={IdLow32} high32={TypeHigh32})";
            }
        }

        /// <summary>
        /// The client's stored action bar. An empty slot is all zeros - which is what a
        /// character at level 2 with two spells on the bar should mostly show.
        /// </summary>
        public List<WowBActionSlot> ReadActionBar()
        {
            var slots = new List<WowBActionSlot>();
            byte[]? bytes = _memory.ReadBytes64(Abs(WowBOffsets.StoredActionArray), ActionSlotCount * 8);
            if (bytes == null || bytes.Length != ActionSlotCount * 8)
                return slots;
            for (int i = 0; i < ActionSlotCount; i++)
            {
                ulong raw = BitConverter.ToUInt64(bytes, i * 8);
                if (raw == 0) continue;
                slots.Add(new WowBActionSlot(i + 1, raw));
            }
            return slots;
        }

        private bool TryReadUInt32(ulong address, out uint value)
        {
            value = 0;
            byte[]? bytes = _memory.ReadBytes64(address, 4);
            if (bytes == null || bytes.Length != 4)
                return false;
            value = BitConverter.ToUInt32(bytes, 0);
            return true;
        }

        // ------------------------------------------------------------------
        // Unit data block (stats)
        // ------------------------------------------------------------------

        /// <summary>
        /// Diagnostic low-32-bit stat candidates read from the record's data
        /// block (*(record+0xD8)). Previous live samples were plausible, but
        /// field widths, meanings and general applicability remain unverified.
        /// </summary>
        public readonly struct WowBUnitStats
        {
            public readonly uint Health;
            public readonly uint MaxHealth;
            public readonly uint Power;
            public readonly uint MaxPower;
            public readonly uint UnitFlags;
            public readonly ulong DataPtr;

            public WowBUnitStats(uint health, uint maxHealth, uint power, uint maxPower, uint unitFlags, ulong dataPtr)
            {
                Health = health;
                MaxHealth = maxHealth;
                Power = power;
                MaxPower = maxPower;
                UnitFlags = unitFlags;
                DataPtr = dataPtr;
            }

            public override string ToString()
                => $"hp={Health}/{MaxHealth} pow={Power}/{MaxPower}(max-candidate) uflags=0x{UnitFlags:X} data=0x{DataPtr:X}";
        }

        /// <summary>
        /// True when the record looks like a world object: its flag dword at
        /// +0x48 is initialised (not the 0xCECECECE marker) and its position
        /// is finite. Class-A 0x60-stride records (items/placeholders) fail
        /// this test; class-B 0x110-stride records (units, game objects)
        /// pass it.
        /// </summary>
        public bool IsWorldObject(in WowBPoolObject obj)
        {
            if (obj.IsEmpty)
                return false;

            uint flags = _memory.Read64<uint>(obj.RecordPtr + WowBOffsets.RecordObjectFlagsOffset);
            if (flags == unchecked((uint)WowBOffsets.RecordPoisonDword))
                return false;

            float x = _memory.Read64<float>(obj.RecordPtr + WowBOffsets.RecordPositionOffset);
            float y = _memory.Read64<float>(obj.RecordPtr + WowBOffsets.RecordPositionOffset + 4);
            float z = _memory.Read64<float>(obj.RecordPtr + WowBOffsets.RecordPositionOffset + 8);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
                return false;

            return Math.Abs(x) < 100000f && Math.Abs(y) < 100000f && Math.Abs(z) < 100000f;
        }

        /// <summary>
        /// Reads diagnostic low-32-bit stat candidates for an aligned data
        /// block. False on an unavailable read; these values are not a proof
        /// of field width or usable combat stats.
        /// </summary>
        public bool TryGetUnitStats(ulong recordPtr, out WowBUnitStats stats)
        {
            stats = default;
            if (!IsUserPointer(recordPtr))
                return false;

            ulong data = _memory.ReadPointer64(recordPtr + WowBOffsets.RecordDataBlockPtrOffset);
            if (!IsUserPointer(data))
                return false;

            if (!TryReadUInt32(data + WowBOffsets.UnitDataHealthOffset, out uint health) ||
                !TryReadUInt32(data + WowBOffsets.UnitDataMaxHealthOffset, out uint maximumHealth) ||
                !TryReadUInt32(data + WowBOffsets.UnitDataPowerOffset, out uint power) ||
                !TryReadUInt32(data + WowBOffsets.UnitDataMaxPowerOffset, out uint maximumPower) ||
                !TryReadUInt32(data + WowBOffsets.UnitDataUnitFlagsOffset, out uint unitFlags))
                return false;
            stats = new WowBUnitStats(health, maximumHealth, power, maximumPower, unitFlags, data);
            return true;
        }

        /// <summary>
        /// The unit object (the C++ object FnEntityGetGuid consumes): the
        /// record's data block pointer. It carries a vtable at +0x00
        /// (CGUnitFnTable for NPCs, the player vtable for the local
        /// player), the back-reference to the record at +0x08, the GUID at
        /// +0x10/+0x18 and the reference stat block higher up.
        /// Verified live by WowBHunt29.
        /// </summary>
        public bool TryGetUnitObject(ulong recordPtr, out ulong unitPtr, out ulong vtable, out bool isPlayerVtable)
        {
            unitPtr = 0;
            vtable = 0;
            isPlayerVtable = false;

            if (!IsUserPointer(recordPtr))
                return false;

            ulong unit = _memory.ReadPointer64(recordPtr + WowBOffsets.RecordDataBlockPtrOffset);
            if (!IsUserPointer(unit) || (unit & 7) != 0)
                return false;

            // hunt31 §2: ~45% of the occupied records carry something at
            // +0xD8 that is not an object at all - consecutive slots differ
            // by exactly 0x2F4 (packed-array entries, not pointers) and the
            // qword there is not a vtable. Structural validation removes all
            // of those:
            //   * +0x08 must point back at this very record
            //   * +0x10/+0x18 must repeat the record's 128-bit GUID
            ulong back = _memory.ReadPointer64(unit + WowBOffsets.UnitObjectBackPtrOffset);
            if (back != recordPtr)
                return false;

            ulong uLo = _memory.ReadPointer64(unit + WowBOffsets.UnitObjectGuidLoOffset);
            ulong uHi = _memory.ReadPointer64(unit + WowBOffsets.UnitObjectGuidHiOffset);
            ulong rLo = _memory.ReadPointer64(recordPtr + WowBOffsets.RecordGuidLoOffset);
            ulong rHi = _memory.ReadPointer64(recordPtr + WowBOffsets.RecordGuidHiOffset);
            if (uLo != rLo || uHi != rHi)
                return false;

            ulong vt = _memory.ReadPointer64(unit);
            unitPtr = unit;
            vtable = vt;

            ulong vtRva = vt >= _memory.ImageBase64 ? vt - _memory.ImageBase64 : vt;
            isPlayerVtable = vtRva == WowBOffsets.PlayerObjectVtable;

            return true;
        }

        /// <summary>True when the record's unit object looks like a CGUnit (NPC).</summary>
        public bool IsUnitObject(ulong recordPtr)
        {
            if (!TryGetUnitObject(recordPtr, out _, out ulong vt, out bool isPlayer))
                return false;

            if (isPlayer)
                return true; // the player is also a unit

            ulong vtRva = vt >= _memory.ImageBase64 ? vt - _memory.ImageBase64 : vt;
            return vtRva == WowBOffsets.CGUnitFnTable;
        }

        // ------------------------------------------------------------------
        // Player name (verified: stored inline in the module, no chase)
        // ------------------------------------------------------------------

        /// <summary>The local player's name (module string, read directly).</summary>
        public string ReadPlayerName()
            => WowBOffsets.PlayerNamePrimary == 0 ? string.Empty : (_memory.ReadString64(Abs(WowBOffsets.PlayerNamePrimary), 32) ?? string.Empty);

        // ------------------------------------------------------------------
        // Camera
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the active camera position. Returns false while the
        /// camera offsets are still placeholders.
        /// </summary>
        public bool TryGetCameraPosition(out float x, out float y, out float z)
        {
            x = y = z = 0f;

            if (WowBOffsets.CameraManager == 0 || WowBOffsets.CameraPosOffset == 0)
                return false;

            ulong mgr = _memory.ReadPointer64(Abs(WowBOffsets.CameraManager));
            if (mgr == 0)
                return false;

            ulong camera = _memory.ReadPointer64(mgr + WowBOffsets.CameraArrayOffset);
            if (camera == 0)
                return false;

            x = _memory.Read64<float>(camera + WowBOffsets.CameraPosOffset);
            y = _memory.Read64<float>(camera + WowBOffsets.CameraPosOffset + 4);
            z = _memory.Read64<float>(camera + WowBOffsets.CameraPosOffset + 8);
            return true;
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        /// <summary>
        /// One-line summary of everything the reader can see right now —
        /// designed to be logged on attach to validate the 64-bit read
        /// path end-to-end.
        /// </summary>
        public string DescribeSnapshot()
        {
            var parts = new List<string>
            {
                $"inWorld={(WowBOffsets.GameState == 0 ? "unknown" : IsInWorld.ToString())}",
                $"localGuid={ReadLocalGuid()}",
                $"targetGuid={ReadTargetGuid()}",
                $"zone={ZoneId}/{AreaId}/{SubAreaId}",
                $"zoneText=\"{ZoneText}\"",
                $"entities={EntityCount}",
                $"objMgr=0x{ObjectManagerPtr:X}"
            };

            if (TryGetCameraPosition(out float cx, out float cy, out float cz))
                parts.Add($"camera=({cx:F1}, {cy:F1}, {cz:F1})");
            else
                parts.Add("camera=n/a");

            if (TryGetPlayerObject(out WowBPoolObject pobj) &&
                TryGetObjectTransform(pobj, out float px, out float py, out float pz, out float pf))
            {
                string stats = "stats=n/a";
                if (TryGetUnitStats(pobj.RecordPtr, out WowBUnitStats st))
                    stats = st.ToString();

                string unit = "unit=n/a";
                if (TryGetUnitObject(pobj.RecordPtr, out ulong unitPtr, out ulong vtable, out bool isPlayerVt))
                    unit = $"unit=0x{unitPtr:X}({(isPlayerVt ? "player-vt" : "vt 0x" + (vtable - _memory.ImageBase64).ToString("X"))})";

                parts.Add($"player={ReadPlayerName()} {pobj} pos=({px:F1}, {py:F1}, {pz:F1}) facing={pf:F2} {stats} {unit}");
            }
            else
                parts.Add("playerObject=n/a");

            return string.Join(", ", parts);
        }
    }
}

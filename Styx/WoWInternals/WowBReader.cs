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
<<<<<<< ours
=======
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

            public WowBPoolObject(WowBGuid guid, ulong recordPtr, int family, int slot)
            {
                Guid = guid;
                RecordPtr = recordPtr;
                Family = family;
                Slot = slot;
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
        /// {u32 tag (bit31 = occupied), u32 generation, u64 record*} and the
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

                    uint tag = _memory.Read64<uint>(elem);
                    if ((tag & (uint)WowBOffsets.HandleOccupiedBit) == 0)
                        continue;

                    ulong rec = _memory.ReadPointer64(elem + WowBOffsets.HandlePoolRecordOffset);
                    if (!IsUserPointer(rec))
                        continue;

                    ulong lo = _memory.Read64<ulong>(rec + WowBOffsets.RecordGuidLoOffset);
                    ulong hi = _memory.Read64<ulong>(rec + WowBOffsets.RecordGuidHiOffset);
                    if (lo == 0UL && hi == 0UL)
                        continue;

                    result.Add(new WowBPoolObject(new WowBGuid(lo, hi), rec, fam, (int)slot));
                }
            }

            return result;
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

        // ------------------------------------------------------------------
        // Unit data block (stats)
        // ------------------------------------------------------------------

        /// <summary>
        /// Unit stats read from the record's data block (*(record+0xD8)).
        /// Live-verified offsets: health/maxHealth pairs and the power pair;
        /// the reference-build layout survived on 70291 (hunt28 read
        /// health 84/84, power 96 on a fresh character).
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
                => $"hp={Health}/{MaxHealth} pow={Power}/{MaxPower} uflags=0x{UnitFlags:X} data=0x{DataPtr:X}";
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

        /// <summary>Reads the unit stat block for a record. False when the record has no data block.</summary>
        public bool TryGetUnitStats(ulong recordPtr, out WowBUnitStats stats)
        {
            stats = default;
            if (!IsUserPointer(recordPtr))
                return false;

            ulong data = _memory.ReadPointer64(recordPtr + WowBOffsets.RecordDataBlockPtrOffset);
            if (!IsUserPointer(data))
                return false;

            stats = new WowBUnitStats(
                _memory.Read64<uint>(data + WowBOffsets.UnitDataHealthOffset),
                _memory.Read64<uint>(data + WowBOffsets.UnitDataMaxHealthOffset),
                _memory.Read64<uint>(data + WowBOffsets.UnitDataPowerOffset),
                _memory.Read64<uint>(data + WowBOffsets.UnitDataMaxPowerOffset),
                _memory.Read64<uint>(data + WowBOffsets.UnitDataUnitFlagsOffset),
                data);
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
            => WowBOffsets.PlayerName == 0 ? string.Empty : (_memory.ReadString64(Abs(WowBOffsets.PlayerName), 32) ?? string.Empty);

        // ------------------------------------------------------------------
>>>>>>> theirs
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
                $"inWorld={IsInWorld}",
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

<<<<<<< ours
=======
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

>>>>>>> theirs
            return string.Join(", ", parts);
        }
    }
}

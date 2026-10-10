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

            return string.Join(", ", parts);
        }
    }
}

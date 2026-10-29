#if WOWB_READER_TESTS
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

// A deterministic byte-addressed process-memory substitute. No game or native
// calls: exercises the real pool reader and snapshot filtering logic.
var mem = new Memory();
ulong module = mem.ImageBase64;
ulong pool = 0x20000000, array = 0x20010000;
WowBGuid player = new(7, 0x0801000000000000);
WowBGuid npc = new(42, 0x1234000000000000);
WowBGuid sameLow = new(42, 0x5678000000000000);
mem.Guid(module + WowBOffsets.PlayerGuid, player);
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
mem.U64(module + WowBOffsets.HandlePools, pool);
mem.U64(pool + WowBOffsets.HandlePoolArrayOffset, array);
mem.U32(pool + WowBOffsets.HandlePoolCapacityOffset, 4);
Add(0, 0x30000000, 0x40000000, player, WowBOffsets.PlayerObjectVtable, 1, 2, 3);
Add(1, 0x30001000, 0x40001000, npc, WowBOffsets.CGUnitFnTable, 4, 5, 6);
Add(2, 0x30002000, 0x40002000, sameLow, WowBOffsets.CGUnitFnTable, 7, 8, 9);
Add(3, 0x30003000, 0x40003000, new WowBGuid(50, 60), 0x1234, 4, 5, 6); // unknown vtable
var reader = new WowBReader(mem);
reader.ReadPlayerName();
Require(mem.LastStringReadAddress == module + WowBOffsets.PlayerNamePrimary, "player name RVA");
Require(reader.TryCaptureWorldSnapshot(out var snapshot) && snapshot != null, "capture with local player");
Require(snapshot!.Units.Count == 3, "filter unknown vtable");
Require(snapshot.LocalGuid == player && snapshot.TargetGuid == npc, "full GUIDs");
Require(snapshot.Units[0].IsLocalPlayer && snapshot.Units[0].Location.X == 1, "local transform");
Require(snapshot.TryGetLocalPlayer(out var localView) && localView.Guid == player && localView.IsLocalPlayer, "local observation accessor");
Require(snapshot.TryGetSelectedTarget(out var targetView) && targetView.Guid == npc, "target observation accessor");
Require(localView.CandidateHealth.HasValue && localView.CandidateHealth.Value.CurrentRaw64 == 0x100000005UL &&
    localView.CandidateHealth.Value.MaximumRaw64 == 0x20000000AUL && localView.CandidateHealth.Value.CurrentLow32 == 5,
    "local health candidate preserves both qwords");
Require(targetView.CandidateHealth.HasValue && targetView.CandidateHealth.Value.MaximumLow32 == 10, "selected target health candidate");
Require(snapshot.TryGetUnit(sameLow, out var otherHealth) && !otherHealth.CandidateHealth.HasValue, "other units not sampled for candidate health");
Require(reader.TryGetUnitStats(0x30000000, out var legacyStats) && legacyStats.Health == 5, "complete diagnostic stats");
mem.Remove(0x40000000 + WowBOffsets.UnitDataPowerOffset + 1);
Require(!reader.TryGetUnitStats(0x30000000, out _), "failed diagnostic stat read is unavailable");
mem.U32(0x40000000 + WowBOffsets.UnitDataPowerOffset, 30);
// Preserve the unit if the 16-byte candidate is partially unreadable, but do
// not fabricate zeros. A readable all-zero candidate remains distinguishable.
mem.Remove(0x40001000 + WowBOffsets.UnitDataMaxHealthOffset + 1);
Require(reader.TryCaptureWorldSnapshot(out var missingHealth) && missingHealth!.TryGetSelectedTarget(out var noHealth) &&
    !noHealth.CandidateHealth.HasValue, "unreadable candidate remains unavailable");
mem.U64(0x40001000 + WowBOffsets.UnitDataMaxHealthOffset, 0x20000000AUL);
mem.U64(0x40001000 + WowBOffsets.UnitDataHealthOffset, 0);
mem.U64(0x40001000 + WowBOffsets.UnitDataMaxHealthOffset, 0);
Require(reader.TryCaptureWorldSnapshot(out var zeroHealth) && zeroHealth!.TryGetSelectedTarget(out var zeroTarget) &&
    zeroTarget.CandidateHealth.HasValue && zeroTarget.CandidateHealth.Value.CurrentRaw64 == 0 &&
    zeroTarget.CandidateHealth.Value.MaximumRaw64 == 0, "readable zero candidate is present");
mem.U64(0x40001000 + WowBOffsets.UnitDataHealthOffset, 0x100000005UL);
mem.U64(0x40001000 + WowBOffsets.UnitDataMaxHealthOffset, 0x20000000AUL);
mem.OnRead = (address, count) =>
{
    if (address == 0x40001000 + WowBOffsets.UnitDataHealthOffset && count == 16)
        mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40002000);
};
Require(reader.TryCaptureWorldSnapshot(out var changedHealth) && !changedHealth!.TryGetSelectedTarget(out _), "changed unit during candidate read rejected");
mem.OnRead = null;
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40001000);
// Compare independently captured snapshots by the complete GUID, not by
// address or the low 64 bits. These are observations, not spawn events.
WowBWorldSnapshot beforeChanges = snapshot;
WowBSnapshotDifference sameCapture = beforeChanges.CompareTo(beforeChanges);
Require(sameCapture.NewlyObserved == 0 && sameCapture.NoLongerObserved == 0 &&
    sameCapture.RecordAddressChanged == 0 && sameCapture.UnitAddressChanged == 0 && !sameCapture.TargetGuidChanged,
    "same snapshot has no differences");
Add(1, 0x30004000, 0x40004000, npc, WowBOffsets.CGUnitFnTable, 4, 5, 6);
Require(reader.TryCaptureWorldSnapshot(out var movedSnapshot) && movedSnapshot != null, "capture relocated record");
WowBSnapshotDifference moved = movedSnapshot!.CompareTo(beforeChanges);
Require(moved.NewlyObserved == 0 && moved.NoLongerObserved == 0 && moved.RecordAddressChanged == 1 &&
    moved.UnitAddressChanged == 1 && !moved.TargetGuidChanged, "relocated record and unit keep full identity");
Add(1, 0x30001000, 0x40001000, npc, WowBOffsets.CGUnitFnTable, 4, 5, 6);
mem.U32(array + WowBOffsets.HandlePoolElementStride, 0);
Require(reader.TryCaptureWorldSnapshot(out var missingSnapshot) && missingSnapshot != null, "capture missing unit");
WowBSnapshotDifference missing = missingSnapshot!.CompareTo(beforeChanges);
Require(missing.NoLongerObserved == 1 && missing.NewlyObserved == 0 && !missing.TargetGuidChanged, "missing observation is not identity change");
mem.U32(array + WowBOffsets.HandlePoolElementStride, (uint)WowBOffsets.HandleOccupiedBit);
mem.U64(0x40003000, module + WowBOffsets.CGUnitFnTable);
Require(reader.TryCaptureWorldSnapshot(out var addedSnapshot) && addedSnapshot != null, "capture new eligible unit");
WowBSnapshotDifference added = addedSnapshot!.CompareTo(beforeChanges);
Require(added.NewlyObserved == 1 && added.NoLongerObserved == 0, "newly observed unit");
mem.U64(0x40003000, module + 0x1234);
mem.Guid(module + WowBOffsets.PlayerTargetGuid, sameLow);
Require(reader.TryCaptureWorldSnapshot(out var differentTarget) && differentTarget != null, "capture target high-half change");
WowBSnapshotDifference targetDifference = differentTarget!.CompareTo(beforeChanges);
Require(targetDifference.TargetGuidChanged && !targetDifference.LocalGuidChanged &&
    targetDifference.NewlyObserved == 0 && targetDifference.NoLongerObserved == 0, "target comparison uses high GUID half");
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);

Require(snapshot.Units[1].Guid != snapshot.Units[2].Guid, "same low GUID retains high half");
Require(snapshot.TryGetUnit(npc, out var observedTarget) && observedTarget.Guid == npc && observedTarget.Location.X == 4, "resolve target by full GUID");
Require(snapshot.TryGetUnit(sameLow, out var other) && other.Location.X == 7, "different high GUID resolves different unit");
Require(!snapshot.TryGetUnit(new WowBGuid(42, 123), out _) && !snapshot.TryGetUnit(default, out _), "reject unknown and empty GUIDs");
// Capture bypasses even a caller-enabled cache and restores the caller's
// state on success, an early return, and exceptions.
mem.EnableCache();
mem.OnRead = (address, count) => Require(!mem.CacheEnabled, "capture bypasses cache");
Require(reader.TryCaptureWorldSnapshot(out snapshot) && mem.CacheEnabled, "cache restored after successful capture");
mem.OnRead = null;
mem.Guid(module + WowBOffsets.PlayerGuid, default);
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && mem.CacheEnabled, "cache restored after early return");
mem.Guid(module + WowBOffsets.PlayerGuid, player);
mem.OnRead = (address, count) => throw new InvalidOperationException("synthetic read error");
try
{
    reader.TryCaptureWorldSnapshot(out snapshot);
    throw new Exception("FAIL: expected synthetic read error");
}
catch (InvalidOperationException ex) when (ex.Message == "synthetic read error")
{
    Require(mem.CacheEnabled, "cache restored after exception");
}
mem.OnRead = (address, count) => Require(!mem.CacheEnabled, "diagnostic bypasses cache");
Require(reader.DiagnoseTarget(npc).StartsWith("passes-filter-on-rescan") && mem.CacheEnabled, "diagnostic restores cache");
Require(reader.DiagnoseTarget(default) == "no-target" && mem.CacheEnabled, "diagnostic early return restores cache");
mem.OnRead = null;
mem.DisableCache();
Require(reader.DiagnoseTarget(npc).StartsWith("passes-filter-on-rescan"), "diagnose known target");
// Exercise stable no-target and changing target during a non-atomic scan.
mem.Guid(module + WowBOffsets.PlayerTargetGuid, default);
Require(reader.TryCaptureWorldSnapshot(out snapshot) && snapshot!.TargetGuid.IsEmpty, "stable empty target captured");
Require(!snapshot!.TryGetSelectedTarget(out _), "empty target accessor does not resolve");
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.Guid(module + WowBOffsets.PlayerTargetGuid, sameLow);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "target changed during capture");
mem.OnRead = null;
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.Guid(module + WowBOffsets.PlayerTargetGuid, default);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "target cleared during capture");
mem.OnRead = null;
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.Remove(module + WowBOffsets.PlayerTargetGuid + 8);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "target unreadable after capture");
mem.OnRead = null;
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
// Slot changes after the pool scan are rejected even when the old record and
// header remain readable. The marker is compared as opaque bytes.
ulong npcSlot = array + WowBOffsets.HandlePoolElementStride;
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U32(npcSlot + 4, 0x12345679);
};
Require(reader.TryCaptureWorldSnapshot(out var slotChanged) && !slotChanged!.TryGetSelectedTarget(out _), "changed slot marker rejected");
mem.OnRead = null;
mem.U32(npcSlot + 4, 0x12345678);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U32(npcSlot, 0);
};
Require(reader.TryCaptureWorldSnapshot(out slotChanged) && !slotChanged!.TryGetSelectedTarget(out _), "unoccupied slot rejected");
mem.OnRead = null;
mem.U32(npcSlot, (uint)WowBOffsets.HandleOccupiedBit);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U64(npcSlot + WowBOffsets.HandlePoolRecordOffset, 0x30002000);
};
Require(reader.TryCaptureWorldSnapshot(out slotChanged) && !slotChanged!.TryGetSelectedTarget(out _), "replaced slot record rejected");
mem.OnRead = null;
mem.U64(npcSlot + WowBOffsets.HandlePoolRecordOffset, 0x30001000);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.Remove(npcSlot + 4);
};
Require(reader.TryCaptureWorldSnapshot(out slotChanged) && !slotChanged!.TryGetSelectedTarget(out _), "unreadable slot rejected");
mem.OnRead = null;
mem.U32(npcSlot + 4, 0x12345678);
// The local player's slot and object are checked again at the end. If the
// player disappears after its record was processed, the entire capture fails.
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U32(array + 4, 0x12345679);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "local slot changed late in capture");
mem.OnRead = null;
mem.U32(array + 4, 0x12345678);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U64(pool + WowBOffsets.HandlePoolArrayOffset, 0x20020000);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "pool array replaced during capture");
mem.OnRead = null;
mem.U64(pool + WowBOffsets.HandlePoolArrayOffset, array);
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U64(0x30000000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40002000);
};
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "local unit changed late in capture");
mem.OnRead = null;
mem.U64(0x30000000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40000000);

// A previously aligned data pointer can change after its first header read.
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40002000);
};
Require(reader.TryCaptureWorldSnapshot(out var alignedChanged) && !alignedChanged!.TryGetSelectedTarget(out _), "aligned pointer change rejects target");
mem.OnRead = null;
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40001000);
mem.U64(0x40001000, module + 0x1234);
Require(reader.DiagnoseTarget(npc).StartsWith("vtable-not-in-unit-filter"), "diagnose unsupported vtable");
mem.U64(0x40001000, module + WowBOffsets.CGUnitFnTable);
// A plausible but unaligned data address is diagnostic-only. It must not be
// accepted as a unit merely because it can be read.
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x50000004);
mem.U64(0x50000004, 0x1111);
mem.U64(0x5000000C, 0x2222);
mem.U64(0x50000014, 0x3333);
mem.U64(0x5000001C, 0x4444);
mem.U64(0x30002000 + WowBOffsets.RecordDataBlockPtrOffset, 0x500002F8);
mem.U64(0x30001000 + 0xE8, 0x60000000);
mem.U64(0x60000000, module + WowBOffsets.CGUnitFnTable);
mem.U64(0x60000008, 0x30001000);
mem.Guid(0x60000010, npc);
string unaligned = reader.DiagnoseTarget(npc);
Require(unaligned.StartsWith("data-pointer-not-8-aligned"), "diagnose unaligned data separately");
Require(unaligned.Contains("stride0x2F4Matches=1") && unaligned.Contains("+0xE8:0x60000000"), "report stride and alternate header candidates");
Require(reader.TryCaptureWorldSnapshot(out var filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned unknown header stays excluded");
// Real observation: 4-mod-8 address with CGUnit vtable, record back-pointer
// and both GUID halves. Only the read-only snapshot accepts this candidate.
mem.U64(0x50000004, module + WowBOffsets.CGUnitFnTable);
mem.U64(0x5000000C, 0x30001000);
mem.Guid(0x50000014, npc);
mem.U64(0x50000004 + WowBOffsets.UnitDataHealthOffset, 11);
mem.U64(0x50000004 + WowBOffsets.UnitDataMaxHealthOffset, 22);
Require(reader.TryCaptureWorldSnapshot(out var fourAligned) && fourAligned!.TryGetUnit(npc, out var fourUnit) &&
    fourUnit.UnitAddress == 0x50000004 && fourUnit.Location.X == 4 && fourUnit.CandidateHealth.HasValue &&
    fourUnit.CandidateHealth.Value.CurrentRaw64 == 11 && fourUnit.CandidateHealth.Value.MaximumRaw64 == 22,
    "4-byte candidate with complete header, transform and health words");
Require(!reader.TryGetUnitObject(0x30001000, out _, out _, out _), "legacy unit accessor remains strict");
mem.OnRead = (address, count) =>
{
    if (address == 0x30001000 + WowBOffsets.RecordPositionOffset && count == 20)
        mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40001000);
};
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "changed pointer rejected on uncached recheck");
mem.OnRead = null;
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x50000004);
mem.U64(0x50000004, module + 0x1234);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned unknown vtable rejected");
mem.U64(0x50000004, module + WowBOffsets.CGUnitFnTable);
mem.U64(0x5000000C, 0x30002000);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned wrong back-pointer rejected");
mem.U64(0x5000000C, 0x30001000);
mem.Guid(0x50000014, sameLow);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned wrong GUID high half rejected");
mem.Guid(0x50000014, npc);
mem.Remove(0x5000001F);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned incomplete header rejected");
mem.Guid(0x50000014, npc);
mem.Remove(0x30001000 + WowBOffsets.RecordPositionOffset + 1);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "unaligned unreadable transform rejected");
mem.F32(0x30001000 + WowBOffsets.RecordPositionOffset, 4);
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x50000005);
Require(reader.TryCaptureWorldSnapshot(out filtered) && !filtered!.TryGetUnit(npc, out _), "other alignment rejected");
mem.U64(0x30001000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40001000);
mem.U64(0x30002000 + WowBOffsets.RecordDataBlockPtrOffset, 0x40002000);
mem.Guid(module + WowBOffsets.PlayerTargetGuid, new WowBGuid(123, 456));
Require(reader.DiagnoseTarget(npc).StartsWith("target-changed-or-unreadable-before-scan"), "detect target change");
Require(reader.DiagnoseTarget(new WowBGuid(123, 456)).StartsWith("pool-record-not-found"), "diagnose absent pool record");
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
// An unreadable target GUID must not be reported as an empty target.
mem.Remove(module + WowBOffsets.PlayerTargetGuid + 8);
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "reject unreadable target GUID");
mem.Guid(module + WowBOffsets.PlayerTargetGuid, npc);
// An unreadable transform must not silently become position (0,0,0).
mem.Remove(0x30001000 + WowBOffsets.RecordPositionOffset + 1);
Require(reader.TryCaptureWorldSnapshot(out snapshot) && snapshot!.Units.Count == 2, "reject unreadable transform");
// The local record's back-reference is required.
mem.U64(0x40000000 + WowBOffsets.UnitObjectBackPtrOffset, 0);
Require(!reader.TryCaptureWorldSnapshot(out snapshot) && snapshot == null, "fail closed without local player");
Console.WriteLine("WowBReader snapshot tests passed");

void Add(uint slot, ulong rec, ulong unit, WowBGuid guid, ulong vtRva, float x, float y, float z)
{
    ulong entry = array + slot * WowBOffsets.HandlePoolElementStride;
    mem.U32(entry, (uint)WowBOffsets.HandleOccupiedBit);
    mem.U32(entry + 4, 0x12345678); // opaque marker; semantics are not assumed
    mem.U64(entry + WowBOffsets.HandlePoolRecordOffset, rec);
    mem.Guid(rec + WowBOffsets.RecordGuidLoOffset, guid);
    mem.U64(rec + WowBOffsets.RecordDataBlockPtrOffset, unit);
    mem.U64(unit + WowBOffsets.UnitObjectBackPtrOffset, rec);
    mem.Guid(unit + WowBOffsets.UnitObjectGuidLoOffset, guid);
    mem.U64(unit, module + vtRva);
    mem.U64(unit + WowBOffsets.UnitDataHealthOffset, 0x100000005UL);
    mem.U64(unit + WowBOffsets.UnitDataMaxHealthOffset, 0x20000000AUL);
    mem.U32(unit + WowBOffsets.UnitDataPowerOffset, 30);
    mem.U32(unit + WowBOffsets.UnitDataMaxPowerOffset, 30);
    mem.U32(unit + WowBOffsets.UnitDataUnitFlagsOffset, 8);
    mem.F32(rec + WowBOffsets.RecordPositionOffset, x);
    mem.F32(rec + WowBOffsets.RecordPositionOffset + 4, y);
    mem.F32(rec + WowBOffsets.RecordPositionOffset + 8, z);
    mem.F32(rec + WowBOffsets.RecordPositionOffset + 12, 0);
    mem.F32(rec + WowBOffsets.RecordPositionOffset + 16, 1);
}
static void Require(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
}

namespace Styx.Logic.Pathing
{
    public readonly struct WoWPoint
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public WoWPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
}

namespace GreenMagic
{
    public sealed class Memory
    {
        private readonly Dictionary<ulong, byte> bytes = new();
        public bool Is64BitTarget => true;
        public ulong LastStringReadAddress { get; private set; }
        public Action<ulong, int>? OnRead { get; set; }
        public bool CacheEnabled { get; private set; }
        public void EnableCache() => CacheEnabled = true;
        public void DisableCache() => CacheEnabled = false;
        public IDisposable TemporaryCacheState(bool enabledTemporarily) => new CacheScope(this, enabledTemporarily);
        private sealed class CacheScope : IDisposable
        {
            private readonly Memory memory;
            private readonly bool previous;
            public CacheScope(Memory memory, bool enabled)
            {
                this.memory = memory;
                previous = memory.CacheEnabled;
                memory.CacheEnabled = enabled;
            }
            public void Dispose() => memory.CacheEnabled = previous;
        }
        public ulong ImageBase64 => 0x100000000;
        public ulong GetAbsolute64(ulong rva) => ImageBase64 + rva;
        public void Remove(ulong address) => bytes.Remove(address);
        private void Put(ulong address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++) bytes[address + (ulong)i] = data[i];
        }
        public void U64(ulong address, ulong value) => Put(address, BitConverter.GetBytes(value));
        public void U32(ulong address, uint value) => Put(address, BitConverter.GetBytes(value));
        public void F32(ulong address, float value) => Put(address, BitConverter.GetBytes(value));
        public void Guid(ulong address, WowBGuid guid) { U64(address, guid.Low); U64(address + 8, guid.High); }
        public byte[]? ReadBytes64(ulong address, int count)
        {
            OnRead?.Invoke(address, count);
            var result = new byte[count];
            for (int i = 0; i < count; i++)
                if (!bytes.TryGetValue(address + (ulong)i, out result[i])) return null;
            return result;
        }
        public ulong ReadPointer64(ulong address) => Read64<ulong>(address);
        public T Read64<T>(ulong address)
        {
            int size = typeof(T) == typeof(byte) ? 1 : typeof(T) == typeof(ulong) ? 8 : 4;
            byte[]? data = ReadBytes64(address, size);
            if (data == null) return default!;
            if (typeof(T) == typeof(ulong)) return (T)(object)BitConverter.ToUInt64(data);
            if (typeof(T) == typeof(uint)) return (T)(object)BitConverter.ToUInt32(data);
            if (typeof(T) == typeof(int)) return (T)(object)BitConverter.ToInt32(data);
            if (typeof(T) == typeof(float)) return (T)(object)BitConverter.ToSingle(data);
            if (typeof(T) == typeof(byte)) return (T)(object)data[0];
            throw new NotSupportedException(typeof(T).FullName);
        }
        public string ReadString64(ulong address, int maxLength = 256)
        {
            LastStringReadAddress = address;
            return string.Empty;
        }
    }
}

#endif

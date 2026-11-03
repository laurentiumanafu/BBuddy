#if WOWB_READER_TESTS
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

// Discovery policy only; these assertions do not validate any client offsets.
foreach (string name in new[] { "Wow", "WowB", "Wow-decoded", "other" })
    Require(ClientAttachPolicy.IsCandidate(70707, name), "70707 accepted automatically: " + name);
Require(ClientAttachPolicy.ProcessNames().Contains("Wow-decoded"), "decoded process discovery");
Require(ClientAttachPolicy.IsCandidate(12340, "Wow") && ClientAttachPolicy.IsCandidate(70291, "WowB"), "supported builds unchanged");
Require(ClientAttachPolicy.IsCandidate(0, "Luacct") && ClientAttachPolicy.IsCandidate(70450, "WowB"), "existing unversioned and name exceptions preserved");
Require(!ClientAttachPolicy.IsCandidate(70450, "Wow") && !ClientAttachPolicy.IsCandidate(-1, "WowB"), "unsupported or unreadable build rejected");
Require(ClientAttachPolicy.DefaultBuild == 70707 && ClientAttachPolicy.IsRegisteredBuild(70707), "70707 is the current default registered build");
Require(ClientAttachPolicy.SelectionPriority(70707) > ClientAttachPolicy.SelectionPriority(70291) &&
        ClientAttachPolicy.SelectionPriority(70291) > ClientAttachPolicy.SelectionPriority(12340), "latest registered build preferred");
Require(ClientAttachPolicy.SelectionPriority(99999) < ClientAttachPolicy.SelectionPriority(12340) &&
        ClientAttachPolicy.SelectionPriority(0) < ClientAttachPolicy.SelectionPriority(12340) &&
        !ClientAttachPolicy.IsRegisteredBuild(99999), "unknown and name-only builds do not outrank supported builds");
var selectionOrder = new[] { (build: 0, pid: 2), (build: 70291, pid: 3), (build: 70707, pid: 9),
                             (build: 99999, pid: 1), (build: 70707, pid: 4), (build: 12340, pid: 5) }
    .OrderByDescending(p => ClientAttachPolicy.SelectionPriority(p.build)).ThenBy(p => p.pid).ToArray();
Require(selectionOrder.Select(p => p.pid).SequenceEqual(new[] { 4, 9, 3, 5, 1, 2 }), "process selection preference and deterministic ties");

ExperimentalOffsetsTests.Run(); // synthetic routing checks, not live validation
RotationTests.Run(); // hotbar/priority logic only, not client-state validation
CombatResearchTests.Run(); // bounded read-only trace fixtures, not live offsets
GatherSettingsTests.Run(); // persistence and default policy, not live mouse isolation
DeathCheckpointTests.Run(); // synthetic recording policy, not a live death test
DefenceHandoffTests.Run(); // target-switch policy only, not live defence
CorpseDialogTests.Run(); // image matching only; no live capture or click
CorpseSpotTests.Run(); // geometric heuristic only; no live NPC or mesh test

// Foreground classification for the opt-in gather diagnostic; no native calls.
IntPtr gameWindow = new IntPtr(42), otherWindow = new IntPtr(43);
Require(GatherBackgroundFocus.Classify(IntPtr.Zero, gameWindow, 0, 9) == GatherBackgroundFocus.State.Unavailable, "no foreground window");
Require(GatherBackgroundFocus.Classify(gameWindow, gameWindow, 0, 9) == GatherBackgroundFocus.State.Game, "game HWND foreground");
Require(GatherBackgroundFocus.Classify(otherWindow, gameWindow, 9, 9) == GatherBackgroundFocus.State.Game, "game-owned foreground window");
Require(GatherBackgroundFocus.Classify(otherWindow, gameWindow, 10, 9) == GatherBackgroundFocus.State.OtherApplication, "different process foreground");
Require(GatherBackgroundFocus.Classify(otherWindow, gameWindow, 0, 9) == GatherBackgroundFocus.State.Unavailable, "foreground process unavailable");
Require(GatherBackgroundFocus.Classify(otherWindow, gameWindow, 10, 0) == GatherBackgroundFocus.State.Unavailable, "game process unavailable");
Require(GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.OtherApplication, true), "background cursor movement automatically allowed with geometry");
Require(!GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.OtherApplication, false), "missing geometry rejected");
Require(!GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.Unavailable, true), "unavailable focus rejected");
Require(!GatherBackgroundFocus.MayRelax(GatherBackgroundFocus.State.Game, true), "foreground retains fresh cursor checks");
Require(GatherBackgroundFocus.RetryCursorInterruption(false, true), "unposted cursor interruption retries promptly");
Require(!GatherBackgroundFocus.RetryCursorInterruption(true, true) &&
        !GatherBackgroundFocus.RetryCursorInterruption(false, false), "posted clicks and other failures retain normal cast wait");

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
// Bounded table diagnostics: no process memory is read by this layout test.
byte[] dbRoot = new byte[0x220], dbStorage = new byte[0x100];
BitConverter.GetBytes(0x210000UL).CopyTo(dbRoot, 0x10);
BitConverter.GetBytes(7UL).CopyTo(dbRoot, 0x18);
BitConverter.GetBytes(0x220000UL).CopyTo(dbRoot, 0x28);
BitConverter.GetBytes(7UL).CopyTo(dbRoot, 0x30);
BitConverter.GetBytes(0x230000UL).CopyTo(dbStorage, 8);
BitConverter.GetBytes(7U).CopyTo(dbStorage, 0x38);
BitConverter.GetBytes(440U).CopyTo(dbStorage, 0x48);
BitConverter.GetBytes(0x240000UL).CopyTo(dbStorage, 0xB0);
BitConverter.GetBytes(453UL).CopyTo(dbStorage, 0xB8);
Require(WowBFactionDbSnapshotLayout.TryCreate(dbRoot, dbStorage, out var dbLayout) &&
    dbLayout.RowsLength == 3080 && dbLayout.IndexLength == 1812 && dbLayout.FieldsLength == 168, "bounded faction table regions");
BitConverter.GetBytes(8U).CopyTo(dbStorage, 0x38);
Require(WowBFactionDbSnapshotLayout.TryCreate(dbRoot, dbStorage, out var changedLayout) &&
    !dbLayout.SameRegions(changedLayout), "changed faction layout rejected");
BitConverter.GetBytes(7U).CopyTo(dbStorage, 0x38);
BitConverter.GetBytes(ulong.MaxValue).CopyTo(dbStorage, 0xB8);
Require(!WowBFactionDbSnapshotLayout.TryCreate(dbRoot, dbStorage, out _), "oversized faction index rejected");
BitConverter.GetBytes(453UL).CopyTo(dbStorage, 0xB8);
BitConverter.GetBytes(0x7FFFFFFFFFF0UL).CopyTo(dbStorage, 8);
Require(!WowBFactionDbSnapshotLayout.TryCreate(dbRoot, dbStorage, out _), "overflowing faction row address rejected");
byte[] paletteFixture = new byte[24];
BitConverter.GetBytes(0x250000UL).CopyTo(paletteFixture, 0);
BitConverter.GetBytes(8UL).CopyTo(paletteFixture, 8);
Require(WowBFactionDbSnapshotLayout.TryPalette(paletteFixture, 0, out ulong palAddr, out int palSize) && palAddr == 0x250000UL && palSize == 32, "bounded palette");
BitConverter.GetBytes(4097UL).CopyTo(paletteFixture, 8);
Require(!WowBFactionDbSnapshotLayout.TryPalette(paletteFixture, 0, out _, out _), "oversized palette rejected");
// The separate type-4 array palettes use bounded pointer vectors and byte lengths.
BitConverter.GetBytes(0x260000UL).CopyTo(dbRoot, 0x40);
BitConverter.GetBytes(7UL).CopyTo(dbRoot, 0x48);
Require(WowBFactionDbSnapshotLayout.TryArrayPalettePointers(dbRoot, out ulong ptrAddr, out int ptrLength) &&
    ptrAddr == 0x260000UL && ptrLength == 56, "bounded array palette pointers");
byte[] arrayFields = new byte[7 * 24], arrayPointers = new byte[7 * 8];
BitConverter.GetBytes(6112U).CopyTo(arrayFields, 6 * 24 + 4);
BitConverter.GetBytes(4U).CopyTo(arrayFields, 6 * 24 + 8);
BitConverter.GetBytes(8U).CopyTo(arrayFields, 6 * 24 + 20);
BitConverter.GetBytes(0x270000UL).CopyTo(arrayPointers, 6 * 8);
Require(WowBFactionDbSnapshotLayout.TryArrayPalette(arrayFields, arrayPointers, 6, out ulong arrayAddr, out int arraySize) &&
    arrayAddr == 0x270000UL && arraySize == 6112, "bounded array palette bytes");
BitConverter.GetBytes(16385U).CopyTo(arrayFields, 6 * 24 + 4);
Require(!WowBFactionDbSnapshotLayout.TryArrayPalette(arrayFields, arrayPointers, 6, out _, out _), "oversized or misaligned array palette rejected");
BitConverter.GetBytes(6112U).CopyTo(arrayFields, 6 * 24 + 4);
BitConverter.GetBytes(7U).CopyTo(arrayFields, 6 * 24 + 20);
Require(!WowBFactionDbSnapshotLayout.TryArrayPalette(arrayFields, arrayPointers, 6, out _, out _), "unknown palette element count rejected");
// Synthetic primary table: diagnostic-only model, not a live reaction assertion.
byte[] relationStorage = new byte[0x100], relationFields = new byte[7 * 24], relationRows = new byte[4 * 7];
byte[] relationIndex = new byte[4 * 4], relationScalar = new byte[8], relationEnemy = new byte[32], relationFriend = new byte[32];
BitConverter.GetBytes(7U).CopyTo(relationStorage, 0x38);
BitConverter.GetBytes(1U).CopyTo(relationStorage, 0x40);
BitConverter.GetBytes(10U).CopyTo(relationStorage, 0x44);
BitConverter.GetBytes(4U).CopyTo(relationStorage, 0x48);
BitConverter.GetBytes(4UL).CopyTo(relationStorage, 0xB8);
int[] relationModes = { 1, 5, 1, 3, 1, 4, 4 }, relationOffsets = { 0, 12, 27, 31, 35, 39, 46 };
int[] relationWidths = { 12, 15, 4, 4, 4, 7, 8 };
for (int i = 0; i < 7; i++)
{
    BitConverter.GetBytes((uint)relationModes[i]).CopyTo(relationFields, i * 24 + 8);
    BitConverter.GetBytes((uint)relationOffsets[i]).CopyTo(relationFields, i * 24 + 12);
    BitConverter.GetBytes((uint)relationWidths[i]).CopyTo(relationFields, i * 24 + 16);
}
BitConverter.GetBytes(8U).CopyTo(relationFields, 3 * 24 + 4);
BitConverter.GetBytes(32U).CopyTo(relationFields, 5 * 24 + 4);
BitConverter.GetBytes(32U).CopyTo(relationFields, 6 * 24 + 4);
BitConverter.GetBytes(8U).CopyTo(relationFields, 5 * 24 + 20);
BitConverter.GetBytes(8U).CopyTo(relationFields, 6 * 24 + 20);
BitConverter.GetBytes(2U).CopyTo(relationScalar, 0);
// player, friendly, neutral, aggressive (little-endian 7-byte packed records)
ulong[] packedFixture = {
    3UL | (72UL << 12) | (3UL << 27) | (12UL << 35),
    47UL | (2UL << 27) | (4UL << 35),
    7UL | (1024UL << 12) | (1UL << 31),
    37UL | (1UL << 12) | (8UL << 27) | (1UL << 31) | (1UL << 35)
};
for (int i = 0; i < packedFixture.Length; i++)
{
    byte[] encodedRow = BitConverter.GetBytes(packedFixture[i]);
    Array.Copy(encodedRow, 0, relationRows, i * 7, 7);
    BitConverter.GetBytes((ushort)(i + 2)).CopyTo(relationIndex, i * 4);
    BitConverter.GetBytes((ushort)i).CopyTo(relationIndex, i * 4 + 2);
}
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out int rf, out int rr, out _) && rf == 4 && rr == 4,
    "template-only friendly fixture");
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 5, 3, out rf, out rr, out _) && rf == 3 && rr == 3,
    "template-only neutral fixture");
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 6, 3, out rf, out rr, out _) && rf == 1 && rr == 1,
    "template-only aggressive fixture");
// Array precedence and zero termination: an enemy entry overrides a friendly mask,
// while an entry after zero must not be considered.
BitConverter.GetBytes((ushort)3).CopyTo(relationEnemy, 0);
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out rf, out rr, out _) && rf == 1 && rr == 4,
    "enemy array takes precedence and remains directional");
BitConverter.GetBytes((ushort)0).CopyTo(relationEnemy, 0);
BitConverter.GetBytes((ushort)3).CopyTo(relationEnemy, 4);
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out rf, out rr, out _) && rf == 4 && rr == 4,
    "array terminates at zero");
BitConverter.GetBytes((ushort)0).CopyTo(relationEnemy, 4);
byte[] rowWithBadPalette = BitConverter.GetBytes(packedFixture[1] | (1UL << 39));
Array.Copy(rowWithBadPalette, 0, relationRows, 7, 7);
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out _, out _, out _), "palette index bounds");
Array.Copy(BitConverter.GetBytes(packedFixture[1]), 0, relationRows, 7, 7);
byte[] rowWithHostileFlag = BitConverter.GetBytes(packedFixture[2] | (0x2000UL << 12));
Array.Copy(rowWithHostileFlag, 0, relationRows, 14, 7);
Require(WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 5, 3, out rf, out rr, out _) && rf == 1 && rr == 3,
    "fallback template bit is directional");
Array.Copy(BitConverter.GetBytes(packedFixture[2]), 0, relationRows, 14, 7);
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 10, 3, out _, out _, out _), "missing row unavailable");
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, null, relationFriend, 4, 3, out _, out _, out _), "missing palette unavailable");
BitConverter.GetBytes(2U).CopyTo(relationFields, 2 * 24 + 8);
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out _, out _, out _), "unknown compression unavailable");
BitConverter.GetBytes(1U).CopyTo(relationFields, 2 * 24 + 8);
BitConverter.GetBytes((ushort)0xFFFF).CopyTo(relationIndex, 4 + 2);
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out _, out _, out _), "special row unavailable");
BitConverter.GetBytes((ushort)1).CopyTo(relationIndex, 4 + 2);
BitConverter.GetBytes((ushort)2).CopyTo(relationIndex, 4);
Require(!WowBFactionTemplateDiagnostic.TryCompare(relationStorage, relationFields, relationRows, relationIndex,
    relationScalar, relationEnemy, relationFriend, 4, 3, out _, out _, out _), "duplicate table key unavailable");
// Faction-template ID is diagnostic, not a reaction or attackability result.
mem.U32(0x40001000 + WowBOffsets.UnitDataFactionTemplateOffset, 189);
Require(reader.TryReadFactionTemplateId(0x40001000, out uint factionId) && factionId == 189, "faction template ID");
mem.U32(0x40001000 + WowBOffsets.UnitDataFactionTemplateOffset, 0);
Require(reader.TryReadFactionTemplateId(0x40001000, out factionId) && factionId == 0, "readable zero faction ID");
mem.Remove(0x40001000 + WowBOffsets.UnitDataFactionTemplateOffset + 1);
Require(!reader.TryReadFactionTemplateId(0x40001000, out _), "unreadable faction ID");
Require(!reader.TryReadFactionTemplateId(0, out _) && !reader.TryReadFactionTemplateId(ulong.MaxValue, out _), "invalid faction pointers");
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
ProbeTests.Run();
CursorProbeTests.Run();
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
        public IntPtr WindowHandle { get; set; } = new IntPtr(1);
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
        public void Bytes(ulong address, byte[] value) => Put(address, value);
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

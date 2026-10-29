using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Styx.Logic.Pathing;

namespace Styx.WoWInternals
{
    /// <summary>
    /// Raw bytes at two candidate health offsets. Their width, signedness and
    /// semantics are not established. A readable zero is not a read failure or
    /// evidence that a unit is dead. No action logic should depend on these.
    /// </summary>
    public readonly struct WowBCandidateHealthWords
    {
        public ulong CurrentRaw64 { get; }
        public ulong MaximumRaw64 { get; }
        public uint CurrentLow32 => (uint)CurrentRaw64;
        public uint MaximumLow32 => (uint)MaximumRaw64;

        internal WowBCandidateHealthWords(ulong currentRaw64, ulong maximumRaw64)
        {
            CurrentRaw64 = currentRaw64;
            MaximumRaw64 = maximumRaw64;
        }
    }

    /// <summary>
    /// A read-only observation, not a legacy WoWUnit and not a durable process
    /// pointer. Addresses and positions may change immediately after capture.
    /// Hostility, health, targetability and NPC type are deliberately unknown.
    /// </summary>
    public readonly struct WowBObservedUnit
    {
        public WowBGuid Guid { get; }
        public ulong RecordAddress { get; }
        public ulong UnitAddress { get; }
        public bool IsLocalPlayer { get; }
        public bool HasPlayerVtable { get; }
        public WoWPoint Location { get; }
        public float Facing { get; }
        /// <summary>Optional raw candidates, sampled only for the local player and selected target.</summary>
        public WowBCandidateHealthWords? CandidateHealth { get; }

        internal WowBObservedUnit(WowBGuid guid, ulong recordAddress, ulong unitAddress,
            bool isLocalPlayer, bool hasPlayerVtable, WoWPoint location, float facing,
            WowBCandidateHealthWords? candidateHealth)
        {
            Guid = guid;
            RecordAddress = recordAddress;
            UnitAddress = unitAddress;
            IsLocalPlayer = isLocalPlayer;
            HasPlayerVtable = hasPlayerVtable;
            Location = location;
            Facing = facing;
            CandidateHealth = candidateHealth;
        }
    }

    /// <summary>
    /// Comparison of two non-atomic observations, not lifecycle events.
    /// A missing GUID is not proof of a despawn; matching GUIDs and addresses
    /// do not prove pointer lifetime or that the object never changed.
    /// </summary>
    public readonly struct WowBSnapshotDifference
    {
        public int NewlyObserved { get; }
        public int NoLongerObserved { get; }
        public int RecordAddressChanged { get; }
        public int UnitAddressChanged { get; }
        public bool TargetGuidChanged { get; }
        public bool LocalGuidChanged { get; }

        internal WowBSnapshotDifference(int newlyObserved, int noLongerObserved,
            int recordAddressChanged, int unitAddressChanged, bool targetGuidChanged, bool localGuidChanged)
        {
            NewlyObserved = newlyObserved;
            NoLongerObserved = noLongerObserved;
            RecordAddressChanged = recordAddressChanged;
            UnitAddressChanged = unitAddressChanged;
            TargetGuidChanged = targetGuidChanged;
            LocalGuidChanged = localGuidChanged;
        }
    }

    /// <summary>
    /// Best-effort, non-atomic read-only snapshot of structurally validated
    /// 70291 player/CGUnit records. It is not sufficient to run the bot.
    /// </summary>
    public sealed class WowBWorldSnapshot
    {
        public DateTime CapturedUtc { get; }
        public WowBGuid LocalGuid { get; }
        public WowBGuid TargetGuid { get; }
        public int PoolSlotsScanned { get; }
        public ReadOnlyCollection<WowBObservedUnit> Units { get; }

        /// <summary>
        /// Finds an observed unit by its complete 128-bit identity. This is
        /// only a lookup in the captured snapshot, not a live validity check.
        /// </summary>
        public bool TryGetUnit(WowBGuid guid, out WowBObservedUnit unit)
        {
            unit = default;
            if (guid.IsEmpty)
                return false;
            foreach (WowBObservedUnit candidate in Units)
            {
                if (candidate.Guid == guid)
                {
                    unit = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves the local player in this captured observation. This does
        /// not re-read the process or establish that the pointer is still live.
        /// </summary>
        public bool TryGetLocalPlayer(out WowBObservedUnit player)
            => TryGetUnit(LocalGuid, out player);

        /// <summary>
        /// Resolves the selected unit in this observation. False means there
        /// is no target or it was not included; it does not prove that the
        /// target does not exist or is not hostile.
        /// </summary>
        public bool TryGetSelectedTarget(out WowBObservedUnit target)
            => TryGetUnit(TargetGuid, out target);

        /// <summary>
        /// Compares observed full GUIDs and addresses with an earlier capture.
        /// Results are observational, not proof of a spawn, despawn or reuse.
        /// </summary>
        public WowBSnapshotDifference CompareTo(WowBWorldSnapshot previous)
        {
            if (previous == null)
                throw new ArgumentNullException(nameof(previous));

            var oldUnits = new Dictionary<WowBGuid, WowBObservedUnit>();
            foreach (WowBObservedUnit unit in previous.Units)
                oldUnits[unit.Guid] = unit;

            int added = 0, recordChanged = 0, unitChanged = 0;
            foreach (WowBObservedUnit unit in Units)
            {
                if (!oldUnits.TryGetValue(unit.Guid, out WowBObservedUnit oldUnit))
                {
                    added++;
                    continue;
                }
                if (oldUnit.RecordAddress != unit.RecordAddress)
                    recordChanged++;
                if (oldUnit.UnitAddress != unit.UnitAddress)
                    unitChanged++;
                oldUnits.Remove(unit.Guid);
            }
            return new WowBSnapshotDifference(added, oldUnits.Count, recordChanged, unitChanged,
                TargetGuid != previous.TargetGuid, LocalGuid != previous.LocalGuid);
        }

        internal WowBWorldSnapshot(WowBGuid localGuid, WowBGuid targetGuid,
            int poolSlotsScanned, List<WowBObservedUnit> units)
        {
            CapturedUtc = DateTime.UtcNow;
            LocalGuid = localGuid;
            TargetGuid = targetGuid;
            PoolSlotsScanned = poolSlotsScanned;
            Units = units.AsReadOnly();
        }
    }
}

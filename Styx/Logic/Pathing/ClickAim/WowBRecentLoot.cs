using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Short-lived observations of units the Grind rotation actually engaged. This
// does not prove ownership, death, lootability or that our attack landed.
internal sealed class WowBRecentLoot
{
    private const int LifetimeMs = 120000;
    private const int Limit = 24;
    private readonly List<(WowBGuid Guid, ulong Record, ulong Unit, uint Map, int Seen)> _entries = new();

    private void Expire(int now)
    {
        _entries.RemoveAll(e => unchecked(now - e.Seen) < 0 || unchecked(now - e.Seen) >= LifetimeMs);
    }

    internal void Clear() => _entries.Clear();
    internal bool Any(int now) { Expire(now); return _entries.Count != 0; }
    internal void Forget(WowBGuid guid) => _entries.RemoveAll(e => e.Guid == guid);

    internal void Remember(WowBObservedUnit unit, uint map, int now)
    {
        if (unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable ||
            unit.RecordAddress == 0 || unit.UnitAddress == 0 || map == uint.MaxValue) return;
        Expire(now);
        Forget(unit.Guid);
        if (_entries.Count >= Limit) _entries.RemoveAt(0);
        _entries.Add((unit.Guid, unit.RecordAddress, unit.UnitAddress, map, now));
    }

    internal bool Matches(WowBObservedUnit unit, uint map, int now)
    {
        Expire(now);
        foreach (var entry in _entries)
            if (entry.Guid == unit.Guid && entry.Record == unit.RecordAddress && entry.Unit == unit.UnitAddress && entry.Map == map)
                return true;
        return false;
    }
}

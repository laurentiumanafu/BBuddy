using System;

namespace Styx.Logic.Pathing;

// Node defence is latched until combat ends, even if the fight moves away.
internal static class GatherNodeDefence
{
    internal static bool ShouldDefend(bool fighting, bool ignoreMonsters, bool nodeDefence)
        => fighting && (!ignoreMonsters || nodeDefence);

    internal static bool InRange(double dx, double dy, double dz, double range)
        => double.IsFinite(dx) && double.IsFinite(dy) && double.IsFinite(dz) &&
           double.IsFinite(range) && range >= 0 && dx * dx + dy * dy + dz * dz <= range * range;
}

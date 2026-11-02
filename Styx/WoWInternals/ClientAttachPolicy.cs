using System;

namespace Styx.WoWInternals
{
    // 70707 attaches automatically but still uses experimental, unverified offsets.
    internal static class ClientAttachPolicy
    {
        internal const int SupportedLegacyBuild = 12340;
        internal const int SupportedModernBuild = 70291;
        internal const int ExperimentalBuild = 70707;

        internal static string[] ProcessNames() => new[] { "Wow", "WoW", "Luacct", "WowB", "Wow-decoded" };

        internal static bool IsCandidate(int build, string processName)
        {
            if (build < 0) return false;
            return build == SupportedLegacyBuild || build == SupportedModernBuild || build == ExperimentalBuild || build == 0 ||
                processName.Equals("WowB", StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System;
using System.Linq;

namespace Styx.WoWInternals
{
    internal static class ClientAttachPolicy
    {
        internal const int SupportedLegacyBuild = 12340;
        internal const int SupportedModernBuild = 70291;
        // The current default remains experimental: preference is not validation.
        internal const int ExperimentalBuild = 70707;

        // Register a future build here only after its own reader/offset routing is
        // implemented. Selection automatically prefers the highest registered build;
        // never treat an arbitrary newer executable as having compatible offsets.
        private static readonly int[] RegisteredBuilds = { SupportedLegacyBuild, SupportedModernBuild, ExperimentalBuild };
        internal static int DefaultBuild => RegisteredBuilds.Max();
        internal static bool IsRegisteredBuild(int build) => Array.IndexOf(RegisteredBuilds, build) >= 0;
        internal static int SelectionPriority(int build) => IsRegisteredBuild(build) ? build : -1;

        internal static string[] ProcessNames() => new[] { "Wow", "WoW", "Luacct", "WowB", "Wow-decoded" };

        internal static bool IsCandidate(int build, string processName)
        {
            if (build < 0) return false;
            return IsRegisteredBuild(build) || build == 0 ||
                processName.Equals("WowB", StringComparison.OrdinalIgnoreCase);
        }
    }
}

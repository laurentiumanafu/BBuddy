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
        internal const int ExperimentalBuild70755 = 70755;
        internal const int ExperimentalBuild70790 = 70790;
        internal const int ExperimentalBuild70862 = 70862;
        internal static bool IsExperimentalBuild(int build) => build == ExperimentalBuild || build == ExperimentalBuild70755 || build == ExperimentalBuild70790 || build == ExperimentalBuild70862;

        // Versioned offsets are distinct; unknown builds are never assumed compatible.
        private static readonly int[] RegisteredBuilds = { SupportedLegacyBuild, SupportedModernBuild, ExperimentalBuild, ExperimentalBuild70755, ExperimentalBuild70790, ExperimentalBuild70862 };
        internal static int DefaultBuild => RegisteredBuilds.Max();
        internal static bool IsRegisteredBuild(int build) => Array.IndexOf(RegisteredBuilds, build) >= 0;
        internal static int SelectionPriority(int build) => IsRegisteredBuild(build) ? build : -1;

        internal static string[] ProcessNames() => new[] { "Wow", "WoW", "Luacct", "WowB", "Wow-decoded" };

        internal static bool IsCandidate(int build, string processName)
        {
            return IsRegisteredBuild(build);
        }
    }
}

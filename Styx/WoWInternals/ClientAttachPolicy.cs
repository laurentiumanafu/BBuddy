using System;
using System.Linq;

namespace Styx.WoWInternals
{
    // The 70707 path deliberately uses the 70291 reader without porting its offsets.
    // This is an explicit per-launch experiment, not a supported client build.
    internal static class ClientAttachPolicy
    {
        internal const int SupportedLegacyBuild = 12340;
        internal const int SupportedModernBuild = 70291;
        internal const int ExperimentalBuild = 70707;
        internal const string ExperimentalArgument = "/unsafe-wow70707";

        internal static bool ExperimentalEnabled => HasExperimentalArgument(Environment.GetCommandLineArgs());

        internal static bool HasExperimentalArgument(string[] args) =>
            args.Any(arg => string.Equals(arg, ExperimentalArgument, StringComparison.OrdinalIgnoreCase));

        internal static string[] ProcessNames(bool experimentalEnabled) => experimentalEnabled
            ? new[] { "Wow", "WoW", "Luacct", "WowB", "Wow-decoded" }
            : new[] { "Wow", "WoW", "Luacct", "WowB" };

        internal static bool IsCandidate(int build, string processName, bool experimentalEnabled)
        {
            if (build < 0) return false;
            // Do not let the pre-existing WowB name exception implicitly opt in.
            if (build == ExperimentalBuild) return experimentalEnabled;
            return build == SupportedLegacyBuild || build == SupportedModernBuild || build == 0 ||
                processName.Equals("WowB", StringComparison.OrdinalIgnoreCase);
        }
    }
}

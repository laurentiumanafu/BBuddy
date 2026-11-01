#if WOWB_READER_TESTS
using GreenMagic;

namespace Styx.Helpers
{
    public static class Logging
    {
        public static string ApplicationPath { get; set; } = ".";
        public static void Write(string message) { }
    }
}
namespace Styx.Logic.Pathing
{
    internal static class ClickAimUi
    {
        public static bool Running => false;
        public static bool Paused => false;
    }
}
namespace Styx.WoWInternals
{
    public static class ObjectManager
    {
        public static Memory? Wow { get; set; }
        public static WowBReader? WowB { get; set; }
    }
}
#endif

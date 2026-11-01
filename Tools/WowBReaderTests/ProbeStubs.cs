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
    internal static class ClickAimInput
    {
        public static bool TryCursorFraction(System.IntPtr hwnd, out double x, out double y) { x = y = 0; return false; }
        public static bool ClientSize(System.IntPtr hwnd, out int width, out int height) { width = height = 0; return false; }
        public static bool PostMouseMoveFraction(System.IntPtr hwnd, double x, double y) => false;
    }
    internal static class ClickAimAbort { public static int Generation { get; set; } }
    internal static class ClickAimUi
    {
        public static bool Running { get; set; }
        public static bool Paused { get; set; }
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

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
    internal static class ClickAimCalibration { public static string DefaultPath => System.IO.Path.Combine(Styx.Helpers.Logging.ApplicationPath, "Settings", "clickaim-cal.txt"); }
    internal static class ClickAimMath { public static double Len(double x, double y) => System.Math.Sqrt(x * x + y * y); }
    internal static class ClickAimInput
    {
        public static bool TryCursorFraction(System.IntPtr hwnd, out double x, out double y) { x = y = 0; return false; }
        public static bool ClientSize(System.IntPtr hwnd, out int width, out int height) { width = height = 0; return false; }
        public static bool PostMouseMoveFraction(System.IntPtr hwnd, double x, double y) => false;
    }
    internal static class ClickAimAbort { public static int Generation { get; set; } public static bool Requested { get; set; } public static bool Wait(int ms) => Requested; }
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

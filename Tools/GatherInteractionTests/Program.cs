#if GATHER_INTERACTION_TESTS
using System;
using System.Collections.Generic;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

internal static class Program
{
    private static readonly IntPtr Window = new IntPtr(123);
    private static readonly WowBGuid NodeGuid = new WowBGuid(111, 222);
    private static readonly GatherNodeScanner.Node Node = new(NodeGuid,
        new Bots.Gatherbuddy.GatherableNode(123, "Ore", 1), false, new WoWPoint(1, 2, 3), 2);

    private static void Require(bool condition, string label)
    { if (!condition) throw new Exception("Gather interaction fixture failed: " + label); }

    private static void Reset(bool background)
    {
        ObjectManager.Wow = new Memory { WindowHandle = Window };
        ObjectManager.WowB = new WowBReader();
        GatherBackgroundFocus.Focus = background ? GatherBackgroundFocus.State.OtherApplication : GatherBackgroundFocus.State.Game;
        GatherBackgroundFocus.X = GatherBackgroundFocus.Y = 0;
        ClickAimInput.CursorX = ClickAimInput.CursorY = 50;
        ClickAimInput.CursorReadable = true; ClickAimInput.ButtonsUp = true;
        ClickAimInput.HoverNode = false; ClickAimInput.Posts = ClickAimInput.DownCount = 0;
        ClickAimAbort.Requested = false; ClickAimAbort.WaitCount = 0; ClickAimAbort.OnWait = null;
        WowBReader.ReadCount = 0; WowBReader.OnRead = null;
        GatherNodeScanner.Available = true;
        GrindRunner.Fighting = false;
        GatherInteraction.ResetHoverHint();
    }

    private static bool Try(out bool moved)
        => GatherInteraction.TryInteract(Window, Node, _ => true, null, out _, out _, out _, out moved);

    private static void Main()
    {
        Reset(true);
        ClickAimInput.CursorReadable = false; // background must not depend on a desktop cursor read
        ClickAimAbort.OnWait = _ => ClickAimInput.CursorX += 50;
        Require(Try(out bool moved) && !moved && ClickAimInput.DownCount == 1, "background movement allowed and node clicked");

        Reset(false);
        ClickAimAbort.OnWait = n => { if (n == 1) ClickAimInput.CursorX += 40; };
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "foreground motion interrupts before button-down");
        ClickAimAbort.OnWait = null;
        Require(Try(out moved) && !moved && ClickAimInput.DownCount == 1, "fresh foreground search succeeds after movement");

        Reset(false);
        ClickAimAbort.OnWait = n => { if (n == 3) ClickAimInput.CursorX += 40; };
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "movement during final confirmation prevents click");

        Reset(false); ClickAimInput.ButtonsUp = false;
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "foreground held button retries without a click");
        ClickAimInput.ButtonsUp = true;
        Require(Try(out moved) && ClickAimInput.DownCount == 1, "fresh attempt after button release");

        Reset(true);
        ClickAimAbort.OnWait = n => { if (n == 1) GatherBackgroundFocus.Focus = GatherBackgroundFocus.State.Game; };
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "focus transition requires fresh attempt");

        Reset(true); GatherBackgroundFocus.Focus = GatherBackgroundFocus.State.Unavailable;
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "unknown focus retries without a click");

        Reset(true);
        ClickAimAbort.OnWait = n => { if (n == 1) GatherBackgroundFocus.X = 10; };
        Require(!Try(out moved) && !moved && ClickAimInput.DownCount == 0, "changed game geometry prevents click");

        Reset(false);
        WowBReader.OnRead = n => n == 3 ? new WowBGuid(7, 8) : default;
        Require(!Try(out moved) && moved && ClickAimInput.DownCount == 0, "changed final foreground hover retries instead of clicking");

        Reset(false);
        ClickAimAbort.OnWait = n => { if (n == 2) ClickAimAbort.Requested = true; };
        Require(!Try(out moved) && ClickAimInput.DownCount == 0, "STOP prevents click");

        Reset(true); GrindRunner.Fighting = true;
        Require(!Try(out moved) && ClickAimInput.DownCount == 0, "combat prevents click");

        Reset(true); GatherNodeScanner.Available = false;
        Require(!Try(out moved) && ClickAimInput.DownCount == 0, "missing fresh node prevents click");

        Reset(true);
        Require(!GatherInteraction.TryInteract(Window, Node, _ => false, null, out _, out _, out _, out _) &&
                ClickAimInput.DownCount == 0, "ineligible fresh node prevents click");
        Reset(true);
        Require(!GatherInteraction.TryInteract(Window, Node, _ => true, () => false, out _, out _, out _, out _) &&
                ClickAimInput.DownCount == 0, "extra Grind safety veto prevents click");
        Console.WriteLine("Synthetic GatherInteraction foreground/background fixtures passed (no live game or Windows input test).");
    }
}

namespace GreenMagic
{
    public sealed class Memory
    {
        public IntPtr WindowHandle { get; set; }
        public bool Is64BitTarget => true;
        public IDisposable TemporaryCacheState(bool cached) => new Disposable();
        private sealed class Disposable : IDisposable { public void Dispose() { } }
    }
}
namespace Styx.Helpers { internal static class Logging { public static void Write(string text) { } } }
namespace Styx.Offsets { }
namespace Styx.WoWInternals
{
    public readonly struct WowBGuid : IEquatable<WowBGuid>
    {
        public readonly ulong Low, High;
        public WowBGuid(ulong low, ulong high) { Low = low; High = high; }
        public bool IsEmpty => Low == 0 && High == 0;
        public bool Equals(WowBGuid other) => Low == other.Low && High == other.High;
        public override bool Equals(object? obj) => obj is WowBGuid other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Low, High);
        public static bool operator ==(WowBGuid a, WowBGuid b) => a.Equals(b);
        public static bool operator !=(WowBGuid a, WowBGuid b) => !a.Equals(b);
    }
    public static class ObjectManager { public static Memory? Wow; public static WowBReader? WowB; }
    public sealed class WowBReader
    {
        public static int ReadCount;
        public static Func<int, WowBGuid>? OnRead;
        public bool IsExperimental70707 => true;
        public int ExperimentalBuildNumber => 70707;
        public ulong MouseoverGuidCandidateRva => 1;
        public bool TryReadMouseoverGuidCandidate(out WowBGuid guid)
        {
            ReadCount++;
            WowBGuid requested = OnRead?.Invoke(ReadCount) ?? default;
            guid = !requested.IsEmpty ? requested : ClickAimInput.HoverNode ? new WowBGuid(111, 222) : default;
            return true;
        }
    }
}
namespace Bots.Gatherbuddy
{
    public sealed class GatherableNode
    {
        public GatherableNode(uint id, string name, uint skill) { }
    }
}
namespace Styx.Logic.Pathing
{
    public readonly struct WoWPoint
    {
        public readonly double X, Y, Z;
        public WoWPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
    }
    internal static class GatherNodeScanner
    {
        internal readonly struct Node
        {
            public readonly WowBGuid Guid;
            public readonly bool IsHerb;
            public readonly WoWPoint Position;
            public readonly double Distance;
            public Node(WowBGuid guid, Bots.Gatherbuddy.GatherableNode kind, bool herb, WoWPoint position, double distance)
            { Guid = guid; IsHerb = herb; Position = position; Distance = distance; }
        }
        public static bool Available = true;
        public static object? Classify(uint entry, out bool herb) { herb = false; return null; }
        public static bool TryCapture(double radius, out List<Node> nodes)
        { nodes = Available ? new List<Node> { new(new WowBGuid(111, 222), new Bots.Gatherbuddy.GatherableNode(123, "Ore", 1), false, new WoWPoint(1, 2, 3), 2) } : new List<Node>(); return Available; }
    }
    internal static class GatherBackgroundFocus
    {
        internal enum State { OtherApplication, Unavailable, Game }
        public static State Focus;
        public static int X, Y;
        internal static State Current(IntPtr hwnd) => Focus;
        internal static bool MayRelax(State state, bool geometryAvailable) => state == State.OtherApplication && geometryAvailable;
        internal static bool TryClientOrigin(IntPtr hwnd, out int x, out int y) { x = X; y = Y; return true; }
    }
    internal static class ClickAimInput
    {
        public static int CursorX, CursorY, Posts, DownCount;
        public static bool CursorReadable, ButtonsUp, HoverNode;
        public static bool TryCursorScreen(out int x, out int y) { x = CursorX; y = CursorY; return CursorReadable; }
        public static bool MouseButtonsUp() => ButtonsUp;
        public static bool ClientSize(IntPtr hwnd, out int width, out int height) { width = 1000; height = 800; return true; }
        public static bool PostMouseMoveFraction(IntPtr hwnd, double x, double y) { Posts++; HoverNode = x != 0.05 || y != 0.05; return true; }
        public static bool ConfirmedRightClick(IntPtr hwnd, int x, int y, int w, int h, Func<bool> confirm, out string reason)
        { reason = "synthetic"; if (ClickAimAbort.Requested || !confirm() || ClickAimAbort.Requested) return false; DownCount++; return true; }
    }
    internal static class ClickAimAbort
    {
        public static bool Requested;
        public static int WaitCount;
        public static Action<int>? OnWait;
        public static bool Wait(int ms) { OnWait?.Invoke(++WaitCount); return Requested; }
    }
    internal static class GrindRunner { public static bool Fighting; public static bool TryReadGatherCombat(out bool fighting) { fighting = Fighting; return true; } }
    internal static class ClickAimPositions
    {
        public static bool TryReadCamera(out double x, out double y, out double z) { x = y = z = 0; return false; }
        public static bool TryRead(out double x, out double y) { x = y = 0; return false; }
        public static double Z() => 0;
    }
    internal static class ClickAimMath
    {
        public static bool ProjectToScreen(double a, double b, double c, double d, double e, double f, double g, double h,
            double i, double j, double k, double l, out int x, out int y) { x = y = 0; return false; }
    }
}
#endif

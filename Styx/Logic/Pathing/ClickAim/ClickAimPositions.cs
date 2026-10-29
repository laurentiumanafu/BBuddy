using System;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Where the character is, from whichever object model is attached.
///
/// The movement port was written against the 3.3.5a path
/// (<see cref="ObjectManager.Me"/> =&gt; <c>LocalPlayer.Location</c>). On the 64-bit
/// client that object is never built: <c>ObjectManager.Update()</c> publishes a
/// <c>WowBWorldSnapshot</c> and returns, and <c>Me</c> stays null. So the mover
/// would have reported "no local player" forever on the exact client it was
/// ported for.
///
/// The 64-bit read path already has what movement needs - the player record and
/// its position - through <c>WowBReader</c>, which is what the field tool used.
/// This class hides the difference behind one call.
/// </summary>
internal static class ClickAimPositions
{
    /// <summary>
    /// A pool scan per read is not expensive, but the mover ticks at 20 Hz and
    /// several callers can ask in the same tick. 40 ms is shorter than one tick.
    /// </summary>
    private const int CacheMs = 40;

    private static readonly object Sync = new object();
    private static double _cachedX;
    private static double _cachedY;
    private static int _cachedAt = -1000000;
    private static bool _cachedOk;
    private static string _source = "none";

    /// <summary>Which reader produced the last successful position.</summary>
    public static string Source { get { return _source; } }

    public static bool TryRead(out double x, out double y)
    {
        // 3.3.5a first: when the legacy model is attached this is a plain read
        // and there is no reason to touch the WowB path at all.
        try
        {
            var me = ObjectManager.Me;
            if (me != null)
            {
                WoWPoint loc = me.Location;
                if (loc.IsValid && Math.Abs(loc.X) < 100000.0 && Math.Abs(loc.Y) < 100000.0)
                {
                    _source = "legacy";
                    x = loc.X;
                    y = loc.Y;
                    return true;
                }
            }
        }
        catch
        {
            // fall through to the 64-bit reader
        }

        int now = Environment.TickCount;
        lock (Sync)
        {
            if (_cachedOk && unchecked(now - _cachedAt) < CacheMs && !unchecked(now < _cachedAt))
            {
                x = _cachedX;
                y = _cachedY;
                return true;
            }

            double nx, ny;
            if (TryReadWowB(out nx, out ny))
            {
                _cachedX = nx;
                _cachedY = ny;
                _cachedAt = now;
                _cachedOk = true;
                _source = "wowb";
                x = nx;
                y = ny;
                return true;
            }

            _cachedOk = false;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// Reads the local player record straight from the 64-bit reader: resolve the
    /// player GUID, find its pool record, read the transform. No snapshot scan,
    /// no object list - just the three reads movement needs.
    /// </summary>
    public static bool TryReadWowB(out double x, out double y)
    {
        x = 0;
        y = 0;
        try
        {
            WowBReader? reader = ObjectManager.WowB;
            if (reader == null) return false;

            WowBReader.WowBPoolObject obj;
            if (!reader.TryGetPlayerObject(out obj) || obj.IsEmpty) return false;

            float fx, fy, fz, facing;
            if (!reader.TryGetObjectTransform(obj, out fx, out fy, out fz, out facing)) return false;
            if (!float.IsFinite(fx) || !float.IsFinite(fy)) return false;
            if (Math.Abs(fx) >= 100000f || Math.Abs(fy) >= 100000f) return false;
            if (fx == 0f && fy == 0f) return false;   // unread record, not a position

            x = fx;
            y = fy;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The character's own facing in radians (client convention: 0 = +Y,
    /// increasing clockwise), or false when it cannot be read. Used by the
    /// relative walk trigger, not by the mover - aiming is done with the camera.
    /// </summary>
    public static bool TryReadFacing(out double radians)
    {
        radians = 0.0;
        try
        {
            WowBReader? reader = ObjectManager.WowB;
            if (reader != null)
            {
                WowBReader.WowBPoolObject obj;
                if (reader.TryGetPlayerObject(out obj) && !obj.IsEmpty)
                {
                    float fx, fy, fz, facing;
                    if (reader.TryGetObjectTransform(obj, out fx, out fy, out fz, out facing) &&
                        float.IsFinite(facing))
                    {
                        radians = facing;
                        return true;
                    }
                }
            }

            var me = ObjectManager.Me;
            if (me != null)
            {
                float rot = me.Rotation;
                if (float.IsFinite(rot))
                {
                    radians = rot;
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }
        return false;
    }

    /// <summary>Z of the local player, or NaN when only X/Y are available.</summary>
    public static double Z()
    {
        try
        {
            WowBReader? reader = ObjectManager.WowB;
            if (reader != null)
            {
                WowBReader.WowBPoolObject obj;
                if (reader.TryGetPlayerObject(out obj) && !obj.IsEmpty)
                {
                    float fx, fy, fz, facing;
                    if (reader.TryGetObjectTransform(obj, out fx, out fy, out fz, out facing)) return fz;
                }
            }
            var me = ObjectManager.Me;
            if (me != null) return me.Location.Z;
        }
        catch
        {
            // ignored
        }
        return double.NaN;
    }
}

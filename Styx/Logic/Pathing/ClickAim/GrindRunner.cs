using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// The grind loop: circle a profile's hotspots and kill what you meet on the way.
///
/// This is the step after "walk to a point". It is built from the three things that are
/// measured to work on 70291, and nothing else:
///
///   * movement - <see cref="RouteRunner"/> on the mmaps, steered by turn keys and a
///     readable facing (256 yd and 105 yd routes, 2026-10-30, both around terrain);
///   * targeting - the client's own TAB selection, verified against
///     <c>WowBWorldSnapshot.TargetGuid</c> and refusing players by vtable;
///   * attacking - action-bar KEYS, believed only when the target's health actually
///     drops.
///
/// The last point is the whole design. A routine knows which spells a character has; this
/// does not, because no routine runs in WowB diagnostic mode and the spellbook has not
/// been read on this build. What can be known is whether a key press did damage - the
/// selected target's health is one of the few per-unit numbers the reader samples - so
/// the loop presses a key, measures, and keeps the keys that work. When none of them
/// works it says so instead of casting into the void for ten minutes.
///
/// Hostility is the client's judgement, not ours: TAB only selects things the client
/// considers enemies, which is the faction filter every grind profile carries
/// (&lt;Factions&gt;) without us being able to read a faction id on this build. The
/// profile's TargetMinLevel/TargetMaxLevel are read and logged, and are not enforced -
/// deliberately, because a wrong level filter that silently refuses everything is worse
/// than fighting one mob that was two levels too low.
/// </summary>
internal static class GrindRunner
{
    // ---- targeting ---------------------------------------------------------
    private const int TabVk = 0x09;
    private const int TabTries = 3;
    private const int TabSettleMs = 250;

    /// <summary>Look this far for something to fight before walking on.</summary>
    private const double SearchYd = 40.0;

    /// <summary>Walk this close before attacking. Melee reach plus a margin.</summary>
    private const double AttackRangeYd = 4.0;

    /// <summary>Further than this while "in range" and the target is running away.</summary>
    private const double ChaseYd = 12.0;

    // ---- attacking ---------------------------------------------------------
    /// <summary>
    /// Keys tried in order. 1-5 are the first five action-bar slots; T and F are the
    /// common attack/interact bindings. Only keys that measurably remove health are kept.
    /// </summary>
    private static readonly int[] AttackKeys = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x54, 0x46 };

    /// <summary>How long to wait for a key press to land before measuring health.</summary>
    private const int AttackSettleMs = 700;

    /// <summary>Presses with no health change before moving to the next key.</summary>
    private const int SwapKeyAfter = 3;

    /// <summary>Give up on one target after this long.</summary>
    private const int EngageTimeoutMs = 75000;

    // ---- loop --------------------------------------------------------------
    /// <summary>Stop after this many hotspots with no reachable path.</summary>
    private const int MaxDeadHotspots = 4;

    private static volatile bool _stop;
    private static bool _running;
    private static int _presses;    // action-bar presses this session, for the summary

    /// <summary>Set by the "stop" request. The loop ends after the current tick.</summary>
    public static void Stop()
    {
        _stop = true;
    }

    public static bool Running { get { return _running; } }

    /// <summary>
    /// Loads a profile's grind areas and grinds the nearest one for
    /// <paramref name="minutes"/>. Returns false if there is nothing to grind.
    /// </summary>
    public static bool Start(string profilePath, int minutes)
    {
        if (_running)
        {
            Logging.Write("[Grind] already grinding; the request was ignored");
            return false;
        }

        List<GrindArea> areas = ProfileRoute.LoadGrindAreas(profilePath);
        if (areas.Count == 0)
        {
            Logging.Write("[Grind] " + profilePath + " has no <GrindArea> hotspots. A quest "
                          + "profile is walked with profile:<path>; a grind profile has the hotspots "
                          + "inside <GrindArea>.");
            return false;
        }

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            Logging.Write("[Grind] no player position - is the character in the world?");
            return false;
        }

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < areas.Count; i++)
        {
            double cx, cy;
            areas[i].Centre(out cx, out cy);
            double d = ClickAimMath.Len(cx - px, cy - py);
            if (d < best) { best = d; nearest = i; }
        }

        GrindArea area = areas[nearest];
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0}: {1} grind area(s) in the file; using \"{2}\" - {3} hotspot(s), {4:F0} yd "
            + "from the player. Target level {5}-{6}, factions '{7}' (read, not enforced: the "
            + "client's own TAB targeting decides what is an enemy on this build).",
            System.IO.Path.GetFileName(profilePath), areas.Count, area.Name, area.Hotspots.Count,
            best, area.TargetMinLevel, area.TargetMaxLevel, area.Factions));

        _stop = false;
        _running = true;

        var thread = new Thread(() => Run(area, minutes));
        thread.IsBackground = true;
        thread.Name = "ClickAimGrind";
        thread.Start();
        return true;
    }

    private static void Run(GrindArea area, int minutes)
    {
        int started = Environment.TickCount;
        int budgetMs = minutes <= 0 ? int.MaxValue : minutes * 60000;

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[Grind] no game window"); _running = false; return; }

        var mover = new ClickAimMover();
        int index = 0;
        int dead = 0;
        int kills = 0, fights = 0, hotspots = 0;
        _presses = 0;

        try
        {
            while (!_stop && unchecked(Environment.TickCount - started) < budgetMs)
            {
                /* Fight first, walk second. A grind area is a ring of points with mobs
                 * between them, so checking before each hop is checking every few
                 * seconds - close enough to catch an add without stopping mid-stride,
                 * which the mover cannot do yet (WalkTo has no interrupt hook). */
                string combatReport;
                if (TryEngage(hwnd, out combatReport))
                {
                    fights++;
                    if (combatReport == "killed") kills++;
                    Logging.Write("[Grind] " + combatReport);
                    continue;
                }

                if (combatReport.Length > 0) Logging.Write("[Grind] " + combatReport);

                RoutePoint next = area.Hotspots[index % area.Hotspots.Count];
                index++;

                var one = new List<RoutePoint> { next };
                bool ok = RouteRunner.Run(mover, one, 1);
                if (ok)
                {
                    hotspots++;
                    dead = 0;
                }
                else
                {
                    dead++;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] could not walk to hotspot ({0:F0}, {1:F0}) - {2} in a row",
                        next.X, next.Y, dead));
                    if (dead >= MaxDeadHotspots)
                    {
                        Logging.Write("[Grind] the last " + dead + " hotspots were all unreachable. "
                                      + "Stopping: a grind loop that cannot reach its ring is a "
                                      + "walk in a circle.");
                        break;
                    }
                }

                Thread.Sleep(150);
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[Grind] stopped with an error: " + ex.Message);
        }
        finally
        {
            try { mover.MoveStop(); } catch { }
            _running = false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] done: {0} hotspot(s) reached, {1} fight(s), {2} kill(s) confirmed, "
            + "{3} action-bar key press(es), {4:F0} s", hotspots, fights, kills, _presses,
            (Environment.TickCount - started) / 1000.0));
    }

    /// <summary>
    /// Finds and fights something, or returns false with a one-line reason.
    ///
    /// Returns true when it engaged at all - the caller counts that as a fight and loops
    /// back to check again.
    /// </summary>
    private static bool TryEngage(IntPtr hwnd, out string report)
    {
        report = "";

        WowBWorldSnapshot? snap;
        if (!Capture(out snap))
        {
            report = "cannot read the world snapshot; nothing to fight";
            return false;
        }

        WowBObservedUnit player;
        if (!snap.TryGetLocalPlayer(out player)) return false;

        double px = player.Location.X, py = player.Location.Y;
        int near = CountNear(snap, px, py, SearchYd);
        if (near == 0) return false;

        /* Tab until the client has selected something. TAB is the client's own "nearest
         * enemy" - the one hostility test this build lets us use - and it refuses
         * friendlies for us. */
        WowBObservedUnit target = default(WowBObservedUnit);
        bool have = false;
        for (int i = 0; i < TabTries && !have; i++)
        {
            ClickAimInput.TapKey(hwnd, TabVk, 40);
            Thread.Sleep(TabSettleMs);
            if (!Capture(out snap)) return false;
            if (snap.TryGetSelectedTarget(out target)) have = true;
        }

        if (!have)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "{0} unit(s) within {1:F0} yd but TAB selected nothing - treating the area as "
                + "empty and walking on", near, SearchYd);
            return false;
        }

        if (target.HasPlayerVtable)
        {
            report = "TAB selected a PLAYER; not attacking. Pressing ESC and walking on";
            ClickAimInput.TapKey(hwnd, 0x1B, 40);      // ESC clears the target
            return false;
        }

        double d = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        if (d > SearchYd)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "TAB selected something {0:F0} yd away - out of the search radius, walking on", d);
            return false;
        }

        report = Fight(hwnd, px, py);
        return true;
    }

    /// <summary>
    /// Closes to attack range and presses action-bar keys until the target dies, is lost,
    /// or the timeout expires. The returned string is what the caller logs.
    /// </summary>
    private static string Fight(IntPtr hwnd, double px, double py)
    {
        int started = Environment.TickCount;
        int keyIndex = 0;
        int strikes = 0;
        int presses = 0;
        int lastHealth = -1;
        var worked = new List<int>();

        WowBWorldSnapshot? snap;
        WowBObservedUnit target;
        if (!Capture(out snap) || !snap.TryGetSelectedTarget(out target))
            return "engaged, then lost the target immediately";

        lastHealth = Health(target);

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] engaged at {0:F1} yd (health {1}); pressing the action bar",
            ClickAimMath.Len(target.Location.X - px, target.Location.Y - py),
            lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unknown"));

        while (!_stop && unchecked(Environment.TickCount - started) < EngageTimeoutMs)
        {
            if (!Capture(out snap)) break;
            if (!snap.TryGetSelectedTarget(out target))
                return "target gone (dead, despawned or out of range) after "
                       + ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture) + " s";

            if (target.HasPlayerVtable)
                return "the target turned out to be a player - disengaged";

            int health = Health(target);
            if (health >= 0 && lastHealth > 0 && health == 0)
                return "killed";

            if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) break;
            double px2 = me.Location.X, py2 = me.Location.Y;
            double d = ClickAimMath.Len(target.Location.X - px2, target.Location.Y - py2);

            if (d > ChaseYd)
                return string.Format(CultureInfo.InvariantCulture,
                    "target is {0:F0} yd away and moving; giving up on it", d);

            if (d > AttackRangeYd)
            {
                /* Approach with the same closed loop that walks routes: face the target,
                 * hold forward. Steer is given the TARGET's position, so "walk toward the
                 * thing I am fighting" is literally the existing mover. */
                double rem;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out rem);
                Thread.Sleep(90);
                continue;
            }

            // In range: stop moving, face it, hit it.
            double remaining;
            ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, false, out remaining);

            int vk = AttackKeys[keyIndex];
            ClickAimInput.TapKey(hwnd, vk, 40);
            presses++;
            _presses++;
            Thread.Sleep(AttackSettleMs);

            if (!Capture(out snap) || !snap.TryGetSelectedTarget(out target))
                return "target gone while attacking after "
                       + ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture) + " s";

            health = Health(target);
            bool damaged = health >= 0 && lastHealth >= 0 && health < lastHealth;

            if (damaged)
            {
                if (worked.IndexOf(vk) < 0)
                {
                    worked.Add(vk);
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] key {0} does damage: health {1} -> {2}. Remembering it",
                        KeyName(vk), lastHealth, health));
                }
                lastHealth = health;
                strikes = 0;
                continue;
            }

            strikes++;
            if (strikes >= SwapKeyAfter)
            {
                strikes = 0;
                keyIndex++;
                if (keyIndex >= AttackKeys.Length)
                {
                    /* Every key has been pressed and none of them removed health. Say it
                     * once and stop, rather than pressing them all again for a minute. */
                    ClickAimKeySteer.Release(hwnd);
                    return worked.Count == 0
                        ? "no key on the action bar removed health (health read "
                          + (health >= 0 ? health.ToString(CultureInfo.InvariantCulture) : "unavailable")
                          + "). The keyboard can walk on this client but it cannot attack - combat "
                          + "needs the routine, or a working Lua call"
                        : "target survived " + presses + " presses of the keys that do damage - "
                          + "it may be out of melee, immune, or need a different ability";
                }
                Logging.Write("[Grind] key " + KeyName(AttackKeys[keyIndex]) + " next ("
                              + strikes + " presses of the previous one changed nothing)");
            }
        }

        ClickAimKeySteer.Release(hwnd);
        return "gave up on the target after " + presses + " presses";
    }

    /// <summary>The selected target's health, or -1 when the reader has no candidate.</summary>
    private static int Health(WowBObservedUnit unit)
    {
        if (!unit.CandidateHealth.HasValue) return -1;
        uint current = unit.CandidateHealth.Value.CurrentLow32;
        return current > int.MaxValue ? -1 : (int)current;
    }

    private static int CountNear(WowBWorldSnapshot snap, double px, double py, double yards)
    {
        int count = 0;
        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit unit = snap.Units[i];
            if (unit.IsLocalPlayer) continue;
            if (ClickAimMath.Len(unit.Location.X - px, unit.Location.Y - py) <= yards) count++;
        }
        return count;
    }

    private static bool Capture(out WowBWorldSnapshot? snapshot)
    {
        snapshot = null;
        WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return false;
        try
        {
            return reader.TryCaptureWorldSnapshot(out snapshot) && snapshot != null;
        }
        catch
        {
            return false;
        }
    }

    private static string KeyName(int vk)
    {
        return "'" + (char)vk + "'";
    }

    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }
}

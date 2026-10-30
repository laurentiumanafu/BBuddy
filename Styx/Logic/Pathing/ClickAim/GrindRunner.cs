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

    /// <summary>How often to re-read the player's level and consider another grind area.</summary>
    private const int LevelCheckMs = 120000;

    /// <summary>
    /// Recover below this much health before pulling the next mob. Singular heals at 60%
    /// (<c>Spell.Heal("Healing Wave", ret => Me.HealthPercent &lt; 60)</c>); the number is
    /// theirs.
    /// </summary>
    private const double RecoverBelowPercent = 60.0;

    /// <summary>Do not try to recover more often than this: it costs two Lua calls a tick.</summary>
    private const int RecoverRetryMs = 5000;

    private static volatile bool _stop;
    private static bool _running;
    private static int _presses;    // action-bar presses this session, for the summary
    private static int _casts;      // Lua UseAction() calls this session, for the summary
    private static GameLua? _lua;   // the client's own Lua, when the bridge came up
    private static bool _barLogged;
    private static List<GameLua.ActionSlot>? _bar;   // the bar as last read, reused for recovery
    private static int _lastRecoverMs;

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

        List<ProfileGrindArea> areas = ProfileRoute.LoadGrindAreas(profilePath);
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

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0}: {1} grind area(s) in the file, levels {2}. Picking one needs the player's "
            + "level, so the choice is made on the grind thread once the client answers.",
            System.IO.Path.GetFileName(profilePath), areas.Count, LevelSpan(areas)));

        _stop = false;
        _running = true;

        var thread = new Thread(() => Run(areas, minutes));
        thread.IsBackground = true;
        thread.Name = "ClickAimGrind";
        thread.Start();
        return true;
    }

    private static void Run(List<ProfileGrindArea> areas, int minutes)
    {
        int started = Environment.TickCount;
        int budgetMs = minutes <= 0 ? int.MaxValue : minutes * 60000;

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[Grind] no game window"); _running = false; return; }

        var mover = new ClickAimMover();
        int index = 0;
        int dead = 0;
        int kills = 0, fights = 0, hotspots = 0;
        int switches = 0;
        int lastLevelCheck = Environment.TickCount;
        int level = 0;
        _presses = 0;
        _casts = 0;
        _barLogged = false;
        _bar = null;
        _lastRecoverMs = 0;

        /* Combat runs on the client's own Lua when it can be reached at all: the action bar
         * ignores key messages on this build, so UseAction(slot) is the only way to make a
         * spell happen that does not depend on guessing a key mapping. Failure here is not
         * fatal - the keyboard path is still there and still honest about doing nothing. */
        try
        {
            int serveSeconds = minutes <= 0 ? 7200 : minutes * 60 + 300;
            string report;
            _lua = GameLua.TryOpen(serveSeconds, out report);
            if (_lua == null)
                Logging.Write("[Grind] Lua: " + report + " - fighting with the keyboard only, which "
                              + "has not been shown to attack on this build");
            else
            {
                Logging.Write("[Grind] Lua: " + report);
                foreach (string line in _lua.Probe()) Logging.Write("[Grind]   " + line);
            }
        }
        catch (Exception ex)
        {
            _lua = null;
            Logging.Write("[Grind] Lua setup threw: " + ex.Message + " - keyboard only");
        }

        /* The profile's own level bands decide which area to grind, not proximity alone: a
         * 19-SubProfile levelling file is a ladder, and grinding the step below your level is
         * exactly the "wandering on non-viable mobs" this is supposed to avoid. */
        level = PlayerLevel(_lua);
        int chosen = SelectArea(areas, level, out string choice);
        Logging.Write("[Grind] " + choice);
        ProfileGrindArea area = areas[chosen];

        try
        {
            while (!_stop && unchecked(Environment.TickCount - started) < budgetMs)
            {
                /* Every LevelCheckMs, ask the level again: a grind profile is a ladder, and the
                 * rung below the one you are on stops being worth fighting. One Lua call, and
                 * it is the only way this loop can ever climb. Timed rather than counted,
                 * because a loop iteration is a whole hotspot leg, not a tick. */
                if (unchecked(Environment.TickCount - lastLevelCheck) >= LevelCheckMs)
                {
                    lastLevelCheck = Environment.TickCount;
                    int now = PlayerLevel(_lua);
                    if (now > 0 && now != level)
                    {
                        string climbed;
                        int nextArea = SelectArea(areas, now, out climbed);
                        if (nextArea != chosen)
                        {
                            switches++;
                            Logging.Write("[Grind] level " + level + " -> " + now + ". " + climbed);
                            chosen = nextArea;
                            area = areas[chosen];
                            index = 0;
                            dead = 0;
                        }
                        level = now;
                    }
                }

                /* Recover first. A grind loop that never heals dies on the third mob, and
                 * this is Singular's Heal behaviour reduced to what a bar without spell
                 * names can express: below 60% health, fire one ability the client calls
                 * usable and does NOT range-check - a self-buff or a self-heal, since an
                 * attack that needed a target would be range-checked. */
                string recovery;
                if (TryRecover(_lua, out recovery))
                {
                    Logging.Write("[Grind] " + recovery);
                    continue;
                }

                /* Fight first, walk second. A grind area is a ring of points with mobs
                 * between them, so checking before each hop is checking every few
                 * seconds - close enough to catch an add without stopping mid-stride,
                 * which the mover cannot do yet (WalkTo has no interrupt hook). */
                string combatReport;
                if (TryEngage(hwnd, out combatReport))
                {
                    fights++;
                    /* The Lua fight reports which slots did the killing, so the word is a
                     * prefix, not the whole line. */
                    if (combatReport.StartsWith("killed", StringComparison.Ordinal)) kills++;
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
            if (_lua != null)
            {
                int calls = _lua.Calls;
                try { _lua.Dispose(); } catch { }
                _lua = null;
                Logging.Write("[Grind] Lua: " + calls + " call(s) answered, payload released");
            }
            _running = false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] done: {0} hotspot(s) reached, {1} fight(s), {2} kill(s) confirmed, "
            + "{3} action-bar key press(es), {4} Lua action(s), {5} area switch(es), {6:F0} s",
            hotspots, fights, kills, _presses, _casts, switches,
            (Environment.TickCount - started) / 1000.0));
    }

    /// <summary>
    /// One attempt to get health back before pulling, or false with an empty report.
    ///
    /// It cannot know which slot is a heal: the build exposes slot state, not a spellbook.
    /// What it can know is that an ability which does not need a target to be in range is a
    /// self-cast, and that a character below 60% health with mana left should use one before
    /// the next mob rather than after it dies. If the slot turns out to be a buff, the cost
    /// is a wasted cooldown, which is the cheapest way to be wrong here.
    /// </summary>
    private static bool TryRecover(GameLua? lua, out string report)
    {
        report = "";
        if (lua == null || !lua.Live) return false;
        if (unchecked(Environment.TickCount - _lastRecoverMs) < RecoverRetryMs) return false;

        double? percent = lua.HealthPercent();
        if (percent == null || percent.Value >= RecoverBelowPercent) return false;

        _lastRecoverMs = Environment.TickCount;
        List<GameLua.ActionSlot> bar = _bar ?? lua.ReadActionBar();
        _bar = bar;
        for (int i = 0; i < bar.Count; i++)
        {
            GameLua.ActionSlot slot = bar[i];
            if (!slot.Usable || slot.InRange.HasValue || slot.Cooldown > 0.2) continue;
            if (!lua.UseSlot(slot.Slot)) return false;
            _casts++;
            Thread.Sleep(AttackSettleMs);
            report = string.Format(CultureInfo.InvariantCulture,
                "health {0:F0}% - used slot {1} [{2}] before the next mob",
                percent.Value, slot.Slot, IconOf(bar, slot.Slot));
            return true;
        }
        report = string.Format(CultureInfo.InvariantCulture,
            "health {0:F0}% and no self-cast ability ready - pulling anyway, badly",
            percent.Value);
        return true;
    }

    /// <summary>
    /// The character's level, or 0 when nothing can say.
    ///
    /// Asked of the client's Lua when the bridge is up (<c>UnitLevel("player")</c>), because
    /// that is a real answer from the running client. There is deliberately no fallback to
    /// the level offset here: <c>WowBOffsets.LevelOffset</c> is marked V-ish, and a wrong
    /// level would move the loop to the wrong rung of a levelling profile - worse than
    /// admitting it does not know and grinding the nearest area.
    /// </summary>
    private static int PlayerLevel(GameLua? lua)
    {
        if (lua != null && lua.Live)
        {
            double? level = lua.Number("UnitLevel", "player");
            if (level.HasValue && level.Value >= 1 && level.Value <= 100) return (int)level.Value;
        }
        return 0;
    }

    /// <summary>
    /// The nearest area whose TargetMinLevel/TargetMaxLevel contains the player's level.
    /// With no level, or no band that matches, it falls back to the nearest area and says so.
    /// </summary>
    private static int SelectArea(List<ProfileGrindArea> areas, int level, out string why)
    {
        double px = 0, py = 0;
        try { ClickAimPositions.TryRead(out px, out py); }
        catch { px = 0; py = 0; }     // also what the self-test runs: no client, no position

        int nearest = 0, inBand = -1;
        double nearestD = double.MaxValue, bandD = double.MaxValue;
        for (int i = 0; i < areas.Count; i++)
        {
            ProfileGrindArea candidate = areas[i];
            double cx, cy;
            candidate.Centre(out cx, out cy);
            double d = ClickAimMath.Len(cx - px, cy - py);
            if (d < nearestD) { nearestD = d; nearest = i; }
            if (level > 0 && InBand(candidate, level) && d < bandD) { bandD = d; inBand = i; }
        }

        int pick = inBand >= 0 ? inBand : nearest;
        ProfileGrindArea chosen = areas[pick];
        double distance = inBand >= 0 ? bandD : nearestD;

        string because;
        if (level <= 0)
            because = "The player level is unknown (no Lua), so this is the NEAREST area, not "
                      + "necessarily the right one.";
        else if (inBand >= 0)
            because = "Chosen because level " + level.ToString(CultureInfo.InvariantCulture)
                      + " is inside its band (" + CountInBand(areas, level).ToString(CultureInfo.InvariantCulture)
                      + " of " + areas.Count.ToString(CultureInfo.InvariantCulture) + " areas match).";
        else
            because = "No area's band contains level " + level.ToString(CultureInfo.InvariantCulture)
                      + "; using the nearest. Bands in this file: " + LevelSpan(areas) + ".";

        why = string.Format(CultureInfo.InvariantCulture,
            "using \"{0}\" - {1} hotspot(s), {2:F0} yd away, target level {3}-{4}, factions "
            + "'{5}'. {6}", chosen.Name, chosen.Hotspots.Count, distance,
            chosen.TargetMinLevel, chosen.TargetMaxLevel, chosen.Factions, because);
        return pick;
    }

    private static bool InBand(ProfileGrindArea area, int level)
    {
        if (area.TargetMinLevel <= 0 || level < area.TargetMinLevel) return false;
        return area.TargetMaxLevel <= 0 || level <= area.TargetMaxLevel;
    }

    private static int CountInBand(List<ProfileGrindArea> areas, int level)
    {
        int count = 0;
        for (int i = 0; i < areas.Count; i++) if (InBand(areas[i], level)) count++;
        return count;
    }

    /// <summary>The distinct level bands a profile wrote, e.g. "1-6, 7-12, 13-18".</summary>
    private static string LevelSpan(List<ProfileGrindArea> areas)
    {
        var parts = new List<string>();
        for (int i = 0; i < areas.Count && parts.Count < 24; i++)
        {
            ProfileGrindArea area = areas[i];
            string band = area.TargetMinLevel <= 0
                ? "unstated"
                : area.TargetMinLevel.ToString(CultureInfo.InvariantCulture) + "-"
                  + (area.TargetMaxLevel > 0
                        ? area.TargetMaxLevel.ToString(CultureInfo.InvariantCulture)
                        : "?");
            if (parts.IndexOf(band) < 0) parts.Add(band);
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
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

        /* The same selection TAB makes, asked for directly. Worth trying first even though
         * the keys are known to reach the client for movement: a Lua target costs one call
         * and cannot be eaten by window focus. */
        if (_lua != null && _lua.Live && _lua.TargetNearestEnemy())
        {
            Thread.Sleep(TabSettleMs);
            if (Capture(out snap) && snap != null && snap.TryGetSelectedTarget(out target)) have = true;
        }

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

        GameLua? lua = _lua;
        report = lua != null && lua.Live ? FightWithActions(hwnd, lua, px, py) : Fight(hwnd, px, py);
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

    /// <summary>
    /// The same fight as <see cref="Fight"/>, but the abilities come from the client's
    /// action bar instead of from a list of guessed keys.
    ///
    /// The bar is read once per session and logged, because it is the first honest answer to
    /// "what skills does this character have" that this bot has ever been able to get: slot,
    /// icon, whether the client thinks it is usable, whether the target is in range, and how
    /// long its cooldown has left. A slot is fired only when the client itself says all three
    /// of those are good - so mana, reagents, range and cooldown are the client's judgement,
    /// not ours. Which of them actually hurt something is still decided by watching the
    /// target's health, exactly as with the keys.
    /// </summary>
    private static string FightWithActions(IntPtr hwnd, GameLua lua, double px, double py)
    {
        int started = Environment.TickCount;
        int casts = 0;
        int lastHealth = -1;
        int idleRounds = 0;
        bool buffed = false;
        var worked = new Dictionary<int, string>();
        List<GameLua.ActionSlot>? bar = null;
        int next = 0;

        WowBWorldSnapshot? snap;
        WowBObservedUnit target;
        if (!Capture(out snap) || snap == null || !snap.TryGetSelectedTarget(out target))
            return "engaged, then lost the target immediately";

        lastHealth = Health(target);
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] engaged at {0:F1} yd (health {1}); fighting with the action bar",
            ClickAimMath.Len(target.Location.X - px, target.Location.Y - py),
            lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unknown"));

        while (!_stop && unchecked(Environment.TickCount - started) < EngageTimeoutMs)
        {
            if (!Capture(out snap) || snap == null) break;
            if (!snap.TryGetSelectedTarget(out target))
                return "target gone (dead, despawned or out of range) after "
                       + ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture) + " s";

            if (target.HasPlayerVtable)
                return "the target turned out to be a player - disengaged";

            int health = Health(target);
            if (health >= 0 && lastHealth > 0 && health == 0)
                return "killed" + (worked.Count == 0 ? "" : " with " + Describe(worked));

            if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) break;
            double d = ClickAimMath.Len(target.Location.X - me.Location.X, target.Location.Y - me.Location.Y);

            if (d > ChaseYd)
                return string.Format(CultureInfo.InvariantCulture,
                    "target is {0:F0} yd away and moving; giving up on it", d);

            /* Never fire into a cast. Singular's Spell.WaitForCast is at the top of every one
             * of its combat behaviours for this reason: a second UseAction mid-cast interrupts
             * the first, which on a caster means never landing anything at all. */
            if (lua.IsCasting())
            {
                Thread.Sleep(150);
                continue;
            }

            /* Close only when nothing on the bar reaches from here. A Lightning Bolt answered
             * IsActionInRange at 30 yd, so a ranged character fights at 30 yd instead of
             * walking into melee range because AttackRangeYd is a melee constant. */
            if (d > AttackRangeYd && (bar == null || !lua.AnySlotInRange(bar)))
            {
                double rem;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out rem);
                Thread.Sleep(90);
                continue;
            }

            double remaining;
            ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, false, out remaining);

            /* Read the bar on the first in-range tick: IsUsableAction and IsActionInRange
             * are only meaningful once the target is selected and close. */
            if (bar == null || !_barLogged)
            {
                List<GameLua.ActionSlot> read = lua.ReadActionBar();
                if (read.Count == 0)
                {
                    string why = lua.Error ?? "every slot reported empty";
                    return "the action bar reads empty (" + why + ") - nothing to cast";
                }
                LogBar(read);
                bar = read;
                _barLogged = true;
            }
            if (bar == null) return "the action bar could not be read";

            // Refresh the state of the slots that could be next; the bar itself rarely changes.
            int slot = -1;
            for (int i = 0; i < bar.Count; i++)
            {
                int at = (next + i) % bar.Count;
                GameLua.ActionSlot candidate = bar[at];
                if (!candidate.Usable) continue;
                if (candidate.InRange == false) continue;
                if (candidate.Cooldown > 0.2) continue;
                slot = candidate.Slot;
                next = (at + 1) % bar.Count;
                break;
            }

            /* One self-buff before the first cast, which is Singular's PreCombatBuffs in one
             * line: a slot the client reports as usable but NOT range-limited is a buff or a
             * stance, not an attack, and the routines always put those up before pulling. */
            if (!buffed)
            {
                buffed = true;
                for (int i = 0; i < bar.Count; i++)
                {
                    if (!bar[i].Usable || bar[i].InRange.HasValue || bar[i].Cooldown > 0.2) continue;
                    lua.UseSlot(bar[i].Slot);
                    _casts++;
                    Thread.Sleep(AttackSettleMs);
                    break;
                }
            }

            if (slot < 0)
            {
                /* Everything is on cooldown or out of range. That is a legitimate state for
                 * a second or two; ten seconds of it means the bar cannot reach this mob. */
                if (++idleRounds >= 20)
                    return "no usable ability for the selected target after "
                           + ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture)
                           + " s - the bar has " + bar.Count + " slot(s): " + Describe(bar);
                Thread.Sleep(400);
                continue;
            }

            if (!lua.UseSlot(slot))
            {
                /* The call itself failed: report the client's own words once and stop, rather
                 * than firing into an error a hundred times. */
                return "UseAction(" + slot + ") failed: " + (lua.Error ?? "no reason given");
            }

            casts++;
            _casts++;
            Thread.Sleep(AttackSettleMs);

            if (!Capture(out snap) || snap == null || !snap.TryGetSelectedTarget(out target))
                return "target gone while casting after "
                       + ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture) + " s";

            health = Health(target);
            if (health >= 0 && lastHealth >= 0 && health < lastHealth)
            {
                string name = IconOf(bar, slot);
                if (!worked.ContainsKey(slot))
                {
                    worked[slot] = name;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] slot {0} [{1}] does damage: health {2} -> {3}. Remembering it",
                        slot, name, lastHealth, health));
                }
                lastHealth = health;
                idleRounds = 0;
                next = 0;      // priority order: the best ability first, every time
            }

            // Keep the cooldown picture current without re-reading icons and range.
            lua.RefreshCooldowns(bar);
        }

        ClickAimKeySteer.Release(hwnd);
        return "gave up on the target after " + casts + " action(s)"
               + (worked.Count == 0 ? " and no damage at all - the bar answered, but nothing hurt it"
                                       + (lua.Error == null ? "" : " (last error: " + lua.Error + ")")
                                    : " using " + Describe(worked));
    }

    private static void LogBar(List<GameLua.ActionSlot> bar)
    {
        Logging.Write("[Grind] action bar (" + bar.Count + " slot(s)):");
        for (int i = 0; i < bar.Count; i++)
            Logging.Write("[Grind]   " + bar[i]);
    }

    private static string IconOf(List<GameLua.ActionSlot> bar, int slot)
    {
        for (int i = 0; i < bar.Count; i++)
            if (bar[i].Slot == slot) return System.IO.Path.GetFileNameWithoutExtension(bar[i].Icon);
        return "?";
    }

    private static string Describe(Dictionary<int, string> worked)
    {
        var parts = new List<string>();
        foreach (KeyValuePair<int, string> pair in worked)
            parts.Add("slot " + pair.Key.ToString(CultureInfo.InvariantCulture) + " [" + pair.Value + "]");
        return string.Join(", ", parts.ToArray());
    }

    private static string Describe(List<GameLua.ActionSlot> bar)
    {
        var parts = new List<string>();
        for (int i = 0; i < bar.Count; i++) parts.Add(bar[i].ToString());
        return string.Join("; ", parts.ToArray());
    }

    /// <summary>
    /// Checks the parts of grinding that do not need a game: reading a levelling profile's
    /// bands, and choosing an area by level. Everything it touches is pure or file-only, so a
    /// failure here is a code problem rather than a client problem.
    ///
    /// The fixtures are the shapes real profiles use, because that is where this broke
    /// silently before: a band written as an attribute, or one level up on the SubProfile,
    /// reads as 0 and switches level selection off without saying anything.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0, total = 0;
        var fails = new System.Text.StringBuilder();
        Action<string, bool> check = (label, ok) =>
        {
            total++;
            if (ok) passed++;
            else fails.Append(" [").Append(label).Append(']');
        };

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "clickaim-grind-selftest-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            System.IO.File.WriteAllText(path,
                "<?xml version=\"1.0\"?><HBProfile>"
              + "<SubProfile><Name>Ladder 1</Name><TargetMinLevel>1</TargetMinLevel>"
              +   "<TargetMaxLevel>6</TargetMaxLevel><Factions>16 109</Factions>"
              +   "<GrindArea><Hotspot X=\"100\" Y=\"0\" Z=\"40\"/>"
              +     "<Hotspot X=\"140\" Y=\"0\" Z=\"40\"/></GrindArea></SubProfile>"
              + "<SubProfile><Name>Ladder 2</Name><TargetMinLevel>7</TargetMinLevel>"
              +   "<TargetMaxLevel>12</TargetMaxLevel>"
              +   "<GrindArea><Hotspot X=\"200\" Y=\"0\" Z=\"40\"/></GrindArea></SubProfile>"
              + "<SubProfile><Name>Attributes</Name>"
              +   "<GrindArea TargetMinLevel=\"13\" TargetMaxLevel=\"18\">"
              +     "<Hotspot X=\"300\" Y=\"0\" Z=\"40\"/></GrindArea></SubProfile>"
              + "<SubProfile><Name>Short names</Name><MinLevel>19</MinLevel><MaxLevel>24</MaxLevel>"
              +   "<GrindArea><Hotspot X=\"400\" Y=\"0\" Z=\"40\"/></GrindArea></SubProfile>"
              + "<SubProfile><Name>Band on the sub-profile</Name>"
              +   "<TargetMinLevel>25</TargetMinLevel><TargetMaxLevel>30</TargetMaxLevel>"
              +   "<GrindArea><Hotspot X=\"500\" Y=\"0\" Z=\"40\"/></GrindArea></SubProfile>"
              + "<SubProfile><Name>Empty</Name><GrindArea><TargetMinLevel>31</TargetMinLevel>"
              +   "</GrindArea></SubProfile>"
              + "</HBProfile>");

            List<ProfileGrindArea> areas = ProfileRoute.LoadGrindAreas(path);
            check("areasLoaded", areas.Count == 5);          // the sixth has no hotspot
            if (areas.Count == 5)
            {
                check("bandElements", areas[0].TargetMinLevel == 1 && areas[0].TargetMaxLevel == 6);
                check("bandAttributes", areas[2].TargetMinLevel == 13 && areas[2].TargetMaxLevel == 18);
                check("bandShortNames", areas[3].TargetMinLevel == 19 && areas[3].TargetMaxLevel == 24);
                check("bandOnSubProfile", areas[4].TargetMinLevel == 25 && areas[4].TargetMaxLevel == 30);
                check("factions", areas[0].Factions == "16 109");
                check("hotspots", areas[0].Hotspots.Count == 2 && areas[1].Hotspots.Count == 1);
                check("nameFromSubProfile", areas[1].Name == "Ladder 2");
            }

            check("inBandInside", InBand(new ProfileGrindArea { TargetMinLevel = 7, TargetMaxLevel = 12 }, 7)
                               && InBand(new ProfileGrindArea { TargetMinLevel = 7, TargetMaxLevel = 12 }, 12));
            check("inBandOutside", !InBand(new ProfileGrindArea { TargetMinLevel = 7, TargetMaxLevel = 12 }, 6)
                                && !InBand(new ProfileGrindArea { TargetMinLevel = 7, TargetMaxLevel = 12 }, 13));
            check("inBandOpenEnded", InBand(new ProfileGrindArea { TargetMinLevel = 40 }, 60));
            check("inBandUnstated", !InBand(new ProfileGrindArea(), 60));

            string span = LevelSpan(areas);
            check("levelSpan", span.Contains("1-6") && span.Contains("25-30"));

            /* The choice itself: with the level inside a band, the area picked must be one
             * whose band contains it - never merely the closest one. */
            if (areas.Count == 5)
            {
                string why;
                int pick = SelectArea(areas, 20, out why);
                check("selectByBand", pick >= 0 && pick < 5 && InBand(areas[pick], 20));
                check("selectSaysWhy", why.Contains("19-24") || why.Contains("inside its band"));

                pick = SelectArea(areas, 0, out why);
                check("selectWithoutLevel", pick >= 0 && why.Contains("unknown"));
            }
        }
        catch (Exception ex)
        {
            total++;
            fails.Append(" [selftestThrew:").Append(ex.Message).Append(']');
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }

        if (fails.Length == 0) return string.Format("Grind self-test {0}/{1} passed", passed, total);
        return string.Format("Grind self-test {0}/{1} FAILED:{2}", passed, total, fails);
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

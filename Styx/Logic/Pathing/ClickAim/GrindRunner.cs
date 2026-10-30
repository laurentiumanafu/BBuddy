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
///     <c>WowBWorldSnapshot.TargetGuid</c> and refusing players by vtable, with a
///     right-click on the nearest readable unit as the fallback;
///   * attacking - a right-click on the mob, which the client turns into "select it and
///     auto-attack", believed only when the target's health actually drops.
///
/// The last point is the whole design. A routine knows which spells a character has; this
/// does not, because no routine runs in WowB diagnostic mode. What can be known is whether
/// a click did damage - every unit's health is one 32-bit read in its data block - so the
/// loop clicks, measures, and says so when nothing lands.
///
/// NO LUA. The Lua bridge is retired (see <see cref="GameLua"/>): taking the client's Lua
/// thread over is the only way it could send a UseAction, and it crashed wowb twice on
/// 2026-10-30 - FrameScript_IsLuaThread at FrameScriptEvents.cpp:208 on the first attempt,
/// then "Lua stack is unbalanced" at Ui/GameUI.cpp:8250 and a 0xC0000028 unwind fault at
/// 15:34 within a second of Start. A bot that walks and auto-attacks is worth more than one
/// that crashes the client.
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

    /// <summary>
    /// Further than this and the target is abandoned instead of chased.
    ///
    /// It was 12 yd, and on 2026-10-30 that produced "engaged at 16.9 yd ... target is 17 yd
    /// away and moving; giving up on it" 22 times in six seconds: TAB picks the nearest
    /// enemy rather than a close one, so almost every engage started outside 12 yd and ended
    /// before a single swing. A mob this side of 20 yd is a mob to walk to.
    /// </summary>
    private const double ChaseYd = 20.0;

    // ---- attacking ---------------------------------------------------------
    /// <summary>
    /// What <c>UnitFlags</c> is NOT: a reaction bit. The 16:06 run printed the player as
    /// <c>uflags=0x808</c> - the same 0x800 that looked like "hostile" on the mobs - so 0x800
    /// cannot be hostility, and it is no longer used to rank targets. It overlaps what
    /// 3.3.5a calls the transient combat/PvP bits; hostility stays with the client's own
    /// filter (TAB) and with "something here is close enough to hit us".
    /// </summary>
    private const uint ReactionUnknownMask = 0x800;

    /// <summary>
    /// Something worth fighting this close cuts a hotspot leg short. The 16:07 run walked
    /// 25-40 s legs while mobs were hitting it, because the loop could only look between
    /// legs; 18 yd is inside a caster's 28 yd range and close enough that the mob is coming.
    /// </summary>
    private const double InterruptYd = 18.0;

    /// <summary>Stand at a hotspot this long looking for a mob before walking to the next.</summary>
    private const int HotspotDwellMs = 9000;

    /// <summary>
    /// A mob's level, from <c>data+0x1E40</c>, against mine. Above +3 is a corpse run and
    /// more than 4 below is grey (no experience, no loot worth the walk). Outside that band
    /// the mob is skipped, which is the "do not wander at non-viable mobs" rule. An
    /// unreadable level (0) is allowed through: a wrong offset must not skip every mob.
    /// </summary>
    private const int LevelAbovePlayer = 3;
    private const int LevelBelowPlayer = 4;

    /// <summary>Units this run has looked at and refused; capped so it cannot grow forever.</summary>
    private static readonly HashSet<WowBGuid> _notViable = new HashSet<WowBGuid>();

    /// <summary>How long to wait for a key press to land before measuring health.</summary>
    private const int AttackSettleMs = 700;

    /// <summary>Give up on one target after this long.</summary>
    private const int EngageTimeoutMs = 75000;

    // ---- loop --------------------------------------------------------------
    /// <summary>Stop after this many hotspots with no reachable path.</summary>
    private const int MaxDeadHotspots = 4;

    /// <summary>How often to re-read the player's level and consider another grind area.</summary>
    private const int LevelCheckMs = 120000;

    private static volatile bool _stop;
    private static bool _running;
    private static int _presses;    // action-bar presses this session, for the summary
    /* No Lua, on purpose. Taking the client's own Lua thread over - the only way to send
     * UseAction without tripping FrameScript_IsLuaThread - crashed wowb twice on 2026-10-30:
     * the assert at FrameScriptEvents.cpp:208 on the first attempt, and on the second
     * "Lua stack is unbalanced" at Ui/GameUI.cpp:8250 followed by a 0xC0000028 unwind fault
     * in ntdll, both within a second of Start. Everything this loop needs is memory it
     * already reads, or the client's own input handling. */
    private static int _clicks;     // right-clicks that selected or attacked something

    /// <summary>
    /// The target a fight gave up on, and when. Without this the loop answers "that mob is
    /// 17 yd away and moving" by selecting it again immediately: the 2026-10-30 log counted
    /// 22 fights in 6 seconds that way, 0.29 s apart, and then went nowhere because every
    /// iteration was spent re-deciding to give up on the same mob.
    /// </summary>
    private static WowBGuid _gaveUpGuid;
    private static int _gaveUpAt;

    /// <summary>Leave a target it gave up on alone for this long.</summary>
    private const int GiveUpMs = 15000;

    /// <summary>A fight that ended this fast never fired anything; pause before the next one.</summary>
    private const int GiveUpBackoffMs = 600;

    /// <summary>The camera's field of view: the mover's own default, and what its log prints.</summary>
    private const double CameraFovDeg = 90.0;

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
        _clicks = 0;
        _gaveUpAt = 0;
        _gaveUpGuid = default(WowBGuid);
        _notViable.Clear();

        /* What the client can be asked without Lua: level, health, the stored action bar
         * and what is standing around. It is printed once because it is the answer to
         * "does this build let a bot see anything at all" - and if any of it is missing the
         * log says which read failed rather than pretending. */
        ReportMemory();

        /* The profile's own level bands decide which area to grind, not proximity alone: a
         * 19-SubProfile levelling file is a ladder, and grinding the step below your level is
         * exactly the "wandering on non-viable mobs" this is supposed to avoid. */
        level = PlayerLevel();
        int chosen = SelectArea(areas, level, out string choice);
        Logging.Write("[Grind] " + choice);
        ProfileGrindArea area = areas[chosen];

        try
        {
            while (!_stop && unchecked(Environment.TickCount - started) < budgetMs)
            {
                /* Every LevelCheckMs, read the level again: a grind profile is a ladder, and the
                 * rung below the one you are on stops being worth fighting. One 32-bit read in
                 * the player's data block, and it is the only way this loop can ever climb.
                 * Timed rather than counted, because a loop iteration is a whole hotspot leg,
                 * not a tick. */
                if (unchecked(Environment.TickCount - lastLevelCheck) >= LevelCheckMs)
                {
                    lastLevelCheck = Environment.TickCount;
                    int now = PlayerLevel();
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

                /* No recovery step: healing needs a cast, a cast needs the action bar, and
                 * clicking a heal button 40 pixels wide on a 3D scene is a guess. Auto-attack
                 * plus the client's own swing is what a level-2 character does anyway. */

                /* Fight first, walk second. A grind area is a ring of points with mobs
                 * between them, so checking before each hop is checking every few
                 * seconds - close enough to catch an add without stopping mid-stride,
                 * which the mover cannot do yet (WalkTo has no interrupt hook). */
                if (PlayerDead())
                {
                    Logging.Write("[Grind] the local player reads 0 health - the character is dead or "
                                  + "being resurrected. Stopping instead of walking a corpse round the ring.");
                    break;
                }

                string combatReport;
                if (TryEngage(hwnd, out combatReport))
                {
                    fights++;
                    /* The Lua fight reports which slots did the killing, so the word is a
                     * prefix, not the whole line. */
                    if (combatReport.StartsWith("killed", StringComparison.Ordinal)) kills++;
                    Logging.Write("[Grind] " + combatReport);
                    if (combatReport.StartsWith("the character died", StringComparison.Ordinal)) break;
                    continue;
                }

                if (combatReport.Length > 0) Logging.Write("[Grind] " + combatReport);

                RoutePoint next = area.Hotspots[index % area.Hotspots.Count];
                index++;

                var one = new List<RoutePoint> { next };

                /* While walking, look for a fight every few seconds instead of only at the
                 * end of the leg. The 16:07 run walked 25-40 s at a time with mobs on it and
                 * never turned round; a leg is a whole hotspot, so "check between legs" was
                 * checking every 40 s. */
                RouteRunner.ShouldInterrupt = () => FightNeeded(hwnd, InterruptYd);
                bool ok;
                try { ok = RouteRunner.Run(mover, one, 1); }
                finally { RouteRunner.ShouldInterrupt = null; }

                if (!ok && RouteRunner.LastRunInterrupted)
                {
                    Logging.Write("[Grind] the walk was cut short on purpose: something worth fighting "
                                  + "came within " + InterruptYd.ToString("F0", CultureInfo.InvariantCulture)
                                  + " yd. Fighting it instead of walking on");
                    continue;
                }

                if (ok)
                {
                    hotspots++;
                    dead = 0;

                    /* Stand here for a moment. A hotspot is a place mobs walk past, and
                     * arriving and immediately leaving is how a grind loop spends an hour
                     * walking a ring without ever fighting. */
                    string dwell = DwellAtHotspot(hwnd, ref fights, ref kills);
                    if (dwell.Length > 0) Logging.Write("[Grind] " + dwell);
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
            + "{3} key press(es), {4} click(s) that landed, {5} area switch(es), {6:F0} s",
            hotspots, fights, kills, _presses, _clicks, switches,
            (Environment.TickCount - started) / 1000.0));
    }

    /// <summary>
    /// What the client can be asked without Lua: the player's level and health from its own
    /// memory, the stored action bar, and who is standing around. One block per run, because
    /// it is the answer to "can this build tell a bot anything", and a failed read has to
    /// look like a failed read rather than like an empty world.
    /// </summary>
    private static void ReportMemory()
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null)
        {
            Logging.Write("[Grind] memory: no WowB reader - nothing can be read");
            return;
        }

        int level = reader.ReadPlayerLevel();
        Logging.Write(level > 0
            ? "[Grind] memory: player level " + level.ToString(CultureInfo.InvariantCulture)
              + " (the level word at data+" + Styx.Offsets.WowBOffsets.UnitDataLevelOffset.ToString("X", CultureInfo.InvariantCulture) + ")"
            : "[Grind] memory: the player's level word did not read - the area will be picked by distance");

        List<Styx.WoWInternals.WowBReader.WowBActionSlot> bar = reader.ReadActionBar();
        if (bar.Count == 0)
            Logging.Write("[Grind] memory: the stored action array at 0x"
                + Styx.Offsets.WowBOffsets.StoredActionArray.ToString("X", CultureInfo.InvariantCulture)
                + " reads empty - either the character has nothing on the bar or the packing is not a plain qword");
        else
        {
            Logging.Write("[Grind] memory: " + bar.Count + " filled action slot(s):");
            for (int i = 0; i < bar.Count; i++) Logging.Write("[Grind]   " + bar[i]);
        }

        WowBWorldSnapshot? snap;
        if (Capture(out snap) && snap != null)
        {
            WowBObservedUnit me;
            if (snap.TryGetLocalPlayer(out me))
            {
                if (reader.TryGetUnitFields(me.UnitAddress, out Styx.WoWInternals.WowBReader.WowBUnitFields f))
                    Logging.Write("[Grind] memory: player " + f);
                else
                    Logging.Write("[Grind] memory: the player's unit fields did not read at 0x" + me.UnitAddress.ToString("X"));
            }
            Logging.Write("[Grind] memory: " + Math.Max(0, snap.Units.Count - 1)
                + " other unit(s) in the pool; each fight line names the one it picked");
        }
        else
            Logging.Write("[Grind] memory: the world snapshot did not validate this time");
    }

    /// <summary>
    /// The character's level, read from the unit data block (the same one the snapshot reads
    /// health from), or 0 when it cannot be read. This used to come from Lua
    /// <c>UnitLevel("player")</c>; the Lua bridge is retired.
    /// </summary>
    private static bool PlayerDead()
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return false;
        try
        {
            WowBWorldSnapshot? snap;
            if (!reader.TryCaptureWorldSnapshot(out snap) || snap == null) return false;
            WowBObservedUnit me;
            if (!snap.TryGetLocalPlayer(out me)) return false;
            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (!reader.TryGetUnitFields(me.UnitAddress, out f)) return false;
            return f.MaxHealth > 0 && f.Health == 0;
        }
        catch { return false; }
    }

    private static int PlayerLevel()
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return 0;
        try { return reader.ReadPlayerLevel(); }
        catch { return 0; }
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

        /* Three ways to make the client select something, and the order matters.
         *
         * 1. TAB. No memory and no Lua, and the client applies its own hostility filter, so
         *    a guard or a quest NPC is never picked. It is also how we find out whether the
         *    window still takes keyboard input at all.
         * 2. A right-click on the nearest unit. No hostility filter - the snapshot says
         *    where units are, not who they are - so it is tried when TAB found nothing,
         *    which is exactly the 2026-10-30 case ("11 unit(s) within 40 yd but nothing
         *    selected them"). A unit that turns out to be friendly is remembered and never
         *    clicked again, so the list of what NOT to attack is learned instead of assumed. */
        WowBObservedUnit target = default(WowBObservedUnit);
        bool have = false;

        /* Turn before asking. TAB only considers enemies in FRONT of the character and a
         * right-click can only select what is on screen, so with the mob behind us both
         * paths answer "nothing here" - which is precisely what the 16:07 run logged for
         * two and a half minutes while mobs were attacking. */
        WowBObservedUnit aim;
        double aimYd;
        if (NearestViable(snap, px, py, SearchYd, out aim, out aimYd))
        {
            ClickAimKeySteer.Face(aim.Location.X, aim.Location.Y, hwnd, 20.0, 6);
            if (!Capture(out snap) || snap == null) return false;
            if (!snap.TryGetLocalPlayer(out player)) return false;
            px = player.Location.X;
            py = player.Location.Y;
        }

        for (int i = 0; i < TabTries && !have; i++)
        {
            ClickAimInput.TapKey(hwnd, TabVk, 40);
            Thread.Sleep(TabSettleMs);
            if (!Capture(out snap)) return false;
            if (snap.TryGetSelectedTarget(out target)) have = true;
        }

        string clickNote = "";
        if (!have && snap != null && TryClickAttack(hwnd, snap, px, py, out target, out clickNote))
            have = true;

        if (!have)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "{0} unit(s) within {1:F0} yd but nothing selected them - treating the area as "
                + "empty and walking on", near, SearchYd)
                + (clickNote.Length == 0 ? "" : " (the right-click attempt: " + clickNote + ")");
            return false;
        }

        /* It is the mob the last fight gave up on: selecting it again would repeat that
         * decision every third of a second. Walk to the next hotspot instead. */
        if (Refused(target.Guid))
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "the only target in reach is the one given up on {0:F0} s ago - walking on to the next hotspot",
                unchecked(Environment.TickCount - _gaveUpAt) / 1000.0);
            return false;
        }

        if (target.HasPlayerVtable)
        {
            report = "the client selected a PLAYER; not attacking. Pressing ESC and walking on";
            ClickAimInput.TapKey(hwnd, 0x1B, 40);      // ESC clears the target
            return false;
        }

        /* TAB applies the client's own hostility filter but knows nothing about levels, so
         * the level band is enforced here too. Killing something ten levels up is a corpse
         * run, and a grey mob is a wasted walk. */
        Styx.WoWInternals.WowBReader? esp = ObjectManager.WowB;
        int myLevel = PlayerLevel();
        if (esp != null && esp.TryGetUnitFields(target.UnitAddress,
                out Styx.WoWInternals.WowBReader.WowBUnitFields tf) && !LevelViable(tf.Level, myLevel))
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "TAB selected a level-{0} unit and this character is level {1} (viable: {2}-{3}) - "
                + "not attacking; ESC and walking on",
                tf.Level, myLevel, Math.Max(1, myLevel - LevelBelowPlayer), myLevel + LevelAbovePlayer);
            _notViable.Add(target.Guid);
            if (_notViable.Count > 256) _notViable.Clear();   // a long session on a crowded map
            ClickAimInput.TapKey(hwnd, 0x1B, 40);
            return false;
        }


        double d = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        if (d > SearchYd)
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "it selected something {0:F0} yd away - out of the search radius, walking on", d);
            return false;
        }

        /* The fight is a right-click plus the client's own auto-attack, and the verdict comes
         * from the mob's health in memory. No Lua is involved at any point. */
        int fightStarted = Environment.TickCount;
        report = Fight(hwnd, px, py);

        /* A fight that ended inside a second and a half never pressed anything: it gave up
         * (or lost) immediately. Remember the target so the next iteration walks instead of
         * selecting the same mob again. */
        if (unchecked(Environment.TickCount - fightStarted) < 1500
            && report.IndexOf("killed", StringComparison.Ordinal) < 0)
        {
            _gaveUpGuid = target.Guid;
            _gaveUpAt = Environment.TickCount;
            Thread.Sleep(GiveUpBackoffMs);
        }

        return true;
    }

    /// <summary>
    /// Right-clicks the nearest viable mob, which is how a player starts a fight: the client
    /// selects it, turns into it and starts auto-attack by itself.
    ///
    /// Nothing is written to memory and no Lua is called, so this is the one engage path that
    /// cannot kill the client with an assert. False means "no clickable mob"; the reason
    /// (when there is a useful one) comes back in <paramref name="note"/>.
    /// </summary>
    private static bool TryClickAttack(IntPtr hwnd, WowBWorldSnapshot snap, double px, double py,
                                      out WowBObservedUnit target, out string note)
    {
        target = default(WowBObservedUnit);
        note = "";

        int w, h;
        if (!ClickAimInput.ClientSize(hwnd, out w, out h)) return false;

        /* Nearest viable unit: never the player, never anything with a player vtable, never
         * the mob the last fight gave up on, and - from memory, not a guess - never a corpse
         * or a unit with no level. Those two words are the only "is this a monster" test this
         * client has offered so far; the hostility filter that actually works is TAB, which
         * is why this is the fallback. */
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        var units = snap.Units;
        int myLevel = PlayerLevel();

        WowBObservedUnit best = default(WowBObservedUnit);
        double bestYd = double.MaxValue;
        bool found = false;

        WowBObservedUnit nearAny = default(WowBObservedUnit);
        double nearAnyYd = double.MaxValue;
        bool haveAny = false;

        int skipped = 0, unviable = 0, lower = 0;
        for (int i = 0; i < units.Count; i++)
        {
            WowBObservedUnit unit = units[i];
            if (unit.IsLocalPlayer) continue;
            if (unit.HasPlayerVtable) continue;
            if (Refused(unit.Guid)) continue;
            if (_notViable.Contains(unit.Guid)) continue;
            double d = ClickAimMath.Len(unit.Location.X - px, unit.Location.Y - py);
            if (d <= 0.5 || d > SearchYd) continue;

            /* What the client says about it, if it says anything: a corpse has no health and
             * the level word decides whether the fight is worth the walk. UnitFlags is NOT
             * used - see ReactionUnknownMask. */
            if (reader != null && reader.TryGetUnitFields(unit.UnitAddress,
                    out Styx.WoWInternals.WowBReader.WowBUnitFields f))
            {
                if (f.Health == 0) { skipped++; continue; }
                if (!LevelViable(f.Level, myLevel)) { unviable++; continue; }
            }
            else lower++;

            if (d < nearAnyYd) { nearAnyYd = d; nearAny = unit; haveAny = true; }

            if (d < bestYd)
            {
                bestYd = d;
                best = unit;
                found = true;
            }
        }

        if (!found && haveAny)
        {
            best = nearAny;
            bestYd = nearAnyYd;
            found = true;
        }

        if (!found)
        {
            note = string.Format(CultureInfo.InvariantCulture,
                "nothing clickable within {0:F0} yd of a level-{1} character: {2} corpse(s), "
                + "{3} outside the {4}-{5} band, {6} with unreadable fields"
                + ", and the rest is the target it just gave up on",
                SearchYd, myLevel, skipped, unviable,
                Math.Max(1, myLevel - LevelBelowPlayer), myLevel + LevelAbovePlayer, lower);
            return false;
        }

        /* Face it first: a right-click can only select what is on screen, and "the nearest
         * mob is behind the camera or off the screen" was the answer on every single engage
         * of the 16:07 run. Turn, then project; if the first projection still fails, turn
         * again and retry instead of giving up with a mob in range. */
        int dx, dy;
        if (!TryProject(hwnd, px, py, best.Location.X, best.Location.Y, best.Location.Z, out dx, out dy))
        {
            ClickAimKeySteer.Face(best.Location.X, best.Location.Y, hwnd, 15.0, 8);
            if (!TryProject(hwnd, px, py, best.Location.X, best.Location.Y, best.Location.Z, out dx, out dy))
            {
                note = string.Format(CultureInfo.InvariantCulture,
                    "the nearest mob ({0:F1} yd) would not come on screen even after turning to it",
                    bestYd);
                return false;
            }
        }

        ClickAimInput.Click(hwnd, dx, dy);       // right-click: select + attack
        _clicks++;
        Thread.Sleep(TabSettleMs);

        WowBWorldSnapshot? after;
        if (!Capture(out after) || after == null) return false;
        if (!after.TryGetSelectedTarget(out target))
        {
            note = string.Format(CultureInfo.InvariantCulture,
                "a right-click at ({0}, {1}) on the mob {2:F1} yd away selected nothing - the client "
                + "may have the mob under something else, or the click did not land", dx, dy, bestYd);
            return false;
        }

        if (target.Guid != best.Guid)
            return true;      // it selected SOMETHING; whether it is the one we aimed at is the client's call

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] right-clicked the mob {0:F1} yd away - the client selected it and starts the attack itself",
            bestYd));
        return true;
    }

    /// <summary>
    /// Whether a mob of <paramref name="mobLevel"/> is worth attacking at
    /// <paramref name="myLevel"/>. A level the client did not report (0) is allowed: the
    /// level word is read from <c>data+0x1E40</c>, which the 2026-10-30 dump confirmed for a
    /// level-2 player and level-3/4 mobs, but a mob whose fields do not read must not be
    /// silently invisible.
    /// </summary>
    private static bool LevelViable(uint mobLevel, int myLevel)
    {
        if (mobLevel == 0 || myLevel <= 0) return true;
        if ((int)mobLevel > myLevel + LevelAbovePlayer) return false;
        return (int)mobLevel >= myLevel - LevelBelowPlayer;
    }

    /// <summary>
    /// Whether the walk should stop here. This runs on the route thread between polyline
    /// points, so it costs one snapshot: no clicks, no keys, no decisions beyond "is there
    /// something alive and level-appropriate this close".
    /// </summary>
    private static bool FightNeeded(IntPtr hwnd, double radius)
    {
        if (_stop) return false;

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;
        if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) return false;

        WowBObservedUnit unit;
        double d;
        if (!NearestViable(snap, me.Location.X, me.Location.Y, radius, out unit, out d)) return false;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0} is {1:F1} yd away - stopping the walk to deal with it", unit.Guid, d));
        return true;
    }

    /// <summary>
    /// Stands at a hotspot for <see cref="HotspotDwellMs"/>, fighting anything that turns
    /// up, then says what happened. Returns "" when nothing did.
    /// </summary>
    private static string DwellAtHotspot(IntPtr hwnd, ref int fights, ref int kills)
    {
        int dwellStart = Environment.TickCount;
        int fought = 0;
        int killed = 0;

        while (!_stop && unchecked(Environment.TickCount - dwellStart) < HotspotDwellMs)
        {
            string report;
            if (TryEngage(hwnd, out report))
            {
                fights++;
                fought++;
                if (report.StartsWith("killed", StringComparison.Ordinal)) { kills++; killed++; }
                Logging.Write("[Grind] " + report);
                continue;
            }
            if (report.Length > 0) Logging.Write("[Grind] " + report);
            Thread.Sleep(400);
        }

        if (fought == 0) return "";
        return string.Format(CultureInfo.InvariantCulture,
            "stood at the hotspot and fought {0} time(s), {1} of them ending as kills", fought, killed);
    }

    /// <summary>
    /// The nearest unit worth walking into a fight with, or false when there is none.
    ///
    /// Used for two things: turning the character toward the nearest plausible mob before
    /// TAB (which only looks in front), and deciding whether a hotspot leg should be cut
    /// short. It deliberately does not judge hostility - the flags cannot (see
    /// <see cref="ReactionUnknownMask"/>) - so it only applies the three things the client
    /// is certain about: not the player, not a corpse, inside the level band.
    /// </summary>
    private static bool NearestViable(WowBWorldSnapshot snap, double px, double py, double radius,
                                      out WowBObservedUnit unit, out double yards)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        int myLevel = PlayerLevel();

        unit = default(WowBObservedUnit);
        yards = double.MaxValue;
        bool found = false;

        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit candidate = snap.Units[i];
            if (candidate.IsLocalPlayer) continue;
            if (candidate.HasPlayerVtable) continue;
            if (Refused(candidate.Guid)) continue;
            if (_notViable.Contains(candidate.Guid)) continue;

            double d = ClickAimMath.Len(candidate.Location.X - px, candidate.Location.Y - py);
            if (d <= 0.5 || d > radius || d >= yards) continue;

            if (reader != null && reader.TryGetUnitFields(candidate.UnitAddress,
                    out Styx.WoWInternals.WowBReader.WowBUnitFields f))
            {
                if (f.Health == 0) continue;
                if (!LevelViable(f.Level, myLevel)) continue;
            }

            unit = candidate;
            yards = d;
            found = true;
        }
        return found;
    }

    /// <summary>True while the target a fight gave up on is still off limits.</summary>
    private static bool Refused(WowBGuid guid)
    {
        if (guid.IsEmpty || _gaveUpAt == 0) return false;
        if (guid != _gaveUpGuid) return false;
        return unchecked(Environment.TickCount - _gaveUpAt) < GiveUpMs;
    }

    /// <summary>
    /// Where a world point sits on the client's screen, in pixels from the middle. The same
    /// projection the mover aims its walk clicks with: the camera's position is readable, it
    /// looks at the player, and geometry needs no calibration.
    /// </summary>
    private static bool TryProject(IntPtr hwnd, double px, double py,
                                   double tx, double ty, double tz, out int dx, out int dy)
    {
        dx = 0;
        dy = 0;

        double camX, camY, camZ;
        if (!ClickAimPositions.TryReadCamera(out camX, out camY, out camZ)) return false;

        double pz = ClickAimPositions.Z();
        if (double.IsNaN(pz)) pz = camZ;

        int w, h;
        if (!ClickAimInput.ClientSize(hwnd, out w, out h)) return false;

        return ClickAimMath.ProjectToScreen(camX, camY, camZ, px, py, pz,
                                            tx, ty, tz, w / 2.0, h / 2.0, CameraFovDeg,
                                            out dx, out dy);
    }

    /// <summary>Right-clicks one unit where it stands: select and attack, done by the client.</summary>
    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py)
    {
        int dx, dy;
        if (!TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z,
                        out dx, out dy))
            return false;
        ClickAimInput.Click(hwnd, dx, dy);
        return true;
    }

    /// <summary>
    /// One fight: Singular's lowbie druid rotation, driven by the action bar the client
    /// keeps in memory and verified against the mob's health and my own mana.
    ///
    /// The order is Singular's (<c>ClassSpecific/Druid/Lowbie.cs</c>), with the spells this
    /// character does not have yet left out because they are not on the bar:
    ///
    ///   1. if health is at or below 60% and a heal is on the bar - heal self (F1, then the
    ///      heal's slot key), which is Singular's <c>Spell.BuffSelf("Rejuvenation", ...)</c>;
    ///   2. auto-attack: a right-click on the mob, which is Singular's
    ///      <c>CreateAutoAttack(true)</c> plus the target selection it assumes;
    ///   3. Wrath, from range, on the global cooldown timer.
    ///
    /// Every step is measured: a heal has to raise my health, a cast has to cost mana or
    /// take health off the mob, and if two casts in a row do neither the rotation says the
    /// key did not cast and falls back to melee. Nothing here is reported as working
    /// because it was sent.
    /// </summary>
    private static string Fight(IntPtr hwnd, double px, double py)
    {
        int started = Environment.TickCount;
        int lastHealth = -1;
        int presses = 0;
        int castFailures = 0;
        int lastHealMs = 0;
        int lastCastMs = 0;
        int quietRounds = 0;
        bool castEverLanded = false;        // a Wrath has demonstrably cast this fight
        bool melee = false;                 // set when casting is shown not to work
        bool announced = false;
        var worked = new List<string>();

        WowBWorldSnapshot? snap;
        WowBObservedUnit target;
        if (!Capture(out snap) || snap == null || !snap.TryGetSelectedTarget(out target))
            return "engaged, then lost the target immediately";

        lastHealth = Health(target);

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        ClickAimAction? damage = ClickAimCombat.FirstDamage(bar);      // Wrath
        ClickAimAction? heal = ClickAimCombat.FirstHeal(bar);          // Healing Touch
        ClickAimAction? auto = ClickAimCombat.AutoAttack(bar);         // Attack (6603)

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] engaged at {0:F1} yd (mob health {1}); bar: {2}",
            ClickAimMath.Len(target.Location.X - px, target.Location.Y - py),
            lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unknown",
            ClickAimCombat.Describe(bar)));

        while (!_stop && unchecked(Environment.TickCount - started) < EngageTimeoutMs)
        {
            if (!Capture(out snap) || snap == null) break;
            if (!snap.TryGetSelectedTarget(out target))
            {
                /* A mob that dies takes the target with it, and so does one that runs out of
                 * range, so this is "killed or fled". Saying only "gone" made a run with real
                 * kills report zero of them. */
                string secs = ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture);
                return worked.Count == 0
                    ? "target gone after " + secs + " s and nothing had landed - it fled or the client dropped it"
                    : "killed or fled after " + secs + " s; what landed: " + string.Join(", ", worked.ToArray());
            }

            if (target.HasPlayerVtable)
                return "the target turned out to be a player - disengaged";

            int health = Health(target);
            if (health >= 0 && lastHealth > 0 && health == 0)
                return "killed" + (worked.Count == 0 ? "" : " with " + string.Join(", ", worked.ToArray()));

            WowBObservedUnit me;
            if (!snap.TryGetLocalPlayer(out me)) break;
            double d = ClickAimMath.Len(target.Location.X - me.Location.X, target.Location.Y - me.Location.Y);

            if (d > ChaseYd)
                return string.Format(CultureInfo.InvariantCulture,
                    "target is {0:F0} yd away and moving; giving up on it", d);

            /* Face it before doing anything. A cast begun with the mob behind the character
             * is refused by the client ("you are not facing your target"), and a
             * right-click can only select what is on screen: the 16:09 run stood 3.3 yd
             * from its target and could not touch it for exactly this reason. */
            ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, 25.0, 4);

            /* My own vitals, from the same block the mob's health comes from. */
            uint myHealth = 0, myMaxHealth = 0, myMana = 0;
            Styx.WoWInternals.WowBReader.WowBUnitFields mine;
            if (reader != null && reader.TryGetUnitFields(me.UnitAddress, out mine))
            {
                myHealth = mine.Health;
                myMaxHealth = mine.MaxHealth;
                myMana = mine.Power;
            }
            double myPercent = myMaxHealth > 0 ? myHealth * 100.0 / myMaxHealth : 100.0;

            /* A heal that lands too late is a corpse. Saying so beats pressing keys at a
             * dead character, which is what the 16:09 run did for its last four seconds
             * ("health 0% and Healing Touch did not lift it"). */
            if (myMaxHealth > 0 && myHealth == 0)
                return "the character died at " + presses + " press(es) into the fight; stopping the grind";

            if (!announced)
            {
                announced = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] rotation: heal at {0:F0}% health ({1}), damage {2}, auto-attack {3}",
                    ClickAimCombat.HealBelowPercent,
                    heal == null ? "none on the bar" : "slot " + heal.Slot + " '" + ClickAimCombat.KeyLabel(heal.KeyVk) + "' " + heal.Name,
                    damage == null ? "none on the bar" : "slot " + damage.Slot + " '" + ClickAimCombat.KeyLabel(damage.KeyVk) + "' " + damage.Name,
                    auto == null ? "right-click only" : "slot " + auto.Slot + " '" + ClickAimCombat.KeyLabel(auto.KeyVk) + "'"));
            }

            /* ---- 1. heal, where Singular puts it: before everything else ---- */
            if (heal != null && myMaxHealth > 0 && myPercent <= ClickAimCombat.HealBelowPercent
                && unchecked(Environment.TickCount - lastHealMs) >= ClickAimCombat.HealRetryMs)
            {
                lastHealMs = Environment.TickCount;
                ClickAimInput.TapKey(hwnd, 0x70, 40);                  // F1 = target self (Blizzard default)
                Thread.Sleep(120);
                ClickAimInput.TapKey(hwnd, heal.KeyVk, 40);

                /* Wait for the heal to actually land. Healing Touch is a 1.5 s cast on top
                 * of the retarget, and the previous version read my health once at 1.8 s and
                 * reported "did not lift it (still 12/113)" for heals that were still being
                 * cast - four times in one fight. Poll instead. */
                uint afterHealth = 0;
                for (int waited = 0; waited < ClickAimCombat.HealVerifyMs && !_stop; waited += 300)
                {
                    Thread.Sleep(300);
                    WowBWorldSnapshot? healed;
                    if (reader == null || !Capture(out healed) || healed == null) continue;
                    if (healed.TryGetLocalPlayer(out WowBObservedUnit me2)
                        && reader.TryGetUnitFields(me2.UnitAddress, out mine))
                        afterHealth = mine.Health;
                    if (afterHealth > myHealth) break;
                }

                if (afterHealth > myHealth)
                {
                    string label = "heal " + heal.Name;
                    if (!worked.Contains(label)) worked.Add(label);
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] health {0:F0}% -> {1}/{2} with {3} (slot {4}). Back to the mob",
                        myPercent, afterHealth, myMaxHealth, heal.Name, heal.Slot));
                }
                else
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] health {0:F0}% and {1} did not lift it (still {2}/{3}) - either F1 is not "
                        + "bound to target-self or the key did not cast; carrying on",
                        myPercent, heal.Name, afterHealth, myMaxHealth));
                }

                /* Retarget the SAME mob. A single TAB is not enough: if F1 did move the
                 * target to me, one TAB picks whatever the client considers nearest, which
                 * is not necessarily the thing that is hitting us. Cycle until the guid
                 * matches, and give up after four tries rather than churning the target. */
                WowBGuid wanted = target.Guid;
                for (int tries = 0; tries < 4; tries++)
                {
                    WowBWorldSnapshot? back;
                    WowBObservedUnit selected;
                    if (Capture(out back) && back != null && back.TryGetSelectedTarget(out selected)
                        && selected.Guid == wanted)
                        break;
                    ClickAimInput.TapKey(hwnd, TabVk, 40);
                    Thread.Sleep(TabSettleMs);
                }
                continue;
            }

            /* ---- 2. how to reach it: from range if casting works, else close in ---- */
            if (!melee && d > ClickAimCombat.CasterRangeYd)
            {
                double approach;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out approach);
                Thread.Sleep(90);
                continue;
            }

            /* ---- 3. Wrath, on the global cooldown timer ---- */
            if (!melee && damage != null && myMana >= 10
                && unchecked(Environment.TickCount - lastCastMs) >= ClickAimCombat.CastSettleMs)
            {
                lastCastMs = Environment.TickCount;
                uint manaBefore = myMana;
                int mobBeforeHealth = lastHealth;
                ClickAimInput.TapKey(hwnd, damage.KeyVk, 40);
                presses++;
                _presses++;

                /* Wait for the cast instead of timing it. One read 1.8 s after the keypress
                 * called a Wrath that was still in flight a failure, and two of those put
                 * the whole fight into melee. Poll mana and the mob's health for up to
                 * CastVerifyMs and stop as soon as either moves. */
                uint manaAfter = 0;
                int mobAfter = mobBeforeHealth;
                bool spentMana = false;
                bool hurtMob = false;
                for (int waited = 0; waited < ClickAimCombat.CastVerifyMs && !_stop; waited += 250)
                {
                    Thread.Sleep(250);
                    WowBWorldSnapshot? afterCast;
                    if (!Capture(out afterCast) || afterCast == null) continue;

                    WowBObservedUnit me3;
                    if (reader != null && afterCast.TryGetLocalPlayer(out me3)
                        && reader.TryGetUnitFields(me3.UnitAddress, out mine))
                        manaAfter = mine.Power;
                    WowBObservedUnit t3;
                    if (afterCast.TryGetSelectedTarget(out t3)) mobAfter = Health(t3);

                    spentMana = manaAfter > 0 && manaAfter < manaBefore;
                    hurtMob = mobAfter >= 0 && mobBeforeHealth >= 0 && mobAfter < mobBeforeHealth;
                    if (spentMana || hurtMob) break;
                }

                if (spentMana || hurtMob)
                {
                    castEverLanded = true;
                    string label = damage.Name + " (slot " + damage.Slot.ToString(CultureInfo.InvariantCulture) + ")";
                    if (!worked.Contains(label)) worked.Add(label);
                    if (hurtMob)
                    {
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Grind] {0} does damage: mob health {1} -> {2} (mana {3} -> {4})",
                            damage.Name, lastHealth, mobAfter, manaBefore, manaAfter));
                        lastHealth = mobAfter;
                    }
                    castFailures = 0;
                    continue;
                }

                castFailures++;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} (slot {1}, key '{2}') changed nothing in {3} ms: mana {4} -> {5}, "
                    + "mob health {6} - {7} failure(s) this fight{8}",
                    damage.Name, damage.Slot, ClickAimCombat.KeyLabel(damage.KeyVk), ClickAimCombat.CastVerifyMs,
                    manaBefore, manaAfter,
                    mobAfter >= 0 ? mobAfter.ToString(CultureInfo.InvariantCulture) : "unreadable",
                    castFailures, castEverLanded ? " (but it has cast before)" : ""));

                /* Only give up on casting when it has NEVER worked in this fight. A cast
                 * that fails while the mob is out of range or behind us is a temporary
                 * condition, and one good Wrath earlier proves the key and the key map. */
                if (!castEverLanded && castFailures >= 3)
                {
                    melee = true;
                    Logging.Write("[Grind] this key has not cast once this fight - switching to "
                                  + "auto-attack rather than standing still");
                }
                continue;
            }

            /* ---- 4. melee: close, right-click, let the client swing ---- */
            if (d > AttackRangeYd)
            {
                double closer;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out closer);
                Thread.Sleep(90);
                continue;
            }

            double facingLeft;
            ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, false, out facingLeft);

            if (TryClickUnit(hwnd, target, me.Location.X, me.Location.Y))
            {
                _clicks++;
                if (!worked.Contains("right-click auto-attack")) worked.Add("right-click auto-attack");
            }
            else if (auto != null)
            {
                ClickAimInput.TapKey(hwnd, auto.KeyVk, 40);       // slot 1 = Attack
            }
            presses++;
            Thread.Sleep(AttackSettleMs);

            WowBWorldSnapshot? afterSwing;
            if (!Capture(out afterSwing) || afterSwing == null || !afterSwing.TryGetSelectedTarget(out target))
                return worked.Count == 0
                    ? "target gone while attacking and nothing had landed"
                    : "killed or fled while attacking; what landed: " + string.Join(", ", worked.ToArray());

            health = Health(target);
            if (health >= 0 && lastHealth >= 0 && health < lastHealth)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] auto-attack does damage: mob health {0} -> {1}", lastHealth, health));
                lastHealth = health;
                continue;
            }

            /* Melee and ranged both reported nothing: stop at twelve rounds rather than
             * standing there for the full engage timeout. */
            if (++quietRounds >= 12) break;
        }

        ClickAimKeySteer.Release(hwnd);

        if (worked.Count > 0)
            return "gave up on the target after " + presses + " press(es) but "
                   + string.Join(", ", worked.ToArray()) + " landed";

        return "nothing landed on the target: "
               + (damage == null ? "no damage spell on the bar" : "the spell key did not cast")
               + (auto == null ? "" : " and auto-attack did not start")
               + "; its health is still "
               + (lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unreadable");
    }

    /// <summary>
    /// A unit's health straight out of its data block, or -1 when the block will not read.
    ///
    /// This used to come from the snapshot's diagnostic candidate words, which are only
    /// sampled for the player and the selected target. Reading the block directly works for
    /// any unit, which is what "is this mob dying" needs, and it is the same 0x1E30 word the
    /// attach line has been printing as hp=75/95 since the max-health word was fixed.
    /// </summary>
    private static int Health(WowBObservedUnit unit)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader != null && reader.TryGetUnitFields(unit.UnitAddress, out Styx.WoWInternals.WowBReader.WowBUnitFields f))
            return f.Health > int.MaxValue ? -1 : (int)f.Health;

        /* Fall back to the snapshot's sample, which exists only for the player and the
         * selected target - better than claiming -1 when the block read failed. */
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

            /* Which mobs are worth attacking, from the level word the 15:57 dump confirmed:
             * a level-2 character fighting level 3 and 4 mobs is inside the band, level 9 is
             * not, and a level that did not read is allowed rather than skipped. */
            check("viableSameLevel", LevelViable(2, 2));
            check("viableTheDumpedMobs", LevelViable(3, 2) && LevelViable(4, 2));
            check("viableTooHigh", !LevelViable(9, 2));
            check("viableGrey", !LevelViable(1, 9));
            check("viableUnknownLevel", LevelViable(0, 2) && LevelViable(4, 0));

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


    private static IntPtr Handle()
    {
        try { return ObjectManager.Wow.WindowHandle; }
        catch { return IntPtr.Zero; }
    }
}

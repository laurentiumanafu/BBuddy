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
    /// The distance from which a fight will WALK to its target. It used to be the distance
    /// at which the fight gave up, and the 16:46 run gave up on 18 mobs in 20 seconds for
    /// standing 21-39 yd away - "it selects monsters but will not attack them".
    ///
    /// Singular's own numbing for the same idea is
    /// <c>Movement.CreateMoveToTargetBehavior(true, 20f)</c> in the lowbie druid pull and
    /// combat routines, so 20 yd is the number it moves in to.
    /// </summary>
    private const double ChaseYd = 20.0;

    /// <summary>
    /// Past this the fight really does give up: the target walked away further than the
    /// search radius, and chasing it leaves the hotspot ring.
    /// </summary>
    private const double GiveUpYd = 45.0;

    // ---- attacking ---------------------------------------------------------
    /// <summary>
    /// <c>UnitFlags</c> bit 0x800, which the three 2026-10-30 dumps agree means IN COMBAT:
    ///
    ///   15:57  player 0x80808 while fighting     mob being fought 0x80800   idle mobs 0x800 / 0x0
    ///   16:42  player 0x808  while fighting      the mob it fought 0x80800
    ///   17:22  player 0x8    standing in town, fighting nothing
    ///
    /// It is NOT reaction: the player carries it too. But as "this unit is fighting right now"
    /// it is exactly what a grind loop needs, and it is what replaces the untargeted
    /// right-click: on 2026-10-30 17:22 that clicked a friendly townsman at 3.6 yd and the
    /// "fight" was against the town.
    /// </summary>
    private const uint InCombatFlag = 0x800;

    /// <summary>
    /// Something worth fighting this close cuts a hotspot leg short. The 16:07 run walked
    /// 25-40 s legs while mobs were hitting it, because the loop could only look between
    /// legs; 18 yd is inside a caster's 28 yd range and close enough that the mob is coming.
    /// </summary>
    private const double InterruptYd = 18.0;

    /// <summary>
    /// How long the walk is left alone after an interrupt that did not turn into a fight.
    /// </summary>
    private const int InterruptRetryMs = 4000;

    /// <summary>Stand at a hotspot this long looking for a mob before walking to the next.</summary>
    private const int HotspotDwellMs = 9000;

    // ---- looting ----------------------------------------------------------
    /// <summary>
    /// How far a corpse will be walked to for looting. A kill leaves the body where the mob
    /// was, which for a caster is up to a cast away.
    /// </summary>
    private const double LootYd = 18.0;

    /// <summary>How long to let the client open (and auto-loot) the window before looking again.</summary>
    private const int LootSettleMs = 900;

    /// <summary>Tries per corpse before giving up on it for this session.</summary>
    private const int LootTries = 2;

    /// <summary>Corpses this run has already tried; a corpse that cannot be looted is not retried forever.</summary>
    private static readonly HashSet<WowBGuid> _looted = new HashSet<WowBGuid>();

    /// <summary>Corpses successfully looted this session, for the summary.</summary>
    private static int _loots;

    /// <summary>Set once the corpse word-diff has been printed; it is a discovery aid, not a log line.</summary>
    private static bool _lootProbed;

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
    /// Where the projection lands relative to a body, in client pixels, learned from a click the
    /// client confirmed by selecting the unit. See <see cref="TryClickUnit"/>.
    /// </summary>
    private static int _clickBiasX;
    private static int _clickBiasY;
    private static bool _clickBiasKnown;

    /// <summary>
    /// The target a fight gave up on, and when. Without this the loop answers "that mob is
    /// 17 yd away and moving" by selecting it again immediately: the 2026-10-30 log counted
    /// 22 fights in 6 seconds that way, 0.29 s apart, and then went nowhere because every
    /// iteration was spent re-deciding to give up on the same mob.
    /// </summary>
    private static WowBGuid _gaveUpGuid;
    private static int _gaveUpAt;

    /// <summary>
    /// TickCount before which the walk is not to be interrupted again, because the last
    /// interrupt found nothing it could actually fight.
    /// </summary>
    private static int _interruptQuietUntil;

    /// <summary>Last time the "in combat with nothing to fight" note was printed.</summary>
    private static int _combatQuietAt;

    /// <summary>Last time the "TAB has a target out of range" note was printed.</summary>
    private static int _farQuietAt;

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
        _clickBiasX = 0;
        _clickBiasY = 0;
        _clickBiasKnown = false;
        _gaveUpAt = 0;
        _gaveUpGuid = default(WowBGuid);
        _interruptQuietUntil = 0;
        _combatQuietAt = 0;
        _farQuietAt = 0;
        _notViable.Clear();
        _looted.Clear();
        _loots = 0;
        _lootProbed = false;

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
                    if (combatReport.StartsWith("killed", StringComparison.Ordinal)
                        || combatReport.IndexOf("killed or fled", StringComparison.Ordinal) >= 0)
                        kills++;
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
                bool ok = false;
                try { ok = RouteRunner.Run(mover, one, 1); }
                finally { RouteRunner.ShouldInterrupt = null; }

                if (!ok && RouteRunner.LastRunInterrupted)
                {
                    Logging.Write("[Grind] the walk was cut short on purpose: something worth fighting "
                                  + "came within " + InterruptYd.ToString("F0", CultureInfo.InvariantCulture)
                                  + " yd. Fighting it instead of walking on");

                    /* If the next pass does not actually fight anything, do not interrupt
                     * again for a moment - otherwise a target that can be neither engaged
                     * nor ignored pins the character in place indefinitely. */
                    _interruptQuietUntil = Environment.TickCount + InterruptRetryMs;
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
            + "{3} corpse(s) looted, {4} key press(es), {5} click(s) that landed, {6} area switch(es), {7:F0} s",
            hotspots, fights, kills, _loots, _presses, _clicks, switches,
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
        if (!Capture(out snap) || snap == null)
        {
            report = "cannot read the world snapshot; nothing to fight";
            return false;
        }

        WowBObservedUnit player;
        if (!snap.TryGetLocalPlayer(out player)) return false;

        double px = player.Location.X, py = player.Location.Y;
        int near = CountNear(snap, px, py, SearchYd);

        /* ONE way to choose a target: TAB.
         *
         * The right-click fallback is gone. It cannot tell a mob from a guard - the snapshot
         * carries positions, levels and flags, and no reaction word - so on 2026-10-30 17:22
         * it walked up to a friendly townsman 3.6 yd away and right-clicked him, which is a
         * "fight" against the town and nothing else. TAB is the client's own enemy filter
         * and it is the only thing here that knows who is an enemy. If TAB selects nothing,
         * the answer is "nothing here to fight", which is a correct answer in a town.
         *
         * The cost of being wrong is asymmetric: a missed mob is a wasted walk, an allied
         * NPC is a broken run and a reputation. Hence the strict version. */
        WowBObservedUnit target = default(WowBObservedUnit);
        bool have = false;

        /* Turn toward whatever is actually fighting before asking: TAB only considers
         * enemies in FRONT of the character (the 16:07 run logged "behind the camera" for
         * two and a half minutes because of that). */
        WowBObservedUnit aim;
        double aimYd;
        if (NearestFighting(snap, px, py, SearchYd, out aim, out aimYd))
        {
            ClickAimKeySteer.Face(aim.Location.X, aim.Location.Y, hwnd, 20.0, 6);
            if (!Capture(out snap) || snap == null) return false;
            if (!snap.TryGetLocalPlayer(out player)) return false;
            px = player.Location.X;
            py = player.Location.Y;
        }

        Styx.WoWInternals.WowBReader? selReader = ObjectManager.WowB;
        for (int i = 0; i < TabTries && !have; i++)
        {
            ClickAimInput.TapKey(hwnd, TabVk, 40);
            Thread.Sleep(TabSettleMs);
            if (!Capture(out snap)) return false;
            if (!snap.TryGetSelectedTarget(out target)) continue;

            /* Keep cycling past a corpse. Straight after a kill the body is still selected
             * and TAB is what moves off it; accepting it here is what made the 17:36 run
             * fight the same dead mob three times. */
            Styx.WoWInternals.WowBReader.WowBUnitFields sel;
            if (selReader != null && selReader.TryGetUnitFields(target.UnitAddress, out sel)
                && sel.Health == 0)
                continue;

            have = true;
        }

        if (!have)
        {
            /* Nothing LIVE came up. If the client still has a corpse selected - which is what
             * a kill leaves behind, and what TAB with nowhere to go leaves alone - clear it,
             * so the next attempt starts from nothing instead of from a body. */
            Styx.WoWInternals.WowBReader? corpseReader = ObjectManager.WowB;
            Styx.WoWInternals.WowBReader.WowBUnitFields corpseFields;
            if (!target.Guid.IsEmpty && corpseReader != null
                && corpseReader.TryGetUnitFields(target.UnitAddress, out corpseFields)
                && corpseFields.Health == 0)
            {
                /* A body we can see is a body we can take, and this is the moment we are
                 * standing on it: loot first, then clear the target. */
                string loot = LootNearby(hwnd, 2);
                if (loot.Length > 0) Logging.Write("[Grind] " + loot);

                _notViable.Add(target.Guid);
                if (_notViable.Count > 256) _notViable.Clear();
                /* No ESC here. In the client ESC closes a window when one is open and OPENS THE
                 * GAME MENU when none is - and the 18:08 run is that: after every kill the main
                 * menu appeared over the game and swallowed every key. A corpse left selected is
                 * harmless: the fight refuses it by health 0 and TAB steps off it when something
                 * live is around. Leave the selection alone. */
                report = "the only thing the client offered was a corpse (health 0) - walking on "
                         + "without touching it";
                return false;
            }

            /* Nothing selected means nothing hostile in front. In a town that is the whole
             * population: 27 friendly units within 40 yd at 17:22, none of them an enemy. */
            if (near > 0)
                report = string.Format(CultureInfo.InvariantCulture,
                    "{0} unit(s) within {1:F0} yd, none of them selected by TAB - the client only "
                    + "targets enemies, so nothing here is attackable and the walk continues",
                    near, SearchYd);
            return false;
        }

        /* It is the mob the last fight gave up on: selecting it again would repeat that
         * decision every third of a second. Walk to the next hotspot instead.
         *
         * UNLESS it is still on us. "Gave up" means "do not chase", not "stand still while
         * something eats you" - and the 17:26 run turned it into exactly that: the fight
         * gave up at 22 yd, the mob kept hitting, and every 0.3 s the loop interrupted the
         * walk for a target it then refused to fight. The character stood rooted for 40 s. */
        if (Refused(target.Guid) && !(PlayerInCombat() && TargetIsFighting(target)))
        {
            report = string.Format(CultureInfo.InvariantCulture,
                "the only target in reach is the one given up on {0:F0} s ago - walking on to the next hotspot",
                unchecked(Environment.TickCount - _gaveUpAt) / 1000.0);
            return false;
        }

        if (target.HasPlayerVtable)
        {
            _notViable.Add(target.Guid);
            if (_notViable.Count > 256) _notViable.Clear();
            report = "the client selected a PLAYER; not attacking, walking on";
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
                + "not attacking, walking on",
                tf.Level, myLevel, Math.Max(1, myLevel - LevelBelowPlayer), myLevel + LevelAbovePlayer);
            _notViable.Add(target.Guid);
            if (_notViable.Count > 256) _notViable.Clear();   // a long session on a crowded map
            return false;
        }


        double d = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        if (d > SearchYd)
        {
            /* TAB keeps whatever was last targeted when nothing better is in front, so this
             * is usually a stale pick rather than a distant enemy. It is not attacked, and it
             * is NOT cleared with ESC either - ESC with no window open opens the game menu, and
             * the 18:08 run came back with the main menu over the game after every kill. A
             * selected mob 40 yd out does nothing; the walk continues under it. Log it at most
             * every five seconds - the 17:45 log printed this six times in eight seconds and
             * it drowned everything else. */
            if (unchecked(Environment.TickCount - _farQuietAt) > 5000)
            {
                _farQuietAt = Environment.TickCount;
                report = string.Format(CultureInfo.InvariantCulture,
                    "TAB has a target {0:F0} yd away ({1:F0} yd is the search radius) - not chasing it, "
                    + "walking on", d, SearchYd);
            }
            return false;
        }

        /* The fight presses the action bar and right-clicks the SELECTED target, and the
         * verdict comes from the mob's health and my own mana in memory. No Lua anywhere. */
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

        /* Nothing landed at all - not a point of health, not one mana. Either the target
         * cannot be attacked by us (a friendly unit the client let through, an immune NPC) or
         * it is out of reach. Never select it again this session instead of repeating the
         * same nothing every three seconds. */
        if (report.StartsWith("nothing landed", StringComparison.Ordinal))
        {
            _notViable.Add(target.Guid);
            if (_notViable.Count > 256) _notViable.Clear();
            Logging.Write("[Grind] giving up on " + target.Guid
                          + " for this session: nothing this rotation has could touch it");
        }

        /* A kill leaves a body, and the character is already standing next to it. Take it
         * before deciding where to walk: this is the cheapest loot there is. */
        if (report.StartsWith("killed", StringComparison.Ordinal)
            || report.IndexOf("killed or fled", StringComparison.Ordinal) >= 0)
        {
            string loot = LootNearby(hwnd, 3);
            if (loot.Length > 0) Logging.Write("[Grind] " + loot);
        }

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

        /* An interrupt that produced no fight is not worth repeating four times a second.
         * The 17:26 run logged 130 interrupt/refuse pairs in 40 s and the character never
         * took a step; this is the brake. */
        if (_interruptQuietUntil != 0 && unchecked(Environment.TickCount - _interruptQuietUntil) < 0)
            return false;

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;
        if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) return false;

        WowBObservedUnit unit;
        double d;

        /* Being attacked is the strongest reason to stop walking, and it needs no hostility
         * guess: our own flags say so. But the flag alone is NOT enough to stop for - the
         * 17:45 run had the player at 0x808 for minutes while TAB considered the whole area
         * empty (3 nearby units, all corpses), so every leg was interrupted after two points
         * and the character never left the first field. Stop only when there is something to
         * stop FOR. */
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        Styx.WoWInternals.WowBReader.WowBUnitFields mine;
        bool inCombat = reader != null && reader.TryGetUnitFields(me.UnitAddress, out mine)
                        && (mine.UnitFlags & InCombatFlag) != 0;

        if (inCombat)
        {
            if (NearestFighting(snap, me.Location.X, me.Location.Y, radius, out unit, out d))
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} is {1:F1} yd away and in combat - stopping the walk to deal with it",
                    unit.Guid, d));
                return true;
            }

            if (unchecked(Environment.TickCount - _combatQuietAt) > 15000)
            {
                _combatQuietAt = Environment.TickCount;
                Logging.Write("[Grind] the character reads as in combat but nothing nearby is fighting - "
                              + "walking on (the flag alone is not a reason to stop)");
            }
            return false;
        }

        if (!NearestFighting(snap, me.Location.X, me.Location.Y, radius, out unit, out d)) return false;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0} is {1:F1} yd away and in combat - stopping the walk to deal with it", unit.Guid, d));
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
                else if (report.IndexOf("killed or fled", StringComparison.Ordinal) >= 0) { kills++; killed++; }
                Logging.Write("[Grind] " + report);
                continue;
            }
            if (report.Length > 0) Logging.Write("[Grind] " + report);
            Thread.Sleep(400);
        }

        /* Anything left lying around while standing here is worth the walk. */
        string loot = LootNearby(hwnd, 3);

        if (fought == 0 && loot.Length == 0) return "";
        if (fought == 0) return loot;
        return string.Format(CultureInfo.InvariantCulture,
            "stood at the hotspot and fought {0} time(s), {1} of them ending as kills{2}",
            fought, killed, loot.Length == 0 ? "" : "; " + loot);
    }

    // ------------------------------------------------------------------ looting
    /// <summary>
    /// Right-clicks corpses so the client loots them.
    ///
    /// How a player loots with auto-loot on: a right-click on the body, and the client opens
    /// the window, takes everything and closes it. So that is all this does - no Lua, no
    /// memory write, no reading of the loot itself. The user has auto-loot on, so there is
    /// nothing here that reads a loot window, and nothing that presses a key to take an item.
    ///
    /// The only thing that can be observed from outside is whether the body left the pool.
    /// A body that leaves was looted. A body that stays might still have been looted (bones
    /// linger), so a second attempt is made with an ESC between them to close a window that
    /// stayed open, and then the body is left alone for the session - one visit per corpse,
    /// never a loop. If auto-loot were off, the windows would stay open and every corpse would
    /// report that it is still there, which is the symptom the log would show.
    ///
    /// Nothing happens while something is attacking: a fight outranks loot, and the corpse
    /// is not going anywhere.
    /// </summary>
    private static string LootNearby(IntPtr hwnd, int maxCorpses)
    {
        if (_stop || maxCorpses <= 0) return "";
        if (UnderAttack()) return "";      // a fight outranks the loot, and the body keeps

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return "";

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return "";
        WowBObservedUnit me;
        if (!snap.TryGetLocalPlayer(out me)) return "";

        /* Corpses, nearest first. A unit with no health is a body; the pool keeps it for a
         * while after the kill (the 17:36 log engaged one at 2.8 yd with health 0). */
        var corpses = new List<KeyValuePair<double, WowBObservedUnit>>();
        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit unit = snap.Units[i];
            if (unit.IsLocalPlayer) continue;
            if (unit.HasPlayerVtable) continue;
            if (_looted.Contains(unit.Guid)) continue;

            double d = ClickAimMath.Len(unit.Location.X - me.Location.X, unit.Location.Y - me.Location.Y);
            if (d <= 0.5 || d > LootYd) continue;

            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (!reader.TryGetUnitFields(unit.UnitAddress, out f) || f.Health != 0) continue;

            int at = corpses.Count;
            for (int j = 0; j < corpses.Count; j++)
            {
                if (d < corpses[j].Key) { at = j; break; }
            }
            corpses.Insert(at, new KeyValuePair<double, WowBObservedUnit>(d, unit));
        }

        if (corpses.Count == 0) return "";

        int clicked = 0, gone = 0, unreached = 0;
        for (int i = 0; i < corpses.Count && clicked + unreached < maxCorpses && !_stop; i++)
        {
            WowBObservedUnit corpse = corpses[i].Value;
            _looted.Add(corpse.Guid);          // one visit per body, whatever happens

            int result = TryLootOne(hwnd, corpse);
            if (result == LootUnderAttack)
            {
                /* Something started fighting while the body was being reached. Nothing is
                 * recorded against the corpse beyond the one visit: a fight outranks loot. */
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] gave up on the body of {0} ({1:F1} yd away) - something started fighting",
                    corpse.Guid, corpses[i].Key));
                break;
            }
            if (result == LootNoClick)
            {
                unreached++;
                continue;                      // TryLootOne already said exactly what failed
            }

            clicked++;
            _loots++;
            if (result > 0)
            {
                gone++;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] looted the body of {0} ({1:F1} yd away) - the corpse is gone, so the client "
                    + "took it", corpse.Guid, corpses[i].Key));
            }
            else
            {
                /* Bones stay in the pool, so a body that is still there is not proof of failure -
                 * but the click itself WAS confirmed by the client selecting the body, which is
                 * the part that used to be taken on faith. */
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] looted the body of {0} ({1:F1} yd away); the bones are still in the pool, "
                    + "which is normal", corpse.Guid, corpses[i].Key));
            }
        }

        if (clicked == 0 && unreached == 0) return "";
        return string.Format(CultureInfo.InvariantCulture,
            "looting: {0} body(s) taken{1}{2}", clicked,
            gone == 0 ? "" : ", " + gone + " gone from the pool",
            unreached == 0 ? "" : ", " + unreached + " the mouse could not reach");
    }

    /// <summary>
    /// Walks to one corpse and right-clicks it, and reports what happened: 1 when the body left
    /// the pool, 0 when the client confirmed the click by selecting the body and the body is
    /// still there (bones), <see cref="LootNoClick"/> when no point around the projected one
    /// made the client select it, and <see cref="LootUnderAttack"/> when something started
    /// fighting. Walks only within <see cref="LootYd"/>, because a loot trip is not worth a
    /// fight with something else.
    /// </summary>
    private static int TryLootOne(IntPtr hwnd, WowBObservedUnit corpse)
    {
        int started = Environment.TickCount;

        for (int attempt = 0; attempt < LootTries && !_stop; attempt++)
        {
            /* Get within reach first. A right-click on a body further away starts a walk, not a
             * loot, and the walk is what the mover is for. */
            while (unchecked(Environment.TickCount - started) < 12000 && !_stop)
            {
                double px, py;
                if (!ClickAimPositions.TryRead(out px, out py)) break;
                double d = ClickAimMath.Len(corpse.Location.X - px, corpse.Location.Y - py);
                if (d <= 4.5) break;
                if (UnderAttack()) return LootUnderAttack;
                double remaining;
                ClickAimKeySteer.Steer(corpse.Location.X, corpse.Location.Y, hwnd, true, out remaining);
                Thread.Sleep(90);
            }

            ClickAimKeySteer.ReleaseHeld();
            StopAndSettle(hwnd);

            double lx, ly;
            if (!ClickAimPositions.TryRead(out lx, out ly)) return LootNoClick;

            ClickAimKeySteer.Face(corpse.Location.X, corpse.Location.Y, hwnd, 30.0, 4);

            /* One probe per session: five windows of the corpse's own object, read before and
             * after a click the client CONFIRMED selected the body. A word that moves across
             * that click is the loot flag, and finding it finally makes "was it looted" a
             * reading instead of an assumption. */
            bool probe = !_lootProbed && !_clickBiasKnown;
            uint[][]? before = probe ? CaptureWords(corpse) : null;

            /* mustSelect: the click only counts once the client says this body is the target.
             * A right-click aimed at a point that lands on the character's own back (which is
             * where a unit 3 yd in front projects to) selects nothing and loots nothing, and
             * "I clicked" was being reported as "I looted" all through the 18:35 run. */
            string how;
            if (!TryClickUnit(hwnd, corpse, lx, ly, true, out how))
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Loot] could not reach the body of {0} with the mouse: {1}", corpse.Guid, how));
                return LootNoClick;
            }

            _clicks++;
            _lootProbed = true;

            /* The client opens, takes and closes the window in well under a second with
             * auto-loot on; a body that is going to leave the pool leaves soon after. */
            for (int waited = 0; waited < 1500 && !_stop; waited += 300)
            {
                Thread.Sleep(300);
                if (!BodyStillThere(corpse.Guid)) return 1;
            }

            if (before != null)
            {
                Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
                if (reader != null) DescribeWordDiff(reader, corpse, before);
            }

            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Loot] the right-click on {0} landed ({1}); the body is still in the pool", corpse.Guid, how));

            if (attempt + 1 < LootTries) StopAndSettle(hwnd);
        }

        return 0;
    }

    /// <summary>The click found no point on the body at all.</summary>
    private const int LootNoClick = -2;

    /// <summary>Something started hitting the character while the loot was being reached.</summary>
    private const int LootUnderAttack = -1;

    /// <summary>
    /// The windows of a unit object that the loot probe reads. They bracket the fields this
    /// build already uses (health +0x1E30, level +0x1E40, flags +0x1E60) with enough room on
    /// both sides for a dynamic-flags word to sit among them.
    /// </summary>
    private static readonly int[] LootProbeWindows = { 0x1900, 0x1B00, 0x1D00, 0x1F00, 0x2100 };

    /// <summary>Reads <see cref="LootProbeWindows"/> of a unit, or null when none is readable.</summary>
    private static uint[][]? CaptureWords(WowBObservedUnit unit)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return null;
        var windows = new uint[LootProbeWindows.Length][];
        bool any = false;
        for (int i = 0; i < LootProbeWindows.Length; i++)
        {
            uint[] words;
            if (reader.TryReadWords(unit.UnitAddress + (ulong)LootProbeWindows[i], 64, out words))
            {
                windows[i] = words;
                any = true;
            }
            else
            {
                windows[i] = new uint[0];
            }
        }
        return any ? windows : null;
    }

    /// <summary>
    /// Prints the words of a corpse that moved while it was being looted, as
    /// <c>+0x1E64: 1 -> 0</c>. Only the words that moved are printed - five 64-word windows are
    /// 320 numbers and the interesting one is the flag that was 1 while the body had loot in it.
    /// </summary>
    private static void DescribeWordDiff(Styx.WoWInternals.WowBReader reader, WowBObservedUnit corpse,
                                         uint[][] before)
    {
        int changed = 0;
        for (int i = 0; i < LootProbeWindows.Length && i < before.Length; i++)
            changed += WordDiff(reader, corpse.UnitAddress, LootProbeWindows[i], before[i]);

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Loot] {0} word(s) of the body's own object moved across the loot click{1}",
            changed,
            changed == 0
                ? " - this build keeps its loot state somewhere these windows do not reach, so the "
                  + "only signal left is the body leaving the pool"
                : ""));
    }

    /// <summary>Prints one window's differences and returns how many words moved.</summary>
    private static int WordDiff(Styx.WoWInternals.WowBReader reader, ulong unitAddress, int offset,
                                uint[]? before)
    {
        if (before == null || before.Length == 0) return 0;
        uint[] after;
        if (!reader.TryReadWords(unitAddress + (ulong)offset, before.Length, out after)) return 0;
        int changed = 0;
        for (int i = 0; i < before.Length && i < after.Length; i++)
        {
            if (before[i] == after[i]) continue;
            changed++;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Loot] word +0x{0:X}: 0x{1:X8} -> 0x{2:X8}", offset + i * 4, before[i], after[i]));
        }
        return changed;
    }

    /// <summary>Whether a guid is still in the pool as a body (no health).</summary>
    private static bool BodyStillThere(WowBGuid guid)
    {
        if (guid.IsEmpty) return false;
        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;
        for (int i = 0; i < snap.Units.Count; i++)
        {
            if (snap.Units[i].Guid == guid) return true;
        }
        return false;
    }

    /// <summary>
    /// The nearest unit that is IN COMBAT right now - the only hostility signal this build
    /// offers (<see cref="InCombatFlag"/>) - or false when there is none.
    ///
    /// Used for two things: turning toward the thing attacking us before TAB (which only
    /// looks in front), and deciding whether a hotspot leg should be cut short. A friendly
    /// NPC standing in a town is not in combat, which is exactly why it is not used as a
    /// target any more.
    /// </summary>
    private static bool NearestFighting(WowBWorldSnapshot snap, double px, double py, double radius,
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

            bool fighting = false;
            if (reader != null && reader.TryGetUnitFields(candidate.UnitAddress,
                    out Styx.WoWInternals.WowBReader.WowBUnitFields f))
            {
                if (f.Health == 0) continue;
                if (!LevelViable(f.Level, myLevel)) continue;
                fighting = (f.UnitFlags & InCombatFlag) != 0;
            }
            if (!fighting) continue;

            unit = candidate;
            yards = d;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Whether something is actually on the character: the in-combat flag AND a unit that is
    /// fighting within 25 yd. The flag alone lags for seconds after a kill, which would have
    /// made every loot trip wait for a combat state that is already over.
    /// </summary>
    private static bool UnderAttack()
    {
        if (!PlayerInCombat()) return false;
        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;
        if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) return false;
        WowBObservedUnit unit;
        double d;
        return NearestFighting(snap, me.Location.X, me.Location.Y, 25.0, out unit, out d);
    }

    /// <summary>
    /// Whether the local player's own <c>UnitFlags</c> say "in combat". This is the one read
    /// that answers "are we being attacked" without guessing at hostility: it was 0x808 at
    /// 16:42 while a mob was on the character and 0x8 at 17:22 while standing in town.
    /// </summary>
    private static bool PlayerInCombat()
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
            return (f.UnitFlags & InCombatFlag) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether the unit is fighting right now, from its own <c>UnitFlags</c> bit. Used to
    /// decide that a target the last fight gave up on is nevertheless the thing attacking
    /// us, in which case not fighting it is not an option.
    /// </summary>
    private static bool TargetIsFighting(WowBObservedUnit unit)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return false;
        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        return reader.TryGetUnitFields(unit.UnitAddress, out f) && (f.UnitFlags & InCombatFlag) != 0;
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

    /// <summary>
    /// Lets go of forward and waits until the character has actually stopped. A cast pressed
    /// while the client still considers the character in motion is refused with "cannot do
    /// that while moving" and costs the whole cast window, so this is worth two position
    /// reads: the same point twice means stopped.
    /// </summary>
    private static void StopAndSettle(IntPtr hwnd)
    {
        ClickAimKeySteer.ReleaseHeld();

        double lastX = double.NaN, lastY = double.NaN;
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(120);
            double x, y;
            if (!ClickAimPositions.TryRead(out x, out y)) return;
            if (!double.IsNaN(lastX) && ClickAimMath.Len(x - lastX, y - lastY) < 0.15) return;
            lastX = x;
            lastY = y;
        }
    }

    /// <summary>Right-clicks one unit where it stands: select and attack, done by the client.</summary>
    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py)
    {
        string how;
        return TryClickUnit(hwnd, unit, px, py, false, out how);
    }

    /// <summary>
    /// Right-clicks a unit, and - when <paramref name="mustSelect"/> - does not call it a
    /// success until the client says that unit is now the target.
    ///
    /// This exists because the projection cannot be trusted to land on a body by itself. The
    /// 18:35 run right-clicked a corpse at a projected point that was 2.8 yd away and the body
    /// did not move a word: a unit standing in front of the character is very close to the
    /// camera's own axis, which puts the projected point on the character's BACK, and a click
    /// there is a click on nothing. The same reason explains why no right-click auto-attack has
    /// ever done damage in a log.
    ///
    /// So: click the projected point, then a small ladder of points around it, and stop at the
    /// first one the client actually selects something with. The pixel offset that worked is
    /// kept for the rest of the session (<see cref="_clickBiasX"/>), which turns every later
    /// unit click back into a single click.
    /// </summary>
    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,
                                     bool mustSelect, out string how)
    {
        how = "";
        int dx, dy;
        if (!TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z, out dx, out dy))
        {
            how = "the projected point is behind the camera or off the screen";
            return false;
        }

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        bool canVerify = mustSelect && reader != null && reader.ReadTargetGuid() != unit.Guid;

        /* The offset that worked last time. If it stops working (the camera moved, the
         * character turned) the sweep below relearns it. */
        if (_clickBiasKnown)
        {
            ClickAimInput.Click(hwnd, dx + _clickBiasX, dy + _clickBiasY);
            if (!canVerify) return true;
            Thread.Sleep(150);
            if (reader != null && reader.ReadTargetGuid() == unit.Guid)
            {
                how = string.Format(CultureInfo.InvariantCulture,
                    "the click landed on the learned offset {0},{1} px", _clickBiasX, _clickBiasY);
                return true;
            }
            _clickBiasKnown = false;
            ClickAimKeySteer.ReleaseHeld();
            ClickAimInput.TapKey(hwnd, 0x57, 40);          // W: cancel a click-walk the miss started
        }

        int[] xs = { 0, -40, 40, -80, 80, -120, 120 };
        int[] ys = { 0, 30, 60, -25, 90, -50, 120 };

        int tried = 0;
        for (int yi = 0; yi < ys.Length && tried < 18 && !_stop; yi++)
        {
            for (int xi = 0; xi < xs.Length && tried < 18 && !_stop; xi++)
            {
                ClickAimInput.Click(hwnd, dx + xs[xi], dy + ys[yi]);
                tried++;
                if (!canVerify) return true;

                Thread.Sleep(150);
                if (reader == null) return true;
                if (reader.ReadTargetGuid() != unit.Guid)
                {
                    /* A click that missed the body landed on the ground, and a right-click on
                     * the ground walks there. Stop the walk and turn back before the next try,
                     * or the ladder is climbed from the wrong place. */
                    ClickAimKeySteer.ReleaseHeld();
                    ClickAimInput.TapKey(hwnd, 0x57, 40);
                    ClickAimKeySteer.Face(unit.Location.X, unit.Location.Y, hwnd, 40.0, 2);
                    continue;
                }

                _clickBiasX = xs[xi];
                _clickBiasY = ys[yi];
                _clickBiasKnown = true;
                how = string.Format(CultureInfo.InvariantCulture,
                    "the projected point missed the body; {0},{1} px from it selected it, and that offset "
                    + "is now used for every unit click", xs[xi], ys[yi]);
                return true;
            }
        }

        how = string.Format(CultureInfo.InvariantCulture,
            "none of {0} click points around the projected one selected it", tried);
        return false;
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
        int faceFailures = 0;
        bool meleeOnce = false;

        bool castEverLanded = false;        // a Wrath has demonstrably cast this fight
        bool melee = false;                 // set when casting is shown not to work
        bool announced = false;
        var worked = new List<string>();

        WowBWorldSnapshot? snap;
        WowBObservedUnit target;
        if (!Capture(out snap) || snap == null || !snap.TryGetSelectedTarget(out target))
            return "engaged, then lost the target immediately";

        lastHealth = Health(target);

        /* The kill leaves the corpse selected, and TAB keeps a target when there is nothing
         * else to move to - so the next "engage" is the body of the mob that just died. The
         * 17:36 run did exactly that three times: three 3.2 s Wrath attempts at a
         * health-0 target, then 25 s of turning in circles at 0.7 yd. The death check below
         * needs a health reading that was ever above zero, which a corpse can never give. */
        if (lastHealth == 0)
            return "the target is already dead (health 0) - that is a corpse left selected, not a fight";

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        ClickAimAction? damage = ClickAimCombat.FirstDamage(bar);      // Wrath
        ClickAimAction? heal = ClickAimCombat.FirstHeal(bar);          // Healing Touch
        ClickAimAction? auto = ClickAimCombat.AutoAttack(bar);         // Attack (6603)

        /* Whatever the walk was doing, it is over: the first thing this fight does is press a
         * key, and a cast begun while the character is still moving is refused by the client
         * with "cannot do that while moving" and does nothing. Let go and let the character
         * come to a stop first - that is the message the user kept seeing in chat. */
        StopAndSettle(hwnd);

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

            /* ChaseYd is how far the fight will WALK to a target, never the distance at
             * which it gives up. The 16:46 run gave up 18 times in 20 s on mobs 21-39 yd
             * away, which is exactly "it selects monsters and won't attack them": TAB had
             * picked a mob inside the search radius and the fight refused it for being
             * outside the chase radius. Give-up is now the search radius plus a margin. */
            if (d > GiveUpYd)
                return string.Format(CultureInfo.InvariantCulture,
                    "target is {0:F0} yd away - beyond the {1:F0} yd the fight will walk; giving up on it",
                    d, GiveUpYd);

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

            /* ---- 2. reach it: walk to casting range, do not walk PAST it ----
             *
             * The 16:46 run had the character casting and then closing into melee, which is
             * the one thing a caster should not do while it has mana. So: approach only
             * while out of casting range, and stop the moment the target is inside it. */
            bool canCast = !melee && damage != null && myMana >= 10;

            if (canCast && d > ClickAimCombat.CasterRangeYd)
            {
                double approach;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out approach);
                Thread.Sleep(90);
                continue;
            }

            if (canCast && d > ClickAimCombat.CasterRangeYd - 2.0)
                ClickAimKeySteer.ReleaseHeld();      // arrived: stop walking before casting

            /* ---- 3. Wrath, on the global cooldown timer ----
             *
             * Everything below this point assumes casting is NOT viable, so when it is, the
             * loop leaves here - it either approached, cast, or waits for the cooldown. The
             * 17:26 run fell through on the cooldown tick into the melee branch and logged
             * "cannot cast at it and it is 22 yd away" with 139 mana, then gave up on a mob
             * it had hit with Wrath one second earlier. */
            if (canCast && unchecked(Environment.TickCount - lastCastMs) < ClickAimCombat.CastSettleMs)
            {
                Thread.Sleep(120);
                continue;
            }

            if (canCast)
            {
                /* Stop moving before the key. The client answers a cast begun while walking
                 * with "cannot do that while moving" and does nothing, which is what the
                 * user saw in this run's chat. */
                StopAndSettle(hwnd);

                /* Turn and CHECK before spending the key. A cast begun while the mob is not
                 * in front is refused by the client, and the previous version paid 3.2 s for
                 * that refusal every time - which the 16:42 log shows as nine "changed
                 * nothing in 3200 ms" lines for one mob. */
                if (!ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, 30.0, 8))
                {
                    faceFailures++;
                    if (faceFailures == 1 || faceFailures % 5 == 0)
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Grind] cannot turn onto the target ({0:F0} yd away) - the arrow keys are not "
                            + "moving the facing field. Held the cast {1} time(s)",
                            d, faceFailures));
                    Thread.Sleep(150);
                    continue;
                }

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

                /* Casting has already been faced for, so a failure now is range, line of
                 * sight or a moving target. Three in a row without landing: melee, rather
                 * than standing at 20 yd pressing a key that does nothing. */
                if (castFailures >= 3)
                {
                    melee = true;
                    Logging.Write(castEverLanded
                        ? "[Grind] the last 3 casts landed nothing even while facing the target - "
                          + "closing to melee for the rest of this fight"
                        : "[Grind] this key has not cast once this fight - switching to auto-attack "
                          + "rather than standing still");
                }
                continue;
            }

            /* ---- 4. melee: close, right-click, let the client swing ----
             *
             * This is the fallback, not a habit: it runs only when casting is off the table
             * (no damage spell, under 10 mana, or three casts that landed nothing), because
             * a right-click both starts auto-attack AND walks the character into the mob.
             * If it ever runs while the character still has mana, that is the bug the user
             * reported twice - "auto-attacking while casting wrath" - so it says so. */
            if (!meleeOnce)
            {
                meleeOnce = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] melee from here: casting is {0} (damage spell {1}, mana {2}, {3} failed cast(s))",
                    melee ? "switched off for this fight" : "not available",
                    damage == null ? "none on the bar" : damage.Name, myMana, castFailures));
            }

            if (d > AttackRangeYd)
            {
                /* Walk in, but not across the whole ring: melee is a decision, and a mob that
                 * keeps backing off is not worth chasing. If it is on US, the limit is the
                 * give-up distance instead - a mob hitting the character has to be answered,
                 * which is the other half of the 17:26 stall. */
                double chase = PlayerInCombat() ? GiveUpYd : ChaseYd;
                if (d > chase)
                    return string.Format(CultureInfo.InvariantCulture,
                        "cannot cast at it and it is {0:F0} yd away (the melee chase is {1:F0} yd); "
                        + "giving up on it", d, chase);
                double closer;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out closer);
                Thread.Sleep(90);
                continue;
            }

            /* In melee the character has to face the mob for its swings to land and for the
             * right-click to select it (it selects what is on screen, and the mob is at our
             * feet). Turn, and only swing once the turn has actually happened. */
            /* Do NOT steer by bearing when the mob is underfoot. At 0.7-1.1 yd the bearing to
             * the target is numerically unstable - a hand's width of player movement flips it
             * by 180 deg - and the 17:36 log is that failure in full: "want 62 / want 176 /
             * want -1 / want 157" with a 400 ms arrow tap each time, for 25 s, on a corpse.
             * Inside 2 yd, face the point with a wide tolerance and swing. */
            if (d > 2.0)
            {
                double facingLeft;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, false, out facingLeft);
            }
            double tolerance = d <= 2.0 ? 60.0 : 20.0;
            if (!ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, tolerance, 4))
            {
                faceFailures++;
                if (faceFailures == 1 || faceFailures % 5 == 0)
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] cannot turn onto the mob at arm's length ({0:F1} yd) - the arrow keys are "
                        + "not moving the facing field ({1} time(s))", d, faceFailures));
                Thread.Sleep(120);
                continue;
            }

            /* A click that lands is not a click that damages: the 17:36 log kept reporting
             * "right-click auto-attack landed" for swings at a corpse. Credit it only when
             * the mob's health actually moved, below. */
            /* Same rule for the swing: no click while the forward key is still down. Turning
             * is fine, walking is not. */
            ClickAimKeySteer.ReleaseHeld();
            if (TryClickUnit(hwnd, target, me.Location.X, me.Location.Y))
            {
                _clicks++;
            }
            /* The Attack hotkey goes out whether the click landed or not. The click is what a
             * player would do and it also selects the mob, but a click aimed at a body 1 yd
             * ahead can land on the character's own model - the key cannot miss, and the
             * target is already selected by TAB. */
            if (auto != null) ClickAimInput.TapKey(hwnd, auto.KeyVk, 40);
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
                if (!worked.Contains("right-click auto-attack")) worked.Add("right-click auto-attack");
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

            /* The distances have to nest: melee reach &lt; the melee chase &lt; casting range &lt;
             * the point at which a fight gives up &lt; the radius units are searched in. Getting
             * this order wrong is what produced "selects monsters but will not attack them". */
            check("rangesNest", AttackRangeYd < ChaseYd
                              && ChaseYd < ClickAimCombat.CasterRangeYd
                              && ClickAimCombat.CasterRangeYd < SearchYd
                              && SearchYd < GiveUpYd);
            check("interruptInsideSearch", InterruptYd < SearchYd && InterruptYd > AttackRangeYd);

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

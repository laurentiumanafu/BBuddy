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

    /// <summary>
    /// How far from the CHARACTER something may stand and still be fought. This is the user's
    /// number: "try to find more monsters around the character (max 100 yards, if no monsters
    /// are around THEN move to next hotspot)".
    ///
    /// It started at 40 yd, which is why the 13:23 log has the line
    /// "TAB has a target 48 yd away (40 yd is the search radius) - not chasing it": the client
    /// offered a mob 48 yd out and the bot walked on past it. The radius is a circle around the
    /// character - not around the path, and not around the hotspot - so a mob behind the
    /// character or twenty yards back up the leg is a mob to fight, not a mob to leave.
    /// </summary>
    private const double SearchYd = 100.0;

    /// <summary>Walk this close before attacking. Melee reach plus a margin.</summary>
    private const double AttackRangeYd = 4.0;

    /// <summary>
    /// The distance from which a fight will WALK to its target: the same radius the search
    /// accepts, because "a monster 40 yards away ... it should move to the monster and attack
    /// it" is the user's report. It was 20 yd - Singular's
    /// <c>Movement.CreateMoveToTargetBehavior(true, 20f)</c> in the lowbie druid pull - and 20
    /// yd is fine for a mob that is already in front, but it is the wrong number for a bot that
    /// is supposed to clear a 100 yd circle: an out-of-mana level-2 warlock refused everything
    /// between 20 and 40 yd and stood there.
    /// </summary>
    private const double ChaseYd = SearchYd;

    /// <summary>
    /// Past this the fight really does give up: the target walked away beyond the search
    /// radius by a margin, so chasing it is no longer the same decision the search made.
    /// </summary>
    private const double GiveUpYd = SearchYd + 15.0;

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
    /// <c>UnitFlags</c> bit 0x2. The client's own attack predicate - the function the
    /// registered <c>UnitCanAttack</c> handler calls - tests bit 1 of the dword at object
    /// +0x1E60 and returns false on the path that takes it (see hunt/WOWB_70291_TRACE_FINDINGS.md,
    /// 16:27 "it also reads second object +0x1E60, testing bit 1"), and bit 1 is
    /// UNIT_FLAG_NON_ATTACKABLE in every field layout this engine has used. It is used here as a
    /// FILTER, never as proof: a unit carrying the bit is skipped, and a unit without it still
    /// has to survive the fight before it counts as a monster.
    /// </summary>
    private const uint NonAttackableFlag = 0x2;

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

    /// <summary>
    /// The longest a hotspot is stood at, looking for a mob. It is a ceiling, not a timer: the
    /// hotspot ends as soon as two fresh scans of the pool inside <see cref="SearchYd"/> find
    /// nothing alive, which is the user's rule ("if no monsters are around THEN move to next
    /// hotspot"). It used to be 9 s and was the only rule, so a fight that started at second 8
    /// was cut in half and a clear hotspot was still ignored for nine seconds.
    /// </summary>
    private const int HotspotDwellMs = 30000;

    /// <summary>How long to wait between two scans before calling a hotspot finished.</summary>
    private const int AreaRescanMs = 1200;

    // ---- looting ----------------------------------------------------------
    /// <summary>
    /// How far a corpse will be walked to for looting. A kill leaves the body where the mob
    /// was, which for a caster is up to a cast away.
    /// </summary>
    private const double LootYd = 18.0;

    /// <summary>Health and mana this high count as "rested enough"; see <see cref="RestIfNeeded"/>.</summary>
    private const int RestFullPercent = 90;

    /// <summary>
    /// How far off the path a mob may be and still be worth stopping the walk for: the same
    /// 100 yd the hotspot uses, because a leg is walked through the same circle the character
    /// fights in. It was 30 yd, which is why every mob the walk passed was "not taken".
    /// </summary>
    private const double PullYd = SearchYd;

    /// <summary>How often the walk asks the client whether there is something to kill nearby.</summary>
    private const int PullProbeMs = 1500;

    /// <summary>
    /// How close the walk stops short of the mob: inside <see cref="AttackRangeYd"/> once the
    /// character's own momentum has been spent, instead of through it and out the other side.
    /// </summary>
    private const double MeleeEngageYd = AttackRangeYd + 1.2;

    /// <summary>
    /// How long to leave the client's swing alone before re-pressing Attack. A swing lands every
    /// ~2-3 s; pressing the key more often than that is a global cooldown spent for nothing.
    /// </summary>
    private const int MeleePressMs = 1200;

    /// <summary>
    /// How long without a landed swing before the swing is assumed to have stopped.
    ///
    /// The Attack action TOGGLES on this client, so pressing it "to be sure" turns the swing
    /// off. Four seconds of silence is the signal that it really has stopped; until then the
    /// key is left alone and only the melee ability is pressed.
    /// </summary>
    private const int MeleeSwingQuietMs = 4000;

    /// <summary>
    /// How long without a landing swing before the character turns onto the mob again. Facing is
    /// only needed because the client refuses a swing it is not facing for; a character that is
    /// hurting the mob is already facing it, and re-facing it every pass is what made the 23:00
    /// run circle the mob.
    /// </summary>
    private const int MeleeRefaceAfterMs = 2500;

    /// <summary>
    /// The gap between clicks used to START a swing when the bar has no Attack key. A click at
    /// arm's length can land on the ground and become a walk order, so it is a last resort and
    /// never more than one every few seconds.
    /// </summary>
    private const int MeleeClickMs = 2500;

    /// <summary>How long to wait in melee when nothing was sent this pass.</summary>
    private const int MeleeWaitMs = 350;

    /// <summary>How long to wait for the client to take the body before deciding it is bones.</summary>
    private const int LootSettleMs = 1500;

    /// <summary>
    /// How close the character has to be before the loot click is worth sending. A right-click
    /// on a body further away is not a loot at all: the client reads it as "walk there", which
    /// is exactly what the 18:44 run did for 17 s and then blamed on the projection.
    /// </summary>
    private const double LootReachYd = 4.0;

    /// <summary>How long the walk to a corpse may take before the loot is given up.</summary>
    private const int LootWalkMs = 20000;


    /// <summary>Corpses this run has already tried; a corpse that cannot be looted is not retried forever.</summary>
    private static readonly HashSet<WowBGuid> _looted = new HashSet<WowBGuid>();

    /// <summary>Corpses successfully looted this session, for the summary.</summary>
    private static int _loots;

    /// <summary>Set once the corpse word-diff has been printed; it is a discovery aid, not a log line.</summary>
    private static bool _lootProbed;

    /// <summary>A pixel a left-click probe proved is empty ground - the safe way to deselect.</summary>
    private static int _emptyPixelX;
    private static int _emptyPixelY;
    private static bool _emptyPixelKnown;

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

    /// <summary>When the walk last asked the client (TAB) whether anything nearby is attackable.</summary>
    private static int _lastPullProbeMs;

    /// <summary>When a refused pull was last explained, so the log is not one line per probe.</summary>
    private static int _pullQuietAt;

    /// <summary>Last time the "in combat with nothing to fight" note was printed.</summary>
    private static int _combatQuietAt;

    /// <summary>Last time the "TAB has a target out of range" note was printed.</summary>
    private static int _farQuietAt;

    /// <summary>
    /// Last time "nothing alive within the search radius" was printed. This used to be the
    /// corpse line, printed once per engage attempt - the 13:24 run printed it eight times in
    /// eight seconds while the character stood over a body it had already looted.
    /// </summary>
    private static int _clearQuietAt;

    /// <summary>Last time the pool said something was there and TAB disagreed.</summary>
    private static int _poolQuietAt;

    /// <summary>
    /// Whether the last <see cref="TryEngage"/> scan found the area empty. Set only when the
    /// snapshot itself has nothing alive and level-viable inside the search radius, so a caller
    /// can stop waiting at a hotspot without guessing from the report text.
    /// </summary>
    private static bool _scanWasClear;

    /// <summary>Minimum spacing between two memory-based (left-click) selections.</summary>
    private const int PoolSelectMs = 2500;

    /// <summary>
    /// How far away a unit the POOL offers may be and still be walked to without TAB having
    /// agreed first. The full <see cref="SearchYd"/> circle is what the bot fights in, but the
    /// pool cannot tell a wolf from a guard, so an unverified pick from it is only worth a short
    /// walk: 50 yd covers the user's "a monster 40 yards away is skipped" and leaves the far half
    /// of the circle to TAB, which does know. Anything the pool sees further out is picked up on
    /// the way in, by the walk or by the next scan.
    /// </summary>
    private const double PoolSelectYd = 50.0;
    private static int _lastPoolSelectMs;

    /// <summary>
    /// Units the pool offered and the left-click search could not put the cursor on. One miss is
    /// bad luck (the body may be behind the character's own model for one frame); the second
    /// miss means "this one is not clickable from here" and it is set aside like any other
    /// refused unit, so a hotspot can still become clear and the walk can move on.
    /// </summary>
    private static readonly HashSet<WowBGuid> _poolMisses = new HashSet<WowBGuid>();

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
        _lastPullProbeMs = 0;
        _pullQuietAt = 0;
        _clearQuietAt = 0;
        _poolQuietAt = 0;
        _lastPoolSelectMs = 0;
        _scanWasClear = false;
        _combatQuietAt = 0;
        _farQuietAt = 0;
        _notViable.Clear();
        _poolMisses.Clear();
        _looted.Clear();
        _loots = 0;
        _lootProbed = false;
        _emptyPixelKnown = false;

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

                /* Once, before the first fight rather than in the middle of one: whatever this
                 * class buffs itself with (Mark of the Wild, Arcane Intellect, ...). */
                string buffed = ApplyBuffsIfNeeded(hwnd);
                if (buffed.Length > 0) Logging.Write("[Grind] " + buffed);

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

                /* Between fights, with nothing on us: eat and drink if the user's percentages say
                 * so. This is the cheapest place in the loop to sit - the next thing it would do
                 * is walk, and walking is what cancels a meal. */
                string rested = RestIfNeeded(hwnd);
                if (rested.Length > 0) Logging.Write("[Grind] " + rested);

                RoutePoint next = area.Hotspots[index % area.Hotspots.Count];
                index++;

                var one = new List<RoutePoint> { next };

                /* While walking, look for a fight every few seconds instead of only at the
                 * end of the leg. The 16:07 run walked 25-40 s at a time with mobs on it and
                 * never turned round; a leg is a whole hotspot, so "check between legs" was
                 * checking every 40 s. */
                /* The same radius the character fights in: a leg is walked through the same
                 * circle a hotspot is cleared in, so "monsters between hotspots" are monsters
                 * this stops for. */
                RouteRunner.ShouldInterrupt = () => FightNeeded(hwnd, SearchYd);
                bool ok = false;
                try { ok = RouteRunner.Run(mover, one, 1); }
                finally { RouteRunner.ShouldInterrupt = null; }

                if (!ok && RouteRunner.LastRunInterrupted)
                {
                    Logging.Write("[Grind] the walk was cut short on purpose: something worth fighting "
                                  + "is within " + PullYd.ToString("F0", CultureInfo.InvariantCulture)
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

        /* The user's own numbers, from Settings\clickaim-grind.txt. Read here, once per run, so
         * a change made in the window (or in a text editor) is what this run uses. */
        ClickAimSettings.Load();
        _dotGuid = default(WowBGuid);
        _dotUntil = 0;
        _manaPeak = 0;
        _healthPeak = 0;
        _buffsApplied = false;
        _healCostGuess = 0;
        _castCosts.Clear();
        Logging.Write("[Grind] combat settings: " + ClickAimSettings.Describe());

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

                /* Who is around, straight out of the pool and BEFORE any key is pressed. This is
                 * the line that answers "do we have a memory offset so the bot sees the monsters
                 * around it": position and distance come from the snapshot, level, health and
                 * flags from each unit's data block. What is missing - and what this census is
                 * meant to help find - is a reaction word: compare a wolf and a guard in the same
                 * list and the field that differs is the faction/reaction candidate. */
                int shown = 0;
                for (int i = 0; i < snap.Units.Count && shown < 10; i++)
                {
                    WowBObservedUnit u = snap.Units[i];
                    if (u.IsLocalPlayer) continue;
                    double d = ClickAimMath.Len(u.Location.X - me.Location.X, u.Location.Y - me.Location.Y);
                    if (d > SearchYd) continue;
                    Styx.WoWInternals.WowBReader.WowBUnitFields uf;
                    if (!reader.TryGetUnitFields(u.UnitAddress, out uf)) continue;
                    shown++;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind]   in the pool {0:F1} yd away: level {1}, hp {2}/{3}, flags 0x{4:X}",
                        d, uf.Level, uf.Health, uf.MaxHealth, uf.UnitFlags));
                }
                if (shown == 0)
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind]   no readable unit inside {0:F0} yd of the character", SearchYd));
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

        /* Three ways to choose a target, in this order, and the order is the whole design:
         *
         *   1. what is already selected (the walk asks once, TAB costs a key);
         *   2. TAB - the client's own enemy filter, the only thing on this build that knows
         *      who is an enemy;
         *   3. the unit pool in memory, selected with a LEFT-click, when TAB has nothing live
         *      to offer but the pool says something alive is standing right there.
         *
         * Step 3 is the answer to the 13:23 report ("it's not attacking monsters between
         * hotspots unless the monsters are in front of the character ... do we have a memory
         * offset so that the bot sees the monsters around it?"). The pool has always had the
         * units, their positions, levels and unit flags; what it does not have is a reaction
         * (friendly/hostile) word, which is why TAB stays first and why a pool pick is only
         * trusted as far as the fight can verify it.
         *
         * The right-CLICK fallback stays gone. It cannot tell a mob from a guard, and on
         * 2026-10-30 17:22 it walked up to a friendly townsman 3.6 yd away and right-clicked
         * him: a right-click is "attack and walk to it", so the "fight" was against the town.
         * A left-click SELECTS and does nothing else, which is a very different proposition.
         * */
        WowBObservedUnit target = default(WowBObservedUnit);
        bool have = false;

        /* What the pool says, before any key is pressed. A radius with nothing alive and
         * level-viable in it is the user's "if no monsters are around THEN move to next
         * hotspot", and answering it here is also what stops the eight-times-in-eight-seconds
         * "the only thing the client offered was a corpse" loop: the corpse is not in this
         * count, so an area holding only a body answers "clear" without pressing TAB once. */
        WowBObservedUnit nearest;
        double nearestYd;
        int live, unread;
        bool haveNearest = NearestKillable(snap, px, py, SearchYd, out nearest, out nearestYd,
                                          out live, out unread);
        if (!haveNearest && unread == 0)
        {
            _scanWasClear = true;
            if (unchecked(Environment.TickCount - _clearQuietAt) > 15000)
            {
                _clearQuietAt = Environment.TickCount;
                report = string.Format(CultureInfo.InvariantCulture,
                    "nothing alive and level-viable within {0:F0} yd of the character ({1} unit(s) in the pool "
                    + "inside that radius, none of them a fight) - the area is clear", SearchYd, near);
            }
            return false;
        }
        _scanWasClear = false;

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

        /* TAB only considers what is in FRONT of the character (the 16:07 run logged
         * "behind the camera" for two and a half minutes because of that). The pool knows where
         * the nearest live unit is, so turn onto it before asking - a turn costs no walk, and it
         * is what turns a mob standing behind the character from a "nothing offered" into a
         * target. Nothing is walked yet: this is a facing change only. */
        if (!haveNearest)
            NearestKillable(snap, px, py, SearchYd, out nearest, out nearestYd, out live, out unread);
        if (haveNearest && nearestYd > 2.0)
            ClickAimKeySteer.Face(nearest.Location.X, nearest.Location.Y, hwnd, 25.0, 4);

        Styx.WoWInternals.WowBReader? selReader = ObjectManager.WowB;

        /* The walk may already have selected the mob it stopped for (TryPullOnTheWay presses
         * TAB once to ask the client who is attackable). Take that selection when it passes the
         * same tests, instead of pressing TAB again and cycling PAST the very mob the walk
         * stopped for. */
        if (TryCurrentSelection(snap, selReader, px, py, out target))
        {
            have = true;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] going with the target already selected: {0} ({1:F1} yd)", target.Guid,
                ClickAimMath.Len(target.Location.X - px, target.Location.Y - py)));
        }

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

        /* ---- step 3: the pool, and a left-click to select it ----
         *
         * TAB had nothing live to offer, but the unit pool says something alive and inside the
         * level band is standing <nearestYd> yd away. Ask the client to select it the only other
         * way a player can: a LEFT-click on it. A left-click selects and does nothing else - it
         * is not an attack order and not a walk order - and FindUnitPixel only reports success
         * when the client's own target guid answers with that unit, so a probe that lands on the
         * ground costs nothing but the probe.
         *
         * The last check is the fight itself: a friendly NPC takes no damage from anything and
         * the fight gives up on it after a few presses and blacklists it, so a town costs a
         * couple of wasted presses rather than a reputation. */
        if (!have && haveNearest && nearestYd <= PoolSelectYd
            && unchecked(Environment.TickCount - _lastPoolSelectMs) >= PoolSelectMs)
        {
            _lastPoolSelectMs = Environment.TickCount;

            uint nearestLevel = 0;
            Styx.WoWInternals.WowBReader? poolReader = ObjectManager.WowB;
            Styx.WoWInternals.WowBReader.WowBUnitFields nearestFields;
            if (poolReader != null && poolReader.TryGetUnitFields(nearest.UnitAddress, out nearestFields))
                nearestLevel = nearestFields.Level;

            int fx, fy;
            string how;
            bool selected = FindUnitPixel(hwnd, nearest, px, py, out fx, out fy, out how);
            if (selected && Capture(out snap) && snap != null && snap.TryGetSelectedTarget(out target))
            {
                have = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] TAB offered nothing live, so the pool was used: {0} is alive at {1:F1} yd "
                    + "(level {2}) and the client selected it on a left-click at {3},{4} px",
                    target.Guid, nearestYd, nearestLevel, fx, fy));
            }
            else
            {
                if (!_poolMisses.Add(nearest.Guid))
                {
                    /* Second miss: stop letting this one keep the hotspot open. */
                    _notViable.Add(nearest.Guid);
                    if (_notViable.Count > 256) _notViable.Clear();
                }
                if (unchecked(Environment.TickCount - _poolQuietAt) > 15000)
                {
                    _poolQuietAt = Environment.TickCount;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] the pool has {0} alive at {1:F1} yd (level {2}) and the left-click search "
                        + "could not put the cursor on it: {3}", nearest.Guid, nearestYd, nearestLevel,
                        how));
                }
            }
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
                /* Once per 15 s, not once per attempt: the 13:24 run printed this eight times
                 * in eight seconds while the character stood over a body it had looted. */
                if (unchecked(Environment.TickCount - _clearQuietAt) > 15000)
                {
                    _clearQuietAt = Environment.TickCount;
                    report = "the only thing the client offered was a corpse (health 0) - walking on "
                             + "without touching it";
                }
                return false;
            }

            /* Nothing selected: TAB offered nothing, and the pool's own candidate could not be
             * clicked. In a town that is the whole population - 27 friendly units within 40 yd at
             * 17:22, none of them an enemy - so this is a correct answer there too. */
            if (unchecked(Environment.TickCount - _clearQuietAt) > 15000)
            {
                _clearQuietAt = Environment.TickCount;
                report = string.Format(CultureInfo.InvariantCulture,
                    "{0} unit(s) within {1:F0} yd; {2} alive and level-viable, but TAB selected none of "
                    + "them and the pool's nearest could not be selected either - nothing attackable here, "
                    + "the walk continues", near, SearchYd, live);
            }
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
    /// Whether the client's current selection is a LIVE unit within reach - the cheapest
    /// possible target, because selecting it costs no key at all. A corpse is not one (that is
    /// what left the bot fighting the same dead mob three times), and neither is anything
    /// further than <see cref="PullYd"/>: a stale selection from an earlier leg must not turn
    /// into a walk across the field.
    /// </summary>
    private static bool TryCurrentSelection(WowBWorldSnapshot snap, Styx.WoWInternals.WowBReader? reader,
                                            double px, double py, out WowBObservedUnit target)
    {
        target = default(WowBObservedUnit);
        if (reader == null) return false;
        if (!snap.TryGetSelectedTarget(out target)) return false;
        if (target.IsLocalPlayer) return false;

        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        if (!reader.TryGetUnitFields(target.UnitAddress, out f)) return false;
        if (f.Health == 0) return false;

        double d = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        return d <= PullYd;
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

        if (NearestFighting(snap, me.Location.X, me.Location.Y, radius, out unit, out d))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] {0} is {1:F1} yd away and in combat - stopping the walk to deal with it", unit.Guid, d));
            return true;
        }

        /* Nothing is fighting us - but that is not the same as "nothing here worth killing",
         * and treating the two as one is why the character walked past mobs on every leg and
         * only fought at the hotspots (the user's report). Ask the client instead of guessing:
         * TAB applies its own hostility filter, so whatever it selects is attackable by
         * definition, and the level band and the distance are checked here. */
        if (TryPullOnTheWay(hwnd, me)) return true;

        /* TAB can only offer what is in front of the character, so a mob beside or behind it
         * leaves the walk running. The unit pool has no such blind spot: if something alive and
         * level-viable is standing within the short radius, stop the walk and let the main loop
         * do the selecting - the left-click search that selects a pool unit takes seconds and
         * does not belong on the route thread. */
        WowBObservedUnit poolUnit;
        double poolYd;
        int poolLive, poolUnread;
        if (NearestKillable(snap, me.Location.X, me.Location.Y, PoolSelectYd, out poolUnit, out poolYd,
                            out poolLive, out poolUnread))
        {
            if (unchecked(Environment.TickCount - _poolQuietAt) > 5000)
            {
                _poolQuietAt = Environment.TickCount;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] the pool has {0} alive and level-viable {1:F1} yd away - stopping the walk to "
                    + "deal with it", poolUnit.Guid, poolYd));
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// While walking, looks for an attackable mob near the path and reports whether the walk
    /// should stop for it.
    ///
    /// Why TAB and not the snapshot: the snapshot has positions, levels and flags but no
    /// reaction word, so it cannot tell a wolf from a guard - the 17:22 run right-clicked a
    /// friendly townsman on exactly that mistake. TAB is the client's own enemy filter, it costs
    /// one key and one read, and if it selects nothing the walk simply continues.
    ///
    /// Throttled: one probe every <see cref="PullProbeMs"/>, so a leg is not a TAB machine gun,
    /// and the probe is skipped while the interrupt quiet period is running (the caller sets it
    /// after a pull that did not turn into a fight).
    /// </summary>
    private static bool TryPullOnTheWay(IntPtr hwnd, WowBObservedUnit me)
    {
        if (unchecked(Environment.TickCount - _lastPullProbeMs) < PullProbeMs) return false;
        _lastPullProbeMs = Environment.TickCount;

        /* What is already selected costs no key: if it is good enough, take it. TAB would move
         * the selection to the NEXT enemy, which is how a probe can walk past the mob it was
         * looking at. */
        WowBWorldSnapshot? snap;
        WowBObservedUnit current;
        if (Capture(out snap) && snap != null && snap.TryGetSelectedTarget(out current))
        {
            string selectedWhy;
            double selectedYd;
            if (PullWorthTaking(current, me, out selectedWhy, out selectedYd))
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} is {1:F1} yd off the path and attackable - stopping the walk to pull it",
                    current.Guid, selectedYd));
                return true;
            }
        }

        ClickAimInput.TapKey(hwnd, TabVk, 40);
        Thread.Sleep(TabSettleMs);

        WowBWorldSnapshot? after;
        if (!Capture(out after) || after == null) return false;

        /* The local player moved while the probe was in flight: read the distance from the
         * fresh snapshot rather than from the one that started the probe. */
        WowBObservedUnit now;
        if (!after.TryGetLocalPlayer(out now)) return false;

        WowBObservedUnit candidate;
        if (!after.TryGetSelectedTarget(out candidate)) return false;

        string why;
        double d;
        if (!PullWorthTaking(candidate, now, out why, out d))
        {
            /* Say why only when the client DID offer something - a probe that found nothing is
             * the normal case on a quiet stretch and does not deserve a log line. */
            if (why.Length > 0 && unchecked(Environment.TickCount - _pullQuietAt) > 15000)
            {
                _pullQuietAt = Environment.TickCount;
                Logging.Write("[Grind] TAB offered a target while walking, not taking it: " + why);
            }
            return false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0} is {1:F1} yd off the path and attackable - stopping the walk to pull it",
            candidate.Guid, d));
        return true;
    }

    /// <summary>
    /// Whether a unit the client just offered is worth interrupting a walk for: alive, not a
    /// player, not one that has already been given up on, inside the level band, and within
    /// <see cref="PullYd"/>.
    /// </summary>
    private static bool PullWorthTaking(WowBObservedUnit candidate, WowBObservedUnit me,
                                        out string why, out double yards)
    {
        why = "";
        yards = double.MaxValue;

        if (candidate.IsLocalPlayer) return false;
        if (candidate.HasPlayerVtable) { why = "the client offered a PLAYER"; return false; }
        if (Refused(candidate.Guid) || _notViable.Contains(candidate.Guid)) return false;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return false;

        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        if (!reader.TryGetUnitFields(candidate.UnitAddress, out f)) return false;
        if (f.Health == 0) return false;                       // a corpse: never worth a walk

        yards = ClickAimMath.Len(candidate.Location.X - me.Location.X,
                                 candidate.Location.Y - me.Location.Y);
        if (yards > PullYd) return false;

        int myLevel = PlayerLevel();
        if (!LevelViable(f.Level, myLevel))
        {
            why = string.Format(CultureInfo.InvariantCulture,
                "it is level {0} and this character is level {1}", f.Level, myLevel);
            return false;
        }

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

        /* Two clear scans in a row end the hotspot. One is not enough: a single read can miss a
         * mob that the client is still streaming in, and the whole point of standing here is that
         * mobs walk past. Two scans a second and a bit apart cannot both miss the same wolf. */
        int clearScans = 0;

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
                clearScans = 0;               // something was here: this hotspot is not finished
                continue;
            }
            if (report.Length > 0) Logging.Write("[Grind] " + report);

            /* "_scanWasClear" is the pool's own answer, not a string match: it is set only when
             * the snapshot had nothing alive and level-viable inside the radius. Walk on the
             * moment that answer is stable. */
            if (_scanWasClear && ++clearScans >= 2)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] nothing left within {0:F0} yd after being here {1:F0} s - moving to the next hotspot",
                    SearchYd, unchecked(Environment.TickCount - dwellStart) / 1000.0));
                break;
            }
            if (!_scanWasClear) clearScans = 0;
            Thread.Sleep(AreaRescanMs);
        }

        /* Anything left lying around while standing here is worth the walk. */
        string loot = LootNearby(hwnd, 3);

        if (fought == 0 && loot.Length == 0) return "";
        if (fought == 0) return loot;
        return string.Format(CultureInfo.InvariantCulture,
            "stood at the hotspot and fought {0} time(s), {1} of them ending as kills{2}",
            fought, killed, loot.Length == 0 ? "" : "; " + loot);
    }

    /// <summary>
    /// Presses the self-buffs on the bar once per run: Mark of the Wild, Thorns, Arcane
    /// Intellect, Power Word: Fortitude - whatever this class has. The client's buff list is not
    /// readable on this build, so "is it already up" cannot be asked; once per run is the honest
    /// version of the question, and a duplicate buff costs one global cooldown.
    /// </summary>
    private static string ApplyBuffsIfNeeded(IntPtr hwnd)
    {
        if (_buffsApplied || _stop || !ClickAimSettings.UseBuffs) return "";

        /* Out of combat only: a buff is a cast, and a cast in a fight is a wasted global. The
         * flag stays false until a quiet moment actually happens, so a run that starts next to
         * a mob still gets its buffs on the first calm iteration. */
        if (PlayerInCombat() || UnderAttack()) return "";
        _buffsApplied = true;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return "";

        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        var applied = new List<string>();
        for (int i = 0; i < bar.Count && applied.Count < 2 && !_stop; i++)
        {
            if (!bar[i].IsBuff) continue;
            ClickAimInput.TapKey(hwnd, bar[i].KeyVk, 40);
            _presses++;
            Thread.Sleep(ClickAimCombat.CastSettleMs);
            applied.Add(bar[i].Name + " (slot " + bar[i].Slot + " '"
                        + ClickAimCombat.KeyLabel(bar[i].KeyVk) + "')");
        }

        if (applied.Count == 0) return "";
        return "buffed with " + string.Join(", ", applied.ToArray());
    }

    // ------------------------------------------------------------------ the heal's price

    /// <summary>
    /// Whether there is enough power to pay for a heal.
    ///
    /// Two rules, and both come from a real failure. The user's character was below 60% health
    /// with no mana and the rotation kept pressing Healing Touch: every press failed for want of
    /// mana, every press ate a global cooldown, and the mob finished the character off. So:
    ///
    ///  * power must be at least <see cref="ClickAimSettings.HealMinManaPercent"/> of the best
    ///    maximum this session has seen (the max-power word reads 100 for 139 power on this
    ///    build, hence the peak rather than the field);
    ///  * and, once a heal has been seen to cost something, power must cover that cost with a
    ///    little to spare - a heal that leaves nothing behind is a heal that stops the fight.
    ///
    /// When this says no, the rotation does NOT heal. It fights with auto-attack and anything
    /// still affordable, which is what a player does.
    /// </summary>
    private static bool HealPowerOk(uint power, uint maxPower)
    {
        /* The price rule first, and it does not depend on a maximum being readable. The
         * 22:47 run is why: the max-power word read 0 that session, the old code took "no
         * denominator" for "no opinion" and returned true, and a shaman with 13 power pressed
         * Healing Wave twice while a mob took it apart. A heal whose cost has been measured is
         * a heal whose affordability can be answered outright. */
        if (_healCostGuess > 0 && power < _healCostGuess + _healCostGuess / 4)
            return false;

        uint denom = DenomPower(maxPower);
        if (denom == 0)
        {
            /* No maximum and no price learned yet: the only honest rule left is that a heal
             * needs power to exist at all, so that the press is not a guaranteed refusal. */
            return power > 0;
        }

        if (ClickAimSettings.HealMinManaPercent > 0
            && power * 100.0 / denom < ClickAimSettings.HealMinManaPercent)
            return false;

        return true;
    }

    /// <summary>
    /// The denominator for any power percentage: the recorded maximum, else the highest value
    /// seen this session, else the value in hand. The max-power word is a candidate on this
    /// build and has been seen reading both 100 for 139 power and 0 - the peak is what makes a
    /// percentage mean something either way.
    /// </summary>
    private static uint DenomPower(uint maxPower)
    {
        uint denom = maxPower;
        if (denom < _manaPeak) denom = _manaPeak;
        return denom;
    }

    /// <summary>
    /// Power needed to cast <paramref name="spell"/>: the cost learned from a cast that worked,
    /// plus a margin, or <paramref name="fallback"/> while nothing is known yet.
    ///
    /// This is what makes the character auto-attack when its mana is gone. The 22:47 run kept
    /// casting Lightning Bolt at 13 power because the only gate was `power >= 10`: every press
    /// was refused for want of mana, and the mob was never answered with a swing.
    /// </summary>
    private static uint PowerForCast(ClickAimAction? spell, uint fallback)
    {
        if (spell == null) return fallback;
        uint cost;
        if (!_castCosts.TryGetValue(spell.Id, out cost) || cost == 0) return fallback;
        return cost + cost / 5;
    }

    /// <summary>
    /// The spell just pressed and the power in hand before it, waiting for the next read.
    ///
    /// On this client the cost is taken from the bar at the END of the cast, and the poll that
    /// notices the mob's health move can land before that: every Lightning Bolt in the 22:47 log
    /// printed "mana 150 -> 150, 135 -> 135" because the drop appeared one read later. So the
    /// press is remembered and the price is measured on the next look.
    /// </summary>
    private static int _pendingCastId;
    private static uint _pendingCastMana;

    /// <summary>Remembers what a spell costs, from a cast that spent power.</summary>
    private static void LearnCastCost(int spellId, uint before, uint after)
    {
        if (spellId <= 0 || before <= after) return;
        uint spent = before - after;
        uint known;
        if (!_castCosts.TryGetValue(spellId, out known) || spent > known) _castCosts[spellId] = spent;
    }

    /// <summary>
    /// Remembers what one heal costs. The evidence is any press where power actually dropped -
    /// a heal the mob out-damaged still proves what it costs, and learning from it early is what
    /// stops the next press from being made with power that cannot cover it.
    /// </summary>
    private static void LearnHealCost(uint before, uint after)
    {
        if (before <= after) return;                        // nothing was spent: no evidence
        uint spent = before - after;
        // Keep the largest single drop: a crit heal or a cheap rank would understate the price.
        if (spent > _healCostGuess) _healCostGuess = spent;
    }

    // ------------------------------------------------------------------ eating and drinking

    /// <summary>
    /// Health as a percentage, or -1 when it cannot be read. Max health is a real field on this
    /// build (every log line agrees: 131/131), so it is used directly.
    /// </summary>
    private static int HealthPercent()
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return -1;

        WowBWorldSnapshot? snap;
        WowBObservedUnit me;
        if (!Capture(out snap) || snap == null || !snap.TryGetLocalPlayer(out me)) return -1;

        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        if (!reader.TryGetUnitFields(me.UnitAddress, out f)) return -1;

        if (f.Health > _healthPeak) _healthPeak = f.Health;
        uint max = f.MaxHealth > 0 ? f.MaxHealth : _healthPeak;
        if (max == 0) return -1;

        int percent = (int)Math.Round(f.Health * 100.0 / max);
        return percent > 100 ? 100 : percent;
    }

    /// <summary>
    /// Mana as a percentage, or -1 when it cannot be read.
    ///
    /// The max-power word is a CANDIDATE on this build and it lies: every log line prints
    /// "pow=139/100" for a character whose mana bar is full at 139. A percentage over that 100
    /// would read 139% and the drink rule would never fire, so the denominator is the larger of
    /// the field and the highest value seen this session.
    /// </summary>
    private static int ManaPercent()
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return -1;

        WowBWorldSnapshot? snap;
        WowBObservedUnit me;
        if (!Capture(out snap) || snap == null || !snap.TryGetLocalPlayer(out me)) return -1;

        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        if (!reader.TryGetUnitFields(me.UnitAddress, out f)) return -1;

        if (f.Power > _manaPeak) _manaPeak = f.Power;
        uint max = f.MaxPower;
        if (max < _manaPeak) max = _manaPeak;
        if (max == 0) return -1;

        int percent = (int)Math.Round(f.Power * 100.0 / max);
        return percent > 100 ? 100 : percent;
    }

    /// <summary>
    /// Eats and drinks between fights, at the percentages from the settings, and never while
    /// something is on the character.
    ///
    /// Both are ordinary items on the action bar - slot 11 held Refreshing Spring Water and slot
    /// 12 a Shiny Red Apple on the character this was written for - so this is a key press like
    /// everything else, and the client does the sitting. Nothing moves during the wait: in this
    /// client any movement cancels a meal, which is why the walk is stopped first and the loop
    /// does not touch the movement keys until the rest is over.
    /// </summary>
    private static string RestIfNeeded(IntPtr hwnd)
    {
        if (_stop) return "";
        if (ClickAimSettings.EatBelowPercent <= 0 && ClickAimSettings.DrinkBelowPercent <= 0) return "";
        if (PlayerInCombat() || UnderAttack()) return "";

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return "";

        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        ClickAimAction? food = ClickAimCombat.FindFood(bar, ClickAimSettings.FoodSlot);
        ClickAimAction? drink = ClickAimCombat.FindDrink(bar, ClickAimSettings.DrinkSlot);

        int health = HealthPercent();
        int mana = ManaPercent();

        bool wantFood = food != null && ClickAimSettings.EatBelowPercent > 0
                        && health >= 0 && health <= ClickAimSettings.EatBelowPercent;
        bool wantDrink = drink != null && ClickAimSettings.DrinkBelowPercent > 0
                         && mana >= 0 && mana <= ClickAimSettings.DrinkBelowPercent;

        if (!wantFood && !wantDrink)
        {
            /* Say why once, when a rule is close to firing and cannot: the usual reason is that
             * the item is not on the bar, which is a thing the user can fix in one drag. */
            if (food == null && ClickAimSettings.EatBelowPercent > 0 && health >= 0
                && health <= ClickAimSettings.EatBelowPercent)
                return string.Format(CultureInfo.InvariantCulture,
                    "health is {0}% and the rule says eat, but there is no food item on the action bar "
                    + "(put food on a slot, or set FoodSlot with the Combat settings button)", health);
            if (drink == null && ClickAimSettings.DrinkBelowPercent > 0 && mana >= 0
                && mana <= ClickAimSettings.DrinkBelowPercent)
                return string.Format(CultureInfo.InvariantCulture,
                    "mana is {0}% and the rule says drink, but there is no drink item on the action bar "
                    + "(put a drink on a slot, or set DrinkSlot with the Combat settings button)", mana);
            return "";
        }

        ClickAimKeySteer.ReleaseHeld();
        StopAndSettle(hwnd);

        var said = new List<string>();
        if (wantFood)
        {
            ClickAimInput.TapKey(hwnd, food!.KeyVk, 40);
            _presses++;
            said.Add(string.Format(CultureInfo.InvariantCulture,
                "ate {0} (slot {1} '{2}') at {3}% health", food.Name, food.Slot,
                ClickAimCombat.KeyLabel(food.KeyVk), health));
        }
        if (wantDrink)
        {
            ClickAimInput.TapKey(hwnd, drink!.KeyVk, 40);
            _presses++;
            said.Add(string.Format(CultureInfo.InvariantCulture,
                "drank {0} (slot {1} '{2}') at {3}% mana", drink.Name, drink.Slot,
                ClickAimCombat.KeyLabel(drink.KeyVk), mana));
        }

        /* Sit and let it work, the way a player does: eat and drink to NEARLY full, not to one
         * point past the threshold. The first version stopped the moment the rule cleared, so a
         * character at 34% mana drank for 6 s, reached 39%, walked off, and drank again at the
         * next gap - five meals where one would do. RestFullPercent is the "full enough" line;
         * RestMs is only the ceiling for a character that regens too slowly to get there. */
        int started = Environment.TickCount;
        int afterHealth = health;
        int afterMana = mana;
        while (unchecked(Environment.TickCount - started) < ClickAimSettings.RestMs && !_stop)
        {
            Thread.Sleep(500);
            afterHealth = HealthPercent();
            afterMana = ManaPercent();

            bool healthOk = !wantFood || afterHealth < 0 || afterHealth >= RestFullPercent;
            bool manaOk = !wantDrink || afterMana < 0 || afterMana >= RestFullPercent;
            if (healthOk && manaOk) break;
            if (UnderAttack()) break;
        }

        return string.Join("; ", said.ToArray()) + string.Format(CultureInfo.InvariantCulture,
            " -> health {0}%, mana {1}% after {2:F1} s", afterHealth, afterMana,
            unchecked(Environment.TickCount - started) / 1000.0);
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
        /* Get within reach first, and mean it. A right-click on a body further away is a walk
         * order, not a loot - the 18:44 run spent a whole loot window on that. */
        int walkStart = Environment.TickCount;
        while (unchecked(Environment.TickCount - walkStart) < LootWalkMs && !_stop)
        {
            double px, py;
            if (!ClickAimPositions.TryRead(out px, out py)) break;
            double d = ClickAimMath.Len(corpse.Location.X - px, corpse.Location.Y - py);
            if (d <= LootReachYd) break;
            if (UnderAttack()) return LootUnderAttack;
            double remaining;
            ClickAimKeySteer.Steer(corpse.Location.X, corpse.Location.Y, hwnd, true, out remaining);
            Thread.Sleep(90);
        }

        ClickAimKeySteer.ReleaseHeld();
        StopAndSettle(hwnd);

        double lx, ly;
        if (!ClickAimPositions.TryRead(out lx, out ly)) return LootNoClick;

        double reach = ClickAimMath.Len(corpse.Location.X - lx, corpse.Location.Y - ly);
        if (reach > LootReachYd + 1.0)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Loot] could not get to the body of {0} - still {1:F1} yd away and the reach is "
                + "{2:F0} yd, so no click was sent (a right-click from here is a walk order)",
                corpse.Guid, reach, LootReachYd));
            return LootNoClick;
        }

        ClickAimKeySteer.Face(corpse.Location.X, corpse.Location.Y, hwnd, 30.0, 4);

        /* One probe per session: five windows of the corpse's own object, read before and after
         * a click the client CONFIRMED selected the body. */
        bool probe = !_lootProbed && !_clickBiasKnown;
        uint[][]? before = probe ? CaptureWords(corpse) : null;

        string how;
        if (!TryClickUnit(hwnd, corpse, lx, ly, true, out how))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Loot] could not reach the body of {0} with the mouse: {1}", corpse.Guid, how));
            return LootNoClick;
        }

        _clicks++;
        _lootProbed = true;

        /* ONE click per body, and this is the reason. The 18:57 run looted four corpses and then
         * clicked every one of them a second time, 2.2 s later, because the retry loop could not
         * tell "the click landed and these are bones" from "the click missed" - and a right-click
         * on a body also SELECTS it, so the user watched the bot pick the corpse it had just
         * looted back up. A left-click probe has already proved the click lands on the body, so
         * there is nothing left for a retry to establish. */
        int result = 0;
        for (int waited = 0; waited < LootSettleMs && !_stop; waited += 300)
        {
            Thread.Sleep(300);
            if (!BodyStillThere(corpse.Guid))
            {
                result = 1;
                break;
            }
        }

        if (before != null)
        {
            Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
            if (reader != null) DescribeWordDiff(reader, corpse, before);
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Loot] the right-click on {0} landed ({1}); {2}", corpse.Guid, how,
            result > 0 ? "the body is gone, so the client took it" : "the body is still in the pool as bones"));

        /* Leave the screen the way a player would: nothing targeted. The body is still selected
         * by the loot click, and a left-click on ground the search already proved empty clears
         * the target without moving, attacking, or opening anything. */
        DeselectIfPossible(hwnd);

        return result;
    }

    /// <summary>
    /// Clears the current target with a left-click on a pixel a probe found to be empty ground.
    /// A left-click there deselects and does nothing else; there is no ESC in this client that
    /// does not open the Game Menu.
    /// </summary>
    private static void DeselectIfPossible(IntPtr hwnd)
    {
        if (!_emptyPixelKnown) return;
        ClickAimInput.LeftClick(hwnd, _emptyPixelX, _emptyPixelY);
        Thread.Sleep(120);
    }

    /// <summary>The click found no point on the body at all.</summary>
    private const int LootNoClick = -2;

    /// <summary>Something started hitting the character while the loot was being reached.</summary>
    private const int LootUnderAttack = -1;

    // ---- the rotation's memory across one fight -------------------------------------------

    /// <summary>Target the damage-over-time spell was last put on.</summary>
    private static WowBGuid _dotGuid;

    /// <summary>When that DoT is assumed to have run out (see ClickAimSettings.DotRefreshSeconds).</summary>
    private static int _dotUntil;

    /// <summary>
    /// The highest mana this session has seen. The max-power word on this build is a candidate
    /// and reads 100 while the character has 139 (every log line says "pow=139/100"), so a
    /// percentage taken from it would be over 100% and the drink rule would never fire. The peak
    /// is the honest denominator: mana only goes up while resting or from a level-up, both of
    /// which the peak follows within one read.
    /// </summary>
    private static uint _manaPeak;
    private static uint _healthPeak;

    /// <summary>Buffs are pressed once per run; see <see cref="ApplyBuffsIfNeeded"/>.</summary>
    private static bool _buffsApplied;

    /// <summary>
    /// What the heal has been seen to cost, in power. Learned from the first heal that actually
    /// worked (power before minus power after), because this build exposes no spell costs and a
    /// guess is what makes a character press a heal it cannot pay for.
    /// </summary>
    private static uint _healCostGuess;

    /// <summary>
    /// What each damage spell has been seen to cost, by spell id. Same reasoning as the heal,
    /// and it is what stops the character from casting with power it does not have: below the
    /// price of the spell the rotation stops trying and swings instead.
    /// </summary>
    private static readonly Dictionary<int, uint> _castCosts = new Dictionary<int, uint>();

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
    /// The nearest thing in the unit pool worth walking to: alive, inside the level band, not a
    /// player, not already refused, and not carrying <see cref="NonAttackableFlag"/> - plus the
    /// counts a caller needs to tell "nothing here" from "I could not read anything here":
    /// <paramref name="live"/> is how many units passed every filter inside the radius, and
    /// <paramref name="unread"/> is how many were inside it but would not read at all.
    ///
    /// This is the "does the bot see the monsters around it" half of the 13:23 report. The pool
    /// has had positions, levels and unit flags since the 2026-10-30 dumps, so it can answer
    /// "something alive and level-appropriate is 62 yd away, and it is the nearest one" without
    /// a key press. It cannot answer "is it a wolf": there is no reaction/faction word in this
    /// snapshot yet (WowBObservedUnit says so in its own summary),
    /// which is why TAB is asked FIRST and this is only the fallback.
    /// </summary>
    private static bool NearestKillable(WowBWorldSnapshot snap, double px, double py, double radius,
                                        out WowBObservedUnit unit, out double yards,
                                        out int live, out int unread)
    {
        unit = default(WowBObservedUnit);
        yards = double.MaxValue;
        live = 0;
        unread = 0;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        int myLevel = PlayerLevel();

        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit candidate = snap.Units[i];
            if (candidate.IsLocalPlayer) continue;
            if (candidate.HasPlayerVtable) continue;
            if (Refused(candidate.Guid)) continue;
            if (_notViable.Contains(candidate.Guid)) continue;

            double d = ClickAimMath.Len(candidate.Location.X - px, candidate.Location.Y - py);
            if (d > radius) continue;

            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (reader == null || !reader.TryGetUnitFields(candidate.UnitAddress, out f))
            {
                /* A unit whose block will not read is not an empty area. Counting it as
                 * "unread" keeps a failed read from switching the whole scan off. */
                unread++;
                continue;
            }

            if (f.Health == 0) continue;                       // a corpse is not a monster
            if ((f.UnitFlags & NonAttackableFlag) != 0) continue;
            if (!LevelViable(f.Level, myLevel)) continue;

            live++;
            if (d > 0.4 && d < yards)
            {
                unit = candidate;
                yards = d;
            }
        }

        return live > 0 && yards < double.MaxValue;
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
    /// <summary>Most left-click probes one search may spend looking for a body.</summary>
    private const int ClickProbeMax = 40;

    /// <summary>How long a probe waits for the client to answer with a target.</summary>
    private const int ClickProbeSettleMs = 70;

    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,
                                     bool mustSelect, out string how)
    {
        how = "";

        if (!mustSelect)
        {
            int dx, dy;
            if (!TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z,
                            out dx, out dy))
            {
                how = "the projected point is behind the camera or off the screen";
                return false;
            }
            ClickAimInput.Click(hwnd, dx + (_clickBiasKnown ? _clickBiasX : 0),
                                dy + (_clickBiasKnown ? _clickBiasY : 0));
            return true;
        }

        /* mustSelect: FIND the body with left-click probes, then right-click the pixel that
         * provably has it. Guessing a right-click point and hoping was the wrong shape of
         * thing - the 18:50 run spent eleven right-clicks on a body 4 yd away, every one of
         * which either hit the character's own model or the ground, and there was no way to
         * tell from the log which. */
        int fx, fy;
        if (FindUnitPixel(hwnd, unit, px, py, out fx, out fy, out how))
        {
            ClickAimInput.Click(hwnd, fx, fy);
            return true;
        }

        /* The one case a left-click cannot confirm: the unit is ALREADY the target, so the
         * target guid cannot change and every probe looks like a miss. Right-click the
         * projected point anyway - it is a best effort and the caller is told so. */
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null || reader.ReadTargetGuid() != unit.Guid) return false;

        int bx, by;
        if (!TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z, out bx, out by))
            return false;
        ClickAimInput.Click(hwnd, bx + (_clickBiasKnown ? _clickBiasX : 0),
                            by + (_clickBiasKnown ? _clickBiasY : 0));
        how += "; it was already the target, so this right-click went to the projected point unverified";
        return true;
    }

    /// <summary>
    /// Finds the screen pixel a unit is under, using LEFT clicks and the client's own target
    /// guid as the answer. A left-click selects a unit and does nothing else, so a probe that
    /// misses costs nothing: no walk, no attack, no key.
    ///
    /// The search starts on the projected point (corrected by any offset already learned) and
    /// spirals outward in rings, because a body a few yards ahead of the character projects
    /// onto the character's own model and the error is measured in hundreds of pixels, not
    /// tens. When a probe selects the unit, the difference between that pixel and the projected
    /// one is kept as the session's offset, so later searches start from the right place.
    ///
    /// Returns offsets from the client centre, the same space <see cref="ClickAimInput.Click"/>
    /// takes.
    /// </summary>
    private static bool FindUnitPixel(IntPtr hwnd, WowBObservedUnit unit, double px, double py,
                                      out int foundX, out int foundY, out string how)
    {
        foundX = 0;
        foundY = 0;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null)
        {
            how = "the client reader is gone";
            return false;
        }

        if (reader.ReadTargetGuid() == unit.Guid)
        {
            how = "it is already the target, so a left-click cannot confirm a pixel for it";
            return false;
        }

        int baseDx, baseDy;
        bool projected = TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z,
                                    out baseDx, out baseDy);
        if (!projected)
        {
            baseDx = 0;
            baseDy = 0;
        }

        int centreX = baseDx + (_clickBiasKnown ? _clickBiasX : 0);
        int centreY = baseDy + (_clickBiasKnown ? _clickBiasY : 0);

        int[] radii = { 0, 40, 80, 130, 190, 260 };
        int probes = 0;
        for (int r = 0; r < radii.Length; r++)
        {
            int radius = radii[r];
            int steps = radius == 0 ? 1 : 8;
            for (int s = 0; s < steps; s++)
            {
                if (_stop || probes >= ClickProbeMax) break;

                int dx = centreX;
                int dy = centreY;
                if (radius > 0)
                {
                    double angle = (Math.PI * 2.0 / steps) * s;
                    dx += (int)Math.Round(Math.Cos(angle) * radius);
                    dy += (int)Math.Round(Math.Sin(angle) * radius);
                }

                ClickAimInput.LeftClick(hwnd, dx, dy);
                probes++;
                Thread.Sleep(ClickProbeSettleMs);

                WowBGuid after = reader.ReadTargetGuid();
                if (after != unit.Guid)
                {
                    /* Nothing selected after the click: that pixel is ground (or blank UI), which
                     * makes it a safe place to deselect later - see DeselectIfPossible. */
                    if (after.IsEmpty && !_emptyPixelKnown)
                    {
                        _emptyPixelX = dx;
                        _emptyPixelY = dy;
                        _emptyPixelKnown = true;
                    }
                    continue;
                }

                foundX = dx;
                foundY = dy;
                if (projected)
                {
                    _clickBiasX = dx - baseDx;
                    _clickBiasY = dy - baseDy;
                    _clickBiasKnown = true;
                    how = string.Format(CultureInfo.InvariantCulture,
                        "left-click probe {0} found it {1},{2} px from the projected point; that offset is "
                        + "now used to start every search", probes, _clickBiasX, _clickBiasY);
                }
                else
                {
                    how = string.Format(CultureInfo.InvariantCulture,
                        "left-click probe {0} found it at {1},{2} px after the projection was refused",
                        probes, dx, dy);
                }
                return true;
            }
        }

        how = string.Format(CultureInfo.InvariantCulture,
            "{0} left-click probe(s) found no pixel with that unit under it{1}", probes,
            projected
                ? string.Format(CultureInfo.InvariantCulture, " (search centred {0},{1} px from the screen centre)",
                                centreX, centreY)
                : " (the projection was refused, so the search was centred on the screen)");
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



        _pendingCastId = 0;                 // a cast remembered from the last fight is not evidence
        bool castEverLanded = false;        // a Wrath has demonstrably cast this fight
        bool melee = false;                 // set when casting is shown not to work
        bool announced = false;
        int healFailures = 0;               // presses of the heal key that changed nothing
        bool healBlocked = false;           // no power for it: fight instead of pressing it again
        bool healRefusalLogged = false;
        bool powerShortLogged = false;

        /* Melee bookkeeping: whether the client's swing is running, when it last landed, and
         * when a key or click was last sent. See the melee section of the loop. */
        bool meleeSwinging = false;
        int lastMeleeDamageMs = 0;
        int lastAttackPressMs = 0;
        int lastMeleeClickMs = 0;
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
        ClickAimAction? damage = ClickAimCombat.FirstDamageOrDot(bar);  // Wrath, or Moonfire alone
        ClickAimAction? heal = ClickAimCombat.FirstHeal(bar);           // Healing Touch
        ClickAimAction? auto = ClickAimCombat.AutoAttack(bar);          // Attack (6603)
        ClickAimAction? dot = ClickAimCombat.FirstDot(bar);             // Moonfire, Corruption, ...
        ClickAimAction? meleeHit = ClickAimCombat.FirstMeleeDamage(bar); // Claw, Heroic Strike, ...

        /* How far the damage spell actually reaches. Every spell used to be treated as a 28 yd
         * cast, so a cat stood 25 yd off a mob pressing a 5 yd Claw - the user's "the bot thinks
         * all skills are ranged". A melee ability is not cast at all: the melee half of the
         * rotation closes in, auto-attacks, and presses the key from arm's length. */
        double castRange = damage != null && damage.RangeYd > 0 ? damage.RangeYd : ClickAimCombat.CasterRangeYd;
        double dotRange = dot != null && dot.RangeYd > 0 ? dot.RangeYd : ClickAimCombat.CasterRangeYd;

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
            uint myHealth = 0, myMaxHealth = 0, myMana = 0, myMaxMana = 0;
            Styx.WoWInternals.WowBReader.WowBUnitFields mine;
            if (reader != null && reader.TryGetUnitFields(me.UnitAddress, out mine))
            {
                myHealth = mine.Health;
                myMaxHealth = mine.MaxHealth;
                myMana = mine.Power;
                myMaxMana = mine.MaxPower;      // a candidate on this build: see HealPowerOk
            }

            /* Every read feeds the peak. It used to be fed only while resting, so a fight that
             * began on a full bar had no denominator at all - and an unknown maximum is what
             * let the 22:47 shaman press a heal with 13 power. */
            if (myMana > _manaPeak) _manaPeak = myMana;

            /* The price of the spell pressed last time, measured now (see _pendingCastId). */
            if (_pendingCastId != 0)
            {
                LearnCastCost(_pendingCastId, _pendingCastMana, myMana);
                _pendingCastId = 0;
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
                    "[Grind] rotation: heal at {0}% health and only above {8}% power ({1}), "
                    + "damage {2} at {5:F0} yd{6}, damage over time {3}, auto-attack {4}{7}",
                    ClickAimSettings.HealBelowPercent == 0
                        ? "never"
                        : ClickAimSettings.HealBelowPercent.ToString(CultureInfo.InvariantCulture),
                    heal == null ? "none on the bar" : "slot " + heal.Slot + " '" + ClickAimCombat.KeyLabel(heal.KeyVk) + "' " + heal.Name,
                    damage == null ? "none on the bar" : "slot " + damage.Slot + " '" + ClickAimCombat.KeyLabel(damage.KeyVk) + "' " + damage.Name,
                    !ClickAimSettings.UseDamageOverTime || dot == null
                        ? "off"
                        : "slot " + dot.Slot + " '" + ClickAimCombat.KeyLabel(dot.KeyVk) + "' " + dot.Name
                          + " every " + ClickAimSettings.DotRefreshSeconds.ToString(CultureInfo.InvariantCulture) + " s",
                    auto == null ? "right-click only" : "slot " + auto.Slot + " '" + ClickAimCombat.KeyLabel(auto.KeyVk) + "'",
                    castRange,
                    damage == null || !damage.IsMelee ? ""
                        : " - a melee ability, so the fight closes to arm's length",
                    meleeHit == null ? ""
                        : ", melee ability " + meleeHit.Name + " (slot " + meleeHit.Slot + " '"
                          + ClickAimCombat.KeyLabel(meleeHit.KeyVk) + "')",
                    ClickAimSettings.HealMinManaPercent));
            }

            /* ---- 1. heal, where Singular puts it: before everything else ----
             *
             * ...but only when the character can PAY for it. A heal costs power, and a bot that
             * keeps pressing a heal it cannot afford does not heal: the press fails, the global
             * cooldown is gone, the mob keeps hitting, and the next press fails the same way.
             * That is how the character the user reported died - below 60% health, no mana, and
             * the rotation hammering the heal key while the mob finished it off. Below the line
             * the heal is skipped and the loop carries on to damage and auto-attack, which cost
             * nothing. */
            bool healPowerOk = heal != null && HealPowerOk(myMana, myMaxMana);
            if (heal != null && !healPowerOk && !healBlocked && !healRefusalLogged
                && ClickAimSettings.HealBelowPercent > 0 && myPercent <= ClickAimSettings.HealBelowPercent)
            {
                healRefusalLogged = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] health {0:F0}% is under the heal line and there is {1}/{2} power - not "
                    + "enough for {3} (needs {4}% and a cast's worth), so this fight is fought with "
                    + "auto-attack and whatever still casts", myPercent, myMana,
                    myMaxMana > 0 ? myMaxMana : _manaPeak, heal.Name,
                    ClickAimSettings.HealMinManaPercent));
            }

            if (heal != null && healPowerOk && !healBlocked
                && ClickAimSettings.HealBelowPercent > 0
                && myMaxHealth > 0 && myPercent <= ClickAimSettings.HealBelowPercent
                && unchecked(Environment.TickCount - lastHealMs) >= ClickAimCombat.HealRetryMs)
            {
                lastHealMs = Environment.TickCount;
                meleeSwinging = false;          // a cast stops auto-attack; it must be restarted
                uint powerBeforeHeal = myMana;
                ClickAimInput.TapKey(hwnd, 0x70, 40);                  // F1 = target self (Blizzard default)
                Thread.Sleep(120);
                ClickAimInput.TapKey(hwnd, heal.KeyVk, 40);

                /* Wait for the heal to actually land. Healing Touch is a 1.5 s cast on top
                 * of the retarget, and the previous version read my health once at 1.8 s and
                 * reported "did not lift it (still 12/113)" for heals that were still being
                 * cast - four times in one fight. Poll instead. */
                uint afterHealth = 0;
                uint powerAfterHeal = powerBeforeHeal;
                for (int waited = 0; waited < ClickAimCombat.HealVerifyMs && !_stop; waited += 300)
                {
                    Thread.Sleep(300);
                    WowBWorldSnapshot? healed;
                    if (reader == null || !Capture(out healed) || healed == null) continue;
                    if (healed.TryGetLocalPlayer(out WowBObservedUnit me2)
                        && reader.TryGetUnitFields(me2.UnitAddress, out mine))
                    {
                        afterHealth = mine.Health;
                        powerAfterHeal = mine.Power;
                    }
                    if (afterHealth > myHealth) break;
                }

                if (afterHealth > myHealth)
                {
                    string label = "heal " + heal.Name;
                    if (!worked.Contains(label)) worked.Add(label);
                    healFailures = 0;
                    LearnHealCost(powerBeforeHeal, powerAfterHeal);
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] health {0:F0}% -> {1}/{2} with {3} (slot {4}){5}. Back to the mob",
                        myPercent, afterHealth, myMaxHealth, heal.Name, heal.Slot,
                        _healCostGuess > 0
                            ? " for " + _healCostGuess.ToString(CultureInfo.InvariantCulture) + " power"
                            : ""));
                }
                else
                {
                    /* Two presses that neither lifted health nor spent power is a heal that
                     * cannot be paid for (or a key that cannot cast). Either way pressing it a
                     * third time costs another global cooldown in the middle of a fight the
                     * character is losing, so the heal is off for the rest of this fight. */
                    healFailures++;
                    LearnHealCost(powerBeforeHeal, powerAfterHeal);   // "cast but out-damaged" is still a price
                    bool powerSpent = powerAfterHeal < powerBeforeHeal;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] health {0:F0}% and {1} did not lift it (still {2}/{3}, power {4} -> {5}, "
                        + "{6}) - failure {7} this fight; fighting on instead of pressing it again",
                        myPercent, heal.Name, afterHealth, myMaxHealth, powerBeforeHeal, powerAfterHeal,
                        powerSpent ? "so it cast and the mob out-damaged it" : "the press did nothing at all",
                        healFailures));
                    if (healFailures >= 2)
                    {
                        healBlocked = true;
                        Logging.Write("[Grind] the heal is off for the rest of this fight: two presses "
                                    + "did nothing useful, so the power goes to attacks instead");
                    }
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

            /* ---- 1b. the damage-over-time spell, ONCE per target ----
             *
             * This is what the 19:07 run never did: Moonfire sat on slot 5 for the whole
             * evening while the log printed "spell 8921 (not in the table yet)", because the
             * rotation only ever looked for a direct-damage spell. A DoT is worth a global
             * cooldown once and worthless fifty times, so it is applied per target and then
             * left alone until ClickAimSettings.DotRefreshSeconds has passed. The client's
             * debuff list is not readable on this build, so the timer is our own cast - being
             * a second late costs nothing, being early costs mana. */
            if (dot != null && ClickAimSettings.UseDamageOverTime && !melee
                && (dot.IsMelee || myMana >= PowerForCast(dot, 10)) && d <= dotRange
                && (_dotGuid != target.Guid || unchecked(Environment.TickCount - _dotUntil) >= 0))
            {
                StopAndSettle(hwnd);
                if (!ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, 25.0, 4))
                {
                    Thread.Sleep(120);
                    continue;
                }

                ClickAimInput.TapKey(hwnd, dot.KeyVk, 40);
                presses++;
                _presses++;
                _dotGuid = target.Guid;
                _dotUntil = Environment.TickCount + ClickAimSettings.DotRefreshSeconds * 1000;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} (slot {1}, key '{2}') on {3} at {4:F1} yd - it stays on the target for "
                    + "the next {5} s and is refreshed by a timer, not by spamming it",
                    dot.Name, dot.Slot, ClickAimCombat.KeyLabel(dot.KeyVk), target.Guid, d,
                    ClickAimSettings.DotRefreshSeconds));
                Thread.Sleep(ClickAimCombat.CastSettleMs);
                continue;
            }

            /* ---- 2. reach it: walk to casting range, do not walk PAST it ----
             *
             * The 16:46 run had the character casting and then closing into melee, which is
             * the one thing a caster should not do while it has mana. So: approach only
             * while out of casting range, and stop the moment the target is inside it. */
            /* A melee ability is not cast: it is pressed standing next to the mob, and the
             * melee branch below is what closes the distance and swings. So "can cast" means
             * "there is a spell on the bar that reaches the mob from here". */
            /* Not just "some power left": enough for THIS spell. The cost is learned from a
             * cast that worked (Lightning Bolt costs 15 on the character this was tested on),
             * and below it the character stops casting and swings - which is what the user
             * asked for and what a player does. 10 power was the old gate and it produced
             * cast, refuse, cast, refuse while a mob ate the character. */
            uint needForCast = PowerForCast(damage, 10);
            bool canCast = !melee && damage != null && !damage.IsMelee && myMana >= needForCast;

            if (!canCast && !melee && damage != null && !damage.IsMelee && myMana < needForCast
                && !powerShortLogged)
            {
                powerShortLogged = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} of power left and {1} needs about {2} - not casting again until the "
                    + "bar comes back; auto-attacking instead", myMana, damage.Name, needForCast));
            }

            if (canCast && d > castRange)
            {
                double approach;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out approach);
                Thread.Sleep(90);
                continue;
            }

            if (canCast && d > castRange - 2.0)
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
                meleeSwinging = false;          // a cast stops auto-attack; it must be restarted
                uint manaBefore = myMana;
                int mobBeforeHealth = lastHealth;
                _pendingCastId = damage.Id;
                _pendingCastMana = manaBefore;
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
                    LearnCastCost(damage.Id, manaBefore, manaAfter);
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

                LearnCastCost(damage.Id, manaBefore, manaAfter);   // a refusal costs nothing, a cast does
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

            /* ---- 4. melee: close the distance, then press keys and stand still ----
             *
             * This is the fallback, not a habit: it runs when casting is off the table (no
             * damage spell, not enough power for the one on the bar, or three casts that
             * landed nothing). Walking in is the only movement here; inside AttackRangeYd the
             * character stops, presses Attack, and lets the client's swing do the work. */
            if (!meleeOnce)
            {
                meleeOnce = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] melee from here: casting is {0} (damage spell {1}{2}, power {3}, {4} failed cast(s))",
                    melee ? "switched off for this fight"
                          : damage != null && damage.IsMelee ? "not applicable: the damage spell is a melee ability"
                          : "not available",
                    damage == null ? "none on the bar" : damage.Name,
                    meleeHit == null ? "" : " and " + meleeHit.Name + " to press at arm's length",
                    myMana, castFailures));
            }

            /* Stop the walk a yard and a bit early. A character that walks until it is inside
             * AttackRangeYd is still moving when the key comes up, coasts past the mob, finds
             * itself out of range on the far side and walks back - the ping-pong the user saw
             * as "runs back and forward". Stopping at MeleeEngageYd lets the coast land inside
             * the range instead of through it. */
            if (d > MeleeEngageYd)
            {
                /* Walk in, and keep walking: melee is now the normal half of a level-2
                 * warlock's fight ("18 of power left and Shadow Bolt needs about 30"), so this
                 * is where the character spends most of a kill. The only limit is the give-up
                 * distance, which is the search radius plus a margin - the 13:23 run's "it
                 * skips a monster 40 yards away" was this gate refusing anything past 20 yd
                 * whenever the character was out of mana. */
                double chase = GiveUpYd;
                if (d > chase)
                    return string.Format(CultureInfo.InvariantCulture,
                        "cannot cast at it and it is {0:F0} yd away (the melee chase is {1:F0} yd); "
                        + "giving up on it", d, chase);
                double closer;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out closer);
                Thread.Sleep(90);
                continue;
            }

            /* ---- in melee: swing, do not steer ----
             *
             * The 23:00 run is the reason this section is shaped like this. It did what the
             * old version said to do and looked like a bot having a fit: every pass through
             * the loop re-aimed at the mob (Steer with turn-only), re-faced with the arrow
             * keys, and RIGHT-CLICKED the mob's projected point. A right-click on a mob that is
             * standing on your feet lands on the ground beside it, and a right-click on the
             * ground is a WALK ORDER - so the character ran forward, the distance changed, the
             * loop steered again, and it circled the mob for thirty seconds while auto-attack
             * quietly did the damage. The log is that circle: facing 271 -> 317 -> 245 -> 333
             * -> 175, ninety degrees at a time.
             *
             * So: nothing here moves the character. The walk in is above; inside
             * AttackRangeYd the only things sent are the Attack key, a melee ability key, and -
             * when the swing is NOT running - at most one click every few seconds to start it.
             * And the arrow keys are used only when no swing has landed for a while, because a
             * character that is hurting the mob is already facing it and turning is what broke
             * the last run. */
            ClickAimKeySteer.ReleaseHeld();

            int sinceDamageMs = unchecked(Environment.TickCount - lastMeleeDamageMs);

            /* Is the client's swing still running? It is only known from the result: a swing
             * that has landed the mob's health in the last few seconds is running, and silence
             * for MeleeSwingQuietMs means it has stopped. Everything below keys off this. */
            bool swinging = meleeSwinging && sinceDamageMs < MeleeSwingQuietMs;

            /* Nothing has landed for a few seconds and the mob is still a couple of yards off:
             * the character stopped a shade too early. One short step forward, aimed by the same
             * facing call the walk uses, and no click - a click here is a walk order with no
             * aim and no stop. */
            if (!swinging && sinceDamageMs > MeleeRefaceAfterMs && d > 2.5)
            {
                double nudged;
                ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out nudged);
                Thread.Sleep(220);
                ClickAimKeySteer.ReleaseHeld();
            }

            if (!swinging || sinceDamageMs > MeleeRefaceAfterMs)
            {
                /* Wide tolerance at arm's length: the client's own melee arc is generous and a
                 * precise turn from 3 yd is what produced ninety-degree arrow taps. */
                if (!ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, 45.0, 3))
                {
                    faceFailures++;
                    if (faceFailures == 1 || faceFailures % 5 == 0)
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Grind] cannot turn onto the mob at arm's length ({0:F1} yd) - the arrow keys "
                            + "are not moving the facing field ({1} time(s))", d, faceFailures));
                    Thread.Sleep(150);
                    continue;
                }
            }

            /* Start the swing - ONCE. The Attack action is a TOGGLE on this client: pressing it
             * while auto-attack is already running turns it OFF. The 23:00 run pressed it every
             * 1.2 s on a timer, so the character alternated between starting and stopping its
             * own swing, which is exactly what the user saw.
             *
             * So the rule is now: press it only when the swing is NOT running - i.e. nothing
             * has landed for MeleeSwingQuietMs - and then once, not in a loop. A swing at level
             * 1 lands every two or three seconds, so four seconds of silence means the swing
             * has stopped. The key is preferred to a click because a click at arm's length can
             * be a walk order. */
            bool attacked = false;
            if (auto != null)
            {
                if (!swinging && unchecked(Environment.TickCount - lastAttackPressMs) >= MeleeClickMs)
                {
                    ClickAimInput.TapKey(hwnd, auto.KeyVk, 40);
                    lastAttackPressMs = Environment.TickCount;
                    presses++;
                    _presses++;
                    attacked = true;
                    Logging.Write("[Grind] pressing Attack to start the swing (it stays on until "
                                + "something stops it; pressing it again would turn it off)");
                }
            }
            else if (!swinging && unchecked(Environment.TickCount - lastMeleeClickMs) >= MeleeClickMs)
            {
                lastMeleeClickMs = Environment.TickCount;
                if (TryClickUnit(hwnd, target, me.Location.X, me.Location.Y))
                {
                    _clicks++;
                    presses++;
                    attacked = true;
                }
            }

            /* The melee half of the bar: Claw, Heroic Strike, Sinister Strike, Rend. These are
             * 5 yd abilities, so this is the only branch that can use them - the cast loop would
             * have stood at 28 yd pressing a key the client refuses. Rage and energy are not
             * readable apart from mana here, so the press goes out, and a refusal costs the
             * client's own error line rather than a global cooldown spent twice. */
            if (meleeHit != null && (!swinging || sinceDamageMs >= MeleePressMs))
            {
                ClickAimInput.TapKey(hwnd, meleeHit.KeyVk, 40);
                presses++;
                _presses++;
                if (!worked.Contains(meleeHit.Name + " (pressed at arm's length)"))
                    worked.Add(meleeHit.Name + " (pressed at arm's length)");
                attacked = true;
            }

            /* Nothing was sent this pass: wait out the swing instead of spinning. A level-1
             * swing lands every ~3 s and the loop would otherwise pass here ten times a second. */
            Thread.Sleep(attacked ? AttackSettleMs : MeleeWaitMs);

            WowBWorldSnapshot? afterSwing;
            if (!Capture(out afterSwing) || afterSwing == null || !afterSwing.TryGetSelectedTarget(out target))
                return worked.Count == 0
                    ? "target gone while attacking and nothing had landed"
                    : "killed or fled while attacking; what landed: " + string.Join(", ", worked.ToArray());

            health = Health(target);
            if (health >= 0 && lastHealth >= 0 && health < lastHealth)
            {
                if (!worked.Contains("auto-attack")) worked.Add("auto-attack");
                if (!meleeSwinging)
                {
                    meleeSwinging = true;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] the swing is running (mob health {0} -> {1}) - holding position and "
                        + "letting the client swing", lastHealth, health));
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] auto-attack does damage: mob health {0} -> {1}", lastHealth, health));
                lastHealth = health;
                lastMeleeDamageMs = Environment.TickCount;
                quietRounds = 0;
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

            /* The heal's price. The failure this guards is the one the user reported: a heal
             * pressed with no power to pay for it, over and over, until the character died.
             * 139/100 is the character from the 19:07 log, whose max-power word reads 100. */
            _manaPeak = 0;
            _healCostGuess = 0;
            ClickAimSettings.HealMinManaPercent = 25;
            check("healPowerEnough", HealPowerOk(139, 100));        // full bar
            check("healPowerTooLow", !HealPowerOk(20, 100));        // 20% - under the 25% line
            check("healPowerAtLine", HealPowerOk(25, 100));
            _healCostGuess = 60;
            check("healPowerLearnedCost", !HealPowerOk(60, 100) && HealPowerOk(100, 100));
            _healCostGuess = 0;
            ClickAimSettings.HealMinManaPercent = 0;
            check("healPowerRuleOff", HealPowerOk(1, 100));
            LearnHealCost(139, 99);
            check("healCostLearned", _healCostGuess == 40);
            LearnHealCost(139, 139);                                  // nothing spent: no evidence
            check("healCostIgnoresNoSpend", _healCostGuess == 40);

            /* The 22:47 run: the max-power word read 0, the old code called that "no opinion"
             * and healed anyway, and a shaman pressed Healing Wave at 13 power twice while a
             * mob killed it. An unreadable maximum must never mean yes. */
            _healCostGuess = 0;
            ClickAimSettings.HealMinManaPercent = 25;
            check("healPowerNoMaxRefusesZero", !HealPowerOk(0, 0));
            check("healPowerNoMaxKeepsPositive", HealPowerOk(20, 0));

            /* And what a cast costs, so the character stops casting it cannot afford: the
             * Lightning Bolt in that log cost 15 power, so 13 is not enough to press it again -
             * the melee branch takes over and the character swings. */
            _castCosts.Clear();
            check("castCostUnknown", PowerForCast(null, 10) == 10);
            LearnCastCost(403, 150, 135);
            var bolt = new ClickAimAction { Slot = 2, Id = 403, Kind = ClickAimActionKind.Spell };
            check("castCostLearned", PowerForCast(bolt, 10) == 18);
            check("castCostRefuses13", 13u < PowerForCast(bolt, 10));
            check("castCostAllows50", 50u >= PowerForCast(bolt, 10));
            LearnCastCost(403, 135, 135);        // nothing spent: no evidence, price unchanged
            check("castCostIgnoresNoSpend", PowerForCast(bolt, 10) == 18);
            _castCosts.Clear();
            _healCostGuess = 0;
            ClickAimSettings.HealMinManaPercent = 25;

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
            check("rangesNest", AttackRangeYd < ClickAimCombat.MeleeRangeYd
                              && ClickAimCombat.MeleeRangeYd < ClickAimCombat.CasterRangeYd
                              && ClickAimCombat.CasterRangeYd < SearchYd
                              && SearchYd < GiveUpYd);

            /* The user's number, written down so a later "tidy-up" cannot quietly make the bot
             * short-sighted again: 100 yd around the CHARACTER is what it fights in, the fight
             * walks as far as the search accepts, and the walk-off path stops for the same
             * radius. The 13:23 log lost a mob at 48 yd to a 40 yd radius. */
            check("engagementRadius", Math.Abs(SearchYd - 100.0) < 0.001);
            check("chaseReachesTheSearch", ChaseYd >= SearchYd - 0.001);
            check("pullUsesTheSameRadius", Math.Abs(PullYd - SearchYd) < 0.001);

            /* An unverified pool pick is only walked to inside the short radius; the rest of the
             * circle is TAB's job, because the pool cannot see who is friendly. */
            check("poolSelectInsideSearch", PoolSelectYd > ClickAimCombat.CasterRangeYd
                                            && PoolSelectYd < SearchYd);
            check("giveUpBeyondSearch", GiveUpYd > SearchYd);

            /* Melee reach sits inside the melee chase: a 5 yd ability is pressed from inside
             * the distance the melee branch is willing to close. Getting this wrong is a
             * character that walks to 5 yd and then presses a key the client refuses. */
            check("meleeReachNests", ClickAimCombat.MeleeRangeYd <= ChaseYd
                                   && AttackRangeYd <= ClickAimCombat.MeleeRangeYd);

            /* The walk stops short of the mob so the coast lands inside the range rather than
             * through it - running past and back is the "back and forward" the user saw. */
            check("meleeEngageHysteresis", AttackRangeYd < MeleeEngageYd && MeleeEngageYd < ChaseYd);
            check("meleeWaitsAreSane", MeleePressMs < MeleeRefaceAfterMs
                                    && MeleeClickMs > MeleePressMs
                                    && MeleeWaitMs < AttackSettleMs);
            check("interruptInsideSearch", InterruptYd < SearchYd && InterruptYd > AttackRangeYd);

            /* The walk stops for an attackable mob further out than the old 18 yd interrupt:
             * that radius only fired for something already fighting the character, which is
             * why mobs beside the path were walked past. It still sits inside the search. */
            check("pullInsideSearch", PullYd > InterruptYd && PullYd <= SearchYd
                                     && PullYd >= ClickAimCombat.CasterRangeYd);
            check("pullProbeIsThrottled", PullProbeMs > 0 && PullProbeMs < 5000);

            /* Auto-attack is a toggle on this client: it is only pressed again after this much
             * silence, and the silence window has to be longer than a swing. */
            check("swingQuietWindow", MeleeSwingQuietMs > MeleePressMs && MeleeSwingQuietMs >= 3000);

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

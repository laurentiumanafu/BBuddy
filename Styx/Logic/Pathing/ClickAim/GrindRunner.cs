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

    // ---- getting unstuck ---------------------------------------------------
    /// <summary>
    /// How far the character has to move inside <see cref="StuckMs"/> for a chase to count as
    /// progress. One yard is under a step, so a character that is walking answers it every tick
    /// and a character standing inside a cactus never does.
    /// </summary>
    private const double StuckMoveYd = 1.0;

    /// <summary>
    /// How long a chase may go without moving before the character tries to free itself.
    ///
    /// The run this comes from: the mob was 40 yd away with full health, the character had walked
    /// into a cactus, and it stood there for a minute asking for one more step toward the mob from
    /// the same spot. Nothing in the loop measured its OWN movement - every branch only looked at
    /// the distance to the mob, which never changed. Two and a half seconds of no movement is a
    /// wall, not a slope.
    /// </summary>
    private const int StuckMs = 2500;

    /// <summary>
    /// Escape attempts before the target is dropped. Each one walks a few yards away from the mob
    /// (or to the side of it), which is what a player does when the path ahead is a plant.
    /// </summary>
    private const int StuckEscapes = 3;

    /// <summary>How long one escape step walks for, in milliseconds.</summary>
    private const int StuckEscapeMs = 600;

    /// <summary>
    /// How long a unit the fight could not reach is left out of the target scans. The user's rule:
    /// "it should try to unstuck or blacklist the target for a while if it can't reach it". Ninety
    /// seconds is long enough for the character to be somewhere else, and short enough that a mob
    /// which is still there afterwards is tried again.
    /// </summary>
    private const int UnreachablePauseMs = 90000;

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
    /// <c>UnitFlags</c> bits that mean "this is not something to fight", read from the dword at
    /// object +0x1E60:
    ///
    ///   0x2    NON_ATTACKABLE. The client's own attack predicate - the function the registered
    ///          <c>UnitCanAttack</c> handler calls - tests bit 1 of +0x1E60 and returns false on
    ///          the path that takes it (hunt/WOWB_70291_TRACE_FINDINGS.md, 16:27).
    ///   0x100  IMMUNE_TO_PC  \  measured, not guessed: the 14:23 run dumped the same window for a
    ///   0x200  IMMUNE_TO_NPC /  friendly lazy peon (level 4, hp 86/86) as flags 0x200 and for the
    ///          scorpions it killed as flags 0x0, and the pool census in the same log lists a
    ///          second "friend" as 0x300 - both immune bits. A unit carrying either bit cannot be
    ///          damaged by this character, which is exactly what the fight spent twenty seconds
    ///          proving at 1.3 yd.
    ///
    /// Used as a FILTER, never as proof: a unit carrying a bit is skipped, and a unit without one
    /// still has to be an enemy that TAB agrees with before it counts.
    /// </summary>
    private const uint NotAFightFlag = 0x2 | 0x100 | 0x200;

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
    /// Do not sit down with a fight this close. A meal is cancelled by any damage and a character
    /// sitting next to a wolf is a character about to be eaten, so the rest waits for the next gap
    /// instead - the walk continues and the next tick re-checks.
    /// </summary>
    private const double RestSafeYd = 12.0;

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

    /// <summary>
    /// How long a press of the Attack key is left alone before it is judged to have worked or
    /// not. A swing on a level-3 character takes up to three seconds to land, and the 13:46 log
    /// is a press at 37.9 s, another at 40.8 s, and the first damage at 41.4 s: the second press
    /// toggled OFF the swing the first one had just started. Six seconds covers every swing timer
    /// this character has been seen with, and the cost of waiting is one swing.
    /// </summary>
    private const int MeleePressGraceMs = 6000;

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


    // ---- selling ----------------------------------------------------------
    /// <summary>
    /// How long the walk from a vendor point to the NPC beside it may take. A vendor point is
    /// captured standing next to the vendor, so this is a few yards - the allowance is for a
    /// town doorway and a bad projection, not for crossing the town.
    /// </summary>
    private const int VendorWalkMs = 20000;

    /// <summary>
    /// How close the character has to be before the vendor is right-clicked. Interaction range is
    /// about 5 yd; 6 gives a yard of slack for a click that arrives a step late.
    /// </summary>
    private const double VendorReachYd = 6.0;

    /// <summary>Snapshots taken at the vendor point before deciding there is no NPC there.</summary>
    private const int VendorNpcAttempts = 3;

    /// <summary>
    /// A trip that failed is not retried for this long - walking to the same empty spot every
    /// eight seconds is how a bot spends an hour in a town.
    /// </summary>
    private const int VendorRetryMs = 120000;

    /// <summary>
    /// A trip cut short by a fight is retried sooner than a failed one: the fight is the reason
    /// it stopped, and the bags are still full.
    /// </summary>
    private const int VendorRetryAfterFightMs = 15000;

    /// <summary>A plan longer than this is a recording accident, not a shopping list.</summary>
    private const int SellPlanMaxSteps = 200;

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
    /// Units the fight could not walk to, and until when they are left out of the scans. A mob
    /// behind a cactus is not a mob worth twenty more seconds of walking into it.
    /// </summary>
    private static readonly Dictionary<WowBGuid, int> _unreachableUntil = new Dictionary<WowBGuid, int>();

    // ---- selling (the vendor trip) ----------------------------------------
    /// <summary>Set by the window's "Sell now" button, cleared by the trip it starts.</summary>
    private static volatile bool _vendorDue;

    // ---- "is this character really fighting?" -------------------------------
    /// <summary>Health as last sampled by <see cref="InRealCombat"/>; -1 while out of combat.</summary>
    private static int _lastHpSeen = -1;

    /// <summary>When the health bar last went DOWN. Damage needs no flags to be believed.</summary>
    private static int _lastHurtAt;

    /// <summary>Throttles the "the flag is stale" line to once per 15 s.</summary>
    private static int _realCombatQuietAt;

    /// <summary>Throttles the "not eating next to that" line to once per 15 s.</summary>
    private static int _restHeldQuietAt;

    /// <summary>How long after losing health the character still counts as fighting.</summary>
    private const int CombatEvidenceMs = 8000;

    /// <summary>
    /// Bodies looted since the last trip. This is the bag-full stand-in: the container slots
    /// cannot be read on this build, so the count of things taken is the count of things the
    /// bags now hold. See <see cref="ClickAimSettings.SellAfterLoots"/>.
    /// </summary>
    private static int _lootsSinceTrip;

    /// <summary>Set when a trip cannot work at all (no point, no plan); selling stops for the run.</summary>
    private static bool _vendorImpossible;

    /// <summary>TickCount before which no trip is attempted again.</summary>
    private static int _vendorRetryAt;

    /// <summary>Clicks the sell plan itself sent, for the summary.</summary>
    private static int _sellClicks;

    /// <summary>
    /// Monsters killed since the last trip - the other half of the bag-full stand-in, because the
    /// user's own words are "if 20 monsters are defeated". Counted next to the loots rather than
    /// instead of them: a kill whose corpse could not be looted still filled a bag slot, and a
    /// body looted twice cannot happen, so the two numbers are compared separately.
    /// </summary>
    private static int _killsSinceTrip;

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

    /// <summary>
    /// The longest distance the client's own targeting has been seen to offer a target: the
    /// 13:23 log has "TAB has a target 48 yd away", and the 13:46 run engaged one at 45 yd.
    /// Inside this radius a TAB refusal MEANS something ("the client does not consider this an
    /// enemy"); beyond it a refusal only means "too far for TAB". That is the difference between
    /// blacklisting a friendly peon and blacklisting a wolf.
    /// </summary>
    private const double TabReachYd = 45.0;

    /// <summary>
    /// Units the pool can see inside <see cref="TabReachYd"/> that TAB refused to select, and
    /// when. TAB is the client's own enemy filter, so a refusal is the client saying "not an
    /// enemy" - which is what a friendly NPC is. This exists because the 13:46 run walked up to a
    /// friendly lazy peon and spent twenty seconds casting at it: the old pool fallback selected
    /// it by left-click, and the pool cannot tell a peon from a wolf.
    /// </summary>
    private static readonly Dictionary<WowBGuid, int> _tabMisses = new Dictionary<WowBGuid, int>();

    /// <summary>
    /// Units TAB has refused twice, with the tick until which the scan should ignore them. It is
    /// a PAUSE, not a blacklist: a mob at the edge of TAB's reach is refused because it is too
    /// far, not because it is friendly, and it has to be fought once the character is five yards
    /// closer. After <see cref="TabRefusePauseMs"/> the unit is asked about again.
    /// </summary>
    private static readonly Dictionary<WowBGuid, int> _tabRefusedUntil = new Dictionary<WowBGuid, int>();

    /// <summary>How long a unit TAB refused twice is left out of the scan.</summary>
    private const int TabRefusePauseMs = 60000;

    /// <summary>Minimum spacing between two pool selections (a left-click search costs seconds).</summary>
    private const int PoolSelectMs = 2500;

    /// <summary>
    /// How long a target the POOL picked may show no damage at all, while it is inside TAB's
    /// reach, before the client is asked to settle it. This is the user's number: "if the HP
    /// stays full for 10 seconds and it is in the range of TAB try to use TAB - if nothing is
    /// offered then it means the target is friendly or BLOCKED, so ignore it and try a new target,
    /// or go to the next hotspot if no target is found". A real mob loses health to the first
    /// Shadow Bolt; a peon cannot lose health at all.
    /// </summary>
    private const int PoolVerifyMs = 10000;

    private static int _lastPoolSelectMs;

    /// <summary>
    /// The unit the pool selected and the client has not confirmed yet. The 14:23 run re-engaged
    /// the same peon four times through "going with the target already selected", and every
    /// re-engage came back as a TAB-selected target - so it was never on trial again and the
    /// verdict never ran. The trial belongs to the UNIT, not to the one fight that started it.
    /// </summary>
    private static WowBGuid _unverifiedGuid;

    /// <summary>
    /// Pool units whose left-click selection failed. One miss is bad luck (the body can be behind
    /// the character's own model for a frame); the second means it cannot be clicked from here and
    /// it is set aside, so a hotspot can still become clear.
    /// </summary>
    private static readonly HashSet<WowBGuid> _poolMisses = new HashSet<WowBGuid>();

    /// <summary>Two refusals only count when they are this far apart (a real second look).</summary>
    private const int TabMissSpacingMs = 5000;

    /// <summary>Leave a target it gave up on alone for this long.</summary>
    private const int GiveUpMs = 15000;

    /// <summary>A fight that ended this fast never fired anything; pause before the next one.</summary>
    private const int GiveUpBackoffMs = 600;

    /// <summary>The camera's field of view: the mover's own default, and what its log prints.</summary>
    private const double CameraFovDeg = 90.0;

    /// <summary>
    /// Set by the "stop" request. The loop ends after the current tick - and every loop it can be
    /// sitting inside (the walk, the turn, the cast wait) ends with it, because they all read
    /// <see cref="ClickAimAbort"/> as well.
    /// </summary>
    public static void Stop()
    {
        _stop = true;
        ClickAimAbort.Request();
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
        ClickAimAbort.Clear();
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
        int vendorTrips = 0;
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
        _scanWasClear = false;
        _combatQuietAt = 0;
        _farQuietAt = 0;
        _notViable.Clear();
        _tabMisses.Clear();
        _tabRefusedUntil.Clear();
        _poolMisses.Clear();
        _lastPoolSelectMs = 0;
        _unverifiedGuid = default(WowBGuid);
        _looted.Clear();
        _loots = 0;
        _lootProbed = false;
        _emptyPixelKnown = false;
        _vendorDue = false;
        _vendorImpossible = false;
        _vendorRetryAt = 0;
        _lootsSinceTrip = 0;
        _killsSinceTrip = 0;
        _sellClicks = 0;
        _unreachableUntil.Clear();

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

                /* The table before the road. The 15:50 run walked 200 yd to the vendor at 17 of
                 * 167 power with food and drink on the bar and never took a bite, because the
                 * rest check sat BELOW this one and the trip returns to the top of the loop on
                 * the tick it starts. Eating is cheap, the walk is not, and the thresholds are
                 * the user's own: rest first, then sell.
                 *
                 * Not when the window asked, though. "Sell now" is a button somebody is watching;
                 * a trip the counters asked for can afford to eat first. */
                if (!_vendorDue)
                {
                    string restedFirst = RestIfNeeded(hwnd);
                    if (restedFirst.Length > 0) Logging.Write("[Grind] " + restedFirst);
                }

                /* The bags before the mobs. The 2026-10-31 run pressed "Sell now" and watched the
                 * bot take the next hotspot leg first, because the check sat after the fight and
                 * after the rest - which is the wrong order for a decision about the whole
                 * character rather than about this hotspot. A fight still outranks it (VendorDue
                 * refuses while something is attacking), and the hotspot index is not advanced,
                 * so the character comes back to the ring it left. */
                if (VendorDue())
                {
                    if (VendorTrip(hwnd, mover)) vendorTrips++;
                    continue;
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
                    {
                        kills++;
                        _killsSinceTrip++;
                    }
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

        string bagReport = BagSlotHunt.Report();
        if (bagReport.Length > 0) Logging.Write(bagReport);

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] done: {0} hotspot(s) reached, {1} fight(s), {2} kill(s) confirmed, "
            + "{3} corpse(s) looted, {4} key press(es), {5} click(s) that landed, {6} area switch(es), "
            + "{8} vendor trip(s), {9} sell click(s), {7:F0} s",
            hotspots, fights, kills, _loots, _presses, _clicks, switches,
            (Environment.TickCount - started) / 1000.0, vendorTrips, _sellClicks));
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

        /* The bag-slot hunt: on unless the user turned it off. One read per looted body, and a
         * candidate list at the end of the run - see BagSlotHunt.Reset/Sample/Report. */
        BagSlotHunt.Enabled = ClickAimSettings.BagSlotHunt;
        BagSlotHunt.Reset();

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
        /* Cleared on entry, not only on the scan below: a failed read must not leave a stale
         * "the area is clear" behind for the hotspot loop to act on. */
        _scanWasClear = false;

        /* A trip that was asked for is not a suggestion: nothing NEW is picked up while one is
         * waiting. (The loop asks VendorDue first, so this is the second line of defence - the
         * case it really covers is a request that arrives while this function is already
         * running.) */
        if (_vendorDue && !InRealCombat())
        {
            report = "a vendor trip is waiting - not picking up a new fight before it";
            return false;
        }

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

        /* Two ways to choose a target, and TAB is the last word:
         *
         *   1. what is already selected (the walk asks once, TAB costs a key);
         *   2. TAB - the client's own enemy filter, the only thing on this build that knows
         *      who is an enemy.
         *
         * The unit pool in memory does not select anything any more. It has the units, their
         * positions, levels and unit flags - everything needed to answer "is anything alive
         * within 100 yd, and which way is it" - but no reaction (friendly/hostile) word, and the
         * 13:46 run is what that costs: a left-click pool fallback selected a friendly lazy peon
         * and the bot cast Shadow Bolt at it for twenty seconds. The pool is used for the scan,
         * for turning onto the nearest unit before asking TAB (TAB only looks in front), and for
         * stopping the walk beside a mob. TAB decides.
         *
         * The right-CLICK fallback stays gone too: it cannot tell a mob from a guard, and on
         * 2026-10-30 17:22 it walked up to a friendly townsman 3.6 yd away and right-clicked
         * him - a right-click is "attack and walk to it", so the "fight" was against the town.
         * */
        WowBObservedUnit target = default(WowBObservedUnit);
        bool have = false;

        /* Whether this target came from the pool rather than from TAB. A pool pick is UNVERIFIED
         * - the pool cannot tell a wolf from a peon - so the fight that takes it also has to
         * settle it (see the TAB verdict in Fight). */
        bool poolPick = false;

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
            /* Still unproven if this is the unit the pool picked: see _unverifiedGuid. */
            poolPick = !_unverifiedGuid.IsEmpty && target.Guid == _unverifiedGuid;
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

        /* ---- step 3: the pool, then let the client settle it ----
         *
         * TAB had nothing live to offer (it only looks in front, and only inside its own reach),
         * but the pool says something alive and inside the level band is standing <nearestYd> yd
         * away. Select it with a LEFT-click - a left-click selects and does nothing else, it is
         * not an attack order and not a walk order - and report success only when the client's own
         * target guid answers with that unit, so a probe that lands on the ground costs nothing
         * but the probe.
         *
         * This is the user's rule: move to the alive thing, and if its health has not moved once
         * it is inside TAB's reach, ask TAB. TAB not offering it means it is friendly or blocked,
         * and then it is ignored for the session and the next target is taken - see the verdict in
         * Fight. The 13:46 peon was found by exactly this fallback; what was missing was the
         * verdict, not the fallback.
         */
        if (!have && haveNearest
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
                poolPick = true;
                _unverifiedGuid = target.Guid;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] TAB offered nothing live, so the pool was used: {0} is alive at {1:F1} yd "
                    + "(level {2}) and the client selected it on a left-click at {3},{4} px - it has {5} s "
                    + "inside TAB's reach to prove itself, or TAB will be asked about it",
                    target.Guid, nearestYd, nearestLevel, fx, fy, PoolVerifyMs / 1000));
            }
            else
            {
                if (!_poolMisses.Add(nearest.Guid))
                {
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

        /* ---- step 4: TAB said no twice, so leave that one out of the scan ----
         *
         * A unit the pool can see INSIDE TAB's reach that TAB refuses twice, a few seconds apart,
         * is either a friend or something this character cannot attack; it is left out of the scan
         * for a minute so a hotspot holding nothing but friends can finish. It is a pause, not a
         * blacklist: a mob at the edge of TAB's reach is refused for being far, not for being
         * friendly, and it is asked about again later. */
        if (!have && haveNearest && nearestYd <= TabReachYd)
        {
            int lastMiss;
            int now = Environment.TickCount;
            if (_tabMisses.TryGetValue(nearest.Guid, out lastMiss)
                && unchecked(now - lastMiss) >= TabMissSpacingMs)
            {
                _tabMisses.Remove(nearest.Guid);
                if (_tabRefusedUntil.Count > 256) _tabRefusedUntil.Clear();
                _tabRefusedUntil[nearest.Guid] = now + TabRefusePauseMs;
                if (unchecked(now - _poolQuietAt) > 15000)
                {
                    _poolQuietAt = now;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] {0} is {1:F1} yd away and TAB has refused it twice in a row - the client "
                        + "does not consider it an enemy, so it is left out of the scan for {2} s "
                        + "(a friendly NPC looks exactly like this)", nearest.Guid, nearestYd,
                        TabRefusePauseMs / 1000));
                }
            }
            else if (!_tabMisses.ContainsKey(nearest.Guid))
            {
                if (_tabMisses.Count > 256) _tabMisses.Clear();
                _tabMisses[nearest.Guid] = now;
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
                 * standing on it: loot first, then clear the target. Not when a vendor trip is
                 * waiting, though - in the 15:50 run these two right-clicks were the eight
                 * seconds between "Sell now" and the walk to the vendor. */
                if (_vendorDue && !InRealCombat())
                {
                    Logging.Write("[Grind] a vendor trip is waiting: the corpse is left where it is and "
                                  + "taken on the way back");
                }
                else
                {
                    string loot = LootNearby(hwnd, 2);
                    if (loot.Length > 0) Logging.Write("[Grind] " + loot);
                }

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
        report = Fight(hwnd, px, py, poolPick);

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
            /* A trip that was asked for outranks the corpse on the ground. The 15:50 run pressed
             * "Sell now" at 15:50:35 and spent the next eight seconds right-clicking two bodies
             * before it went - which the user reads, correctly, as "it fights before it sells".
             * The body keeps; the vendor is 200 yd away. */
            if (_vendorDue && !InRealCombat())
            {
                Logging.Write("[Grind] a vendor trip is waiting, so the body left on the ground for now - "
                              + "it is taken on the way back through");
            }
            else
            {
                string loot = LootNearby(hwnd, 3);
                if (loot.Length > 0) Logging.Write("[Grind] " + loot);
            }
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

        /* A unit the flags say cannot be fought is not a target, whoever selected it. The 14:23
         * peon sat at 1.3 yd with 0x200 on it and was taken as "the target already selected" four
         * fights in a row. */
        if ((f.UnitFlags & NotAFightFlag) != 0) return false;

        /* A unit this run has already given up on must not come back through the selection.
         * The 14:23 run is that bug: the fight refused the friendly peon four times in a row -
         * "giving up on ... for this session" then "going with the target already selected",
         * 1.3 yd, forever - because the refusal list was only consulted for pool picks. */
        if (_notViable.Contains(target.Guid)) return false;
        if (Unreachable(target.Guid)) return false;
        int refusedUntil;
        if (_tabRefusedUntil.TryGetValue(target.Guid, out refusedUntil)
            && unchecked(Environment.TickCount - refusedUntil) < 0)
            return false;

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
         * level-viable is standing inside TAB's reach, stop the walk and let the main loop face
         * it and ask TAB - a unit further out than TAB can see is not worth stopping for, because
         * the walk itself is closing that distance. */
        WowBObservedUnit poolUnit;
        double poolYd;
        int poolLive, poolUnread;
        if (NearestKillable(snap, me.Location.X, me.Location.Y, TabReachYd, out poolUnit, out poolYd,
                            out poolLive, out poolUnread))
        {
            if (unchecked(Environment.TickCount - _poolQuietAt) > 5000)
            {
                _poolQuietAt = Environment.TickCount;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] the pool has {0} alive and level-viable {1:F1} yd away ({2} in the pool inside "
                    + "{3:F0} yd, {4} of them unreadable) - stopping the walk to deal with it",
                    poolUnit.Guid, poolYd, poolLive, TabReachYd, poolUnread));
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
        if (Unreachable(candidate.Guid)) return false;

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
                if (report.StartsWith("killed", StringComparison.Ordinal)) { kills++; killed++; _killsSinceTrip++; }
                else if (report.IndexOf("killed or fled", StringComparison.Ordinal) >= 0) { kills++; killed++; _killsSinceTrip++; }
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
        if (InRealCombat()) return "";
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
        /* The verdict, not the flag: the 15:50 run never ate or drank once, and this line is
         * where that came from - the flag stays set for a minute after a fight, so every chance
         * to sit down was spent waiting for a combat that was already over. */
        if (InRealCombat()) return "";

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

        /* Standing next to something that can reach the character is not a place to sit. Said
         * once per 15 s, because a silent skip here looks exactly like the bug this round is
         * fixing: the user watched a character at 10% mana walk 200 yd instead of drinking. */
        if (wantFood || wantDrink)
        {
            WowBWorldSnapshot? around;
            WowBObservedUnit near;
            double nearYd;
            if (Capture(out around) && around != null && around.TryGetLocalPlayer(out WowBObservedUnit here))
            {
                int live2, unread2;
                if (NearestKillable(around, here.Location.X, here.Location.Y, RestSafeYd,
                                    out near, out nearYd, out live2, out unread2)
                    && unchecked(Environment.TickCount - _restHeldQuietAt) > 15000)
                {
                    _restHeldQuietAt = Environment.TickCount;
                    return string.Format(CultureInfo.InvariantCulture,
                        "eating and drinking can wait: something killable is {0:F0} yd away, and a meal "
                        + "with that on the character is a meal that gets interrupted", nearYd);
                }
            }
        }

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
            ClickAimAbort.Wait(500);
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
            _lootsSinceTrip++;

            /* Something just went into the bags: this is the moment a free-slot count moves by
             * one, which is the whole point of the hunt. */
            BagSlotHunt.Sample("looted a body");
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

    // ------------------------------------------------------------------ selling
    /// <summary>
    /// Asks the loop for a vendor trip as soon as nothing is attacking. The window's "Sell now"
    /// button calls this; it does nothing when no run is going.
    /// </summary>
    public static bool RequestVendorTrip()
    {
        if (!_running)
        {
            Logging.Write("[Grind] no run is going, so there is nothing to sell during - start grinding first");
            return false;
        }

        _vendorDue = true;
        Logging.Write("[Grind] a vendor trip was asked for: it is taken before the next fight and before "
                      + "the next leg, as soon as the character is out of combat - a fight already running "
                      + "is finished, but nothing new is picked up and no body is looted on the way");
        return true;
    }

    /// <summary>
    /// Whether this is the moment to go and sell: something was looted since the last trip (the
    /// stand-in for a full bag), or the window asked for it - and never while a mob is on the
    /// character, because a fight outranks the bags.
    /// </summary>
    private static bool VendorDue()
    {
        if (!_running || _stop) return false;
        if (UnderAttack()) return false;

        /* Asked for, before anything else: a point recorded in the window during a run has to be
         * usable without restarting it, so a manual request is not blocked by a run that already
         * decided there was nowhere to sell. */
        if (_vendorDue) return true;

        if (_vendorImpossible) return false;
        if (unchecked(Environment.TickCount - _vendorRetryAt) < 0) return false;

        if (!ClickAimSettings.AutoSell) return false;
        int after = ClickAimSettings.SellAfterLoots;
        if (after <= 0) return false;

        /* "20 monsters are defeated" is the user's own rule, and a kill and a loot are the same
         * corpse. Counted separately and compared separately: whichever number gets there first
         * is the one that decides, so an unlootable body still fills a bag slot and a body cannot
         * be counted twice. */
        return _killsSinceTrip >= after || _lootsSinceTrip >= after;
    }

    /// <summary>
    /// The original CopilotBuddy selling procedure, in the only form this build allows.
    ///
    /// <c>Bots/Grind/LevelBot.cs</c> did it like this: when <c>Me.FreeNormalBagSlots</c> ran out,
    /// or a repair was needed, it set <c>BotPoi.Current = new BotPoi(vendor, PoiType.Sell)</c>,
    /// the POI decorator walked there, <c>WaitContinue(5, IsVendorFrameOpen)</c> waited for the
    /// merchant window, <c>Vendors.SellAllItems()</c> sold everything, and the frame was closed.
    /// Three of those four middle steps are a Lua call or a 3.3.5a address on this build:
    ///
    ///   * the bag slots cannot be counted (there is no container offset in
    ///     <see cref="Styx.Offsets.WowBOffsets"/>), so "full" is the number of bodies looted since
    ///     the last trip - <see cref="ClickAimSettings.SellAfterLoots"/> - and a button;
    ///   * <c>MerchantFrame.Instance.IsVisible</c> does not exist, so the window is given
    ///     <see cref="ClickAimSettings.VendorWaitMs"/> and then the plan runs;
    ///   * <c>SellAllItems</c> is a Lua call, so selling is right-clicking the items in the bags,
    ///     which is what a player does - recorded once in <see cref="VendorFile"/>, replayed here;
    ///   * the vendor coordinates are in no profile in this repository, so the point is captured
    ///     where the character stands, with the same capture the hotspot list uses.
    ///
    /// What is kept from the original: walk to a point, click the NPC, sell, and go back to the
    /// ring. The trip is taken between fights and never during one.
    /// </summary>
    private static bool VendorTrip(IntPtr hwnd, ClickAimMover mover)
    {
        _vendorDue = false;

        /* A malformed file must not take the whole run down with it: this is a plan, not a
         * requirement, and the loop has a fight to get back to. */
        List<VendorFile.VendorPoint> points;
        try { points = VendorFile.Read(); }
        catch (Exception ex)
        {
            _vendorImpossible = true;
            Logging.Write("[Grind] the vendor file could not be read (" + ex.Message
                          + ") - selling is off for the rest of this run");
            return false;
        }

        if (points.Count == 0)
        {
            _vendorImpossible = true;
            Logging.Write("[Grind] the bags are due a trip, but no vendor point is saved: stand at the vendor "
                          + "in town, open Combat settings -> Selling, and press \"Add vendor here\". "
                          + "Selling is off for the rest of this run.");
            return false;
        }

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs;
            return false;
        }

        int at = VendorFile.Nearest(points, px, py);
        if (at < 0) return false;
        VendorFile.VendorPoint point = points[at];

        if (point.Steps.Count == 0)
        {
            _vendorImpossible = true;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] the nearest vendor point ({0}) has no sell steps, so nothing would happen there. "
                + "Record them in Combat settings -> Selling: the merchant window, then the items in the bags. "
                + "Selling is off for the rest of this run.", point));
            return false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] selling first, before the next hotspot: {0} is {1:F0} yd away and has {2} recorded "
            + "step(s); {3} kill(s) and {4} looted bodie(s) since the last trip", point,
            ClickAimMath.Len(point.X - px, point.Y - py), point.Steps.Count, _killsSinceTrip, _lootsSinceTrip));

        var route = new List<RoutePoint>();
        route.Add(new RoutePoint { X = point.X, Y = point.Y, Z = point.Z, Kind = "Vendor", Label = point.Name });

        /* The same interrupt a hotspot leg uses: a mob on the road is dealt with first, and the
         * trip is taken again afterwards, because the bags are still full. */
        RouteRunner.ShouldInterrupt = () => FightNeeded(hwnd, SearchYd);
        bool arrived = false;
        try { arrived = RouteRunner.Run(mover, route, 1); }
        catch (Exception ex) { Logging.Write("[Grind] the walk to the vendor failed: " + ex.Message); }
        finally { RouteRunner.ShouldInterrupt = null; }

        if (!arrived)
        {
            if (RouteRunner.LastRunInterrupted)
            {
                /* No retry delay: the bags are still full and the trip is the whole reason the
                 * loop is in town mode. Only the interrupt itself is quieted, so a leg that keeps
                 * being cut short for something that never turns into a fight cannot pin the
                 * character in place - the same brake a hotspot leg uses. */
                _interruptQuietUntil = Environment.TickCount + InterruptRetryMs;
                _vendorRetryAt = 0;
                Logging.Write("[Grind] the walk to the vendor was cut short: there is something to fight on the "
                              + "way. The trip is taken again as soon as that is dealt with.");
            }
            else
            {
                _vendorRetryAt = Environment.TickCount + VendorRetryMs;
                Logging.Write("[Grind] could not walk to the vendor point - the trip is dropped and retried in "
                              + (VendorRetryMs / 1000) + " s");
            }
            return false;
        }

        WowBObservedUnit npc = default(WowBObservedUnit);
        double npcYd = 0.0;
        bool found = false;
        for (int attempt = 0; attempt < VendorNpcAttempts && !_stop && !found; attempt++)
        {
            if (TryFindVendorNpc(out npc, out npcYd)) { found = true; break; }
            ClickAimAbort.Wait(400);
        }

        if (!found)
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryMs;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] standing on the vendor point {0} and no NPC answers within {1} yd. The point was "
                + "captured {2:F0} yd away, so either the walk stopped short or the vendor is not there any more.",
                point, ClickAimSettings.VendorSearchYd, ClickAimMath.Len(point.X - px, point.Y - py)));
            return false;
        }

        int walkStart = Environment.TickCount;
        while (unchecked(Environment.TickCount - walkStart) < VendorWalkMs && !_stop)
        {
            if (!ClickAimPositions.TryRead(out px, out py)) break;
            double d = ClickAimMath.Len(npc.Location.X - px, npc.Location.Y - py);
            if (d <= VendorReachYd) break;

            if (UnderAttack())
            {
                ClickAimKeySteer.ReleaseHeld();
                _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs;
                Logging.Write("[Grind] something attacked the character on the way to the vendor - the trip "
                              + "waits for the fight");
                return false;
            }

            double remaining;
            ClickAimKeySteer.Steer(npc.Location.X, npc.Location.Y, hwnd, true, out remaining);
            Thread.Sleep(90);
        }

        ClickAimKeySteer.ReleaseHeld();
        StopAndSettle(hwnd);

        if (!ClickAimPositions.TryRead(out px, out py))
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs;
            return false;
        }

        double reach = ClickAimMath.Len(npc.Location.X - px, npc.Location.Y - py);
        if (reach > VendorReachYd + 2.0)
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryMs;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] could not get within {0:F0} yd of the vendor NPC - still {1:F1} yd away, so no click was "
                + "sent (a right-click from there is a walk order, not an interaction)", VendorReachYd, reach));
            return false;
        }

        ClickAimKeySteer.Face(npc.Location.X, npc.Location.Y, hwnd, 25.0, 4);

        /* The right-click that opens the window, confirmed the only way this build can confirm a
         * click: the client's own target guid answers with the NPC. On a friendly NPC this is the
         * interaction, not an attack - a right-click is a walk order on the GROUND and an attack
         * on a hostile unit, which is why the unit is filtered by NotAFightFlag first. */
        string how;
        if (!TryClickUnit(hwnd, npc, px, py, true, out how))
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryMs;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] could not put the cursor on the vendor {0} - the client never selected it: {1}",
                npc.Guid, how));
            return false;
        }
        _clicks++;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] the vendor {0} (seen {1:F1} yd away, clicked at {2:F1} yd) answered the click; giving the "
            + "merchant window {3} ms, then running {4} recorded step(s)",
            npc.Guid, npcYd, reach, ClickAimSettings.VendorWaitMs, point.Steps.Count));
        Thread.Sleep(ClickAimSettings.VendorWaitMs);

        int sent = RunSellPlan(hwnd, point);

        _lootsSinceTrip = 0;
        _killsSinceTrip = 0;
        _vendorRetryAt = 0;

        /* ...and here the same words move the other way, by the number of items sold. A candidate
         * that does that is the bag count, not a coincidence. */
        BagSlotHunt.Sample("sold at the vendor");
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] vendor trip done: {0} of {1} step(s) sent ({2} click(s)). The merchant window cannot be read "
            + "on this build, so the plan is what sells the loot - if the bags still hold junk, the plan needs "
            + "another step.", sent, point.Steps.Count, _sellClicks));
        return true;
    }

    /// <summary>
    /// Replays the recorded plan: the clicks a human made once at this vendor, in order, with the
    /// waits that were recorded with them. Nothing here guesses a position - every click is a
    /// fraction of the client rectangle that was captured with the cursor over the real thing.
    /// </summary>
    private static int RunSellPlan(IntPtr hwnd, VendorFile.VendorPoint point)
    {
        int sent = 0;
        int max = Math.Min(point.Steps.Count, SellPlanMaxSteps);

        for (int i = 0; i < max && !_stop; i++)
        {
            VendorFile.Step step = point.Steps[i];

            /* A fight outranks the shopping. The merchant window closes by itself when the
             * character walks away, and walking away is what the loop does next. */
            if (UnderAttack())
            {
                Logging.Write("[Grind] the sell plan stopped at step "
                              + (i + 1).ToString(CultureInfo.InvariantCulture)
                              + ": something started fighting");
                break;
            }

            if (step.IsWait)
            {
                Thread.Sleep(step.WaitMs);
                sent++;
                continue;
            }

            if (step.IsKey)
            {
                ClickAimInput.TapKey(hwnd, step.Vk, 60);
                _presses++;
            }
            else if (step.IsClick)
            {
                if (ClickAimInput.ClickFraction(hwnd, step.Fx, step.Fy, step.IsRightClick))
                {
                    _clicks++;
                    _sellClicks++;
                }
                else
                {
                    Logging.Write("[Grind] the client size could not be read - the sell plan was stopped");
                    break;
                }
            }
            else
            {
                continue;                      // a kind this build does not know: skip it, do not fail
            }

            sent++;
            Thread.Sleep(Math.Max(60, step.WaitMs));
        }

        return sent;
    }

    /// <summary>
    /// The NPC to click at a vendor point: a unit that cannot be fought, which is exactly what a
    /// vendor is and what a mob is not.
    ///
    /// <see cref="NotAFightFlag"/> is the filter the fight REFUSES targets with. Here it is used
    /// the other way round, and it is the closest thing to a reaction word this build has. When
    /// nothing at the point carries it, a single living unit within 8 yd is tried as a guess and
    /// named as one in the log: the point was captured standing next to the vendor, and a town on
    /// this build is full of guards that also carry the flags.
    /// </summary>
    private static bool TryFindVendorNpc(out WowBObservedUnit npc, out double yards)
    {
        npc = default(WowBObservedUnit);
        yards = double.MaxValue;

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;

        WowBObservedUnit me;
        if (!snap.TryGetLocalPlayer(out me)) return false;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return false;

        double search = ClickAimSettings.VendorSearchYd;
        WowBObservedUnit friendly = default(WowBObservedUnit);
        double friendlyYd = double.MaxValue;
        WowBObservedUnit only = default(WowBObservedUnit);
        double onlyYd = double.MaxValue;
        int living = 0;

        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit candidate = snap.Units[i];
            if (candidate.IsLocalPlayer) continue;
            if (candidate.HasPlayerVtable) continue;

            double d = ClickAimMath.Len(candidate.Location.X - me.Location.X,
                                        candidate.Location.Y - me.Location.Y);
            if (d <= 0.4 || d > search) continue;

            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (!reader.TryGetUnitFields(candidate.UnitAddress, out f)) continue;
            if (f.Health == 0) continue;               // a corpse is not a vendor

            living++;
            if (d < 8.0 && d < onlyYd) { only = candidate; onlyYd = d; }

            if ((f.UnitFlags & NotAFightFlag) == 0) continue;

            if (friendlyYd == double.MaxValue)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] the vendor should be {0} (level {1}, hp {2}/{3}, flags 0x{4:X}) {5:F1} yd away",
                    candidate.Guid, f.Level, f.Health, f.MaxHealth, f.UnitFlags, d));
            }

            if (d < friendlyYd) { friendly = candidate; friendlyYd = d; }
        }

        if (friendlyYd < double.MaxValue)
        {
            npc = friendly;
            yards = friendlyYd;
            return true;
        }

        if (living == 1 && onlyYd < double.MaxValue)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] no unit at the vendor point carries the \"cannot be fought\" flags, but there is exactly "
                + "one living unit within 8 yd ({0:F1} yd) - trying it as the vendor", onlyYd));
            npc = only;
            yards = onlyYd;
            return true;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0} living unit(s) within {1:F0} yd of the vendor point and none of them is marked as "
            + "friendly - there is nobody here to sell to", living, search));
        return false;
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
            ClickAimAbort.Wait(300);
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
            if (Unreachable(candidate.Guid)) continue;

            /* ...and a unit TAB refused twice is left out of the scan for a minute, so a hotspot
             * holding nothing but friendly NPCs can finish. Only the scan: nothing is remembered
             * beyond the pause, because a mob at the edge of TAB's reach is refused for being far,
             * not for being friendly. */
            int refusedUntil;
            if (_tabRefusedUntil.TryGetValue(candidate.Guid, out refusedUntil)
                && unchecked(Environment.TickCount - refusedUntil) < 0)
                continue;

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
            if ((f.UnitFlags & NotAFightFlag) != 0) continue;
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
    /// Whether a unit is FIGHTING within <paramref name="radius"/> - any level, any reaction, no
    /// bookkeeping filters. This is not a target scan: it answers "is a fight happening next to
    /// this character", which is what the client's own in-combat flag is supposed to mean.
    /// <see cref="NearestFighting"/> cannot answer it, because that one throws away everything the
    /// current level band would not fight - and a level-8 mob beating on a level-4 character is
    /// still a fight, however unwise.
    /// </summary>
    private static bool AnyFighting(WowBWorldSnapshot snap, double px, double py, double radius,
                                    out WowBObservedUnit unit, out double yards)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        unit = default(WowBObservedUnit);
        yards = double.MaxValue;
        if (reader == null) return false;

        bool found = false;
        for (int i = 0; i < snap.Units.Count; i++)
        {
            WowBObservedUnit candidate = snap.Units[i];
            if (candidate.IsLocalPlayer) continue;

            double d = ClickAimMath.Len(candidate.Location.X - px, candidate.Location.Y - py);
            if (d <= 0.5 || d > radius || d >= yards) continue;

            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (!reader.TryGetUnitFields(candidate.UnitAddress, out f)) continue;
            if (f.Health == 0) continue;
            if ((f.UnitFlags & InCombatFlag) == 0) continue;

            unit = candidate;
            yards = d;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Whether this character is REALLY in a fight, as opposed to the client's in-combat flag
    /// still being set.
    ///
    /// The 2026-10-31 15:50 run is why this exists. For the whole walk to the vendor the log
    /// repeated "the character reads as in combat but nothing nearby is fighting - walking on",
    /// and that stale flag is what kept the character from eating and drinking: both the rest and
    /// the vendor check asked <see cref="PlayerInCombat"/>, which is one word in one block, and it
    /// stayed set for about a minute after the last kill.
    ///
    /// So the flag is now only the question, never the answer:
    ///
    ///   * not set              -> out of combat, nothing more to look at;
    ///   * set, health falling  -> in combat: damage is the proof that needs no flags;
    ///   * set, something inside <see cref="InterruptYd"/> is fighting -> in combat;
    ///   * set, neither         -> out of combat, and the log says so once in a while.
    ///
    /// The third case is what makes this safe to use for eating and selling: a mob that is on the
    /// character is flagged as fighting, and it is inside 18 yd long before it is a threat.
    /// </summary>
    private static bool InRealCombat()
    {
        if (!PlayerInCombat())
        {
            _lastHurtAt = 0;
            _lastHpSeen = -1;
            return false;
        }

        /* Damage is the proof no flag can fake. Sampled here because this runs between fights,
         * where a health bar that is not full and not rising is a bar being hit. */
        int hp = HealthPercent();
        if (hp >= 0)
        {
            if (_lastHpSeen >= 0 && hp < _lastHpSeen) _lastHurtAt = Environment.TickCount;
            _lastHpSeen = hp;
        }
        if (_lastHurtAt != 0 && unchecked(Environment.TickCount - _lastHurtAt) < CombatEvidenceMs)
            return true;

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return true;      // cannot look: believe the flag
        WowBObservedUnit me;
        if (!snap.TryGetLocalPlayer(out me)) return true;

        WowBObservedUnit unit;
        double d;
        if (AnyFighting(snap, me.Location.X, me.Location.Y, InterruptYd, out unit, out d))
            return true;

        if (unchecked(Environment.TickCount - _realCombatQuietAt) > 15000)
        {
            _realCombatQuietAt = Environment.TickCount;
            Logging.Write("[Grind] the in-combat flag is still set but nothing within "
                          + InterruptYd.ToString("F0", CultureInfo.InvariantCulture)
                          + " yd is fighting and the health bar is not falling - treating the character "
                          + "as out of combat (so eating, drinking and selling can happen)");
        }
        return false;
    }

    /// <summary>
    /// Whether something is actually on the character: the in-combat flag AND a unit that is
    /// fighting within 25 yd. The flag alone lags for seconds after a kill, which would have
    /// made every loot trip wait for a combat state that is already over.
    /// </summary>
    private static bool UnderAttack()
    {
        /* One verdict for the whole class. It used to be its own scan, with two problems the
         * 15:50 run made visible: it threw away the units outside the current level band (so a
         * level-8 mob on a level-4 character was "not a fight"), and it could not tell a stale
         * flag from a real one. */
        return InRealCombat();
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
    /// One fight's movement watchdog: where the character was when it was last seen moving, when
    /// that was, and how many escape attempts have been spent since.
    ///
    /// A class rather than three locals because <see cref="ChaseStep"/> is called from two
    /// branches of the fight (the walk into casting range and the walk into melee) and both have
    /// to share the same tally - otherwise a fight that alternates between the two could spend
    /// three escapes in each and never give up.
    /// </summary>
    private sealed class ChaseWatch
    {
        public double X = double.NaN;
        public double Y = double.NaN;
        public int LastMoveMs;
        public int Escapes;
        public int TotalEscapes;

        public ChaseWatch(int started) { LastMoveMs = started; }
    }

    /// <summary>
    /// One step of a chase, with the stuck watchdog around it.
    ///
    /// Returns true when the fight should carry on, and false with <paramref name="why"/> set when
    /// the character has stopped making progress and the target should be dropped: something in the
    /// geometry is between it and the mob, and walking into it for another minute is not a
    /// strategy. The 2026-10-31 run is the reason: the mob was far away and untouched, the
    /// character was inside a cactus, and it spent a minute asking for one more step toward the
    /// mob from the same spot - the distance to the mob never changed, so no branch noticed.
    ///
    /// Between the "stuck" reading and giving up, it tries to free itself: walk away from the mob,
    /// then to either side of it. That is what a player does, and it is the only movement here
    /// that is not aimed at the target.
    /// </summary>
    private static bool ChaseStep(IntPtr hwnd, ChaseWatch watch, WowBObservedUnit target, out string why)
    {
        why = "";

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            /* No position, no way to tell progress. Keep walking: told nothing, do nothing new. */
            double unknown;
            ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out unknown);
            Thread.Sleep(90);
            return true;
        }

        if (double.IsNaN(watch.X))
        {
            watch.X = px;
            watch.Y = py;
            watch.LastMoveMs = Environment.TickCount;
        }
        else if (ClickAimMath.Len(px - watch.X, py - watch.Y) >= StuckMoveYd)
        {
            watch.X = px;
            watch.Y = py;
            watch.LastMoveMs = Environment.TickCount;
            watch.Escapes = 0;              // it is moving again: whatever was in the way is behind
        }

        if (unchecked(Environment.TickCount - watch.LastMoveMs) < StuckMs)
        {
            double remaining;
            ClickAimKeySteer.Steer(target.Location.X, target.Location.Y, hwnd, true, out remaining);
            Thread.Sleep(90);
            return true;
        }

        if (watch.Escapes >= StuckEscapes || watch.TotalEscapes >= StuckEscapes * 2)
        {
            double stuckYd = ClickAimMath.Len(px - watch.X, py - watch.Y);
            why = string.Format(CultureInfo.InvariantCulture,
                "{0:F1} yd from where it was {1:F1} s ago with the mob still {2:F1} yd away, after {3} "
                + "attempt(s) to walk round whatever is in the way",
                stuckYd, unchecked(Environment.TickCount - watch.LastMoveMs) / 1000.0,
                ClickAimMath.Len(target.Location.X - px, target.Location.Y - py), watch.TotalEscapes);
            return false;
        }

        ClickAimKeySteer.ReleaseHeld();
        string how;
        TryEscapeStep(hwnd, target, watch.Escapes, out how);
        watch.Escapes++;
        watch.TotalEscapes++;
        watch.LastMoveMs = Environment.TickCount;      // give the escape its own window
        watch.X = px;
        watch.Y = py;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] stuck: {0} - escape attempt {1} of {2}, {3}", how, watch.Escapes, StuckEscapes,
            watch.Escapes >= StuckEscapes
                ? "and if this does not free the character the target is dropped"
                : "walking out and trying again"));
        return true;
    }

    /// <summary>
    /// One escape step: walk a few yards away from the mob (or to either side of it), because the
    /// thing in the way is usually straight ahead and dead-on. Steered with the same calibrated
    /// keys as every other walk, so it is a real step and not a hope.
    /// </summary>
    private static bool TryEscapeStep(IntPtr hwnd, WowBObservedUnit target, int attempt, out string how)
    {
        how = "";
        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            how = "the player position does not read, so there is nothing to escape TO";
            return false;
        }

        double away = ClickAimMath.HeadingDeg(px - target.Location.X, py - target.Location.Y);
        double bearing = away + (attempt == 1 ? 80.0 : attempt == 2 ? -80.0 : 0.0);
        double rad = bearing * Math.PI / 180.0;
        double tx = px + Math.Cos(rad) * 5.0;
        double ty = py + Math.Sin(rad) * 5.0;

        for (int walked = 0; walked < StuckEscapeMs && !_stop; walked += 90)
        {
            double remaining;
            ClickAimKeySteer.Steer(tx, ty, hwnd, true, out remaining);
            Thread.Sleep(90);
            if (remaining < 1.0) break;
        }
        ClickAimKeySteer.ReleaseHeld();

        how = string.Format(CultureInfo.InvariantCulture,
            "walking {0} to ({1:F0}, {2:F0}) for {3} ms",
            attempt == 1 ? "to the right of the mob" : attempt == 2 ? "to the left of the mob" : "back from the mob",
            tx, ty, StuckEscapeMs);
        return true;
    }

    /// <summary>
    /// Drops a target the fight could not walk to, for <see cref="UnreachablePauseMs"/>, and
    /// returns the line for the log. The pause is a pause, not a blacklist: a mob behind a cactus
    /// now is a mob standing in the open in two minutes.
    /// </summary>
    private static string DropUnreachable(WowBObservedUnit target, string why)
    {
        if (_unreachableUntil.Count > 256) _unreachableUntil.Clear();
        _unreachableUntil[target.Guid] = Environment.TickCount + UnreachablePauseMs;
        return string.Format(CultureInfo.InvariantCulture,
            "could not reach {0}: {1}. Out of the target scans for {2} s, and the loop takes another one",
            target.Guid, why, UnreachablePauseMs / 1000);
    }

    /// <summary>Whether a unit is still in its "cannot be walked to" pause.</summary>
    private static bool Unreachable(WowBGuid guid)
    {
        if (guid.IsEmpty || _unreachableUntil.Count == 0) return false;
        int until;
        if (!_unreachableUntil.TryGetValue(guid, out until)) return false;
        if (unchecked(Environment.TickCount - until) >= 0)
        {
            _unreachableUntil.Remove(guid);          // the pause is over: ask again
            return false;
        }
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
    private static string Fight(IntPtr hwnd, double px, double py, bool unverified)
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
        bool attackPressed = false;     // the Attack key has been pressed this fight
        int lastMeleeClickMs = 0;
        var worked = new List<string>();

        /* Settling a target the pool picked (see TryEngage step 3). The rule is the user's: while
         * the target's health has not moved, once it is inside TAB's reach, wait PoolVerifyMs and
         * then ask TAB whether the client considers it an enemy at all. */
        bool verifyAsked = false;
        int inReachSince = 0;           // when the target first came inside TAB's reach
        int lastHealthDropMs = started; // when its health last went down
        int lastSeenHealth = -1;

        /* The movement watchdog. It measures the character's own position, not its intent: every
         * branch of this loop only ever looked at the distance to the mob, and a mob 40 yd away
         * behind a cactus keeps that number the same forever. See StuckMs. */
        var watch = new ChaseWatch(started);

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

        /* What the client thinks this thing is, in its own words where it has any: the level and
         * the unit flags that the whole target question hinges on. Printing them on every engage
         * is what will settle the reaction/faction hunt - a wolf and a friendly lazy peon in one
         * log, same line shape, and the field that differs is the answer. */
        string targetWords = "";
        if (reader != null && reader.TryGetUnitFields(target.UnitAddress, out Styx.WoWInternals.WowBReader.WowBUnitFields engaged))
            targetWords = string.Format(CultureInfo.InvariantCulture,
                ", level {0}, hp {1}/{2}, flags 0x{3:X}",
                engaged.Level, engaged.Health, engaged.MaxHealth, engaged.UnitFlags);

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] engaged at {0:F1} yd (mob health {1}{2}); bar: {3}",
            ClickAimMath.Len(target.Location.X - px, target.Location.Y - py),
            lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unknown",
            targetWords,
            ClickAimCombat.Describe(bar)));

        /* Raw words for the faction hunt, one line per engage. See AttackWords. */
        string attackWords = AttackWords(target.UnitAddress);
        if (attackWords.Length > 0) Logging.Write("[Grind] target words:" + attackWords);

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

            /* ---- the pool's target has to be settled by the client ----
             *
             * The pool cannot tell a wolf from a peon: both are "alive, level 4, full health". So a
             * target that came from the pool is on trial, and the clock starts when it first comes
             * inside TAB's reach and runs while nothing happens to it.
             *
             * The user's rule was "ask TAB, and if nothing is offered it is friendly". The 14:23
             * run showed why that alone does not work: TAB on an already-selected unit KEEPS that
             * unit, so the peon came back as "confirmed". So the ask stays, but it only counts when
             * TAB actually moves - and the real verdict is the client's own combat state:
             *
             *   TAB moved to another live unit -> take it, it is the enemy the client wants;
             *   TAB offered nothing at all    -> friendly or blocked, ignore it;
             *   TAB kept the same unit        -> says nothing, fall through to the combat test;
             *
             *   and, whatever TAB said: no damage on the target for PoolVerifyMs, Attack pressed
             *   at arm's length more than MeleeSwingQuietMs ago, and the character is NOT in combat
             *   -> the client never started a fight. That is a friendly NPC or something behind a
             *   wall either way, and it is dropped. */
            if (unverified)
            {
                int seen = Health(target);
                if (seen >= 0 && lastSeenHealth >= 0 && seen < lastSeenHealth)
                {
                    /* Damage on it is the proof the pool could not give: it is a fight. */
                    lastHealthDropMs = Environment.TickCount;
                    if (!_unverifiedGuid.IsEmpty && _unverifiedGuid == target.Guid)
                    {
                        _unverifiedGuid = default(WowBGuid);
                        Logging.Write("[Grind] the pool's target is confirmed by landing damage on it");
                    }
                }
                if (seen >= 0) lastSeenHealth = seen;

                if (inReachSince == 0 && d <= TabReachYd) inReachSince = Environment.TickCount;
            }

            if (unverified && !verifyAsked && inReachSince != 0
                && unchecked(Environment.TickCount - inReachSince) >= PoolVerifyMs
                && unchecked(Environment.TickCount - lastHealthDropMs) >= PoolVerifyMs)
            {
                verifyAsked = true;
                WowBGuid asked = target.Guid;

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} has been in TAB's reach for {1} s at {2:F1} yd with no damage on it - "
                    + "asking the client whether it is an enemy at all",
                    asked, PoolVerifyMs / 1000, d));

                ClickAimInput.TapKey(hwnd, TabVk, 40);
                Thread.Sleep(TabSettleMs);

                WowBWorldSnapshot? verdictSnap;
                WowBObservedUnit verdictUnit = default(WowBObservedUnit);
                bool offered = Capture(out verdictSnap) && verdictSnap != null
                               && verdictSnap.TryGetSelectedTarget(out verdictUnit);
                int offeredHealth = offered ? Health(verdictUnit) : 0;

                if (!offered || offeredHealth == 0)
                {
                    _notViable.Add(asked);
                    if (_notViable.Count > 256) _notViable.Clear();
                    return string.Format(CultureInfo.InvariantCulture,
                        "TAB offered nothing for {0} after {1} s with its health untouched - the client "
                        + "does not treat it as an enemy (friendly NPC or blocked), so it is ignored from "
                        + "now on and the next target is taken", asked, PoolVerifyMs / 1000);
                }

                if (verdictUnit.Guid != asked)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] TAB moved off {0} and selected {1} instead - taking the client's own pick",
                        asked, verdictUnit.Guid));
                    return "TAB moved off the pool's target - re-engaging what the client selected";
                }

                Logging.Write("[Grind] TAB kept the same target, which proves nothing on this client - "
                            + "the verdict is whether a fight starts at all");
            }

            /* The combat verdict. It runs a few seconds after the TAB ask, because a press of
             * Attack needs MeleeSwingQuietMs to prove itself - and on a friendly NPC it never does.
             * The 14:23 peon sat at 1.3 yd with Attack pressed and the character never entered
             * combat; a scorpion enters combat on the first swing. */
            if (unverified && inReachSince != 0
                && unchecked(Environment.TickCount - lastHealthDropMs) >= PoolVerifyMs
                && lastAttackPressMs != 0
                && unchecked(Environment.TickCount - lastAttackPressMs) >= MeleeSwingQuietMs
                && !PlayerInCombat())
            {
                WowBGuid dead = target.Guid;
                _notViable.Add(dead);
                if (_notViable.Count > 256) _notViable.Clear();
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} has taken no damage for {1} s, Attack was pressed at arm's length and the client "
                    + "never put the character in combat - it is not something this character can fight "
                    + "(friendly NPC, or blocked); ignoring it and taking the next target",
                    dead, PoolVerifyMs / 1000);
            }

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
                    ClickAimAbort.Wait(300);
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
                string blocked;
                if (!ChaseStep(hwnd, watch, target, out blocked))
                    return DropUnreachable(target, blocked);
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
                    ClickAimAbort.Wait(250);
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
                string whyCasting = castFailures > 0
                    ? "switched off: the casts landed nothing"
                    : damage == null
                        ? "no damage spell on the bar"
                        : damage.IsMelee
                            ? "not applicable: the damage spell is a melee ability"
                            : "not enough power";

                /* LATCHED for the rest of the fight. The 13:46 run is the reason: the swing was
                 * running, mana regenerated past the price of Shadow Bolt, the cast broke the
                 * swing, the mob hit back and the mana was gone again - every four seconds,
                 * which is the "DPS is lost" the user reported. A player does not stop swinging
                 * to start a 2.5 s cast in melee at level 3, and neither does this now. */
                melee = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] melee from here FOR THE REST OF THIS FIGHT - casting is {0} (damage spell {1}{2}, "
                    + "power {3}, {4} failed cast(s)); a swing that is running stays running",
                    whyCasting,
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
                double chaseYd = GiveUpYd;
                if (d > chaseYd)
                    return string.Format(CultureInfo.InvariantCulture,
                        "cannot cast at it and it is {0:F0} yd away (the melee chase is {1:F0} yd); "
                        + "giving up on it", d, chaseYd);
                string blocked;
                if (!ChaseStep(hwnd, watch, target, out blocked))
                    return DropUnreachable(target, blocked);
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
                ClickAimAbort.Wait(220);
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
            int sincePressMs = unchecked(Environment.TickCount - lastAttackPressMs);
            /* A press is still "in flight" until a swing has had time to land. Without this the
             * loop pressed Attack a second time 2.8 s after the first, which on this client is a
             * toggle: the swing the first press started was turned off again. */
            bool pressPending = lastAttackPressMs != 0 && sincePressMs < MeleePressGraceMs;
            if (auto != null)
            {
                /* One press is the norm. A second is only made when the first demonstrably did
                 * nothing - no swing has landed for twice MeleeSwingQuietMs - because after a
                 * heal or a spell the client keeps auto-attack ON and resumes swinging by
                 * itself, and every extra press is a chance to toggle it off. */
                if (!swinging && !pressPending && sincePressMs >= MeleeClickMs
                    && sinceDamageMs >= (attackPressed ? MeleeSwingQuietMs * 2 : MeleeSwingQuietMs))
                {
                    attackPressed = true;
                    ClickAimInput.TapKey(hwnd, auto.KeyVk, 40);
                    lastAttackPressMs = Environment.TickCount;
                    presses++;
                    _presses++;
                    attacked = true;
                    Logging.Write("[Grind] pressing Attack to start the swing (it stays on until "
                                + "something stops it; pressing it again would turn it off)");
                }
            }
            else if (!swinging && !pressPending
                     && unchecked(Environment.TickCount - lastMeleeClickMs) >= MeleeClickMs)
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
             * standing there for the full engage timeout - but twelve rounds is only ~4 s of
             * waiting, and a level-3 swing lands every 3.5 s. The 13:48 run gave up on a mob at
             * 25 health mid-swing, re-engaged it 0.3 s later and killed it, which is one wasted
             * decide. So a break also needs twice MeleeSwingQuietMs of real silence. */
            if (!unverified && ++quietRounds >= 12
                && unchecked(Environment.TickCount - lastMeleeDamageMs) >= MeleeSwingQuietMs * 2)
                break;
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
    /// <summary>
    /// The raw words the client's own attack predicate inspects - object +0x94, +0x1ABE (bit 7),
    /// +0x1E60 (the flags), +0x1E64 (bit 28), +0x1E68 (bit 8), per the 2026-10-13/16:27 hunt notes
    /// in hunt/WOWB_70291_TRACE_FINDINGS.md - printed as hex next to the level and health of every
    /// target the bot engages.
    ///
    /// This is the faction/reaction hunt, done with the only instrument available outside the
    /// disassembler: a wolf and a friendly peon in one log, same line shape. The bot itself does
    /// not act on any of these numbers - it cannot, none of them has a proven meaning - but the
    /// word that separates a mob from a peon is the offset the whole "does it see monsters" question
    /// is waiting on.
    /// </summary>
    private static string AttackWords(ulong unitAddress)
    {
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null || unitAddress == 0) return "";

        ulong[] bases = { 0x90UL, 0x1AB0UL, 0x1E50UL };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < bases.Length; i++)
        {
            uint[] words;
            if (i > 0) sb.Append(" |");
            sb.Append(" +0x").Append(bases[i].ToString("X", CultureInfo.InvariantCulture)).Append(':');
            if (!reader.TryReadWords(unitAddress + bases[i], 8, out words))
            {
                sb.Append(" unreadable");
                continue;
            }
            for (int w = 0; w < words.Length; w++)
                sb.Append(' ').Append(words[w].ToString("X8", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

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

            /* TAB's reach is inside the circle: the pool may stop the walk that far out, and
             * further than that only the walk itself closes the distance. */
            check("tabReachInsideSearch", TabReachYd > ClickAimCombat.CasterRangeYd
                                           && TabReachYd < SearchYd);

            /* The user's ten seconds for a pool target to prove itself, and it has to fit inside
             * the time one fight is given in total - a peon must not outlast the fight. */
            check("poolVerifyWindow", PoolVerifyMs >= 5000 && PoolVerifyMs <= 15000
                                    && PoolVerifyMs < EngageTimeoutMs);

            /* The flags that mean "not something to fight", each measured against a real peon
             * (0x200/0x300) and real scorpions (0x0) in the 14:23 log. */
            check("notAFightMask", (NotAFightFlag & 0x2) != 0
                                && (NotAFightFlag & 0x100) != 0
                                && (NotAFightFlag & 0x200) != 0);

            /* TAB's verdict comes before the scan gives up on a unit, not after. */
            check("tabRefusePauseFitsVerify", TabRefusePauseMs > PoolVerifyMs
                                            && TabMissSpacingMs < PoolVerifyMs);
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

            /* A press of Attack is a toggle: it must get longer than a whole swing to prove
             * itself before another press is risked. */
            check("meleePressGraceExceedsSwing", MeleePressGraceMs > MeleeSwingQuietMs
                                               && MeleePressGraceMs > MeleePressMs);
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

            /* The vendor plan: a point captured in the field and the clicks a human recorded
             * there, read back as steps. Everything below is file-only, so a failure here is a
             * code problem rather than a missing client. */
            string vendorPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "clickaim-vendors-selftest-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                string vendorError;
                check("vendorAdd", VendorFile.Add(10.0, 20.0, 30.0, out vendorError, vendorPath) == 1);
                check("vendorAddClick", VendorFile.AddStep(0,
                    new VendorFile.Step { Kind = "RightClick", Fx = 0.75, Fy = 0.5, WaitMs = 250 },
                    out vendorError, vendorPath));
                check("vendorAddKey", VendorFile.AddStep(0,
                    new VendorFile.Step { Kind = "Key", Vk = 66, WaitMs = 600 }, out vendorError, vendorPath));

                List<VendorFile.VendorPoint> vendorPoints = VendorFile.Read(vendorPath);
                check("vendorRead", vendorPoints.Count == 1 && vendorPoints[0].Steps.Count == 2);
                if (vendorPoints.Count == 1 && vendorPoints[0].Steps.Count == 2)
                {
                    check("vendorReadPoint", Math.Abs(vendorPoints[0].X - 10.0) < 0.001
                                          && Math.Abs(vendorPoints[0].Y - 20.0) < 0.001);
                    check("vendorReadClick", vendorPoints[0].Steps[0].IsClick
                                          && vendorPoints[0].Steps[0].IsRightClick
                                          && Math.Abs(vendorPoints[0].Steps[0].Fx - 0.75) < 0.001);
                    check("vendorReadKey", vendorPoints[0].Steps[1].IsKey
                                        && vendorPoints[0].Steps[1].Vk == 66);
                }

                check("vendorNearest", VendorFile.Nearest(vendorPoints, 12.0, 20.0) == 0);

                /* Editing a plan: a step that was recorded wrong is deleted on its own, and the
                 * order can be changed without re-recording the whole plan. */
                check("vendorAddSecondClick", VendorFile.AddStep(0,
                    new VendorFile.Step { Kind = "LeftClick", Fx = 0.5, Fy = 0.5, WaitMs = 300 },
                    out vendorError, vendorPath));
                check("vendorRemoveStep", VendorFile.RemoveStep(0, 2, out vendorError, vendorPath));
                List<VendorFile.VendorPoint> edited = VendorFile.Read(vendorPath);
                check("vendorRemoveStepTookTheRightOne", edited.Count == 1 && edited[0].Steps.Count == 2
                                                       && edited[0].Steps[1].IsKey);
                check("vendorRemoveStepRejectsBadIndex", !VendorFile.RemoveStep(0, 9, out vendorError, vendorPath));
                check("vendorMoveStep", VendorFile.MoveStep(0, 1, -1, out vendorError, vendorPath));
                edited = VendorFile.Read(vendorPath);
                check("vendorMoveStepOrder", edited.Count == 1 && edited[0].Steps.Count == 2
                                           && edited[0].Steps[0].IsKey && edited[0].Steps[1].IsClick);
                check("vendorMoveStepClamps", VendorFile.MoveStep(0, 0, -5, out vendorError, vendorPath)
                                           && VendorFile.Read(vendorPath)[0].Steps[0].IsKey);

                check("vendorClearSteps", VendorFile.ClearSteps(0, out vendorError, vendorPath));
                List<VendorFile.VendorPoint> afterClear = VendorFile.Read(vendorPath);
                check("vendorStepsGone", afterClear.Count == 1 && afterClear[0].Steps.Count == 0);

                /* A vendor point captured by mistake goes away whole. */
                check("vendorAddSecondPoint", VendorFile.Add(200.0, 300.0, 0.0, out vendorError, vendorPath) == 2);
                check("vendorRemovePoint", VendorFile.Remove(1, out vendorError, vendorPath));
                check("vendorRemovePointTookTheRightOne", VendorFile.Read(vendorPath).Count == 1);
                check("vendorRemovePointRejectsBadIndex", !VendorFile.Remove(5, out vendorError, vendorPath));
            }
            finally
            {
                try { System.IO.File.Delete(vendorPath); } catch { }
            }

            /* The trip's own arithmetic: the reach is inside the search radius, and a trip cut
             * short by a fight is retried sooner than one that failed outright. */
            check("vendorReachIsInteractionRange", VendorReachYd > 0.0 && VendorReachYd < 8.0);

            /* Getting unstuck: the watchdog has to notice a wall quickly, and the pause that
             * follows a target the fight cannot reach has to outlast the escape attempts - or the
             * loop drops the mob and picks it straight back up. */
            check("stuckWindowIsQuick", StuckMs >= 1500 && StuckMs <= 5000);
            check("stuckEscapeIsAStep", StuckEscapeMs >= 300 && StuckEscapeMs <= 1500);
            check("stuckPauseOutlastsTheEscapes", UnreachablePauseMs > StuckMs * StuckEscapes * 2);
            check("stuckMoveIsUnderAStep", StuckMoveYd > 0.2 && StuckMoveYd < 2.0);
            check("vendorRetryAfterFight", VendorRetryAfterFightMs > 0
                                        && VendorRetryAfterFightMs < VendorRetryMs);
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

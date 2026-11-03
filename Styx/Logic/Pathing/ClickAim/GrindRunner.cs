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
    // With a manual faction filter, move close to a permitted candidate on mesh
    // rather than using indiscriminate pixel probes to search around it.
    private const double FactionApproachYd = 15.0;
    // A repeated TAB miss is inconclusive, but a five-second pause made the
    // same friendly/occluded candidate interrupt roaming again immediately.
    private const int FactionTabRetryMs = 30000;

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
    /// How much closer the character must get to its current mesh follow point to
    /// reset the local stalled-chase timer.
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
    /// Stalled mesh-follow attempts before the target is temporarily dropped.
    /// </summary>
    private const int StuckEscapes = 3;

    // Even a moving goal or repeated replans cannot reset this no-closing limit.
    private const int ChaseNoClosingMs = 20000;

    /// <summary>
    /// How long a unit the fight could not reach is left out of the target scans. The user's rule:
    /// "it should try to unstuck or blacklist the target for a while if it can't reach it". Ninety
    /// seconds is long enough for the character to be somewhere else, and short enough that a mob
    /// which is still there afterwards is tried again.
    /// </summary>
    private const int UnreachablePauseMs = 90000;

    // ---- attacking ---------------------------------------------------------
    // 0x800 occurs on idle units too; it is not a verified combat indicator.

    /// <summary>
    /// Existing conservative target exclusion mask. Static attack-predicate traces test
    /// bit 1 of +0x1E60 on one false-return path; older logs observed other bits on
    /// some town units. These observations do not establish friendly, neutral,
    /// hostile, vendor, or general immunity semantics on build 70291. Keep the
    /// established failsafe, but do not treat the mask (or its absence) as a
    /// complete attackability or reaction classifier.
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

    /// <summary>How long to observe the body after posting a loot interaction.</summary>
    private const int LootSettleMs = 1500;

    /// <summary>
    /// How close the character has to be before the loot click is worth sending. A right-click
    /// on a body further away is not a loot at all: the client reads it as "walk there", which
    /// is exactly what the 18:44 run did for 17 s and then blamed on the projection.
    /// </summary>
    private const double LootReachYd = 4.0;
    // Leave room for movement/position sampling error before posting a loot click.
    private const double RecentLootApproachYd = 3.0;

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
    /// no more than 5 yd; the interaction is withheld if it is farther away.
    /// </summary>
    private const double VendorReachYd = 5.0;

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
    private static readonly WowBRecentLoot RecentLoot = new WowBRecentLoot();
    // Retry only when no right-button down was posted, never on an unverified click.
    private static readonly Dictionary<WowBGuid, int> _recentLootRetryAt = new Dictionary<WowBGuid, int>();
    private static readonly HashSet<WowBGuid> _recentLootRetried = new HashSet<WowBGuid>();
    private static int _recentLootQuietAt;

    /// <summary>Corpse loot interactions sent this session; item collection is unverified.</summary>
    private static int _loots;

    /// <summary>Set once the corpse word-diff has been printed; it is a discovery aid, not a log line.</summary>
    private static bool _lootProbed;

    /// <summary>
    /// Voluntary pulls use the level at <c>data+0x1E40</c>: up to three levels
    /// above and six levels below the player, inclusive. Combat defence does not
    /// use this band. A zero (unknown) level is allowed through by the existing
    /// policy; this band is not a claim about the client's experience colours.
    /// </summary>
    private const int LevelAbovePlayer = 3;
    private const int LevelBelowPlayer = 6;

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
    private static WowBDeathCheckpoint? _deathCheckpoint;
    private static bool _recoveryReady;
    private static int _deathStopGeneration;

    private static bool CheckDeathCheckpoint(bool trackerOnly = false)
    {
        // Standalone Gather uses this fight loop for defence and owns its tracker.
        if (_deathCheckpoint == null && GatherRunner.Running)
        {
            if (GatherRunner.StopIfDeadOrGhost()) return true;
            // During the bounded safety pause, let Gather's own tracker retry
            // instead of stopping the unrelated Grind runner on a fallback read.
            if (trackerOnly) return false;
        }
        bool observed = false;
        bool veto = _deathCheckpoint != null && _deathCheckpoint.Poll(out observed);
        if (!veto && (observed || trackerOnly || !PlayerDead())) return false;
        lock (StartStopGate)
        {
            if (_stop || ClickAimAbort.Requested) return true;
            _deathStopGeneration = _stopGeneration;
            _recoveryReady = ObjectManager.WowB?.IsExperimental70707 == true && _deathCheckpoint?.Recorded != null;
            _stop = true;
        }
        ClickAimKeySteer.ReleaseHeld();
        Logging.Write(_recoveryReady ? "[Grind] death checkpoint recorded; preparing experimental recovery" :
            "[Grind] death/ghost safety check stopped the bot; no usable recovery checkpoint");
        return true;
    }

    // These 70707 combat exits previously requested a global abort immediately.
    // If death and a target/vitals change happened between scheduled polls,
    // that abort prevented the owning runner from recording or recovering it.
    // Never attack the replacement target; pause input and sample death first.
    private static string StopFightForSafety(string reason)
    {
        ClickAimKeySteer.ReleaseHeld();
        Logging.Write("[Grind] " + reason + "; pausing to check for a death/ghost transition before stopping");
        bool veto = WowBDeathCheckpoint.ObserveSafetyExit(() => CheckDeathCheckpoint(trackerOnly: true),
            () => ClickAimAbort.Requested || RouteRunner.StopRequested, ClickAimAbort.Wait);
        if (veto) return "death/ghost safety check; fight halted for recovery if a checkpoint was captured";
        _stop = true;
        ClickAimAbort.Request();
        return reason + "; no death/ghost transition confirmed during safety pause";
    }

    private static bool RecoverIfPending(ClickAimMover mover)
    {
        WowBDeathCheckpoint.Candidate candidate;
        lock (StartStopGate)
        {
            if (!WowBDeathCheckpoint.MayBeginRecovery(_recoveryReady, _deathCheckpoint?.Recorded != null,
                    _deathStopGeneration, _stopGeneration, ClickAimAbort.Requested, RouteRunner.StopRequested)) return false;
            candidate = _deathCheckpoint!.Recorded!.Value;
            _recoveryReady = false;
            _stop = false; // only our own death stop, never a user STOP
        }
        bool recovered = WowBCorpseRecovery.TryRecover(candidate, mover, () => !_stop && !ClickAimAbort.Requested);
        if (!recovered) { _stop = true; return false; }
        _deathCheckpoint = new WowBDeathCheckpoint();
        RecentLoot.Clear();
        _recentLootRetryAt.Clear();
        _recentLootRetried.Clear();
        _focusedFightGuid = default; _defenceHandoffGuid = default; _focusedDamageObserved = false; _unverifiedGuid = default;
        _combatHold = false; _poolApproachHandled = false;
        Buffs.Reset(); // confirmed revival; reapply at the next quiet opportunity
        return !_stop && !ClickAimAbort.Requested;
    }
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
    // Player-only candidate, observed idle -> combat -> idle on 2026-10-31.
    // This is 0x80000, NOT the unrelated 0x800 which can persist while idle.
    private const uint PlayerCombatFlagCandidate = 0x80000;
    private static int _realCombatQuietAt;

    /// <summary>Throttles the "not eating next to that" line to once per 15 s.</summary>
    private static int _restHeldQuietAt;

    /// <summary>
    /// Bodies looted since the last trip. This is the bag-full stand-in: the container slots
    /// cannot be read on this build, so the count of things taken is the count of things the
    /// bags now hold. See <see cref="ClickAimSettings.SellAfterLoots"/>.
    /// </summary>
    private static int _lootsSinceTrip;

    /// <summary>Set when a trip cannot work at all (no point, no plan); selling stops for the run.</summary>
    private static bool _vendorImpossible;
    private static bool _vendorManualPending;

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
    // A bounded TAB cycle encountered an unsuitable live selection and found no
    // fight. Dwell can move on rather than retrying it for the full timeout.
    private static bool _lastEngageRejected;
    private static bool _combatHold;
    private static int _combatHoldLogAt;
    private static int _rejectedQuietAt;

    /// <summary>
    /// The longest distance the client's own targeting has been seen to offer a target: the
    /// 13:23 log has "TAB has a target 48 yd away", and the 13:46 run engaged one at 45 yd.
    /// A TAB miss at any range is inconclusive: facing, reach, UI and other
    /// conditions can prevent selection. It is never proof of friendliness.
    /// </summary>
    private const double TabReachYd = 45.0;

    // Close to the user-requested 40 yd before relying on TAB for a distant
    // pool observation. The pool itself does not identify hostile units.
    private const double PoolApproachYd = 40.0;
    private const int PoolApproachTimeoutMs = 45000;
    private static bool _poolApproachHandled;

    /// <summary>
    /// Units the pool can see inside <see cref="TabReachYd"/> that TAB refused to select, and
    /// when. These misses support a temporary cooldown, not a friendly verdict:
    /// the client can fail to select even an attackable unit.
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
    private const int PoolPixelMissPauseMs = 20000;

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
    // The bot's most recently engaged target, not a claim about attacker identity.
    private static WowBGuid _focusedFightGuid;
    // A changed selection observed during a fight, pending fresh combat and
    // eligibility checks. This is not evidence that the unit is an attacker.
    private static WowBGuid _defenceHandoffGuid;
    private static bool _focusedDamageObserved;
    private static void MarkFocusedAttack(WowBObservedUnit unit)
    {
        if (_focusedFightGuid != unit.Guid) _focusedDamageObserved = false;
        _focusedFightGuid = unit.Guid;
        // Remember only an actually engaged Grind target, never an arbitrary
        // nearby corpse or Gather defence target. The map is mandatory.
        WowBReader? reader = ObjectManager.WowB;
        if (_running && reader != null && reader.IsExperimental70707 &&
            reader.TryReadMapId(out uint map) && !_looted.Contains(unit.Guid))
            RecentLoot.Remember(unit, map, Environment.TickCount);
    }

    /// <summary>
    /// Pool units whose pixel selection failed once. A second miss starts a
    /// temporary cooldown; it does not prove the unit cannot be attacked.
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
    private static readonly object StartStopGate = new object();
    private static int _stopGeneration;
    private static int _starting;

    public static void Stop()
    {
        lock (StartStopGate)
        {
            _stopGeneration++;
            _stop = true;
            ClickAimAbort.Request();
            WowBGrindFactions.EndExperimental70707();
        }
    }

    public static bool Running { get { return _running; } }

    /// <summary>
    /// Loads a profile's grind areas and grinds the nearest one for
    /// <paramref name="minutes"/>. Returns false if there is nothing to grind.
    /// </summary>
    public static bool Start(string profilePath, int minutes)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return false;
        try { return StartCore(profilePath, minutes); }
        finally { Volatile.Write(ref _starting, 0); }
    }

    private static bool StartCore(string profilePath, int minutes)
    {
        int startGeneration;
        lock (StartStopGate) startGeneration = _stopGeneration;
        if (_running)
        {
            Logging.Write("[Grind] already grinding; the request was ignored");
            return false;
        }

        if (!WowBGrindFactions.PrepareForGrind(out string factionError))
        { Logging.Write("[Grind] faction filter could not start: " + factionError); return false; }
        Logging.Write(GrindFactionSettings.Rules.Enabled
            ? "[Grind] faction attack " + GrindFactionSettings.Rules.Mode + " filter ON: " + GrindFactionSettings.Rules.KeyText + "; blocked/unknown NPCs are withheld, even in defence; this is not proof of hostility"
            : "[Grind] faction attack filter OFF; normal targeting remains active");

        WowBReader? trialReader = WowBGrindFactions.ExperimentalBoundReader;
        List<ProfileGrindArea> areas;
        try { areas = ProfileRoute.LoadGrindAreas(profilePath); }
        catch { WowBGrindFactions.EndExperimental70707(); throw; }
        if (areas.Count == 0)
        {
            Logging.Write("[Grind] " + profilePath + " has no <GrindArea> hotspots. A quest "
                          + "profile is walked with profile:<path>; a grind profile has the hotspots "
                          + "inside <GrindArea>.");
            WowBGrindFactions.EndExperimental70707();
            return false;
        }

        double px, py;
        if (!ClickAimPositions.TryRead(out px, out py))
        {
            Logging.Write("[Grind] no player position - is the character in the world?");
            WowBGrindFactions.EndExperimental70707();
            return false;
        }

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] {0}: {1} grind area(s) in the file, levels {2}. Picking one needs the player's "
            + "level, so the choice is made on the grind thread once the client answers.",
            System.IO.Path.GetFileName(profilePath), areas.Count, LevelSpan(areas)));

        lock (StartStopGate)
        {
            if (startGeneration != _stopGeneration || _running)
            { WowBGrindFactions.EndExperimental70707(); return false; }
            _stop = false;
            ClickAimAbort.Clear();
            RouteRunner.StopRequested = false;
            _running = true;
            try
            {
                var thread = new Thread(() => RunGuarded(areas, minutes, profilePath, null, trialReader));
                thread.IsBackground = true;
                thread.Name = "ClickAimGrind";
                thread.Start();
                return true;
            }
            catch { _running = false; WowBGrindFactions.EndExperimental70707(); throw; }
        }
    }

    internal static bool StartRoaming()
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return false;
        try { return StartRoamingCore(); }
        finally { Volatile.Write(ref _starting, 0); }
    }

    private static bool StartRoamingCore()
    {
        int startGeneration;
        lock (StartStopGate) startGeneration = _stopGeneration;
        if (_running) return false;
        if (!WowBGrindFactions.PrepareForGrind(out string factionError))
        { Logging.Write("[Grind] faction filter could not start: " + factionError); return false; }
        Logging.Write(GrindFactionSettings.Rules.Enabled
            ? "[Grind] faction attack " + GrindFactionSettings.Rules.Mode + " filter ON: " + GrindFactionSettings.Rules.KeyText + "; blocked/unknown NPCs are withheld, even in defence; this is not proof of hostility"
            : "[Grind] faction attack filter OFF; normal targeting remains active");
        WowBReader? trialReader = WowBGrindFactions.ExperimentalBoundReader;
        MeshRoam? roam;
        try
        {
            if (!MeshRoam.TryCreate(out roam) || roam == null)
            { WowBGrindFactions.EndExperimental70707(); return false; }
        }
        catch { WowBGrindFactions.EndExperimental70707(); throw; }
        var area = new ProfileGrindArea { Name = "Profile-free local roaming" };
        // The existing area-selection loop expects at least one hotspot; actual
        // destinations are generated from the mesh at each step instead.
        area.Hotspots.Add(new RoutePoint());
        var areas = new List<ProfileGrindArea> { area };
        lock (StartStopGate)
        {
            if (startGeneration != _stopGeneration || _running)
            { WowBGrindFactions.EndExperimental70707(); return false; }
            _stop = false;
            ClickAimAbort.Clear();
            RouteRunner.StopRequested = false;
            _running = true;
            try
            {
                var thread = new Thread(() => RunGuarded(areas, 0, "", roam, trialReader)) { IsBackground = true, Name = "ClickAimGrindRoam" };
                thread.Start();
                return true;
            }
            catch { _running = false; WowBGrindFactions.EndExperimental70707(); throw; }
        }
    }

    private static void RunGuarded(List<ProfileGrindArea> areas, int minutes, string profilePath, MeshRoam? roam, WowBReader? trialReader)
    {
        try
        {
            WowBGrindFactions.ClaimExperimentalGrindThread();
            if (trialReader != null && (ObjectManager.WowB != trialReader || !trialReader.ExperimentalGrindActive)) return;
            Run(areas, minutes, profilePath, roam, trialReader);
        }
        catch (Exception ex) { Logging.Write("[Grind] worker stopped on error: " + ex.Message); }
        finally { _deathCheckpoint = null; WowBGrindFactions.EndExperimental70707(); _running = false; }
    }

    private static void Run(List<ProfileGrindArea> areas, int minutes, string profilePath, MeshRoam? roam, WowBReader? trialReader)
    {
        _deathCheckpoint = new WowBDeathCheckpoint();
        _recoveryReady = false;
        int started = Environment.TickCount;
        int budgetMs = minutes <= 0 ? int.MaxValue : minutes * 60000;
        if (trialReader != null)
            Logging.Write("[Grind] EXPERIMENTAL 70707: continuous faction-filtered Grind. Candidate stats, reaction, flags, hotkeys and movement remain unverified. Monitor closely; use STOP if anything is wrong.");

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) { Logging.Write("[Grind] no game window"); return; }

        var mover = new ClickAimMover();
        int index = 0;
        int dead = 0, roamSampleFailures = 0;
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
        _clearQuietAt = 0;
        _poolQuietAt = 0;
        _scanWasClear = false;
        _lastEngageRejected = false;
        _combatHold = false;
        _poolApproachHandled = false;
        _combatHoldLogAt = unchecked(Environment.TickCount - 15001);
        _rejectedQuietAt = unchecked(Environment.TickCount - 15001);
        _farQuietAt = 0;
        _notViable.Clear();
        _tabMisses.Clear();
        _tabRefusedUntil.Clear();
        _poolMisses.Clear();
        _lastPoolSelectMs = 0;
        _unverifiedGuid = default(WowBGuid);
        _defenceHandoffGuid = default;
        _focusedFightGuid = default(WowBGuid);
        _focusedDamageObserved = false;
        _looted.Clear();
        RecentLoot.Clear();
        _recentLootRetryAt.Clear();
        _recentLootRetried.Clear();
        _recentLootQuietAt = unchecked(Environment.TickCount - 15000);
        _loots = 0;
        _lootProbed = false;
        _vendorDue = false;
        _vendorManualPending = false;
        _vendorImpossible = false;
        _vendorRetryAt = 0;
        _lootsSinceTrip = 0;
        _killsSinceTrip = 0;
        _sellClicks = 0;
        _unreachableUntil.Clear();
        _realCombatQuietAt = 0;
        bool gatherDuringGrind = !_stop && !ClickAimAbort.Requested && GatherRunner.PrepareForGrind(profilePath);

        /* What the client can be asked without Lua: level, health, the stored action bar
         * and what is standing around. It is printed once because it is the answer to
         * "does this build let a bot see anything at all" - and if any of it is missing the
         * log says which read failed rather than pretending. */
        ReportMemory();

        /* The profile's own level bands decide which area to grind, not proximity alone: a
         * 19-SubProfile levelling file is a ladder, and grinding the step below your level is
         * exactly the "wandering on non-viable mobs" this is supposed to avoid. */
        level = PlayerLevel();
        int chosen = 0;
        if (roam == null)
        {
            chosen = SelectArea(areas, level, out string choice);
            Logging.Write("[Grind] " + choice);
        }
        else Logging.Write("[Grind] profile-free mesh roaming; no profile level bands or blackspots, existing combat safety filters still apply");
        ProfileGrindArea area = areas[chosen];

        try
        {
            bool recovered;
            do
            {
            while (!_stop && !ClickAimAbort.Requested && unchecked(Environment.TickCount - started) < budgetMs)
            {
                if (CheckDeathCheckpoint()) break;
                if (trialReader != null && (ObjectManager.WowB != trialReader || !trialReader.ExperimentalGrindActive))
                { Logging.Write("[Grind] 70707 attachment or session authorization changed; stopping"); break; }
                if (trialReader != null && !WowBGrindFactions.Experimental70707BarSafe(trialReader))
                { Logging.Write("[Grind] 70707 action bar changed or became unreadable; stopping. Restart to rescan it."); break; }
                if (roam != null && !roam.MapCurrent())
                { Logging.Write("[Roam] map changed or became unreadable; stopping"); break; }
                /* Every LevelCheckMs, read the level again: a grind profile is a ladder, and the
                 * rung below the one you are on stops being worth fighting. One 32-bit read in
                 * the player's data block, and it is the only way this loop can ever climb.
                 * Timed rather than counted, because a loop iteration is a whole hotspot leg,
                 * not a tick. */
                if (roam == null && unchecked(Environment.TickCount - lastLevelCheck) >= LevelCheckMs)
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

                // Do not wait through a long meal and let a recent corpse expire.
                // A manual or automatic vendor trip still has priority; resting
                // before that trip is handled below as before.
                if (trialReader != null && RecentLoot.Any(Environment.TickCount) &&
                    !_vendorDue && !VendorDue() && !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
                    TryReadGatherCombat(out bool preRestLootCombat) && !preRestLootCombat)
                {
                    string earlyLoot = LootNearby(hwnd, 3, recentOnly: true);
                    if (earlyLoot.Length > 0) { Logging.Write("[Grind] " + earlyLoot); continue; }
                    if (RecentLootRetryPending()) { ClickAimAbort.Wait(200); continue; }
                }

                /* No recovery step: healing needs a cast, a cast needs the action bar, and
                 * clicking a heal button 40 pixels wide on a 3D scene is a guess. Auto-attack
                 * plus the client's own swing is what a level-2 character does anyway. */

                /* Fight first, walk second. A grind area is a ring of points with mobs
                 * between them, so checking before each hop is checking every few
                 * seconds - close enough to catch an add without stopping mid-stride,
                 * which the mover cannot do yet (WalkTo has no interrupt hook). */
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

                // A dying NPC may clear selection or end combat before Fight can
                // observe zero health. Revisit only recently engaged GUIDs, before
                // gathering, a fresh pull or roaming. Combat and selling still win.
                if (trialReader != null && RecentLoot.Any(Environment.TickCount) &&
                    !_vendorDue && !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
                    TryReadGatherCombat(out bool lootCombat) && !lootCombat)
                {
                    string pendingLoot = LootNearby(hwnd, 3, recentOnly: true);
                    if (pendingLoot.Length > 0) { Logging.Write("[Grind] " + pendingLoot); continue; }
                }

                // Initial, timed and post-resurrection buff attempts are made
                // only at a quiet point, after resting/vendor priority.
                Buffs.TryApply(hwnd, () => _stop, () => StopForBuff(hwnd, mover), () => _presses++);
                if (_stop || ClickAimAbort.Requested) break;

                // A nearby resource can be gathered before chasing a distant
                // candidate, but never while in combat or an eligible pull is
                // close. The same safety check interrupts the detour/cast.
                if (gatherDuringGrind && GatherRunner.GatherForGrind(hwnd, mover, GatherAreaSafe)) continue;
                if (_stop || ClickAimAbort.Requested) break;

                string combatReport;
                if (TryEngage(hwnd, out combatReport))
                {
                    fights++;
                    // On the 70707 trial a lost selection does not confirm death.
                    // Preserve the existing 70291 accounting unchanged.
                    if (CountFightAsKill(combatReport, trialReader != null))
                    {
                        kills++;
                        _killsSinceTrip++;
                    }
                    Logging.Write("[Grind] " + combatReport);
                    if (combatReport.StartsWith("the character died", StringComparison.Ordinal)) break;
                    if (trialReader != null) PollRecentLootAfterFight(hwnd, trialReader);
                    continue;
                }

                if (combatReport.Length > 0) Logging.Write("[Grind] " + combatReport);
                if (_stop || ClickAimAbort.Requested) break;
                if (trialReader != null && ObjectManager.WowB != trialReader) break;
                if (_combatHold || _poolApproachHandled) continue;

                /* Between fights, with nothing on us: eat and drink if the user's percentages say
                 * so. This is the cheapest place in the loop to sit - the next thing it would do
                 * is walk, and walking is what cancels a meal. */
                string rested = RestIfNeeded(hwnd);
                if (rested.Length > 0) Logging.Write("[Grind] " + rested);

                // Combat and vendor checks have priority over an optional resource detour.
                if (gatherDuringGrind && GatherRunner.GatherForGrind(hwnd, mover, GatherAreaSafe)) continue;
                if (_stop || ClickAimAbort.Requested) break;

                RoutePoint next;
                if (roam != null)
                {
                    if (!roam.TryNext(out next))
                    {
                        if (!roam.MapCurrent()) { Logging.Write("[Roam] map changed or became unreadable; stopping"); break; }
                        if (++roamSampleFailures >= 5) { Logging.Write("[Roam] could not find a local mesh destination after five tries; stopping"); break; }
                        Logging.Write("[Roam] no local mesh destination found; waiting before retry");
                        ClickAimAbort.Wait(2000);
                        continue;
                    }
                    roamSampleFailures = 0;
                }
                else { next = area.Hotspots[index % area.Hotspots.Count]; index++; }

                var one = new List<RoutePoint> { next };

                /* While walking, look for a fight every few seconds instead of only at the
                 * end of the leg. The 16:07 run walked 25-40 s at a time with mobs on it and
                 * never turned round; a leg is a whole hotspot, so "check between legs" was
                 * checking every 40 s. */
                /* The same radius the character fights in: a leg is walked through the same
                 * circle a hotspot is cleared in, so "monsters between hotspots" are monsters
                 * this stops for. */
                bool resourceInterrupt = false;
                int lastFightCheck = unchecked(Environment.TickCount - 500);
                int lastRoamMapCheck = unchecked(Environment.TickCount - 500);
                RouteRunner.ShouldInterrupt = () =>
                {
                    if (roam != null && unchecked(Environment.TickCount - lastRoamMapCheck) >= 500)
                    {
                        lastRoamMapCheck = Environment.TickCount;
                        if (!roam.MapCurrent()) return true;
                    }
                    if (unchecked(Environment.TickCount - lastFightCheck) >= 500)
                    {
                        lastFightCheck = Environment.TickCount;
                        if (CheckDeathCheckpoint() || FightNeeded(hwnd, SearchYd)) return true;
                    }
                    if (gatherDuringGrind && GatherRunner.HasGrindResource(GatherAreaSafe))
                    { resourceInterrupt = true; return true; }
                    return false;
                };
                RouteRunner.PollInterruptDuringWalk = true;
                RouteRunner.RequireMeshDuringSession = roam != null;
                bool ok = false;
                try { ok = RouteRunner.RunWithinSession(mover, one, 1); }
                finally { RouteRunner.ShouldInterrupt = null; RouteRunner.PollInterruptDuringWalk = false; RouteRunner.RequireMeshDuringSession = false; }

                if (!ok && RouteRunner.LastRunInterrupted)
                {
                    if (roam != null && !roam.MapCurrent())
                    { Logging.Write("[Roam] map changed or became unreadable; stopping"); break; }
                    if (resourceInterrupt)
                    {
                        if (roam == null) index--; // resume the same profile hotspot after the detour
                        Logging.Write("[Grind] resource in range; pausing the hotspot leg to reconsider combat and gathering");
                        continue;
                    }
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
            recovered = _recoveryReady && !ClickAimAbort.Requested && RecoverIfPending(mover);
            } while (recovered && !_stop && !ClickAimAbort.Requested && unchecked(Environment.TickCount - started) < budgetMs);
        }
        catch (Exception ex)
        {
            Logging.Write("[Grind] stopped with an error: " + ex.Message);
        }
        finally
        {
            try { mover.MoveStop(); } catch { }
        }

        string bagReport = BagSlotHunt.Report();
        if (bagReport.Length > 0) Logging.Write(bagReport);

        string killSummary = trialReader != null ? "target(s) observed at zero candidate health" : "kill(s) confirmed";
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] done: {0} hotspot(s) reached, {1} fight(s), {2} " + killSummary + ", "
            + "{3} corpse loot interaction(s) sent (loot unverified), {4} key press(es), {5} click(s) that landed, {6} area switch(es), "
            + "{8} vendor trip(s), {9} sell click(s), {7:F0} s",
            hotspots, fights, kills, _loots, _presses, _clicks, switches,
            (Environment.TickCount - started) / 1000.0, vendorTrips, _sellClicks));
    }

    private static bool CountFightAsKill(string report, bool experimental70707)
    {
        bool ambiguous = report.IndexOf("killed or fled", StringComparison.Ordinal) >= 0;
        return (report.StartsWith("killed", StringComparison.Ordinal) || ambiguous) &&
            (!experimental70707 || !ambiguous);
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
        Buffs.Reset();
        _healCostGuess = 0;
        _healCostSpellId = 0;
        _castCosts.Clear();
        Logging.Write("[Grind] combat settings: " + ClickAimSettings.Describe() + "; rotation " + ClickAimSettings.RotationClass);

        int level = reader.ReadPlayerLevel();
        ulong levelOffset = reader.IsExperimental70707 ? Styx.Offsets.Wow70707Offsets.LevelCandidateOffset : Styx.Offsets.WowBOffsets.UnitDataLevelOffset;
        Logging.Write(level > 0
            ? "[Grind] memory: player level " + level.ToString(CultureInfo.InvariantCulture)
              + " (" + (reader.IsExperimental70707 ? "candidate " : "") + "level word at data+" + levelOffset.ToString("X", CultureInfo.InvariantCulture) + ")"
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

        /* Whether the eat/drink rules have anything to press, said at the top of the log.
         * "It never ate or drank, even with food on the bar" is a question a user should not have
         * to answer by searching a 200-line log for a line that is not there. The bags cannot be
         * searched on this build (there is no container offset in WowBOffsets), so a hotbar slot
         * - auto-detected by item id, or the slot number from Combat settings - is the way in,
         * and this line says which of the two answered. */
        try
        {
            List<ClickAimAction> hotbar = ClickAimCombat.ReadBar(reader);
            Logging.Write("[Grind] eating and drinking: "
                + DescribeRestItem("food", ClickAimCombat.FindFood(hotbar, ClickAimSettings.FoodSlot),
                                   ClickAimSettings.EatBelowPercent)
                + "; "
                + DescribeRestItem("drink", ClickAimCombat.FindDrink(hotbar, ClickAimSettings.DrinkSlot),
                                   ClickAimSettings.DrinkBelowPercent));
        }
        catch (Exception ex)
        {
            Logging.Write("[Grind] the eat/drink check could not read the action bar: " + ex.Message);
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

                /* A faction-template ID is useful for diagnosis but is not itself
                 * reaction, reputation, attackability or proof of a hostile target. */
                int shown = 0;
                for (int i = 0; i < snap.Units.Count && shown < 10; i++)
                {
                    WowBObservedUnit u = snap.Units[i];
                    if (u.IsLocalPlayer || u.HasPlayerVtable) continue;
                    if (GrindFactionSettings.Rules.Enabled && !WowBGrindFactions.AllowsPull(reader, u, out _)) continue;
                    double d = ClickAimMath.Len(u.Location.X - me.Location.X, u.Location.Y - me.Location.Y);
                    if (d > SearchYd) continue;
                    Styx.WoWInternals.WowBReader.WowBUnitFields uf;
                    if (!reader.TryGetUnitFields(u.UnitAddress, out uf)) continue;
                    shown++;
                    uint templateId;
                    string templateText = reader.TryReadFactionTemplateId(u.UnitAddress, out templateId)
                        ? templateId.ToString(CultureInfo.InvariantCulture) : "unreadable";
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind]   in the pool {0:F1} yd away: level {1}, hp {2}/{3}, flags 0x{4:X}, faction template ID {5} (not reaction)",
                        d, uf.Level, uf.Health, uf.MaxHealth, uf.UnitFlags, templateText));
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
        WowBReader? trialReader = WowBGrindFactions.ExperimentalBoundReader;
        if (trialReader != null && (ObjectManager.WowB != trialReader || !trialReader.ExperimentalGrindActive))
        { report = "70707 trial attachment or authorization changed"; return false; }
        _lastEngageRejected = false;
        _combatHold = false;
        _poolApproachHandled = false;
        /* Cleared on entry, not only on the scan below: a failed read must not leave a stale
         * "the area is clear" behind for the hotspot loop to act on. */
        _scanWasClear = false;

        // Combat outranks every new pull, roam leg and vendor trip. The bit does
        // not identify an attacker: defence only tries nearby changed TAB picks.
        if (!TryReadGatherCombat(out bool combatNow))
        {
            _combatHold = true;
            if (unchecked(Environment.TickCount - _combatHoldLogAt) >= 15000)
            {
                _combatHoldLogAt = Environment.TickCount;
                report = "combat state unreadable; holding position instead of starting a new pull";
            }
            ClickAimAbort.Wait(300);
            return false;
        }
        if (combatNow)
        {
            _combatHold = true;
            ClickAimKeySteer.ReleaseHeld();
            string defence = DefendNearby(hwnd, true, out bool engagedDefence);
            if (engagedDefence) { report = defence; return true; }
            if (unchecked(Environment.TickCount - _combatHoldLogAt) >= 5000)
            {
                _combatHoldLogAt = Environment.TickCount;
                report = "combat defence: " + defence;
            }
            ClickAimAbort.Wait(300);
            return false;
        }

        _defenceHandoffGuid = default; // no longer in combat; do not carry a prior selection forward

        /* A trip that was asked for is not a suggestion: nothing NEW is picked up while one is
         * waiting. (The loop asks VendorDue first, so this is the second line of defence - the
         * case it really covers is a request that arrives while this function is already
         * running.) */
        if (_vendorDue && !InRealCombat())
        {
            report = "a vendor trip is waiting - not picking up a new fight before it";
            return false;
        }

        // DwellAtHotspot also calls TryEngage directly, without the outer
        // roaming loop. Process recent bodies before it chooses another pull.
        if (trialReader != null && RecentLoot.Any(Environment.TickCount))
        {
            string pendingLoot = LootNearby(hwnd, 3, recentOnly: true);
            if (pendingLoot.Length > 0)
            {
                report = pendingLoot;
                _poolApproachHandled = true;
                return false;
            }
            if (RecentLootRetryPending())
            {
                _poolApproachHandled = true;
                ClickAimAbort.Wait(200);
                return false;
            }
        }

        // Defence always takes precedence above. On 70707 the player reader only
        // permits the observed mana-type-0 layout, so it is safe to use this
        // threshold even if an empty drink still has an action-bar icon.
        if (trialReader != null)
        {
            if (!WaitForManaBeforePull(hwnd, trialReader)) { _combatHold = true; return false; }
            // The wait may have lasted minutes; recheck before even acquiring a target.
            if (!TryReadGatherCombat(out bool combatAfterRecovery) || combatAfterRecovery)
            { _combatHold = true; return false; }
        }

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null)
        {
            report = "cannot read the world snapshot; nothing to fight";
            return false;
        }

        WowBObservedUnit player;
        if (!snap.TryGetLocalPlayer(out player)) return false;

        // A loot right-click can leave a corpse selected. F1 is verified as
        // self-target before TAB is asked again; never use an old ground pixel.
        WowBReader? deadReader = ObjectManager.WowB;
        if (deadReader != null && snap.TryGetSelectedTarget(out WowBObservedUnit deadSelected) &&
            !deadSelected.IsLocalPlayer && !deadSelected.HasPlayerVtable &&
            deadReader.TryGetUnitFields(deadSelected.UnitAddress, out WowBReader.WowBUnitFields deadFields) && deadFields.Health == 0)
        {
            DismissRejectedSelection(hwnd, deadReader, player.Guid, deadSelected.Guid);
            if (_stop || ClickAimAbort.Requested) return false;
            if (!Capture(out snap) || snap == null || !snap.TryGetLocalPlayer(out player)) return false;
        }
        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
        double px = player.Location.X, py = player.Location.Y;
        int near = CountNear(snap, px, py, SearchYd);

        /* Try the current target and TAB, then the existing unverified pool
         * left-click fallback. Pool coordinates, level and flags do not establish
         * hostility; TAB also does not prove that a selected unit is an attacker.
         * The pool pick must retain the existing TAB/damage failsafe in Fight. */
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
            // Even a clear pool can leave an unsuitable live target selected by a
            // previous walk/TAB. Move off it only when it is still the same GUID.
            WowBReader? clearReader = ObjectManager.WowB;
            if (GrindFactionSettings.Rules.Enabled)
            {
                if (!ClearNonSelfFactionSelection(hwnd, clearReader, player.Guid))
                { report = "cannot verify selection cleared; holding before continuing route"; _poolApproachHandled = true; ClickAimAbort.Wait(300); return false; }
            }
            else if (snap.TryGetSelectedTarget(out WowBObservedUnit selected)
                     && IsDismissibleSelection(selected, clearReader))
                DismissRejectedSelection(hwnd, clearReader, player.Guid, selected.Guid);
            _scanWasClear = true;
            if (unchecked(Environment.TickCount - _clearQuietAt) > 15000)
            {
                _clearQuietAt = Environment.TickCount;
                report = string.Format(CultureInfo.InvariantCulture,
                    "no eligible pull candidates within {0:F0} yd of the character ({1} unit(s) in the pool "
                    + "inside that radius) - continuing the route; this is not a hostility verdict", SearchYd, near);
            }
            return false;
        }
        _scanWasClear = false;

        /* TAB only considers what is in FRONT of the character (the 16:07 run logged
         * "behind the camera" for two and a half minutes because of that). The pool knows where
         * the nearest live unit is, so turn onto it before asking - a turn costs no walk, and it
         * is what turns a mob standing behind the character from a "nothing offered" into a
         * target. Nothing is walked yet: this is a facing change only. */
        if (!haveNearest)
            NearestKillable(snap, px, py, SearchYd, out nearest, out nearestYd, out live, out unread);
        Styx.WoWInternals.WowBReader? selReader = ObjectManager.WowB;

        /* A mob may already be selected. Take it when it passes the same tests,
         * instead of pressing TAB again and cycling past it. */
        if (TryCurrentSelection(snap, selReader, px, py, out target))
        {
            /* Still unproven if this is the unit the pool picked: see _unverifiedGuid. */
            poolPick = !_unverifiedGuid.IsEmpty && target.Guid == _unverifiedGuid;
            double selectedYd = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
            // An incidental pool click is not a reason to ignore a much nearer
            // candidate. A client-confirmed selection remains usable as before.
            have = !PreferNearerPoolCandidate(poolPick, haveNearest, target.Guid == nearest.Guid, selectedYd, nearestYd);
            if (have) Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] going with the target already selected: {0} ({1:F1} yd)", target.Guid,
                ClickAimMath.Len(target.Location.X - px, target.Location.Y - py)));
        }

        // A filter-permitted pool candidate is a navigation lead, not proof of
        // hostility. Do not click-screen-search or TAB around distant NPCs.
        // Move toward the exact allowed record on the validated mesh first.
        if (GrindFactionSettings.Rules.Enabled && !have)
        {
            if (!ClearNonSelfFactionSelection(hwnd, selReader, player.Guid))
            { report = "cannot verify selection cleared; holding before faction search"; _poolApproachHandled = true; ClickAimAbort.Wait(300); return false; }
            if (!haveNearest)
            {
                if (unchecked(Environment.TickCount - _clearQuietAt) > 15000)
                { _clearQuietAt = Environment.TickCount; report = "no readable eligible faction candidate; holding this search"; }
                _poolApproachHandled = true; ClickAimAbort.Wait(300); return false;
            }
            if (ShouldApproachPoolCandidate(have, haveNearest, nearestYd, FactionApproachYd))
            {
                _poolApproachHandled = true;
                report = ApproachPoolCandidate(hwnd, nearest, FactionApproachYd);
                return false;
            }
        }

        // The unfiltered path must also approach before the first TAB. Previously
        // its 40-yard mesh check ran only after three TAB presses.
        if (!GrindFactionSettings.Rules.Enabled && ShouldApproachPoolCandidate(have, haveNearest, nearestYd))
        {
            _poolApproachHandled = true;
            report = ApproachPoolCandidate(hwnd, nearest);
            return false;
        }
        if (!have && !haveNearest)
        {
            // Unreadable units are not evidence that a TAB target is in range.
            _combatHold = true;
            report = "no readable nearby candidate for TAB; holding while unit fields are unreadable";
            ClickAimAbort.Wait(300);
            return false;
        }

        if (!have && haveNearest && nearestYd > 2.0 &&
            (!GrindFactionSettings.Rules.Enabled || nearestYd <= FactionApproachYd))
            ClickAimKeySteer.Face(nearest.Location.X, nearest.Location.Y, hwnd, 25.0, 4);

        WowBGuid rejectedGuid = default(WowBGuid);
        for (int i = 0; i < TabTries && !have && !_stop && !ClickAimAbort.Requested && !_vendorDue; i++)
        {
            if (!TryReadGatherCombat(out bool tabCombat) || tabCombat)
            { _combatHold = true; ClickAimKeySteer.ReleaseHeld(); return false; }
            // A mob can move while turning or between TAB attempts. Recheck the
            // permitted, live pool before every press, not just before the turn.
            double tabRadius = GrindFactionSettings.Rules.Enabled ? FactionApproachYd : PoolApproachYd;
            if (!Capture(out WowBWorldSnapshot? tabSnapshot) || tabSnapshot == null ||
                !tabSnapshot.TryGetLocalPlayer(out WowBObservedUnit tabPlayer) ||
                !NearestKillable(tabSnapshot, tabPlayer.Location.X, tabPlayer.Location.Y, tabRadius,
                    out nearest, out nearestYd, out _, out _))
            {
                _poolApproachHandled = true;
                report = "no readable eligible candidate remains close enough for TAB; reconsidering the mesh approach";
                ClickAimAbort.Wait(300);
                return false;
            }
            WowBGuid beforeTab = default(WowBGuid);
            try { if (selReader != null) beforeTab = selReader.ReadTargetGuid(); }
            catch { /* Unknown pre-TAB target cannot confirm a pool pick. */ }
            ClickAimInput.TapKey(hwnd, TabVk, 40);
            if (ClickAimAbort.Wait(TabSettleMs) || _stop) return false;
            if (!Capture(out snap) || snap == null)
            {
                if (GrindFactionSettings.Rules.Enabled)
                { _poolApproachHandled = true; report = "TAB selection unreadable; holding instead of searching by click"; ClickAimAbort.Wait(300); }
                return false;
            }
            if (!TryCurrentSelection(snap, selReader, px, py, out target))
            {
                // TAB can keep selecting the same low-level, refused or otherwise
                // unsuitable unit. Do not let it block cycling or the pool fallback.
                if (GrindFactionSettings.Rules.Enabled)
                {
                    rejectedGuid = target.Guid;
                    _lastEngageRejected = !target.Guid.IsEmpty && !target.IsLocalPlayer;
                    if (!ClearNonSelfFactionSelection(hwnd, selReader, player.Guid))
                    { report = "TAB selected an unsuitable or unreadable NPC and F1 could not be verified; holding"; _poolApproachHandled = true; return false; }
                }
                else if (IsDismissibleSelection(target, selReader))
                { rejectedGuid = target.Guid; _lastEngageRejected = true; }
                continue;
            }
            // An unchanged pool pick is not proof that TAB confirmed hostility.
            poolPick = !_unverifiedGuid.IsEmpty && target.Guid == _unverifiedGuid
                       && (beforeTab.IsEmpty || beforeTab == target.Guid);
            double tabYd = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
            if (PreferNearerPoolCandidate(poolPick, haveNearest, target.Guid == nearest.Guid, tabYd, nearestYd))
            {
                // TAB kept a much farther unverified click; try the nearer pool
                // candidate instead of letting this same click win every cycle.
                rejectedGuid = target.Guid;
                _lastEngageRejected = true;
                continue;
            }
            have = true;
        }
        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
        if (!TryReadGatherCombat(out bool beforePoolCombat) || beforePoolCombat)
        { _combatHold = true; ClickAimKeySteer.ReleaseHeld(); return false; }

        if (GrindFactionSettings.Rules.Enabled && !have)
        {
            // Do not search by indiscriminate left clicks: they can select a
            // nearby NPC of a different faction. TAB can still select another
            // unit, which is rejected above and never treated as attackable.
            if (haveNearest)
            {
                if (_tabRefusedUntil.Count > 256) _tabRefusedUntil.Clear();
                _tabRefusedUntil[nearest.Guid] = unchecked(Environment.TickCount + FactionTabRetryMs);
            }
            _interruptQuietUntil = unchecked(Environment.TickCount + InterruptRetryMs);
            report = "TAB did not select an eligible allowed NPC; briefly deferring the candidate (not a hostility verdict). No pixel search was used";
            return false;
        }

        // Do not waste dozens of pixel clicks on an unselected unit near the
        // edge of the 100 yd pool. Approach on a strictly validated mesh and
        // retry normal TAB/pool selection once a candidate is within 40 yd.
        if (ShouldApproachPoolCandidate(have, haveNearest, nearestYd))
        {
            _poolApproachHandled = true;
            report = ApproachPoolCandidate(hwnd, nearest);
            return false;
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
            bool poolCombatInterrupted = false;
            int poolCombatCheckAt = unchecked(Environment.TickCount - 250);
            bool selected = FindUnitPixel(hwnd, nearest, px, py, out fx, out fy, out how, () =>
            {
                if (_stop || ClickAimAbort.Requested || _vendorDue) return true;
                if (unchecked(Environment.TickCount - poolCombatCheckAt) < 200) return false;
                poolCombatCheckAt = Environment.TickCount;
                poolCombatInterrupted = !TryReadGatherCombat(out bool fighting) || fighting;
                return poolCombatInterrupted;
            });
            // A pool click, including an incidental hit on another unit, is never
            // evidence of hostility. Remember the selection even if the following
            // snapshot/read fails, so the next attempt cannot mistake it for TAB.
            if (selected) _unverifiedGuid = nearest.Guid;
            try
            {
                WowBGuid afterPool = poolReader == null ? default(WowBGuid) : poolReader.ReadTargetGuid();
                if (!afterPool.IsEmpty && afterPool != player.Guid) _unverifiedGuid = afterPool;
            }
            catch { /* The confirmed nearest GUID above remains unverified. */ }
            if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
            if (poolCombatInterrupted || !TryReadGatherCombat(out bool afterPoolCombat) || afterPoolCombat)
            { _combatHold = true; ClickAimKeySteer.ReleaseHeld(); return false; }
            if (selected && Capture(out snap) && snap != null
                && TryCurrentSelection(snap, poolReader, px, py, out target) && target.Guid == nearest.Guid)
            {
                have = true;
                poolPick = true;
                _poolMisses.Remove(nearest.Guid);
                _unverifiedGuid = target.Guid;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] TAB offered nothing live, so the pool was used: {0} is alive at {1:F1} yd "
                    + "(level {2}) and the client selected it on a left-click at {3},{4} px - it has {5} s "
                    + "inside TAB's reach to prove itself, or TAB will be asked about it",
                    target.Guid, nearestYd, nearestLevel, fx, fy, PoolVerifyMs / 1000));
            }
            else
            {
                if (!selected)
                {
                    if (_poolMisses.Count > 256) _poolMisses.Clear();
                    if (!_poolMisses.Add(nearest.Guid))
                    {
                        _poolMisses.Remove(nearest.Guid);
                        if (_tabRefusedUntil.Count > 256) _tabRefusedUntil.Clear();
                        _tabRefusedUntil[nearest.Guid] = unchecked(Environment.TickCount + PoolPixelMissPauseMs);
                        Logging.Write("[Grind] two pixel searches missed " + nearest.Guid +
                            "; temporarily deferring it for 20 s, not classifying it friendly or unattackable");
                    }
                }
                if (unchecked(Environment.TickCount - _poolQuietAt) > 15000)
                {
                    _poolQuietAt = Environment.TickCount;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] the pool has {0} alive at {1:F1} yd (level {2}); {3}: {4}",
                        nearest.Guid, nearestYd, nearestLevel,
                        selected ? "the selection changed or could not be rechecked" : "the left-click search did not select it",
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
                        "[Grind] {0} is {1:F1} yd away; TAB did not confirm this candidate twice, "
                        + "so it is left out of the scan for {2} s (not a friendly verdict)", nearest.Guid, nearestYd,
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
            if (_lastEngageRejected && TryReadGatherCombat(out bool currentlyFighting) && !currentlyFighting)
            {
                // Let the route make progress after a bounded search; combat is never
                // suppressed, and the normal pool fallback above remains available.
                _interruptQuietUntil = unchecked(Environment.TickCount + InterruptRetryMs);
                DismissRejectedSelection(hwnd, selReader, player.Guid, rejectedGuid);
                if (unchecked(Environment.TickCount - _rejectedQuietAt) >= 15000)
                {
                    _rejectedQuietAt = Environment.TickCount;
                    Logging.Write("[Grind] TAB offered an unsuitable or previously refused live target "
                        + rejectedGuid + "; tried other targets and will move on if none can be selected");
                }
            }
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
                    string loot = LootNearby(hwnd, 2, recentOnly: trialReader != null);
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
        if (Refused(target.Guid) && !InRealCombat())
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
            // A level can become viable after the player levels up; do not put this
            // GUID on the session-long refusal list. It is rechecked on every scan.
            _lastEngageRejected = true;
            DismissRejectedSelection(hwnd, esp, player.Guid, target.Guid);
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
        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
        if (!TryReadGatherCombat(out bool beforeFightCombat) || beforeFightCombat)
        { _combatHold = true; ClickAimKeySteer.ReleaseHeld(); return false; }
        // A faction key and a TAB selection are not a live reaction verdict.
        // Keep the existing unverified-target / 10-second pool failsafe even
        // when filtered mode selected a candidate through TAB.
        if (GrindFactionSettings.Rules.Enabled) poolPick = true;
        int fightStarted = Environment.TickCount;
        LogFactionFight(esp, target, false);
        report = Fight(hwnd, px, py, poolPick, expectedPull: target);

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
                string loot = LootNearby(hwnd, 3, recentOnly: trialReader != null);
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
        if (target.IsLocalPlayer || target.HasPlayerVtable || target.Guid.IsEmpty) return false;

        if (!WowBGrindFactions.AllowsPull(reader, target, out _)) return false;
        Styx.WoWInternals.WowBReader.WowBUnitFields f;
        if (!reader.TryGetUnitFields(target.UnitAddress, out f)) return false;
        if (!SelectionFieldsValid(f.Health, f.UnitFlags, f.Level, PlayerLevel())) return false;

        /* A unit this run has already given up on must not come back through the selection.
         * The 14:23 run is that bug: the fight refused the friendly peon four times in a row -
         * "giving up on ... for this session" then "going with the target already selected",
         * 1.3 yd, forever - because the refusal list was only consulted for pool picks. */
        if (_notViable.Contains(target.Guid)) return false;
        if (Unreachable(target.Guid)) return false;
        if (Refused(target.Guid) && !InRealCombat()) return false;
        int refusedUntil;
        if (_tabRefusedUntil.TryGetValue(target.Guid, out refusedUntil)
            && unchecked(Environment.TickCount - refusedUntil) < 0)
            return false;

        double d = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        return d <= PullYd;
    }

    // A fresh selected target must still be exactly the unit admitted by the
    // faction policy. Grind defence uses the same gate; GatherBuddy does not.
    private static bool FactionPullStillAllowed(WowBReader? reader, WowBObservedUnit expected, WowBObservedUnit actual)
    {
        if (!GrindFactionSettings.Rules.Enabled) return true;
        return !expected.Guid.IsEmpty && !actual.IsLocalPlayer && !actual.HasPlayerVtable &&
            actual.Guid == expected.Guid && actual.RecordAddress == expected.RecordAddress &&
            actual.UnitAddress == expected.UnitAddress && WowBGrindFactions.AllowsPull(reader, actual, out _);
    }

    // Re-read the selected GUID immediately before attack input; the loop's
    // snapshot can be stale after facing, healing or another asynchronous change.
    private static bool FactionAttackInputAllowed(WowBReader? reader, WowBObservedUnit expected, bool enforce)
    {
        if (!enforce || !GrindFactionSettings.Rules.Enabled) return true;
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (_stop || ClickAimAbort.Requested || reader == null || memory == null) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            return ObjectManager.WowB == reader && ObjectManager.Wow == memory &&
                (!reader.IsExperimental70707 || WowBGrindFactions.Experimental70707BarSafe(reader)) &&
                reader.ReadTargetGuid() == expected.Guid && FactionPullStillAllowed(reader, expected, expected) &&
                reader.ReadTargetGuid() == expected.Guid && ObjectManager.WowB == reader && ObjectManager.Wow == memory;
        }
        catch { return false; }
    }

    // Even unfiltered defence must not send a spell after STOP, attachment or
    // target changes. The 70707 session/bar check is independent of faction mode.
    private static bool FightAttackInputAllowed(WowBReader? reader, GreenMagic.Memory? memory, WowBGuid target, WowBObservedUnit expected, bool enforceFaction)
    {
        if (_stop || ClickAimAbort.Requested || reader == null || memory == null || target.IsEmpty) return false;
        try
        {
            using (memory.TemporaryCacheState(false))
            {
                if (ObjectManager.WowB != reader || ObjectManager.Wow != memory || reader.ReadTargetGuid() != target ||
                    (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))) return false;
            }
            return !_stop && !ClickAimAbort.Requested && FactionAttackInputAllowed(reader, expected, enforceFaction);
        }
        catch { return false; }
    }

    // End the old rotation without stopping the bot. The newly selected GUID
    // is only a hint; defence re-reads combat, unit, range and faction before
    // starting a fresh fight. No spell intended for the old target is sent.
    private static string YieldForTargetChange(WowBReader? reader, GreenMagic.Memory? memory, WowBGuid previous)
    {
        ClickAimKeySteer.ReleaseHeld();
        if (reader?.IsExperimental70707 != true || memory == null || ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            return "selected target or faction changed; attack input withheld";
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return "stopped";
        if (CheckDeathCheckpoint(trackerOnly: true)) return "death/ghost safety check; fight halted for recovery if a checkpoint was captured";
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return "stopped";
        WowBGuid selected = reader.ReadTargetGuid();
        if (WowBDefenceHandoff.MayQueue(previous, selected, _stop || ClickAimAbort.Requested || RouteRunner.StopRequested))
        {
            _defenceHandoffGuid = selected;
            Logging.Write("[Grind] target changed from " + previous + " to " + selected + "; checking the new selection for defence");
            return "target changed; checking the new selection for defence";
        }
        return "selected target or faction changed; attack input withheld; defence will recheck";
    }

    private static void LogFactionFight(WowBReader? reader, WowBObservedUnit unit, bool defence)
    {
        if (!GrindFactionSettings.Rules.Enabled) return;
        string observed = WowBGrindFactions.TryReadBoundKey(reader, unit, out uint key)
            ? key.ToString(CultureInfo.InvariantCulture) : "unreadable";
        Logging.Write("[Grind] " + (defence ? "defensive" : "voluntary") + " target " + unit.Guid +
            " faction key " + observed + "; filter " + GrindFactionSettings.Rules.Mode + " " + GrindFactionSettings.Rules.KeyText +
            "; attack checks will re-read identity, selection and key");
    }

    private static bool ShouldApproachPoolCandidate(bool hasSelection, bool hasCandidate, double distance, double stopRadius = PoolApproachYd)
        => !hasSelection && hasCandidate && double.IsFinite(distance) && distance > stopRadius && distance <= SearchYd;

    private static bool PreferNearerPoolCandidate(bool unverified, bool haveNearest, bool same,
                                                   double selectedYd, double nearestYd)
        => unverified && haveNearest && !same && selectedYd > nearestYd + 10.0;

    private static bool SelectionFieldsValid(uint health, uint flags, uint mobLevel, int myLevel)
        => health > 0 && (flags & NotAFightFlag) == 0 && LevelViable(mobLevel, myLevel);

    // Do not treat a failed read or a corpse as a rejected live target. In
    // particular, the corpse path still gets its existing single loot handling.
    private static bool IsDismissibleSelection(WowBObservedUnit target, WowBReader? reader)
    {
        if (reader == null || target.Guid.IsEmpty || target.IsLocalPlayer
            || !reader.TryGetUnitFields(target.UnitAddress, out WowBReader.WowBUnitFields fields)
            || fields.Health == 0) return false;
        if (target.HasPlayerVtable || !SelectionFieldsValid(fields.Health, fields.UnitFlags, fields.Level, PlayerLevel())) return true;
        if (!WowBGrindFactions.AllowsPull(reader, target, out _)) return true;
        if (_notViable.Contains(target.Guid) || Unreachable(target.Guid)) return true;
        if (Refused(target.Guid) && !InRealCombat()) return true;
        return _tabRefusedUntil.TryGetValue(target.Guid, out int until)
            && unchecked(Environment.TickCount - until) < 0;
    }

    // F1 is the client's default self-target key, not ESC (which opens the menu).
    // Confirm both the rejected GUID immediately before the key and the player GUID
    // afterwards; if F1 is rebound, we cannot claim that the selection was changed.
    // Never interfere with a possible fight or with an unreadable combat state.
    private static void DismissRejectedSelection(IntPtr hwnd, WowBReader? reader, WowBGuid player, WowBGuid rejected)
    {
        if (_stop || ClickAimAbort.Requested || hwnd == IntPtr.Zero || reader == null
            || player.IsEmpty || rejected.IsEmpty || rejected == player || _vendorDue) return;
        try
        {
            GreenMagic.Memory? memory = ObjectManager.Wow;
            if (memory == null) return;
            using var uncached = memory.TemporaryCacheState(false);
            if (ObjectManager.WowB != reader || reader.ReadTargetGuid() != rejected
                || !TryReadGatherCombat(out bool fighting) || fighting || _stop || ClickAimAbort.Requested
                || ObjectManager.Wow != memory || ObjectManager.WowB != reader
                || reader.ReadTargetGuid() != rejected) return;
            ClickAimInput.TapKey(hwnd, 0x70, 40);
            if (ClickAimAbort.Wait(TabSettleMs) || _stop) return;
            if (ObjectManager.Wow == memory && ObjectManager.WowB == reader && reader.ReadTargetGuid() == player)
                Logging.Write("[Grind] moved selection off " + rejected + " to self (F1 verified; dismissal only, no heal)");
            else
                Logging.Write("[Grind] F1 did not confirm self-target; selection was not cleared");
        }
        catch { Logging.Write("[Grind] could not verify the rejected target was dismissed"); }
    }

    // TAB may keep or select a blocked unit. Clear it before looking
    // for a new target; F1 is not trusted unless the player's GUID is observed.
    private static bool ClearFactionSelection(IntPtr hwnd, WowBReader? reader, WowBGuid player, WowBGuid selected)
    {
        if (selected.IsEmpty || selected == player) return true;
        if (reader == null || player.IsEmpty || _stop || ClickAimAbort.Requested) return false;
        DismissRejectedSelection(hwnd, reader, player, selected);
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (memory == null || _stop || ClickAimAbort.Requested) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            return ObjectManager.Wow == memory && ObjectManager.WowB == reader && reader.ReadTargetGuid() == player;
        }
        catch { return false; }
    }

    // Read the actual selected GUID, including selections absent from the pool
    // snapshot, rather than treating an unresolved target as no selection.
    private static bool ClearNonSelfFactionSelection(IntPtr hwnd, WowBReader? reader, WowBGuid player)
    {
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (reader == null || memory == null || player.IsEmpty || _stop || ClickAimAbort.Requested) return false;
        try
        {
            WowBGuid selected;
            using (memory.TemporaryCacheState(false))
            {
                if (ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
                selected = reader.ReadTargetGuid();
            }
            return ClearFactionSelection(hwnd, reader, player, selected);
        }
        catch { return false; }
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
        if (!TryReadGatherCombat(out bool combatNow) || combatNow) return true;

        /* An interrupt that produced no fight is not worth repeating four times a second.
         * The 17:26 run logged 130 interrupt/refuse pairs in 40 s and the character never
         * took a step; this is the brake. */
        if (_interruptQuietUntil != 0 && unchecked(Environment.TickCount - _interruptQuietUntil) < 0)
        {
            // The combat check above also runs during this quiet period.
            return false;
        }

        WowBWorldSnapshot? snap;
        if (!Capture(out snap) || snap == null) return false;
        if (!snap.TryGetLocalPlayer(out WowBObservedUnit me)) return false;

        // A nearby unit's 0x800 bit is also present on idle units. It cannot identify
        // an attacker, and must not block the ordinary TAB/pool search.

        /* Nothing is fighting us - but that is not the same as "nothing here worth killing",
         * and treating the two as one is why the character walked past mobs on every leg and
         * only fought at the hotspots (the user's report). Interrupt for a level-viable
         * pool candidate; the main loop checks faction and distance before asking TAB.
         * Neither pool eligibility nor TAB is proof of hostility. */
        // Do not press TAB from a roaming leg before measuring the pool candidate.
        // The main loop approaches on mesh and then asks TAB at close range.

        /* The pool has no facing blind spot. Stop for a level-viable observation
         * within the search radius; the main loop can approach distant candidates
         * on a validated mesh before asking TAB. This does not establish hostility. */
        WowBObservedUnit poolUnit;
        double poolYd;
        int poolLive, poolUnread;
        if (NearestKillable(snap, me.Location.X, me.Location.Y, radius, out poolUnit, out poolYd,
                            out poolLive, out poolUnread))
        {
            if (unchecked(Environment.TickCount - _poolQuietAt) > 5000)
            {
                _poolQuietAt = Environment.TickCount;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] the pool has {0} alive and level-viable {1:F1} yd away ({2} in the pool inside "
                    + "{3:F0} yd, {4} of them unreadable) - stopping the walk to deal with it",
                    poolUnit.Guid, poolYd, poolLive, radius, poolUnread));
            }
            return true;
        }

        return false;
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

        /* _vendorDue ends the stay as well: the window's "Sell now" is a person watching a
         * character stand in a field, and a dwell is thirty seconds of exactly that. */
        while (!_stop && !_vendorDue && unchecked(Environment.TickCount - dwellStart) < HotspotDwellMs)
        {
            string report;
            if (TryEngage(hwnd, out report))
            {
                fights++;
                fought++;
                if (CountFightAsKill(report, ObjectManager.WowB?.IsExperimental70707 == true))
                { kills++; killed++; _killsSinceTrip++; }
                Logging.Write("[Grind] " + report);
                WowBReader? recentReader = WowBGrindFactions.ExperimentalBoundReader;
                if (recentReader != null) PollRecentLootAfterFight(hwnd, recentReader);
                clearScans = 0;               // something was here: this hotspot is not finished
                continue;
            }
            if (report.Length > 0) Logging.Write("[Grind] " + report);
            if (_combatHold || _poolApproachHandled) { clearScans = 0; continue; }
            if (_lastEngageRejected && TryReadGatherCombat(out bool fighting) && !fighting)
            {
                Logging.Write("[Grind] no acceptable target after TAB and pool checks; leaving this hotspot");
                break;
            }

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
            if (ClickAimAbort.Wait(AreaRescanMs)) break;
        }
        if (_stop || ClickAimAbort.Requested) return "";

        /* Anything left lying around while standing here is worth the walk - unless a trip is
         * waiting, in which case the bodies keep and the vendor does not. */
        string loot = "";
        if (_vendorDue)
            Logging.Write("[Grind] leaving the hotspot with a vendor trip waiting: the bodies stay put, "
                          + "the trip goes first");
        else
            loot = LootNearby(hwnd, 3, recentOnly: ObjectManager.WowB?.IsExperimental70707 == true);

        if (fought == 0 && loot.Length == 0) return "";
        if (fought == 0) return loot;
        return string.Format(CultureInfo.InvariantCulture,
            "stood at the hotspot and fought {0} time(s), {1} of them ending as kills{2}",
            fought, killed, loot.Length == 0 ? "" : "; " + loot);
    }

    // Grind and standalone Gather share this scheduler, but only one runner
    // owns the client session at a time. Each start and confirmed resurrection
    // resets it. The timer records key attempts, not aura or cast success.
    private static readonly ClickAimBuffController Buffs = new ClickAimBuffController();

    private static void StopForBuff(IntPtr hwnd, ClickAimMover mover)
    {
        bool oldQuiet = mover.QuietKeyStop;
        try { mover.QuietKeyStop = true; mover.MoveStop(); }
        finally { mover.QuietKeyStop = oldQuiet; }
        StopAndSettle(hwnd);
    }

    internal static bool BuffForGather(IntPtr hwnd, ClickAimMover mover, Func<bool> stopped)
        => Buffs.TryApply(hwnd, stopped, () => StopForBuff(hwnd, mover));

    // ------------------------------------------------------------------ the heal's price

    // This is a threshold and a net-spend estimate, not an authoritative spell-cost read.
    private static bool HealPowerOk(uint power, uint maxPower)
        => ClickAimHealPolicy.PowerAllowed(power, maxPower, _manaPeak,
                                          ClickAimSettings.HealMinManaPercent, _healCostGuess);

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

    // Between voluntary pulls, the drink percentage is also the minimum mana
    // to resume. We cannot know whether a hotbar item still exists in the bags,
    // so this applies regardless of whether a drink icon was present or pressed.
    // Currently limited to 70707: its player field reader requires mana type 0;
    // the 70291 power type is not yet reliably identified (rage must not wait).
    private static bool ManaRecoveryNeeded(int threshold, int mana)
        => threshold > 0 && (mana < 0 || mana < threshold);

    private static bool WaitForManaBeforePull(IntPtr hwnd, WowBReader reader)
    {
        int threshold = ClickAimSettings.DrinkBelowPercent;
        if (threshold <= 0) return true;
        int mana = ManaPercent();
        if (!ManaRecoveryNeeded(threshold, mana)) return true;
        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
        ClickAimKeySteer.ReleaseHeld();
        if (!StopForRest(hwnd)) return false;
        Logging.Write("[Rest] holding before another pull until mana reaches " + threshold +
                      "% (currently " + (mana < 0 ? "unreadable" : mana + "%") + "); combat, vendor and STOP interrupt the wait");
        int loggedAt = Environment.TickCount;
        while (!_stop && !ClickAimAbort.Requested && !_vendorDue && ObjectManager.WowB == reader && reader.ExperimentalGrindActive)
        {
            if (!WowBGrindFactions.Experimental70707BarSafe(reader)) return false;
            if (!TryReadGatherCombat(out bool fighting) || fighting) return false;
            mana = ManaPercent();
            if (!ManaRecoveryNeeded(threshold, mana))
            {
                Logging.Write("[Rest] mana recovered to " + mana + "%; voluntary pulls may resume");
                return true;
            }
            if (unchecked(Environment.TickCount - loggedAt) >= 15000)
            {
                loggedAt = Environment.TickCount;
                Logging.Write("[Rest] still waiting for mana " + threshold + "% (currently " +
                              (mana < 0 ? "unreadable" : mana + "%") + ")");
            }
            if (ClickAimAbort.Wait(500)) return false;
        }
        return false;
    }

    /// <summary>
    /// One half of the "eating and drinking:" line: what will be pressed, and why not if nothing
    /// will be. A slot the user set is honoured whatever is in it (<see
    /// cref="ClickAimCombat.FindItem"/>), so "I put the key in the settings" is a complete answer.
    /// </summary>
    private static string DescribeRestItem(string what, ClickAimAction? item, int belowPercent)
    {
        if (belowPercent <= 0) return what + " rule is off (0%)";
        if (item == null)
            return what + " NOT found on the action bar - put an item on a slot, or set its slot "
                   + "number in Combat settings (a slot number alone is enough)";
        return what + " '" + item.Name + "' in slot " + item.Slot.ToString(CultureInfo.InvariantCulture)
               + " (key " + ClickAimCombat.KeyLabel(item.KeyVk) + "), taken at "
               + belowPercent.ToString(CultureInfo.InvariantCulture) + "% or less";
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
        /* The old 0x800 bit remained set while idle. The candidate player combat bit
         * is 0x80000; unreadable flags conservatively postpone the meal. */
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
            if (!Capture(out around) || around == null || !around.TryGetLocalPlayer(out WowBObservedUnit here))
            {
                if (unchecked(Environment.TickCount - _restHeldQuietAt) <= 15000) return "";
                _restHeldQuietAt = Environment.TickCount;
                return "eating and drinking postponed: nearby units could not be read";
            }
            int live2, unread2;
            bool threat = NearestKillable(around, here.Location.X, here.Location.Y, RestSafeYd,
                                          out near, out nearYd, out live2, out unread2, null, false);
            if (threat || unread2 > 0)
            {
                if (unchecked(Environment.TickCount - _restHeldQuietAt) <= 15000) return "";
                _restHeldQuietAt = Environment.TickCount;
                return threat
                    ? string.Format(CultureInfo.InvariantCulture,
                        "eating and drinking can wait: something potentially attackable is {0:F0} yd away", nearYd)
                    : "eating and drinking postponed: a nearby unit could not be read";
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

        if (!StopForRest(hwnd))
        {
            if (_stop || ClickAimAbort.Requested) return "";
            if (unchecked(Environment.TickCount - _restHeldQuietAt) <= 15000) return "";
            _restHeldQuietAt = Environment.TickCount;
            return "eating and drinking postponed: could not confirm the character is stationary";
        }
        // Combat or Stop can arrive while movement is settling.
        if (_stop || ClickAimAbort.Requested || InRealCombat()) return "";
        if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader)) return "action bar changed; rest withheld";

        var said = new List<string>();
        if (wantFood)
        {
            ClickAimInput.TapKey(hwnd, food!.KeyVk, 40);
            _presses++;
            said.Add(string.Format(CultureInfo.InvariantCulture,
                "pressed food key for {0} (slot {1} '{2}') at {3}% health", food.Name, food.Slot,
                ClickAimCombat.KeyLabel(food.KeyVk), health));
        }
        if (wantDrink && !_stop && !ClickAimAbort.Requested && !InRealCombat())
        {
            ClickAimInput.TapKey(hwnd, drink!.KeyVk, 40);
            _presses++;
            said.Add(string.Format(CultureInfo.InvariantCulture,
                "pressed drink key for {0} (slot {1} '{2}') at {3}% mana", drink.Name, drink.Slot,
                ClickAimCombat.KeyLabel(drink.KeyVk), mana));
        }

        if (said.Count == 0) return "";
        Logging.Write("[Rest] " + string.Join("; ", said.ToArray()) + "; holding still");

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
            if (ClickAimAbort.Wait(500)) break;
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
    /// A corpse disappearing does not prove loot was collected. One loot click is sent per
    /// visited corpse. With skinning explicitly enabled, a user-confirmed beast entry may
    /// get one separate skinning click after the loot wait and fresh safety/identity checks.
    /// Loot window state, skinnability and actual item collection are not verified.
    ///
    /// Nothing happens while something is attacking: a fight outranks loot, and the corpse
    /// is not going anywhere.
    /// </summary>
    // Death/selection transitions and snapshots need not arrive on the same tick.
    // Give the last engaged GUID a short observation window, without waiting
    // through combat, a vendor request, death, an attachment change or STOP.
    private static void PollRecentLootAfterFight(IntPtr hwnd, WowBReader reader)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue ||
                ObjectManager.WowB != reader || !reader.ExperimentalGrindActive ||
                !RecentLoot.Any(Environment.TickCount) || CheckDeathCheckpoint() ||
                !TryReadGatherCombat(out bool fighting) || fighting || VendorDue()) return;
            string loot = LootNearby(hwnd, 3, recentOnly: true);
            if (loot.Length > 0) { Logging.Write("[Grind] " + loot); return; }
            if (attempt < 4 && ClickAimAbort.Wait(200)) return;
        }
    }

    private static bool RecentLootRetryPending()
    {
        int now = Environment.TickCount;
        foreach (int retryAt in _recentLootRetryAt.Values)
            if (unchecked(now - retryAt) < 0) return true;
        return false;
    }

    private static string LootNearby(IntPtr hwnd, int maxCorpses, bool recentOnly = false)
    {
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue || maxCorpses <= 0) return "";
        if (recentOnly)
        {
            if (!TryReadGatherCombat(out bool fighting) || fighting) return "";
        }
        else if (UnderAttack()) return "";

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return "";
        uint recentMap = uint.MaxValue;
        if (recentOnly && (!reader.TryReadMapId(out recentMap) || recentMap == uint.MaxValue)) return "";

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
            if (recentOnly && !RecentLoot.Matches(unit, recentMap, Environment.TickCount)) continue;
            if (recentOnly && _recentLootRetryAt.TryGetValue(unit.Guid, out int retryAt) &&
                unchecked(Environment.TickCount - retryAt) < 0) continue;
            // 70707 corpse classifications are candidates; never probe a body
            // outside the same active faction filter used for combat.
            if (reader.IsExperimental70707 && !WowBGrindFactions.AllowsPull(reader, unit, out _)) continue;

            double d = ClickAimMath.Len(unit.Location.X - me.Location.X, unit.Location.Y - me.Location.Y);
            if (!double.IsFinite(d) || d > LootYd || (!recentOnly && d <= 0.5) ||
                (recentOnly && Math.Abs(unit.Location.Z - me.Location.Z) > 5.0)) continue;

            Styx.WoWInternals.WowBReader.WowBUnitFields f;
            if (!reader.TryGetUnitFields(unit.UnitAddress, out f) || f.Health != 0) continue;

            int at = corpses.Count;
            for (int j = 0; j < corpses.Count; j++)
            {
                if (d < corpses[j].Key) { at = j; break; }
            }
            corpses.Insert(at, new KeyValuePair<double, WowBObservedUnit>(d, unit));
        }

        if (corpses.Count == 0)
        {
            if (recentOnly && RecentLoot.Any(Environment.TickCount) &&
                unchecked(Environment.TickCount - _recentLootQuietAt) >= 15000)
            {
                _recentLootQuietAt = Environment.TickCount;
                Logging.Write("[Loot] no recently engaged GUID is currently an eligible nearby zero-health body; " +
                    "it may be alive, missing, unreadable, blocked, out of range or already visited (loot not verified)");
            }
            return "";
        }

        int clicked = 0, gone = 0, unreached = 0;
        for (int i = 0; i < corpses.Count && clicked + unreached < maxCorpses && !_stop; i++)
        {
            WowBObservedUnit corpse = corpses[i].Value;
            if (recentOnly)
            {
                Logging.Write("[Loot] recently engaged GUID " + corpse.Guid +
                    " is now observed at zero candidate health; loot ownership is unverified");
                if (!ApproachRecentCorpse(hwnd, reader, recentMap, corpse, out string reason))
                {
                    if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue || VendorDue() ||
                        !TryReadGatherCombat(out bool stillFighting) || stillFighting) break;
                    Logging.Write("[Loot] recent corpse approach withheld: " + reason);
                    _looted.Add(corpse.Guid);
                    RecentLoot.Forget(corpse.Guid);
                    _recentLootRetryAt.Remove(corpse.Guid);
                    _recentLootRetried.Remove(corpse.Guid);
                    unreached++;
                    continue;
                }
            }
            _looted.Add(corpse.Guid);          // one visit per body, whatever happens

            int result = TryLootOne(hwnd, corpse, out uint lootMap, out bool lootPixelConfirmed, recentOnly);
            if (result == LootUnderAttack)
            {
                /* Something started fighting while the body was being reached. Nothing is
                 * recorded against the corpse beyond the one visit: a fight outranks loot. */
                if (recentOnly && (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue || VendorDue() ||
                    !TryReadGatherCombat(out bool interruptedCombat) || interruptedCombat))
                    _looted.Remove(corpse.Guid); // retry only after the interruption clears
                else if (recentOnly)
                {
                    RecentLoot.Forget(corpse.Guid);
                    _recentLootRetryAt.Remove(corpse.Guid);
                    _recentLootRetried.Remove(corpse.Guid);
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] deferred the body of {0} ({1:F1} yd away) - combat or another safety interruption",
                    corpse.Guid, corpses[i].Key));
                break;
            }
            if (result == LootNoClick)
            {
                if (recentOnly && _recentLootRetried.Count > 256)
                {
                    _recentLootRetried.Clear();
                    _recentLootRetryAt.Clear();
                }
                if (recentOnly && _recentLootRetried.Add(corpse.Guid))
                {
                    _recentLootRetryAt[corpse.Guid] = unchecked(Environment.TickCount + 5000);
                    _looted.Remove(corpse.Guid);
                    Logging.Write("[Loot] no right-button down was sent; one guarded retry is eligible after 5 seconds");
                }
                else if (recentOnly)
                {
                    RecentLoot.Forget(corpse.Guid);
                    _recentLootRetryAt.Remove(corpse.Guid);
                    _recentLootRetried.Remove(corpse.Guid);
                }
                unreached++;
                continue;
            }
            if (recentOnly)
            {
                RecentLoot.Forget(corpse.Guid);
                _recentLootRetryAt.Remove(corpse.Guid);
                _recentLootRetried.Remove(corpse.Guid);
            }

            clicked++;
            _loots++;
            _lootsSinceTrip++;
            if (result == 0 && _running && GatherSettings.Skinning)
            {
                if (!lootPixelConfirmed)
                    Logging.Write("[Skin] not attempted: loot pixel was not confirmed");
                else if (WowBSkinning.IsConfirmed(reader.IsExperimental70707, corpse.Guid))
                    TrySkinLootedBeast(hwnd, reader, corpse, lootMap);
                else
                    Logging.Write("[Skin] not attempted: unconfirmed beast entry candidate "
                        + WowBSkinning.EntryCandidate(corpse.Guid).ToString(CultureInfo.InvariantCulture));
            }

            // Observe possible bag changes; the interaction does not prove any item was collected.
            BagSlotHunt.Sample("loot interaction");
            if (result > 0)
            {
                gone++;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] sent a loot interaction for {0} ({1:F1} yd away); the corpse was not observed afterward (loot not verified)",
                    corpse.Guid, corpses[i].Key));
            }
            else
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] sent a loot interaction for {0} ({1:F1} yd away); the corpse was still observed (loot not verified)",
                    corpse.Guid, corpses[i].Key));
            }
        }

        if (clicked == 0 && unreached == 0) return "";
        return string.Format(CultureInfo.InvariantCulture,
            "looting: {0} interaction(s) sent{1}{2}", clicked,
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

        if (_vendorManualPending) return true;
        if (!ClickAimSettings.AutoSell) return false;
        int after = ClickAimSettings.SellAfterLoots;
        if (after <= 0) return false;

        // Count looted bodies only: kills alone do not imply a bag item was acquired.
        return _lootsSinceTrip >= after;
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
        _vendorManualPending |= _vendorDue;
        _vendorDue = false;
        _vendorImpossible = false;

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

        WowBReader? vendorReader = ObjectManager.WowB;
        if (vendorReader == null || !vendorReader.TryReadMapId(out uint currentMap))
        { _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs; Logging.Write("[Grind] map unreadable; town trip held"); return false; }
        int at = -1; double closest = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].TownMap != currentMap || points[i].MerchantGuids.Count == 0 ||
                points[i].Steps.Count == 0 || points[i].Steps.Count > SellPlanMaxSteps) continue;
            double townDistance = ClickAimMath.Len(points[i].X - px, points[i].Y - py);
            if (double.IsFinite(townDistance) && townDistance < closest) { closest = townDistance; at = i; }
        }
        if (at < 0)
        {
            _vendorImpossible = true;
            Logging.Write("[Grind] no complete sell plan has a town hotspot on this map, a user-confirmed merchant, and a nonempty plan within the step limit. Existing plans are unchanged; configure them under Selling.");
            return false;
        }
        VendorFile.VendorPoint point = points[at];
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] selling first, before the next hotspot: {0} is {1:F0} yd away and has {2} recorded "
            + "step(s); {3} kill(s) and {4} looted bodie(s) since the last trip", point,
            ClickAimMath.Len(point.X - px, point.Y - py), point.Steps.Count, _killsSinceTrip, _lootsSinceTrip));

        var route = new List<RoutePoint>();
        route.Add(new RoutePoint { X = point.X, Y = point.Y, Z = point.Z, Kind = "Vendor", Label = point.Name });

        /* The same interrupt a hotspot leg uses: a mob on the road is dealt with first, and the
         * trip is taken again afterwards, because the bags are still full. */
        // A pending vendor trip outranks new pulls; only actual/unknown combat
        // interrupts this leg. Do not reset the active session's STOP state.
        var previousInterrupt = RouteRunner.ShouldInterrupt;
        bool previousPoll = RouteRunner.PollInterruptDuringWalk;
        bool previousRequireMesh = RouteRunner.RequireMeshDuringSession;
        RouteRunner.ShouldInterrupt = () => !TryReadGatherCombat(out bool fighting) || fighting ||
            ObjectManager.WowB != vendorReader || !vendorReader.TryReadMapId(out uint routeMap) || routeMap != currentMap;
        RouteRunner.PollInterruptDuringWalk = true;
        RouteRunner.RequireMeshDuringSession = true;
        bool arrived = false;
        try { arrived = RouteRunner.RunWithinSession(mover, route, 1); }
        catch (Exception ex) { Logging.Write("[Grind] the walk to the town hotspot failed: " + ex.Message); }
        finally { RouteRunner.ShouldInterrupt = previousInterrupt; RouteRunner.PollInterruptDuringWalk = previousPoll; RouteRunner.RequireMeshDuringSession = previousRequireMesh; }

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
                Logging.Write("[Grind] the town walk was interrupted by STOP, combat, an unreadable state or a map change. The trip remains pending.");
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
            if (TryFindVendorNpc(point, out npc, out npcYd)) { found = true; break; }
            ClickAimAbort.Wait(400);
        }

        if (!found)
        {
            _vendorRetryAt = Environment.TickCount + VendorRetryMs;
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] no user-confirmed live merchant was found within {1} yd of the character at town point {0}. The point was {2:F0} yd away at departure.",
                point, ClickAimSettings.VendorSearchYd, ClickAimMath.Len(point.X - px, point.Y - py)));
            return false;
        }

        // Track the exact user-confirmed GUID and live object identity while
        // following a complete mesh path. Never walk directly to a stale position.
        int walkStart = Environment.TickCount;
        var vendorWatch = new ChaseWatch(walkStart);
        try
        {
            while (unchecked(Environment.TickCount - walkStart) < VendorWalkMs && !_stop && !ClickAimAbort.Requested)
            {
                if (!VendorCandidateStillValid(point, npc, out WowBObservedUnit currentNpc, out WowBObservedUnit currentPlayer, out double distance))
                { _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs; Logging.Write("[Grind] merchant, map or combat state changed; town trip stopped"); return false; }
                npc = currentNpc;
                if (distance <= VendorReachYd) break;
                if (!ChaseStep(hwnd, vendorWatch, npc, out string why, true))
                { _vendorRetryAt = Environment.TickCount + VendorRetryMs; Logging.Write("[Grind] merchant mesh approach stopped: " + why); return false; }
            }
        }
        finally { ClickAimKeySteer.ReleaseHeld(); }
        if (!StopForRest(hwnd) || !VendorCandidateStillValid(point, npc, out npc, out WowBObservedUnit nearPlayer, out double reach) || reach > VendorReachYd)
        { _vendorRetryAt = Environment.TickCount + VendorRetryMs; Logging.Write("[Grind] merchant is not safely reachable within 5 yd; no interaction sent"); return false; }
        ClickAimKeySteer.Face(npc.Location.X, npc.Location.Y, hwnd, 25.0, 4);
        // Always require a fresh left-click selection; never fall back to an
        // unverified projected right-click, even when the faction filter is off.
        string how;
        if (!TryClickRecordedMerchant(hwnd, point, npc, out how))
        { _vendorRetryAt = Environment.TickCount + VendorRetryMs; Logging.Write("[Grind] merchant interaction withheld: " + how); return false; }
        _clicks++;

        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] sent right-click at user-confirmed merchant {0} (seen {1:F1} yd away, clicked at {2:F1} yd); waiting "
            + "{3} ms before {4} recorded step(s). Merchant window is not verified",
            npc.Guid, npcYd, reach, ClickAimSettings.VendorWaitMs, point.Steps.Count));
        GreenMagic.Memory? vendorMemory = ObjectManager.Wow;
        if (vendorMemory == null) return false;
        using var vendorUncached = vendorMemory.TemporaryCacheState(false);
        if (ObjectManager.Wow != vendorMemory || ObjectManager.WowB != vendorReader || !WaitVendorSafe(ClickAimSettings.VendorWaitMs) ||
            !VendorCandidateStillValid(point, npc, out _, out _, out double waitDistance) || waitDistance > VendorReachYd ||
            ObjectManager.WowB == null || ObjectManager.WowB.ReadTargetGuid() != npc.Guid)
        { _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs; Logging.Write("[Grind] merchant wait interrupted; sell plan not replayed"); return false; }

        int sent = RunSellPlan(hwnd, point, npc);
        if (sent != point.Steps.Count || _stop || ClickAimAbort.Requested || !TryReadGatherCombat(out bool finalCombat) || finalCombat ||
            !VendorCandidateStillValid(point, npc, out _, out _, out double finalDistance) || finalDistance > VendorReachYd ||
            ObjectManager.Wow != vendorMemory || ObjectManager.WowB != vendorReader || vendorReader.ReadTargetGuid() != npc.Guid)
        { _vendorRetryAt = Environment.TickCount + VendorRetryAfterFightMs; Logging.Write("[Grind] sell plan incomplete; keeping the trip counters"); return false; }

        _lootsSinceTrip = 0;
        _killsSinceTrip = 0;
        _vendorManualPending = false;
        _vendorRetryAt = 0;

        // A replay does not establish that the merchant opened or any items sold.
        BagSlotHunt.Sample("after vendor replay (sales unverified)");
        Logging.Write(string.Format(CultureInfo.InvariantCulture,
            "[Grind] vendor replay: {0} of {1} step(s) sent ({2} click(s)). Merchant state and bag contents "
            + "are not verified; this does not confirm a sale.", sent, point.Steps.Count, _sellClicks));
        return true;
    }

    /// <summary>
    /// Replays the recorded plan: the clicks a human made once at this vendor, in order, with the
    /// waits that were recorded with them. Nothing here guesses a position - every click is a
    /// fraction of the client rectangle that was captured with the cursor over the real thing.
    /// </summary>
    private static bool WaitVendorSafe(int milliseconds)
    {
        int started = Environment.TickCount;
        do
        {
            if (_stop || ClickAimAbort.Requested || !TryReadGatherCombat(out bool fighting) || fighting) return false;
            int left = milliseconds - unchecked(Environment.TickCount - started);
            if (left <= 0) return true;
            if (ClickAimAbort.Wait(Math.Min(100, left))) return false;
        } while (true);
    }

    private static int RunSellPlan(IntPtr hwnd, VendorFile.VendorPoint point, WowBObservedUnit merchant)
    {
        int sent = 0;
        int max = Math.Min(point.Steps.Count, SellPlanMaxSteps);

        for (int i = 0; i < max && !_stop && !ClickAimAbort.Requested; i++)
        {
            VendorFile.Step step = point.Steps[i];

            /* A fight outranks the shopping. The merchant window closes by itself when the
             * character walks away, and walking away is what the loop does next. */
            if (!VendorCandidateStillValid(point, merchant, out _, out _, out double stepDistance) || stepDistance > VendorReachYd ||
                ObjectManager.WowB == null || ObjectManager.WowB.ReadTargetGuid() != merchant.Guid)
            {
                Logging.Write("[Grind] the sell plan stopped at step "
                              + (i + 1).ToString(CultureInfo.InvariantCulture)
                              + ": combat, merchant identity, selection or range is no longer safe");
                break;
            }

            if (step.IsWait)
            {
                if (!WaitVendorSafe(step.WaitMs)) break;
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

            if (!WaitVendorSafe(Math.Max(60, step.WaitMs))) break;
            sent++;
        }

        return sent;
    }

    // The only known merchants are full GUIDs explicitly confirmed by the user.
    // Faction ID, proximity and exclusion flags cannot establish merchant type.
    private static bool TryFindVendorNpc(VendorFile.VendorPoint point, out WowBObservedUnit npc, out double yards)
    {
        npc = default; yards = double.MaxValue;
        if (!TryReadGatherCombat(out bool fighting) || fighting ||
            !Capture(out WowBWorldSnapshot? snap) || snap == null || !snap.TryGetLocalPlayer(out WowBObservedUnit me)) return false;
        var reader = ObjectManager.WowB; var memory = ObjectManager.Wow;
        if (reader == null || memory == null || !reader.TryReadMapId(out uint map) || point.TownMap != map) return false;
        foreach (WowBObservedUnit candidate in snap.Units)
        {
            double d = ClickAimMath.Len(candidate.Location.X - me.Location.X, candidate.Location.Y - me.Location.Y);
            if (!double.IsFinite(d) || d <= 0.4 || d > ClickAimSettings.VendorSearchYd || d >= yards) continue;
            if (!WowBKnownMerchants.IsRecordedLive(point, candidate, memory, reader)) continue;
            npc = candidate; yards = d;
        }
        return !npc.Guid.IsEmpty;
    }

    private static bool VendorCandidateStillValid(VendorFile.VendorPoint point, WowBObservedUnit original,
        out WowBObservedUnit npc, out WowBObservedUnit player, out double distance)
    {
        npc = default; player = default; distance = double.MaxValue;
        if (_stop || ClickAimAbort.Requested || _vendorDue || !TryReadGatherCombat(out bool fighting) || fighting) return false;
        var memory = ObjectManager.Wow; var reader = ObjectManager.WowB;
        if (memory == null || reader == null || !reader.TryReadMapId(out uint map) || point.TownMap != map ||
            !Capture(out WowBWorldSnapshot? snap) || snap == null || !snap.TryGetLocalPlayer(out player) ||
            !snap.TryGetUnit(original.Guid, out npc) || npc.RecordAddress != original.RecordAddress || npc.UnitAddress != original.UnitAddress ||
            !WowBKnownMerchants.IsRecordedLive(point, npc, memory, reader)) return false;
        distance = ClickAimMath.Len(npc.Location.X - player.Location.X, npc.Location.Y - player.Location.Y);
        return double.IsFinite(distance) && !_stop && !ClickAimAbort.Requested && !_vendorDue && ObjectManager.Wow == memory && ObjectManager.WowB == reader;
    }

    private static bool TryClickRecordedMerchant(IntPtr hwnd, VendorFile.VendorPoint point, WowBObservedUnit original, out string how)
    {
        how = "merchant identity or combat state unavailable";
        if (!VendorCandidateStillValid(point, original, out WowBObservedUnit npc, out WowBObservedUnit me, out double distance) || distance > VendorReachYd) return false;
        WowBReader? reader = ObjectManager.WowB; GreenMagic.Memory? memory = ObjectManager.Wow;
        if (reader == null || memory == null || !ClearNonSelfFactionSelection(hwnd, reader, me.Guid)) return false;
        if (!FindUnitPixel(hwnd, npc, me.Location.X, me.Location.Y, out int x, out int y, out how,
            () => !VendorCandidateStillValid(point, original, out _, out _, out double probeDistance) || probeDistance > VendorReachYd)) return false;
        if (!VendorCandidateStillValid(point, original, out _, out _, out double finalDistance) || finalDistance > VendorReachYd ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader || reader.ReadTargetGuid() != original.Guid)
        { how = "merchant or selection changed after the left-click probe"; return false; }
        ClickAimInput.Click(hwnd, x, y);
        return true;
    }

    // Re-read the exact engaged body and player, not the current selection.
    // The candidate fields and map are experimental and loot rights are unknown.
    private static bool RecentCorpseCurrent(WowBReader reader, GreenMagic.Memory memory, uint map,
        WowBObservedUnit original, out WowBObservedUnit corpse, out WowBObservedUnit player, out double distance)
    {
        corpse = default; player = default; distance = double.MaxValue;
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue || CheckDeathCheckpoint() ||
            ObjectManager.WowB != reader || ObjectManager.Wow != memory ||
            !TryReadGatherCombat(out bool fighting) || fighting || VendorDue() ||
            !reader.TryReadMapId(out uint currentMap) || currentMap != map ||
            !Capture(out WowBWorldSnapshot? snap) || snap == null || !snap.TryGetLocalPlayer(out player) ||
            !snap.TryGetUnit(original.Guid, out corpse) || corpse.IsLocalPlayer || corpse.HasPlayerVtable ||
            corpse.RecordAddress != original.RecordAddress || corpse.UnitAddress != original.UnitAddress ||
            !RecentLoot.Matches(corpse, map, Environment.TickCount) ||
            !reader.TryGetUnitFields(corpse.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health != 0 ||
            !WowBGrindFactions.AllowsPull(reader, corpse, out _)) return false;
        distance = ClickAimMath.Len(corpse.Location.X - player.Location.X, corpse.Location.Y - player.Location.Y);
        return double.IsFinite(distance) && distance <= LootYd &&
            Math.Abs(corpse.Location.Z - player.Location.Z) <= 5.0 &&
            !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && !_vendorDue &&
            ObjectManager.WowB == reader && ObjectManager.Wow == memory;
    }

    private static bool RecentLootInReach(WowBObservedUnit corpse, WowBObservedUnit player)
    {
        double dx = corpse.Location.X - player.Location.X, dy = corpse.Location.Y - player.Location.Y;
        double dz = corpse.Location.Z - player.Location.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return double.IsFinite(distance) && distance <= LootReachYd;
    }

    private static bool ApproachRecentCorpse(IntPtr hwnd, WowBReader reader, uint map, WowBObservedUnit original, out string reason)
    {
        reason = "identity, zero health, map or combat state unavailable";
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (memory == null) return false;
        int started = Environment.TickCount;
        var watch = new ChaseWatch(started);
        try
        {
            while (unchecked(Environment.TickCount - started) < LootWalkMs)
            {
                if (CheckDeathCheckpoint() || !RecentCorpseCurrent(reader, memory, map, original,
                        out WowBObservedUnit corpse, out WowBObservedUnit player, out double distance)) return false;
                if (distance <= RecentLootApproachYd)
                {
                    if (!RecentLootInReach(corpse, player))
                    { reason = "corpse is too far vertically for a guarded loot click"; return false; }
                    return true;
                }
                if (!ChaseStep(hwnd, watch, corpse, out reason, true)) return false;
            }
            reason = "mesh approach timed out";
            return false;
        }
        finally { ClickAimKeySteer.ReleaseHeld(); }
    }

    /// <summary>
    /// Attempts one corpse interaction: 1 when a body is no longer observed, 0 when it
    /// remains in the pool, <see cref="LootNoClick"/> when no right-button down was sent,
    /// or <see cref="LootUnderAttack"/> on a safety interruption. A selected pixel and
    /// posted input do not prove loot was received. Recent 70707 corpses use a mesh approach.
    /// </summary>
    private static int TryLootOne(IntPtr hwnd, WowBObservedUnit corpse, out uint lootMap, out bool lootPixelConfirmed, bool recentOnly = false)
    {
        lootMap = uint.MaxValue;
        lootPixelConfirmed = false;
        WowBReader? lootReader = ObjectManager.WowB;
        if (lootReader != null && lootReader.TryReadMapId(out uint currentMap)) lootMap = currentMap;
        GreenMagic.Memory? recentMemory = ObjectManager.Wow;
        uint recentMap = lootMap;
        Func<bool>? recentSafe = recentOnly && lootReader != null && recentMemory != null
            ? () => RecentCorpseCurrent(lootReader, recentMemory, recentMap, corpse, out WowBObservedUnit currentCorpse, out WowBObservedUnit currentPlayer, out _) &&
                    RecentLootInReach(currentCorpse, currentPlayer)
            : null;
        if (recentOnly && (recentSafe == null || !recentSafe())) return LootUnderAttack;
        /* Get within reach first, and mean it. A right-click on a body further away is a walk
         * order, not a loot - the 18:44 run spent a whole loot window on that. */
        int walkStart = Environment.TickCount;
        while (unchecked(Environment.TickCount - walkStart) < LootWalkMs && !_stop)
        {
            double px, py;
            if (!ClickAimPositions.TryRead(out px, out py)) break;
            double d = ClickAimMath.Len(corpse.Location.X - px, corpse.Location.Y - py);
            if (d <= LootReachYd) break;
            if (recentOnly) return LootNoClick; // recent bodies use the validated mesh approach only
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

        // A killing blow commonly leaves this corpse selected. For an opted-in
        // beast, clear it safely before the loot probe so that both the first
        // interaction and the later skinning click can use a confirmed pixel.
        GreenMagic.Memory? lootMemory = ObjectManager.Wow;
        if (_running && GatherSettings.Skinning && lootReader != null && lootMemory != null &&
            WowBSkinning.IsConfirmed(lootReader.IsExperimental70707, corpse.Guid) &&
            lootReader.ReadTargetGuid() == corpse.Guid &&
            Capture(out WowBWorldSnapshot? preLoot) && preLoot != null && preLoot.TryGetLocalPlayer(out WowBObservedUnit lootPlayer) &&
            SkinCorpseCurrent(lootReader, lootMemory, lootMap, corpse, lootPlayer.Guid, out _, out _))
            DismissRejectedSelection(hwnd, lootReader, lootPlayer.Guid, corpse.Guid);

        /* One probe per session: five windows of the corpse's own object, read before and after
         * a click the client CONFIRMED selected the body. */
        bool probe = !_lootProbed && !_clickBiasKnown;
        uint[][]? before = probe ? CaptureWords(corpse) : null;

        string how;
        if (!TryClickUnit(hwnd, corpse, lx, ly, true, out how, out lootPixelConfirmed, recentSafe))
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Loot] could not reach the body of {0} with the mouse: {1}", corpse.Guid, how));
            if (recentOnly && (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue || VendorDue() ||
                !TryReadGatherCombat(out bool lootInterrupted) || lootInterrupted)) return LootUnderAttack;
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
            if (ClickAimAbort.Wait(300) || _stop) break;
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
            "[Loot] requested right-click on {0} ({1}); {2}; loot is not verified", corpse.Guid, how,
            result > 0 ? "body not observed afterward" : "body still observed"));

        // Loot can leave the corpse selected. Do not click a stale screen pixel:
        // if it is still selected and combat is clear, F1 targets self and is verified.
        WowBReader? afterLootReader = ObjectManager.WowB;
        if (!_stop && !ClickAimAbort.Requested && afterLootReader != null &&
            Capture(out WowBWorldSnapshot? afterLoot) && afterLoot != null &&
            afterLoot.TryGetLocalPlayer(out WowBObservedUnit afterLootPlayer))
            DismissRejectedSelection(hwnd, afterLootReader, afterLootPlayer.Guid, corpse.Guid);

        return result;
    }

    // Skinning is separate from the single loot click: only a user-confirmed beast
    // entry candidate is eligible, and neither the loot nor the skin is observable.
    private static bool SkinQuiet(WowBReader reader, GreenMagic.Memory memory, uint map)
    {
        if (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue ||
            !GatherSettings.Skinning || map == uint.MaxValue || ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            return false;
        if (CheckDeathCheckpoint() || !reader.TryReadMapId(out uint now) || now != map ||
            !TryReadGatherCombat(out bool fighting) || fighting || VendorDue()) return false;
        return !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested && !_vendorDue &&
            ObjectManager.Wow == memory && ObjectManager.WowB == reader;
    }

    private static bool SkinCorpseCurrent(WowBReader reader, GreenMagic.Memory memory, uint map,
                                          WowBObservedUnit original, WowBGuid playerGuid,
                                          out WowBObservedUnit current, out WowBObservedUnit player)
    {
        current = default; player = default;
        if (!SkinQuiet(reader, memory, map) || !WowBSkinning.IsConfirmed(reader.IsExperimental70707, original.Guid) ||
            !Capture(out WowBWorldSnapshot? snapshot) || snapshot == null ||
            !snapshot.TryGetLocalPlayer(out player) || player.Guid != playerGuid ||
            !snapshot.TryGetUnit(original.Guid, out current) || current.IsLocalPlayer || current.HasPlayerVtable ||
            current.RecordAddress != original.RecordAddress || current.UnitAddress != original.UnitAddress ||
            !reader.TryGetUnitFields(current.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health != 0 ||
            (reader.IsExperimental70707 && !WowBGrindFactions.AllowsPull(reader, current, out _))) return false;
        double dx = current.Location.X - player.Location.X, dy = current.Location.Y - player.Location.Y;
        double dz = current.Location.Z - player.Location.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= LootReachYd + 1.0 && SkinQuiet(reader, memory, map);
    }

    private static void TrySkinLootedBeast(IntPtr hwnd, WowBReader reader, WowBObservedUnit original, uint map)
    {
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (memory == null) return;
        using var uncached = memory.TemporaryCacheState(false);
        if (!SkinQuiet(reader, memory, map) ||
            !Capture(out WowBWorldSnapshot? snap) || snap == null || !snap.TryGetLocalPlayer(out WowBObservedUnit player)) return;
        WowBGuid playerGuid = player.Guid;
        if (!SkinCorpseCurrent(reader, memory, map, original, playerGuid, out WowBObservedUnit corpse, out player)) return;

        // First loot normally dismissed the corpse. If not, clear it with verified
        // F1; never use the unverified projected-point fallback for skinning.
        if (reader.ReadTargetGuid() == corpse.Guid)
            DismissRejectedSelection(hwnd, reader, playerGuid, corpse.Guid);
        if (!SkinCorpseCurrent(reader, memory, map, original, playerGuid, out corpse, out player) ||
            reader.ReadTargetGuid() == corpse.Guid || memory.WindowHandle != hwnd ||
            !ClickAimInput.ClientSize(hwnd, out int width, out int height)) return;
        if (!FindUnitPixel(hwnd, corpse, player.Location.X, player.Location.Y, out int x, out int y, out string how,
            () => !SkinCorpseCurrent(reader, memory, map, original, playerGuid, out _, out _)))
        { Logging.Write("[Skin] no confirmed corpse pixel: " + how); return; }
        int pixelX = width / 2 + x, pixelY = height / 2 + y;
        if (pixelX <= 0 || pixelY <= 0 || pixelX >= width - 1 || pixelY >= height - 1 ||
            !ClickAimInput.PostMouseMoveFraction(hwnd, pixelX / (double)(width - 1), pixelY / (double)(height - 1)))
        { Logging.Write("[Skin] right-click withheld: could not position the client cursor"); return; }
        if (ClickAimAbort.Wait(200) || _stop) return;
        bool hoverRead = reader.TryReadMouseoverGuidCandidate(out WowBGuid skinHover);
        bool sent = ClickAimInput.ConfirmedRightClick(hwnd, pixelX, pixelY, width, height, () =>
            memory.WindowHandle == hwnd && SkinCorpseCurrent(reader, memory, map, original, playerGuid, out _, out _) &&
            reader.ReadTargetGuid() == original.Guid && reader.TryReadMouseoverGuidCandidate(out WowBGuid hover) && hover == original.Guid &&
            !_stop && !ClickAimAbort.Requested, out string reason);
        if (!sent)
        {
            Logging.Write("[Skin] right-click withheld: " + reason + "; candidate hover before final check " +
                (!hoverRead ? "unreadable" : skinHover == original.Guid ? "matched" : "did not match") + " (unverified)");
            return;
        }
        _clicks++;
        Logging.Write("[Skin] requested one right-click on confirmed beast " + original.Guid
            + "; loot state, skinning cast and items are not verified");

        // No turns or movement while a possible cast/auto-loot runs. Yield promptly
        // to combat, map changes, death, vendor requests and user STOP.
        for (int waited = 0; waited < 6000; waited += 200)
            if (ClickAimAbort.Wait(200) || !SkinQuiet(reader, memory, map)) return;
        if (SkinQuiet(reader, memory, map) && reader.ReadTargetGuid() == original.Guid)
            DismissRejectedSelection(hwnd, reader, playerGuid, original.Guid);
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

    /// <summary>
    /// Largest observed net power drop after this spell's heal key, reset for a different
    /// spell/rank and for each run. Regeneration or other effects can distort this estimate;
    /// it is not authoritative spell-cost or cast-success data.
    /// </summary>
    private static uint _healCostGuess;
    private static int _healCostSpellId;

    /// <summary>
    /// What each damage spell has been seen to cost, by spell id. Same reasoning as the heal,
    /// and it is what stops the character from casting with power it does not have: below the
    /// price of the spell the rotation stops trying and swings instead.
    /// </summary>
    private static readonly Dictionary<int, uint> _castCosts = new Dictionary<int, uint>();
    // Conservative estimates survive target changes; these are NOT client cooldown reads.
    private static readonly Dictionary<string, int> _rotationCooldowns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
                ? " - no loot-state signal is established in these windows; disappearance also does not prove loot"
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
                                        out int live, out int unread, int? playerLevel = null, bool applyFactionFilter = true)
    {
        unit = default(WowBObservedUnit);
        yards = double.MaxValue;
        live = 0;
        unread = 0;

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        int myLevel = playerLevel ?? PlayerLevel();

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

            if (applyFactionFilter && reader != null && !WowBGrindFactions.AllowsPull(reader, candidate, out bool factionUnknown))
            { if (factionUnknown) unread++; continue; }
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
    /// Player-only 70291 candidate. A controlled sample read +0x1E60 as 0x808 idle,
    /// 0x80808 fighting and 0x8 after combat. The candidate is 0x80000, not 0x800.
    /// One cycle is promising but not proof for every class or situation. Unknown reads
    /// conservatively hold rest/vendor actions. Never infer a particular attacker from it.
    /// </summary>
    private static bool CombatFlagSaysFight(uint? flags)
    {
        return !flags.HasValue || (flags.Value & PlayerCombatFlagCandidate) != 0;
    }

    internal static bool TryReadGatherCombat(out bool fighting)
    {
        fighting = false;
        WowBReader? reader = ObjectManager.WowB;
        GreenMagic.Memory? memory = ObjectManager.Wow;
        try
        {
            if (reader == null || memory == null) return false;
            using var uncached = memory.TemporaryCacheState(false);
            if (!Capture(out WowBWorldSnapshot? snap) || snap == null
                || !snap.TryGetLocalPlayer(out WowBObservedUnit player)
                || !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields fields)
                || fields.MaxHealth == 0 || fields.Health == 0
                || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            fighting = (fields.UnitFlags & PlayerCombatFlagCandidate) != 0;
            return true;
        }
        catch { return false; }
    }

    // Nearby NPCs can be attackers even when unlisted. Further out, retain
    // priority for level-viable allowed pulls; an unreadable key/health blocks
    // conservatively. Neither proximity nor an allowlist proves hostility.
    private const double GatherNpcSafetyYd = 12.0, GatherPullPriorityYd = 40.0;
    private static bool GatherUnitBlocks(bool local, bool player, double distance, bool readable, uint health,
                                         bool eligible, bool factionUnknown)
    {
        if (local || player || distance > GatherPullPriorityYd) return false;
        if (!double.IsFinite(distance)) return true;
        if (!readable) return true;
        if (health == 0) return false;
        return distance <= GatherNpcSafetyYd || factionUnknown || eligible;
    }

    private static bool GatherAreaSafe()
    {
        if (_stop || ClickAimAbort.Requested || _vendorDue) return false;
        WowBReader? reader = ObjectManager.WowB;
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (reader == null || memory == null) return false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!Capture(out WowBWorldSnapshot? snap) || snap == null
                || !snap.TryGetLocalPlayer(out WowBObservedUnit player)
                || !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields self)
                || self.Health == 0 || (self.UnitFlags & PlayerCombatFlagCandidate) != 0) return false;
            foreach (WowBObservedUnit unit in snap.Units)
            {
                if (unit.IsLocalPlayer || unit.HasPlayerVtable) continue;
                double distance = ClickAimMath.Len(unit.Location.X - player.Location.X, unit.Location.Y - player.Location.Y);
                if (distance > GatherPullPriorityYd) continue;
                bool readable = reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields);
                bool unknown = false;
                bool eligible = readable && fields.Health > 0 && LevelViable(fields.Level, (int)self.Level) &&
                    WowBGrindFactions.AllowsPull(reader, unit, out unknown);
                if (GatherUnitBlocks(unit.IsLocalPlayer, unit.HasPlayerVtable, distance, readable, readable ? fields.Health : 0, eligible, unknown))
                    return false;
            }
            return !_stop && !ClickAimAbort.Requested && !_vendorDue && ObjectManager.Wow == memory && ObjectManager.WowB == reader;
        }
        catch { return false; }
    }

    // Gather uses the Grind rotation only while the player combat candidate is set.
    // A nearby selection, even one made with TAB, does not identify the attacker.
    internal static void PrepareGatherDefence()
    {
        lock (StartStopGate)
        {
            if (ClickAimAbort.Requested) return;
            _stop = false;
        }
        _vendorDue = false;
        _notViable.Clear();
        _unreachableUntil.Clear();
        _tabRefusedUntil.Clear();
        _unverifiedGuid = default(WowBGuid);
        _defenceHandoffGuid = default;
        _focusedFightGuid = default(WowBGuid);
        _focusedDamageObserved = false;
        _gaveUpAt = 0;
        ClickAimSettings.Load();
        Buffs.Reset();
        _manaPeak = 0;
        _healthPeak = 0;
        _healCostGuess = 0;
        _healCostSpellId = 0;
        _castCosts.Clear();
        _restHeldQuietAt = unchecked(Environment.TickCount - 15001);
        // Seed the observed peaks now; if already low and max-power is unreadable,
        // a reliable mana percentage cannot be reconstructed from one observation.
        int health = HealthPercent();
        int mana = ManaPercent();
        Logging.Write("[Gather] recovery uses Combat settings and configured food/drink hotbar slots; initial health="
                      + health + "% mana=" + mana + "% (power maximum is a candidate; session peak is also used)");
    }

    internal static void ClearGatherFightAfterRecovery()
    {
        // Do not touch _stop here: STOP may have arrived during recovery.
        _focusedFightGuid = default; _defenceHandoffGuid = default; _focusedDamageObserved = false; _unverifiedGuid = default;
        _combatHold = false; _poolApproachHandled = false;
        Buffs.Reset(); // Gather calls this only after confirmed revival.
    }

    internal static string RestForGather(IntPtr hwnd)
    {
        return RestIfNeeded(hwnd);
    }

    private static bool ShouldYieldRemotePull(double targetYd, double castRange, bool nearbyOther, bool attackInitiated = false)
        => targetYd > GatherDefenceRadius &&
           (targetYd > Math.Max(castRange, MeleeEngageYd) || (nearbyOther && !attackInitiated));

    // An NPC nearby is only a reason to reconsider a remote pull, never an
    // assertion that it is hostile or the attacker.
    private static bool HasNearbyOtherNpc(WowBWorldSnapshot snap, WowBObservedUnit player, WowBGuid target, WowBReader? reader)
    {
        if (reader == null) return true;
        foreach (WowBObservedUnit unit in snap.Units)
        {
            if (unit.IsLocalPlayer || unit.HasPlayerVtable || unit.Guid == target || unit.Guid.IsEmpty) continue;
            if (ClickAimMath.Len(unit.Location.X - player.Location.X, unit.Location.Y - player.Location.Y) > GatherDefenceRadius) continue;
            if (!reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields) || fields.Health > 0) return true;
        }
        return false;
    }

    // Proximity is a heuristic, not attacker identity or hostility proof. Never chase
    // a remote TAB pick while a nearby NPC is hitting the character.
    private const double GatherDefenceRadius = 12.0;

    internal static string DefendGatherer(IntPtr hwnd)
        => DefendNearby(hwnd, ObjectManager.WowB?.IsExperimental70707 == true, out _);

    // Levels are deliberately absent: while combat is active, a grey or high-level
    // attacker is still a threat. This is not a hostility/attacker predicate.
    private static bool DefenceFieldsValid(uint health, uint flags, bool previouslyRefused)
        => health > 0 && (flags & NotAFightFlag) == 0 && !previouslyRefused;

    private static bool TryTakeDefenceHandoff(WowBReader reader, bool grindMode,
                                              out WowBObservedUnit player, out WowBObservedUnit selected)
    {
        player = default; selected = default;
        WowBGuid queued = _defenceHandoffGuid;
        _defenceHandoffGuid = default; // never carry a stale selection into a later fight
        if (queued.IsEmpty || _stop || ClickAimAbort.Requested || RouteRunner.StopRequested || ObjectManager.WowB != reader ||
            !TryReadGatherCombat(out bool fighting) || !fighting ||
            !Capture(out WowBWorldSnapshot? snap) || snap == null ||
            !snap.TryGetLocalPlayer(out player) || !snap.TryGetSelectedTarget(out selected)) return false;
        bool fieldsValid = reader.TryGetUnitFields(selected.UnitAddress, out WowBReader.WowBUnitFields fields) &&
            DefenceFieldsValid(fields.Health, fields.UnitFlags, grindMode && _notViable.Contains(selected.Guid));
        bool factionAllowed = !grindMode || WowBGrindFactions.AllowsPull(reader, selected, out _);
        double distance = ClickAimMath.Len(selected.Location.X - player.Location.X, selected.Location.Y - player.Location.Y);
        return WowBDefenceHandoff.MayFight(queued, selected.Guid, _stop || ClickAimAbort.Requested || RouteRunner.StopRequested,
                   fighting, selected.IsLocalPlayer, selected.HasPlayerVtable, fieldsValid, factionAllowed, distance, GatherDefenceRadius) &&
               ObjectManager.WowB == reader && reader.ReadTargetGuid() == queued &&
               TryReadGatherCombat(out fighting) && fighting && !_stop && !ClickAimAbort.Requested && !RouteRunner.StopRequested;
    }

    private static string FightDefenceWithHandoffs(IntPtr hwnd, WowBReader reader, bool grindMode,
                                                    WowBObservedUnit player, WowBObservedUnit target)
    {
        string report = "";
        // Bound one chain; any remaining handoff is processed by the next
        // defence loop so a stream of changes cannot monopolize the worker.
        for (int count = 0; count < 8 && !_stop && !ClickAimAbort.Requested; count++)
        {
            report = Fight(hwnd, player.Location.X, player.Location.Y, false, true, target.Guid, target, grindMode);
            if (_defenceHandoffGuid.IsEmpty || _stop || ClickAimAbort.Requested || RouteRunner.StopRequested) return report;
            if (CheckDeathCheckpoint(trackerOnly: true)) return "death/ghost safety check; fight halted for recovery if a checkpoint was captured";
            if (count == 7) return report;
            if (!TryTakeDefenceHandoff(reader, grindMode, out player, out target))
            {
                if (!_stop && !ClickAimAbort.Requested)
                    Logging.Write("[Grind] changed target could not be validated for defence; rechecking nearby units");
                return report;
            }
            Logging.Write("[Grind] continuing defence on changed selected NPC " + target.Guid + "; attacker identity remains unknown");
            if (grindMode) LogFactionFight(reader, target, true);
        }
        return report;
    }

    private static string DefendNearby(IntPtr hwnd, bool grindMode, out bool engaged)
    {
        engaged = false;
        if (_stop || ClickAimAbort.Requested || !TryReadGatherCombat(out bool fighting) || !fighting)
        { _defenceHandoffGuid = default; return ""; }
        WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return "cannot read nearby units";
        if (!_defenceHandoffGuid.IsEmpty && TryTakeDefenceHandoff(reader, grindMode, out WowBObservedUnit handoffPlayer, out WowBObservedUnit handoffTarget))
        {
            Logging.Write("[Grind] continuing defence on changed selected NPC " + handoffTarget.Guid + "; attacker identity remains unknown");
            if (grindMode) LogFactionFight(reader, handoffTarget, true);
            engaged = true;
            try { return FightDefenceWithHandoffs(hwnd, reader, grindMode, handoffPlayer, handoffTarget); }
            finally { ClickAimKeySteer.ReleaseHeld(); }
        }
        if (!Capture(out WowBWorldSnapshot? snap) || snap == null
            || !snap.TryGetLocalPlayer(out WowBObservedUnit player)) return "cannot read nearby units";
        var nearby = new List<WowBObservedUnit>();
        int inRange = 0, unreadable = 0, dead = 0, flagged = 0, refused = 0, factionBlocked = 0;
        foreach (WowBObservedUnit unit in snap.Units)
        {
            if (unit.IsLocalPlayer || unit.HasPlayerVtable || unit.Guid.IsEmpty) continue;
            double d = ClickAimMath.Len(unit.Location.X - player.Location.X, unit.Location.Y - player.Location.Y);
            if (d > GatherDefenceRadius) continue;
            inRange++;
            if (!reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields))
            { unreadable++; continue; }
            bool wasRefused = grindMode && _notViable.Contains(unit.Guid);
            if (!DefenceFieldsValid(fields.Health, fields.UnitFlags, wasRefused))
            {
                if (fields.Health == 0) dead++;
                else if ((fields.UnitFlags & NotAFightFlag) != 0) flagged++;
                else refused++;
                continue;
            }
            // The attacker is not known. With the Grind faction filter enabled, a
            // nearby TAB pick is not enough to override the user's hard limit.
            if (grindMode && !WowBGrindFactions.AllowsPull(reader, unit, out _))
            { factionBlocked++; continue; }
            nearby.Add(unit);
        }
        nearby.Sort((a, b) => ClickAimMath.Len(a.Location.X - player.Location.X, a.Location.Y - player.Location.Y)
            .CompareTo(ClickAimMath.Len(b.Location.X - player.Location.X, b.Location.Y - player.Location.Y)));
        WowBObservedUnit focused = default;
        if (_focusedDamageObserved && !_focusedFightGuid.IsEmpty && snap.TryGetSelectedTarget(out focused) && focused.Guid == _focusedFightGuid &&
            !focused.IsLocalPlayer && !focused.HasPlayerVtable &&
            ClickAimMath.Len(focused.Location.X - player.Location.X, focused.Location.Y - player.Location.Y) <= ClickAimCombat.CasterRangeYd &&
            reader.TryGetUnitFields(focused.UnitAddress, out WowBReader.WowBUnitFields focusFields) &&
            DefenceFieldsValid(focusFields.Health, focusFields.UnitFlags, grindMode && _notViable.Contains(focused.Guid)) &&
            (!grindMode || WowBGrindFactions.AllowsPull(reader, focused, out _)))
        {
            Logging.Write("[Grind] continuing the already damaged selected target " + focused.Guid + "; attacker identity remains unknown");
            if (grindMode) LogFactionFight(reader, focused, true);
            engaged = true;
            try { return FightDefenceWithHandoffs(hwnd, reader, grindMode, player, focused); }
            finally { ClickAimKeySteer.ReleaseHeld(); }
        }
        if (nearby.Count == 0)
            return string.Format(CultureInfo.InvariantCulture,
                "no eligible live NPC within 12 yd; holding position (nearby {0}, unreadable {1}, dead {2}, excluded flags {3}, previously refused {4}, faction blocked {5}); {6}",
                inRange, unreadable, dead, flagged, refused, factionBlocked,
                grindMode && GrindFactionSettings.Rules.Enabled ? "attacker identity is unknown; blocked NPCs cannot be safely identified as attackers" : "attacker may be ranged or missing from the snapshot");
        int candidates = Math.Min(nearby.Count, 6);
        for (int i = 0; i < candidates && !_stop && !ClickAimAbort.Requested; i++)
        {
            WowBObservedUnit candidate = nearby[i];
            // Turning is only an aim for TAB. Pool proximity/flags do NOT prove hostility.
            if (!ClickAimKeySteer.Face(candidate.Location.X, candidate.Location.Y, hwnd, 30.0, 6)) continue;
            for (int tries = 0; tries < 3 && !_stop && !ClickAimAbort.Requested; tries++)
            {
                if (!TryReadGatherCombat(out fighting) || !fighting) return "combat ended or state unreadable";
                WowBGuid before = reader.ReadTargetGuid();
                // Reuse an already-selected nearby candidate instead of selecting
                // self to force TAB. Otherwise require TAB to select this candidate.
                // In either case the live checks below still apply; neither
                // proximity nor TAB proves hostility or attacker identity.
                if (before != candidate.Guid)
                {
                    ClickAimInput.TapKey(hwnd, TabVk, 40);
                    if (ClickAimAbort.Wait(TabSettleMs)) return "stopped";
                }
                if (!TryReadGatherCombat(out fighting) || !fighting) return "combat ended or state unreadable";
                if (!Capture(out snap) || snap == null || !snap.TryGetLocalPlayer(out player)
                    || !snap.TryGetSelectedTarget(out WowBObservedUnit selected)
                    || selected.Guid != candidate.Guid || selected.UnitAddress != candidate.UnitAddress
                    || selected.RecordAddress != candidate.RecordAddress
                    || selected.IsLocalPlayer || selected.HasPlayerVtable
                    || !reader.TryGetUnitFields(selected.UnitAddress, out WowBReader.WowBUnitFields current)
                    || !DefenceFieldsValid(current.Health, current.UnitFlags, grindMode && _notViable.Contains(selected.Guid))
                    || (grindMode && !WowBGrindFactions.AllowsPull(reader, selected, out _))
                    || ClickAimMath.Len(selected.Location.X - player.Location.X, selected.Location.Y - player.Location.Y) > GatherDefenceRadius)
                    continue;
                Logging.Write((grindMode ? "[Grind]" : "[Gather]")
                    + " combat: rotation on nearby " + (selected.Guid == before ? "already-selected" : "TAB-selected")
                    + " NPC " + selected.Guid + "; proximity and TAB do not prove it is the attacker");
                if (grindMode) LogFactionFight(reader, selected, true);
                engaged = true;
                try { return FightDefenceWithHandoffs(hwnd, reader, grindMode, player, selected); }
                finally { ClickAimKeySteer.ReleaseHeld(); }
            }
        }
        return "no eligible nearby NPC selected by the defensive TAB check; holding position";
    }

    private static bool InRealCombat()
    {
        WowBReader? reader = ObjectManager.WowB;
        WowBWorldSnapshot? snap;
        WowBObservedUnit me;
        WowBReader.WowBUnitFields fields;
        uint? flags = null;
        try
        {
            if (reader != null && Capture(out snap) && snap != null && snap.TryGetLocalPlayer(out me)
                && reader.TryGetUnitFields(me.UnitAddress, out fields))
                flags = fields.UnitFlags;
        }
        catch { /* An interrupted or failed read is unknown, not safe. */ }
        if (!flags.HasValue && unchecked(Environment.TickCount - _realCombatQuietAt) > 15000)
        {
            _realCombatQuietAt = Environment.TickCount;
            Logging.Write("[Grind] cannot read player combat flags; holding rest and vendor actions until they read again");
        }
        return CombatFlagSaysFight(flags);
    }

    private static bool UnderAttack() { return InRealCombat(); }

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
    private static bool StopForRest(IntPtr hwnd)
    {
        ClickAimKeySteer.ReleaseHeld();
        ClickAimKeySteer.ReleaseArrows(hwnd);
        double lastX = double.NaN, lastY = double.NaN;
        for (int i = 0; i < 6; i++)
        {
            if (ClickAimAbort.Wait(120) || _stop) return false;
            if (!ClickAimPositions.TryRead(out double x, out double y)) return false;
            if (!double.IsNaN(lastX) && ClickAimMath.Len(x - lastX, y - lastY) < 0.15) return true;
            lastX = x;
            lastY = y;
        }
        return false;
    }

    private static void StopAndSettle(IntPtr hwnd)
    {
        ClickAimKeySteer.ReleaseHeld();

        double lastX = double.NaN, lastY = double.NaN;
        for (int i = 0; i < 6; i++)
        {
            if (ClickAimAbort.Wait(120)) return;
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
        => TryClickUnit(hwnd, unit, px, py, mustSelect, out how, out _);

    private static bool TryClickUnit(IntPtr hwnd, WowBObservedUnit unit, double px, double py,
                                     bool mustSelect, out string how, out bool pixelConfirmed, Func<bool>? safeToClick = null)
    {
        how = "";
        pixelConfirmed = false;

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
        // In strict Grind mode, a selected corpse would force the unsafe
        // projected fallback. Move off it with verified F1 first, then search
        // for a fresh left-click selection as normal. Never do this in combat.
        WowBReader? clickReader = ObjectManager.WowB;
        bool strictClick = _running && (GrindFactionSettings.Rules.Enabled || safeToClick != null);
        GreenMagic.Memory? clickMemory = ObjectManager.Wow;
        if (strictClick && (clickReader == null || clickMemory == null)) { how = "client attachment unavailable"; return false; }
        using var uncachedClick = strictClick ? clickMemory?.TemporaryCacheState(false) : null;
        if (strictClick && clickReader != null && clickReader.ReadTargetGuid() == unit.Guid &&
            clickReader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields clickFields) && clickFields.Health == 0 &&
            Capture(out WowBWorldSnapshot? clickSnapshot) && clickSnapshot != null &&
            clickSnapshot.TryGetLocalPlayer(out WowBObservedUnit clickPlayer) &&
            clickSnapshot.TryGetUnit(unit.Guid, out WowBObservedUnit clickCurrent) &&
            clickCurrent.RecordAddress == unit.RecordAddress && clickCurrent.UnitAddress == unit.UnitAddress)
            DismissRejectedSelection(hwnd, clickReader, clickPlayer.Guid, unit.Guid);

        int probeWidth = 0, probeHeight = 0;
        if (safeToClick != null && !ClickAimInput.ClientSize(hwnd, out probeWidth, out probeHeight))
        { how = "client size unavailable before loot probe"; return false; }
        int fx, fy;
        if (FindUnitPixel(hwnd, unit, px, py, out fx, out fy, out how,
                strictClick ? () => !TryReadGatherCombat(out bool probeFighting) || probeFighting ||
                    (safeToClick != null && !safeToClick()) : null))
        {
            if (strictClick && (_stop || ClickAimAbort.Requested || RouteRunner.StopRequested || _vendorDue ||
                clickReader == null || ObjectManager.WowB != clickReader ||
                ObjectManager.Wow != clickMemory || clickReader.ReadTargetGuid() != unit.Guid ||
                !TryReadGatherCombat(out bool postProbeFighting) || postProbeFighting ||
                (safeToClick != null && !safeToClick())))
            { how += "; click withheld because selection or combat state changed"; return false; }
            if (safeToClick != null)
            {
                int x = probeWidth / 2 + fx, y = probeHeight / 2 + fy;
                if (clickReader == null || !ClickAimInput.ClientSize(hwnd, out int width, out int height) ||
                    width != probeWidth || height != probeHeight || x <= 0 || y <= 0 || x >= width - 1 || y >= height - 1 ||
                    !ClickAimInput.PostMouseMoveFraction(hwnd, x / (double)(width - 1), y / (double)(height - 1)))
                { how += "; click withheld: client size, pixel or mouse-move post failed"; return false; }
                // Allow the client to process the move after the left-click probe;
                // do not claim it was delivered to the corpse or collected loot.
                if (ClickAimAbort.Wait(200) || _stop)
                { how += "; click withheld during mouse-move settle"; return false; }
                string hoverState = clickReader.TryReadMouseoverGuidCandidate(out WowBGuid hover)
                    ? (hover == unit.Guid ? "candidate hover matched" : "candidate hover did not match")
                    : "candidate hover unreadable";
                if (!ClickAimInput.ConfirmedRightClick(hwnd, x, y, width, height,
                        () => clickReader.ReadTargetGuid() == unit.Guid && safeToClick(), out string clickReason))
                { how += "; final guarded right-click withheld: " + clickReason + "; " + hoverState; return false; }
                how += "; " + clickReason + "; " + hoverState + " (hover unverified)";
            }
            else ClickAimInput.Click(hwnd, fx, fy);
            pixelConfirmed = true;
            return true;
        }

        /* The one case a left-click cannot confirm: the unit is ALREADY the target, so the
         * target guid cannot change and every probe looks like a miss. Right-click the
         * projected point anyway - it is a best effort and the caller is told so. */
        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        if (_stop || ClickAimAbort.Requested || reader == null || reader.ReadTargetGuid() != unit.Guid) return false;
        if (strictClick)
        { how += "; unverified right-click withheld for this guarded interaction"; return false; }

        int bx, by;
        if (!TryProject(hwnd, px, py, unit.Location.X, unit.Location.Y, unit.Location.Z, out bx, out by) ||
            _stop || ClickAimAbort.Requested)
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
                                      out int foundX, out int foundY, out string how, Func<bool>? cancel = null)
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
                if (_stop || ClickAimAbort.Requested || (cancel != null && cancel()))
                { how = "probe interrupted by stop or combat"; return false; }
                if (probes >= ClickProbeMax) break;

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
                if (ClickAimAbort.Wait(ClickProbeSettleMs))
                { how = "probe stopped"; return false; }

                WowBGuid after = reader.ReadTargetGuid();
                if (after != unit.Guid)
                {
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
    /// One fight's mesh path, goal and bounded progress watchdog.
    ///
    /// A class rather than three locals because <see cref="ChaseStep"/> is called from two
    /// branches of the fight (the walk into casting range and the walk into melee) and both have
    /// to share the same tally - otherwise a fight that alternates between the two could spend
    /// separate retry budgets in each and never give up.
    /// </summary>
    private sealed class ChaseWatch
    {
        public WoWPoint[]? Path;
        public int Point;
        public uint Map;
        public double GoalX, GoalY, GoalZ;
        public int PlannedAt;
        public int LastProgressMs;
        public double BestPointDistance = double.PositiveInfinity;
        public int Stalls;
        public int LastClosingMs;
        public double BestTargetDistance = double.PositiveInfinity;
        public ChaseWatch(int started) { LastProgressMs = started; LastClosingMs = started; }
        public void EndApproach()
        {
            Path = null;
            Stalls = 0;
            BestTargetDistance = double.PositiveInfinity;
            BestPointDistance = double.PositiveInfinity;
            LastClosingMs = Environment.TickCount;
            LastProgressMs = LastClosingMs;
        }
    }

    // Follow a complete validated mesh path one short segment at a time; fight
    // ticks keep checking combat/STOP between steps. Re-plan for moving targets.
    // Count stalled replans for the whole chase, not position changes from walking
    // backwards: the old watchdog repeatedly reset "attempt 1" at the same wall.
    private static bool ChaseStep(IntPtr hwnd, ChaseWatch watch, WowBObservedUnit target, out string why, bool approachOnly = false)
    {
        why = "";
        if (_stop || ClickAimAbort.Requested) { why = "stopped"; return false; }
        if (ObjectManager.WowB?.IsExperimental70707 == true &&
            !ClickAimKeySteer.EnsureChaseTurnKeys(hwnd,
                () => !_stop && !ClickAimAbort.Requested &&
                      (!approachOnly || (TryReadGatherCombat(out bool combat) && !combat)), out string calibration))
        { ClickAimKeySteer.ReleaseHeld(); why = "turn-only calibration failed or was interrupted: " + calibration; return false; }
        if (!ClickAimPositions.TryRead(out double px, out double py))
        { ClickAimKeySteer.ReleaseHeld(); why = "player position unreadable"; return false; }
        int now = Environment.TickCount;
        uint map = ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, px, py);
        if (map == uint.MaxValue || (watch.Path != null && watch.Map != map))
        { ClickAimKeySteer.ReleaseHeld(); why = "map changed or became unreadable during chase"; return false; }
        double targetDistance = ClickAimMath.Len(target.Location.X - px, target.Location.Y - py);
        if (watch.BestTargetDistance - targetDistance >= 2.0)
        { watch.BestTargetDistance = targetDistance; watch.LastClosingMs = now; }
        if (unchecked(now - watch.LastClosingMs) >= ChaseNoClosingMs)
        { ClickAimKeySteer.ReleaseHeld(); why = "no net progress towards the mob for 20 seconds"; return false; }
        bool movedGoal = watch.Path != null && (ClickAimMath.Len(target.Location.X - watch.GoalX,
            target.Location.Y - watch.GoalY) > 5.0 || Math.Abs(target.Location.Z - watch.GoalZ) > 3.0);
        if (watch.Path == null || movedGoal || unchecked(now - watch.PlannedAt) >= 8000)
        {
            ClickAimKeySteer.ReleaseHeld();
            var goal = new RoutePoint { X = target.Location.X, Y = target.Location.Y, Z = target.Location.Z, Kind = "Mob" };
            WoWPoint[]? path = RouteRunner.TryCombatMeshPath(px, py, goal);
            if (_stop || ClickAimAbort.Requested) { why = "stopped"; return false; }
            if (path == null || path.Length < 2
                || ClickAimDiagnostics.ResolveMap((uint)NavGridStore.MapId, px, py) != map)
            { why = "no complete validated mesh path to the mob, or map changed (no straight-walk fallback)"; return false; }
            watch.Path = path;
            watch.Map = map;
            watch.Point = 1;
            watch.GoalX = goal.X; watch.GoalY = goal.Y; watch.GoalZ = goal.Z;
            watch.PlannedAt = Environment.TickCount;
            watch.LastProgressMs = watch.PlannedAt;
            watch.BestPointDistance = double.PositiveInfinity;
            ClickAimKeySteer.BeginMeshPoint();
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Grind] mob chase uses validated mesh: {0} follow points, goal ({1:F1}, {2:F1})",
                path.Length, goal.X, goal.Y));
            // Do not take a step if combat began while the mesh query was running.
            if (!TryReadGatherCombat(out bool fighting) || fighting) return true;
        }

        WoWPoint[] follow = watch.Path;
        while (watch.Point < follow.Length - 1
            && ClickAimMath.Len(follow[watch.Point].X - px, follow[watch.Point].Y - py) <= 2.0)
        {
            watch.Point++;
            watch.BestPointDistance = double.PositiveInfinity;
            watch.LastProgressMs = Environment.TickCount;
            ClickAimKeySteer.BeginMeshPoint();
        }
        WoWPoint point = follow[watch.Point];
        double distance = ClickAimMath.Len(point.X - px, point.Y - py);
        if (watch.BestPointDistance - distance >= StuckMoveYd)
        { watch.BestPointDistance = distance; watch.LastProgressMs = Environment.TickCount; }
        if (unchecked(Environment.TickCount - watch.LastProgressMs) >= StuckMs)
        {
            ClickAimKeySteer.ReleaseHeld();
            watch.Stalls++;
            if (watch.Stalls >= StuckEscapes)
            { why = "no progress along the mesh after " + watch.Stalls + " stalled attempts"; return false; }
            Logging.Write("[Grind] mesh chase stalled; replanning (stall " + watch.Stalls + " of " + StuckEscapes + ")");
            watch.Path = null;
            return true;
        }
        if (!ClickAimKeySteer.TryReadFacing(out double facing) || !double.IsFinite(facing))
        { ClickAimKeySteer.ReleaseHeld(); why = "facing unreadable for mesh-point steering"; return false; }
        if (approachOnly && (_stop || ClickAimAbort.Requested || _vendorDue ||
            !TryReadGatherCombat(out bool approachCombat) || approachCombat))
        { ClickAimKeySteer.ReleaseHeld(); why = "pool approach interrupted before steering"; return false; }
        double remaining;
        if (!ClickAimKeySteer.Steer(point.X, point.Y, hwnd, true, out remaining, true))
        { ClickAimKeySteer.ReleaseHeld(); why = "mesh-point steering failed"; return false; }
        ClickAimAbort.Wait(90);
        return true;
    }

    // A pool unit's position is only a navigation lead, not proof that it is
    // hostile. No click or attack is sent here; normal TAB/pool handling resumes
    // after an eligible unit comes within the requested stop radius.
    private static string ApproachPoolCandidate(IntPtr hwnd, WowBObservedUnit initial, double stopRadius = PoolApproachYd)
    {
        int started = Environment.TickCount;
        var watch = new ChaseWatch(started);
        WowBReader? reader = ObjectManager.WowB;
        GreenMagic.Memory? memory = ObjectManager.Wow;
        if (reader == null || memory == null || initial.Guid.IsEmpty) return "cannot read distant pool candidate";
        string faction = GrindFactionSettings.Rules.Enabled && WowBGrindFactions.TryReadBoundKey(reader, initial, out uint initialKey)
            ? ", observed faction key " + initialKey.ToString(CultureInfo.InvariantCulture) : "";
        Logging.Write("[Grind] approaching pool candidate " + initial.Guid + faction +
            string.Format(CultureInfo.InvariantCulture, " at ({0:F1}, {1:F1}, {2:F1})", initial.Location.X, initial.Location.Y, initial.Location.Z) +
            " on a validated mesh; hostility is unknown, TAB will be retried within " + stopRadius.ToString("F0", CultureInfo.InvariantCulture) + " yd");
        try
        {
            while (!_stop && !ClickAimAbort.Requested && !_vendorDue)
            {
                if (ObjectManager.Wow != memory || ObjectManager.WowB != reader)
                    return "client attachment changed during pool approach";
                if (!TryReadGatherCombat(out bool fighting) || fighting)
                {
                    _combatHold = true;
                    return "combat or unreadable combat state interrupted the pool approach";
                }
                if (!Capture(out WowBWorldSnapshot? current) || current == null ||
                    !current.TryGetLocalPlayer(out WowBObservedUnit me))
                {
                    ClickAimAbort.Wait(300);
                    return "cannot refresh player position during pool approach";
                }
                // Reuse the local player from this snapshot for the level checks;
                // PlayerLevel() would capture the entire pool twice more per step.
                if (!reader.TryGetUnitFields(me.UnitAddress, out WowBReader.WowBUnitFields playerFields))
                {
                    _combatHold = true;
                    ClickAimAbort.Wait(300);
                    return "player fields unreadable; pausing the pool approach";
                }
                int approachLevel = playerFields.Level <= 100 ? (int)playerFields.Level : 0;
                if (!current.TryGetUnit(initial.Guid, out WowBObservedUnit candidate) ||
                    candidate.Guid != initial.Guid || candidate.RecordAddress != initial.RecordAddress ||
                    candidate.UnitAddress != initial.UnitAddress || candidate.IsLocalPlayer || candidate.HasPlayerVtable ||
                    !WowBGrindFactions.AllowsPull(reader, candidate, out _) ||
                    !reader.TryGetUnitFields(candidate.UnitAddress, out WowBReader.WowBUnitFields fields) ||
                    !SelectionFieldsValid(fields.Health, fields.UnitFlags, fields.Level, approachLevel) ||
                    _notViable.Contains(candidate.Guid) || Refused(candidate.Guid) || Unreachable(candidate.Guid))
                {
                    ClickAimAbort.Wait(150);
                    return "distant pool candidate disappeared, changed identity or became ineligible; reconsidering targets";
                }

                double distance = ClickAimMath.Len(candidate.Location.X - me.Location.X, candidate.Location.Y - me.Location.Y);
                if (!double.IsFinite(distance) || distance > GiveUpYd)
                    return DropUnreachable(candidate, "candidate moved outside the search margin");
                if (distance <= stopRadius)
                    return "the pool candidate is within " + stopRadius.ToString("F0", CultureInfo.InvariantCulture) + " yd; stopping to retry TAB";
                if (NearestKillable(current, me.Location.X, me.Location.Y, stopRadius,
                                    out _, out _, out _, out int unreadNearby, approachLevel))
                    return "an eligible pool candidate is within " + stopRadius.ToString("F0", CultureInfo.InvariantCulture) + " yd; stopping to retry TAB";
                if (unreadNearby > 0)
                {
                    _combatHold = true;
                    ClickAimAbort.Wait(300);
                    return "nearby unit fields are unreadable; pausing the pool approach";
                }
                if (unchecked(Environment.TickCount - started) >= PoolApproachTimeoutMs)
                    return DropUnreachable(candidate, "pool approach exceeded its 45-second limit");
                if (!ChaseStep(hwnd, watch, candidate, out string why, true))
                {
                    if (_stop || ClickAimAbort.Requested || _vendorDue) return "pool approach stopped";
                    if (!TryReadGatherCombat(out bool interrupted) || interrupted)
                    { _combatHold = true; return "combat interrupted the pool approach"; }
                    return DropUnreachable(candidate, why);
                }
            }
            return _vendorDue ? "vendor trip interrupted the pool approach" : "pool approach stopped";
        }
        finally { ClickAimKeySteer.ReleaseHeld(); }
    }

    /// <summary>
    /// Drops a target the fight could not walk to, for <see cref="UnreachablePauseMs"/>, and
    /// returns the line for the log. The pause is a pause, not a blacklist: a mob behind a cactus
    /// now is a mob standing in the open in two minutes.
    /// </summary>
    private static string DropUnreachable(WowBObservedUnit target, string why)
    {
        ClickAimKeySteer.ReleaseHeld();
        if (_stop || ClickAimAbort.Requested) return "stopped";
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
    /// One fight using a hotbar-only class priority or the legacy generic selector.
    /// Healing, estimated DoT timing and optional instants share the existing
    /// movement, faction, selected-target, resource and input safeguards. A
    /// keypress or an estimated timer is not proof that a spell landed.
    /// </summary>
    private static string Fight(IntPtr hwnd, double px, double py, bool unverified, bool defenceOnly = false, WowBGuid defenceExpected = default(WowBGuid), WowBObservedUnit expectedPull = default(WowBObservedUnit), bool defenceFactionFilter = false)
    {
        int started = Environment.TickCount;
        int lastHealth = -1;
        int presses = 0;
        int castFailures = 0;
        int lastHealMs = 0;
        int lastCastMs = 0;
        int lastCombatBuffCheckMs = unchecked(started - 1000);
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
        bool noSafeAutoLogged = false;
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

        if (defenceOnly && target.Guid != defenceExpected)
            return "target changed before defence began; not attacking";
        if ((!defenceOnly || defenceFactionFilter) && !FactionPullStillAllowed(ObjectManager.WowB, expectedPull, target))
            return "target or faction key changed before the pull; not attacking";
        WowBGuid defenceTarget = target.Guid;
        lastHealth = Health(target);
        int initialTargetHealth = lastHealth;

        /* The kill leaves the corpse selected, and TAB keeps a target when there is nothing
         * else to move to - so the next "engage" is the body of the mob that just died. The
         * 17:36 run did exactly that three times: three 3.2 s Wrath attempts at a
         * health-0 target, then 25 s of turning in circles at 0.7 yd. The death check below
         * needs a health reading that was ever above zero, which a corpse can never give. */
        if (lastHealth == 0)
            return "the target is already dead (health 0) - that is a corpse left selected, not a fight";

        Styx.WoWInternals.WowBReader? reader = ObjectManager.WowB;
        GreenMagic.Memory? fightMemory = ObjectManager.Wow;
        if (reader == null || fightMemory == null) return "client attachment unavailable; fight withheld";
        List<ClickAimAction> bar = ClickAimCombat.ReadBar(reader);
        ClickAimRotationPlan rotation = ClickAimClassRotation.Build(ClickAimSettings.RotationClass, bar);
        ClickAimAction? damage = rotation.Damage;
        ClickAimAction? heal = rotation.Heal;
        ClickAimAction? auto = ClickAimCombat.AutoAttack(bar);          // Attack (6603)
        ClickAimAction? dot = rotation.Dot;
        ClickAimAction? meleeHit = rotation.Melee;
        bool damageWasDot = damage != null && damage.IsDot;
        Logging.Write("[Grind] hotbar rotation: " + rotation.Class + " (requested " + ClickAimSettings.RotationClass
            + "); damage=" + (damage?.Name ?? "none") + ", extra=" + (rotation.Supplemental?.Name ?? "none")
            + ", DoT=" + (dot?.Name ?? "none") + ", melee=" + (meleeHit?.Name ?? "none")
            + ", heal=" + (heal?.Name ?? "none") + ". Cooldowns and DoT duration are estimates, not client state.");

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

        int lastDeathCheck = unchecked(Environment.TickCount - 500);
        while (!_stop && !ClickAimAbort.Requested && unchecked(Environment.TickCount - started) < EngageTimeoutMs)
        {
            if (unchecked(Environment.TickCount - lastDeathCheck) >= 500)
            {
                lastDeathCheck = Environment.TickCount;
                if (CheckDeathCheckpoint())
                    return "death/ghost safety check; stopping fight for manual recovery";
            }
            if (defenceOnly && (!TryReadGatherCombat(out bool fighting) || !fighting))
                return "combat ended or state unreadable; ending defence";
            if (!Capture(out snap) || snap == null) break;
            if (!snap.TryGetSelectedTarget(out target))
            {
                if (_focusedFightGuid == defenceTarget) _focusedFightGuid = default;
                /* A mob that dies takes the target with it, and so does one that runs out of
                 * range, so this is "killed or fled". Saying only "gone" made a run with real
                 * kills report zero of them. */
                string secs = ((Environment.TickCount - started) / 1000).ToString(CultureInfo.InvariantCulture);
                return worked.Count == 0
                    ? "target gone after " + secs + " s and nothing had landed - it fled or the client dropped it"
                    : "killed or fled after " + secs + " s; what landed: " + string.Join(", ", worked.ToArray());
            }

            if (target.Guid != defenceTarget)
                return YieldForTargetChange(reader, fightMemory, defenceTarget);
            if (target.HasPlayerVtable)
                return "the target turned out to be a player - disengaged";

            int health = Health(target);
            if (reader?.IsExperimental70707 == true && health < 0)
                return StopFightForSafety("70707 target health became unreadable; stopping");
            if (health >= 0 && lastHealth > 0 && health == 0)
            {
                if (_focusedFightGuid == target.Guid) _focusedFightGuid = default;
                return "killed" + (worked.Count == 0 ? "" : " with " + string.Join(", ", worked.ToArray()));
            }
            if (initialTargetHealth > 0 && health > 0 && health < initialTargetHealth)
            { MarkFocusedAttack(target); _focusedDamageObserved = true; }
            if ((!defenceOnly || defenceFactionFilter) && !FactionPullStillAllowed(reader, expectedPull, target))
            { ClickAimKeySteer.ReleaseHeld(); return "faction key or unit identity became unavailable during pull; stopping"; }

            WowBObservedUnit me;
            if (!snap.TryGetLocalPlayer(out me)) break;
            double d = ClickAimMath.Len(target.Location.X - me.Location.X, target.Location.Y - me.Location.Y);
            double defenceLimit = target.Guid == _focusedFightGuid
                ? Math.Max(GatherDefenceRadius + 3.0, castRange) : GatherDefenceRadius + 3.0;
            if (defenceOnly && d > defenceLimit)
                return "defensive target moved beyond its permitted range; not chasing a distant unit";

            if (!defenceOnly)
            {
                if (!TryReadGatherCombat(out bool combatNow))
                { ClickAimKeySteer.ReleaseHeld(); return "combat state unreadable; stopping the pull"; }
                bool committed = _focusedFightGuid == target.Guid || (initialTargetHealth > 0 && health >= 0 && health < initialTargetHealth);
                if (combatNow && ShouldYieldRemotePull(d, castRange, HasNearbyOtherNpc(snap, me, target.Guid, reader), committed))
                {
                    ClickAimKeySteer.ReleaseHeld();
                    return "combat detected during a remote pull; stopping the chase and checking nearby TAB targets";
                }
            }

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

            if (unverified && !verifyAsked && inReachSince != 0 && d <= PoolApproachYd
                && unchecked(Environment.TickCount - inReachSince) >= PoolVerifyMs
                && unchecked(Environment.TickCount - lastHealthDropMs) >= PoolVerifyMs)
            {
                verifyAsked = true;
                if (GrindFactionSettings.Rules.Enabled)
                {
                    // TAB can switch to a blocked unit while auto-attack is
                    // already active. Keep the combat/damage timeout below,
                    // but do not issue another TAB from inside this fight.
                    Logging.Write("[Grind] filtered pool check: no in-fight TAB; using the damage/combat timeout");
                }
                else
                {
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
            }

            /* The combat verdict. It runs after the verification interval (and, with the
             * filter off, the TAB ask), because a press of
             * Attack needs MeleeSwingQuietMs to prove itself - and on a friendly NPC it never does.
             * The 14:23 peon sat at 1.3 yd with Attack pressed and the character never entered
             * combat; a scorpion enters combat on the first swing. */
            if (unverified && inReachSince != 0
                && unchecked(Environment.TickCount - lastHealthDropMs) >= PoolVerifyMs
                && lastAttackPressMs != 0
                && unchecked(Environment.TickCount - lastAttackPressMs) >= MeleeSwingQuietMs
                && !InRealCombat())
            {
                WowBGuid dead = target.Guid;
                _notViable.Add(dead);
                if (_notViable.Count > 256) _notViable.Clear();
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} has taken no damage for {1} s, Attack was pressed at arm's length and the client "
                    + "did not show the candidate player combat bit - it may not be something this character can fight "
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
            else if (reader?.IsExperimental70707 == true)
                return StopFightForSafety("70707 player vitals became unreadable; stopping");

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

            // The settings window may veto a cached action during a fight. Do not
            // send a key whose effective role has since changed; reselect next fight.
            if (heal != null && !heal.IsHeal) heal = null;
            if (dot != null && !dot.IsDot) dot = null;
            if (damage != null && (damageWasDot ? !damage.IsDot || !ClickAimSettings.UseDamageOverTime : !damage.IsDamage)) damage = null;
            if (meleeHit != null && !meleeHit.IsDamage) meleeHit = null;
            if (auto != null && !auto.IsAttack) auto = null;

            /* A heal that lands too late is a corpse. Saying so beats pressing keys at a
             * dead character, which is what the 16:09 run did for its last four seconds
             * ("health 0% and Healing Touch did not lift it"). */
            if (myMaxHealth > 0 && myHealth == 0)
            {
                string reason = "the character died at " + presses + " press(es) into the fight; stopping the grind";
                return reader?.IsExperimental70707 == true ? StopFightForSafety(reason) : reason;
            }

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
            if (heal != null && _healCostSpellId != heal.Id)
            {
                _healCostSpellId = heal.Id;
                _healCostGuess = 0; // never apply another spell or rank's observed cost
            }
            bool healPowerOk = heal != null && HealPowerOk(myMana, myMaxMana);
            if (heal != null && !healPowerOk && !healBlocked && !healRefusalLogged
                && ClickAimSettings.HealBelowPercent > 0 && myPercent <= ClickAimSettings.HealBelowPercent)
            {
                healRefusalLogged = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] health {0:F0}% is under the heal line and there is {1}/{2} power - "
                    + "{3} is below the heal safety gate ({4}% and any observed cost estimate), "
                    + "so this fight uses auto-attack and whatever still casts", myPercent, myMana,
                    myMaxMana > 0 ? myMaxMana : _manaPeak, heal.Name,
                    ClickAimSettings.HealMinManaPercent));
            }

            if (ClickAimHealPolicy.ShouldSelectSelf(heal, healBlocked, myHealth, myMaxHealth,
                    ClickAimSettings.HealBelowPercent,
                    unchecked(Environment.TickCount - lastHealMs) >= ClickAimCombat.HealRetryMs, healPowerOk)
                && heal != null)
            {
                StopAndSettle(hwnd);             // the chase may still be holding forward
                if (_stop || ClickAimAbort.Requested) return "stopped";
                // Recheck the actual slot, live role, attachment, player and vitals after
                // stopping movement, before F1 can select the player.
                uint powerBeforeHeal;
                using (fightMemory.TemporaryCacheState(false))
                {
                    if (reader == null || ObjectManager.WowB != reader || ObjectManager.Wow != fightMemory)
                        return "attachment changed; heal withheld";
                    if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))
                        return "action bar changed; heal withheld";
                    if (!ClickAimHealPolicy.StillOnBar(heal, ClickAimCombat.ReadBar(reader)))
                    { heal = null; continue; }
                    if (!Capture(out WowBWorldSnapshot? healSnapshot) || healSnapshot == null ||
                        !healSnapshot.TryGetLocalPlayer(out WowBObservedUnit healPlayer) ||
                        healPlayer.Guid != me.Guid || healPlayer.UnitAddress != me.UnitAddress ||
                        !reader.TryGetUnitFields(healPlayer.UnitAddress, out WowBReader.WowBUnitFields freshHeal))
                        return reader.IsExperimental70707 ? StopFightForSafety("heal player vitals unreadable; stopping") : "heal player vitals unreadable; heal withheld";
                    if (reader.ReadTargetGuid() != target.Guid)
                        return YieldForTargetChange(reader, fightMemory, defenceTarget);
                    if (!ClickAimHealPolicy.ShouldSelectSelf(heal, healBlocked, freshHeal.Health, freshHeal.MaxHealth,
                        ClickAimSettings.HealBelowPercent, true, HealPowerOk(freshHeal.Power, freshHeal.MaxPower)))
                        continue;
                    powerBeforeHeal = freshHeal.Power;
                    if (_stop || ClickAimAbort.Requested || ObjectManager.Wow != fightMemory || ObjectManager.WowB != reader) return "stopped or attachment changed; heal withheld";
                    lastHealMs = Environment.TickCount;
                    Logging.Write("[Grind] selecting self for " + heal.Name + " (actual mana cost unknown; threshold and observed drop are estimates)");
                    ClickAimInput.TapKey(hwnd, 0x70, 40);                  // F1 = target self (Blizzard default)
                }
                if (ClickAimAbort.Wait(120) || _stop) return "stopped";
                GreenMagic.Memory? healMemory = ObjectManager.Wow;
                if (reader == null || healMemory == null || healMemory != fightMemory) return "self-target or attachment unreadable; heal withheld";
                using (healMemory.TemporaryCacheState(false))
                {
                    if (ObjectManager.WowB != reader || ObjectManager.Wow != healMemory || reader.ReadTargetGuid() != me.Guid)
                        return "F1 did not confirm self-target; heal withheld";
                }
                using (healMemory.TemporaryCacheState(false))
                {
                    if (_stop || ClickAimAbort.Requested || ObjectManager.Wow != healMemory || ObjectManager.WowB != reader) return "stopped or attachment changed; heal withheld";
                    if (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader)) return "action bar changed; heal withheld";
                    bool slotReady = ClickAimHealPolicy.StillOnBar(heal, ClickAimCombat.ReadBar(reader));
                    WowBReader.WowBUnitFields finalHeal = default;
                    bool fieldsReady = Capture(out WowBWorldSnapshot? finalSnapshot) && finalSnapshot != null &&
                        finalSnapshot.TryGetLocalPlayer(out WowBObservedUnit finalPlayer) &&
                        finalPlayer.Guid == me.Guid && finalPlayer.UnitAddress == me.UnitAddress &&
                        reader.TryGetUnitFields(finalPlayer.UnitAddress, out finalHeal);
                    if (!fieldsReady)
                        return reader.IsExperimental70707 ? StopFightForSafety("heal player identity or vitals unreadable; stopping") : "heal player identity or vitals unreadable; heal withheld";
                    if (finalHeal.MaxHealth > 0 && finalHeal.Health == 0)
                        return reader.IsExperimental70707 ? StopFightForSafety("the character died before the heal; stopping") : "the character died before the heal; stopping the fight";
                    bool vitalsReady = ClickAimHealPolicy.ShouldSelectSelf(heal, healBlocked, finalHeal.Health, finalHeal.MaxHealth,
                        ClickAimSettings.HealBelowPercent, true, HealPowerOk(finalHeal.Power, finalHeal.MaxPower));
                    if (_stop || ClickAimAbort.Requested || ObjectManager.Wow != healMemory || ObjectManager.WowB != reader || reader.ReadTargetGuid() != me.Guid)
                        return "stopped or self-target changed; heal withheld";
                    if (!slotReady || !vitalsReady)
                    {
                        Logging.Write("[Grind] heal slot, role or vitals changed after F1; heal withheld, restoring combat target");
                        if (TryRestoreHealTarget(hwnd, reader, fightMemory, target.Guid, expectedPull, defenceOnly, defenceFactionFilter)) continue;
                        if (_stop || ClickAimAbort.Requested) return "stopped";
                        if (reader.IsExperimental70707) return YieldForTargetChange(reader, fightMemory, target.Guid);
                        _stop = true; ClickAimAbort.Request();
                        return "could not recover the same target after withholding heal; stopped rather than switching mobs";
                    }
                    powerBeforeHeal = finalHeal.Power;
                    myHealth = finalHeal.Health;
                    myMaxHealth = finalHeal.MaxHealth;
                    myPercent = myHealth * 100.0 / myMaxHealth;
                    meleeSwinging = false;          // a cast stops auto-attack; it must be restarted
                    ClickAimInput.TapKey(hwnd, heal.KeyVk, 40);
                }

                /* Wait for the heal to actually land. Healing Touch is a 1.5 s cast on top
                 * of the retarget, and the previous version read my health once at 1.8 s and
                 * reported "did not lift it (still 12/113)" for heals that were still being
                 * cast - four times in one fight. Poll instead. */
                uint afterHealth = 0, afterMaxHealth = 0;
                uint powerAfterHeal = powerBeforeHeal;
                for (int waited = 0; waited < ClickAimCombat.HealVerifyMs && !_stop; waited += 300)
                {
                    if (ClickAimAbort.Wait(300) || _stop) return "stopped";
                    WowBWorldSnapshot? healed;
                    if (reader == null || !Capture(out healed) || healed == null) continue;
                    if (healed.TryGetLocalPlayer(out WowBObservedUnit me2)
                        && reader.TryGetUnitFields(me2.UnitAddress, out mine))
                    {
                        afterHealth = mine.Health;
                        afterMaxHealth = mine.MaxHealth;
                        powerAfterHeal = mine.Power;
                    }
                    if (afterHealth > myHealth) break;
                }

                if (_stop || ClickAimAbort.Requested) return "stopped";
                if (afterMaxHealth > 0 && afterHealth == 0)
                {
                    const string reason = "the character died during the heal; stopping the grind";
                    return reader?.IsExperimental70707 == true ? StopFightForSafety(reason) : reason;
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
                            ? " (observed net power drop up to " + _healCostGuess.ToString(CultureInfo.InvariantCulture) + "; actual cost unknown)"
                            : " (actual cost unknown)"));
                }
                else
                {
                    /* Two presses that neither lifted health nor spent power is a heal that
                     * cannot be paid for (or a key that cannot cast). Either way pressing it a
                     * third time costs another global cooldown in the middle of a fight the
                     * character is losing, so the heal is off for the rest of this fight. */
                    LearnHealCost(powerBeforeHeal, powerAfterHeal);
                    bool powerSpent = powerAfterHeal < powerBeforeHeal;
                    // Net health can fall even when a heal landed: incoming damage
                    // and the heal are not separately observable. Do not disable an
                    // apparently paid-for heal as if the key did nothing.
                    healFailures = powerSpent ? 0 : healFailures + 1;
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Grind] {0} did not show a net health gain ({1}/{2}, power {3} -> {4}); {5}",
                        heal.Name, afterHealth, myMaxHealth, powerBeforeHeal, powerAfterHeal,
                        powerSpent ? "power fell, so the heal remains available" : "no power spent; failure " + healFailures));
                    if (healFailures >= 2)
                    {
                        healBlocked = true;
                        Logging.Write("[Grind] the heal is off for the rest of this fight: two presses "
                                    + "showed no health gain or power cost");
                    }
                }

                /* Retarget the SAME mob. A single TAB is not enough: if F1 did move the
                 * target to me, one TAB picks whatever the client considers nearest, which
                 * is not necessarily the thing that is hitting us. Cycle until the guid
                 * matches, and give up after four tries rather than churning the target. */
                WowBGuid wanted = target.Guid;
                bool recovered = TryRestoreHealTarget(hwnd, reader, fightMemory, wanted, expectedPull, defenceOnly, defenceFactionFilter);
                if (!recovered)
                {
                    if (_stop || ClickAimAbort.Requested) return "stopped";
                    const string reason = "could not recover the same target after healing; stopped rather than switching mobs";
                    if (reader.IsExperimental70707) return YieldForTargetChange(reader, fightMemory, wanted);
                    _stop = true; ClickAimAbort.Request(); return reason;
                }
                continue;
            }

            // Optional observed-expired self-only buffs. The shared controller
            // retains the enemy selection, checks the experimental 70707 aura,
            // identity, mana estimate, combat and STOP, and revalidates before input.
            if (ClickAimSettings.UseBuffsInCombat && unchecked(Environment.TickCount - lastCombatBuffCheckMs) >= 1000 &&
                (lastCastMs == 0 || unchecked(Environment.TickCount - lastCastMs) >= ClickAimCombat.CastSettleMs))
            {
                lastCombatBuffCheckMs = Environment.TickCount;
                if (Buffs.TryApplyInCombat(hwnd, () => _stop, me,
                    () => FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter),
                    () => _presses++))
                {
                    presses++;
                    lastCastMs = Environment.TickCount;
                    if (ClickAimAbort.Wait(ClickAimCombat.CastSettleMs)) return "stopped";
                    continue;
                }
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
            if (dot != null && ClickAimSettings.UseDamageOverTime
                && (!melee || (dot.IsMelee && rotation.Class != ClickAimRotationClass.Generic))
                && (dot.IsMelee || myMana >= PowerForCast(dot, 10)) && d <= dotRange
                && (rotation.Class != ClickAimRotationClass.Hunter || d >= 8.0)
                && (rotation.Class != ClickAimRotationClass.Shaman || ClickAimClassRotation.Ready(_rotationCooldowns, ClickAimClassRotation.DotCooldownGroup(dot), Environment.TickCount))
                && (_dotGuid != target.Guid || unchecked(Environment.TickCount - _dotUntil) >= 0))
            {
                StopAndSettle(hwnd);
                if (!ClickAimKeySteer.Face(target.Location.X, target.Location.Y, hwnd, 25.0, 4))
                {
                    Thread.Sleep(120);
                    continue;
                }

                if (!FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter))
                { return YieldForTargetChange(reader, fightMemory, defenceTarget); }
                if (!dot.IsDot || !ClickAimSettings.UseDamageOverTime) return "DoT role or setting changed; attack withheld";
                ClickAimInput.TapKey(hwnd, dot.KeyVk, 40);
                if (rotation.Class == ClickAimRotationClass.Shaman)
                    ClickAimClassRotation.Defer(_rotationCooldowns, ClickAimClassRotation.DotCooldownGroup(dot), 8000, Environment.TickCount);
                MarkFocusedAttack(target);
                presses++;
                _presses++;
                _dotGuid = target.Guid;
                _dotUntil = Environment.TickCount + ClickAimSettings.DotRefreshSeconds * 1000;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] pressed {0} (slot {1}, key '{2}') on {3} at {4:F1} yd - "
                    + "assuming a {5} s DoT duration; application and aura are not verified",
                    dot.Name, dot.Slot, ClickAimCombat.KeyLabel(dot.KeyVk), target.Guid, d,
                    ClickAimSettings.DotRefreshSeconds));
                ClickAimAbort.Wait(ClickAimCombat.CastSettleMs);
                continue;
            }

            // With a DoT as the only ranged action, approach its range for the
            // opener rather than closing all the way to melee before using it.
            if (ClickAimClassRotation.ShouldApproachDotOnly(damage, dot, melee, ClickAimSettings.UseDamageOverTime,
                _dotGuid != target.Guid || unchecked(Environment.TickCount - _dotUntil) >= 0,
                dot == null || rotation.Class != ClickAimRotationClass.Shaman || ClickAimClassRotation.Ready(_rotationCooldowns, ClickAimClassRotation.DotCooldownGroup(dot), Environment.TickCount),
                myMana, PowerForCast(dot, 10), d))
            {
                if (!ChaseStep(hwnd, watch, target, out string blockedDot)) return DropUnreachable(target, blockedDot);
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
            ClickAimAction? optional = ClickAimClassRotation.SelectSupplemental(rotation, melee,
                _focusedFightGuid == target.Guid && _focusedDamageObserved, d, myMana,
                PowerForCast(rotation.Supplemental, 10), Environment.TickCount, _rotationCooldowns);
            ClickAimAction? castDamage = optional ?? damage;
            bool supplemental = optional != null;
            double selectedRange = castDamage != null && castDamage.RangeYd > 0 ? castDamage.RangeYd : castRange;
            uint needForCast = PowerForCast(castDamage, 10);
            // Classic Hunter shots have an estimated minimum range. If the target is
            // closer, the existing melee fallback takes over rather than spamming a shot.
            bool hunterTooClose = rotation.Class == ClickAimRotationClass.Hunter && d < 8.0;
            bool canCast = !melee && castDamage != null && !castDamage.IsMelee && !hunterTooClose && myMana >= needForCast;

            if (!canCast && !melee && damage != null && !damage.IsMelee && myMana < PowerForCast(damage, 10)
                && !powerShortLogged)
            {
                powerShortLogged = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} of power left and {1} needs about {2} - not casting again until the "
                    + "bar comes back; auto-attacking instead", myMana, damage.Name, needForCast));
            }

            if (watch.Path != null && ((canCast && d <= selectedRange) || (!canCast && d <= MeleeEngageYd)))
                watch.EndApproach(); // a later chase gets a fresh progress budget

            if (canCast && d > selectedRange)
            {
                string blocked;
                if (!ChaseStep(hwnd, watch, target, out blocked))
                    return DropUnreachable(target, blocked);
                continue;
            }

            if (canCast && d > selectedRange - 2.0)
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

            if (canCast && !supplemental && !ClickAimClassRotation.Ready(_rotationCooldowns, rotation.DamageGroup, Environment.TickCount))
            {
                Thread.Sleep(120); // estimated cooldown, still refresh target, health and STOP each loop
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
                _pendingCastId = castDamage!.Id;
                _pendingCastMana = manaBefore;
                if (!FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter))
                { return YieldForTargetChange(reader, fightMemory, defenceTarget); }
                if (supplemental ? !castDamage!.IsDamage : (damageWasDot ? !castDamage!.IsDot || !ClickAimSettings.UseDamageOverTime : !castDamage!.IsDamage))
                    return "damage role or DoT setting changed; attack withheld";
                ClickAimInput.TapKey(hwnd, castDamage!.KeyVk, 40);
                ClickAimClassRotation.Defer(_rotationCooldowns,
                    supplemental ? rotation.SupplementalGroup : rotation.DamageGroup,
                    supplemental ? rotation.SupplementalIntervalMs : rotation.DamageIntervalMs, Environment.TickCount);
                MarkFocusedAttack(target);
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
                    if (afterCast.TryGetSelectedTarget(out t3) && t3.Guid == defenceTarget) mobAfter = Health(t3);

                    spentMana = manaAfter > 0 && manaAfter < manaBefore;
                    hurtMob = mobAfter >= 0 && mobBeforeHealth >= 0 && mobAfter < mobBeforeHealth;
                    if (spentMana || hurtMob) break;
                }

                if (spentMana || hurtMob)
                {
                    castEverLanded = true;
                    LearnCastCost(castDamage!.Id, manaBefore, manaAfter);
                    string label = castDamage!.Name + " (slot " + castDamage!.Slot.ToString(CultureInfo.InvariantCulture) + ")";
                    if (!worked.Contains(label)) worked.Add(label);
                    if (hurtMob)
                    {
                        if (_focusedFightGuid == defenceTarget) _focusedDamageObserved = true;
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Grind] {0} does damage: mob health {1} -> {2} (mana {3} -> {4})",
                            castDamage!.Name, lastHealth, mobAfter, manaBefore, manaAfter));
                        lastHealth = mobAfter;
                    }
                    castFailures = 0;
                    continue;
                }

                LearnCastCost(castDamage!.Id, manaBefore, manaAfter);   // a refusal costs nothing, a cast does
                if (supplemental)
                {
                    // A cooldown/range/resource estimate can be wrong. Do not let one
                    // optional key force the whole fight into melee; retry the filler.
                    Logging.Write("[Grind] optional " + castDamage!.Name + " had no observed effect; estimated cooldown applied, returning to the main spell");
                    continue;
                }
                castFailures++;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Grind] {0} (slot {1}, key '{2}') changed nothing in {3} ms: mana {4} -> {5}, "
                    + "mob health {6} - {7} failure(s) this fight{8}",
                    castDamage!.Name, castDamage!.Slot, ClickAimCombat.KeyLabel(castDamage!.KeyVk), ClickAimCombat.CastVerifyMs,
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
                    if (!FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter))
                    { return YieldForTargetChange(reader, fightMemory, defenceTarget); }
                    if (!auto.IsAttack) return "Attack role changed; attack withheld";
                    ClickAimInput.TapKey(hwnd, auto.KeyVk, 40);
                    MarkFocusedAttack(target);
                    lastAttackPressMs = Environment.TickCount;
                    presses++;
                    _presses++;
                    attacked = true;
                    Logging.Write("[Grind] pressing Attack to start the swing (it stays on until "
                                + "something stops it; pressing it again would turn it off)");
                }
            }
            else if (!swinging && !pressPending
                     && !((!defenceOnly || defenceFactionFilter) && GrindFactionSettings.Rules.Enabled)
                     && unchecked(Environment.TickCount - lastMeleeClickMs) >= MeleeClickMs)
            {
                lastMeleeClickMs = Environment.TickCount;
                if (TryClickUnit(hwnd, target, me.Location.X, me.Location.Y))
                {
                    MarkFocusedAttack(target);
                    _clicks++;
                    presses++;
                    attacked = true;
                }
            }

            if (auto == null && !noSafeAutoLogged && (!defenceOnly || defenceFactionFilter) && GrindFactionSettings.Rules.Enabled)
            {
                noSafeAutoLogged = true;
                Logging.Write("[Grind] faction filter: unverified melee right-click withheld; put Attack on an action bar to start auto-attack safely");
            }

            /* The melee half of the bar: Claw, Heroic Strike, Sinister Strike, Rend. These are
             * 5 yd abilities, so this is the only branch that can use them - the cast loop would
             * have stood at 28 yd pressing a key the client refuses. Rage and energy are not
             * readable apart from mana here, so the press goes out, and a refusal costs the
             * client's own error line rather than a global cooldown spent twice. */
            if (meleeHit != null && (!swinging || sinceDamageMs >= MeleePressMs)
                && ClickAimClassRotation.Ready(_rotationCooldowns, rotation.MeleeGroup, Environment.TickCount)
                && (rotation.MeleeIntervalMs == 0 || myMana >= PowerForCast(meleeHit, 10)))
            {
                if (!FightAttackInputAllowed(reader, fightMemory, defenceTarget, expectedPull, !defenceOnly || defenceFactionFilter))
                { return YieldForTargetChange(reader, fightMemory, defenceTarget); }
                if (!meleeHit.IsDamage) return "melee ability role changed; attack withheld";
                ClickAimInput.TapKey(hwnd, meleeHit.KeyVk, 40);
                ClickAimClassRotation.Defer(_rotationCooldowns, rotation.MeleeGroup, rotation.MeleeIntervalMs, Environment.TickCount);
                if (rotation.MeleeIntervalMs > 0) { _pendingCastId = meleeHit.Id; _pendingCastMana = myMana; }
                MarkFocusedAttack(target);
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
        if (_stop || ClickAimAbort.Requested) return "stopped";

        if (worked.Count > 0)
            return "gave up on the target after " + presses + " press(es) but "
                   + string.Join(", ", worked.ToArray()) + " landed";

        return "nothing landed on the target: "
               + (damage == null ? "no damage spell on the bar" : "the spell key did not cast")
               + (auto == null ? "" : " and auto-attack did not start")
               + "; its health is still "
               + (lastHealth >= 0 ? lastHealth.ToString(CultureInfo.InvariantCulture) : "unreadable");
    }

    // TAB is used only to restore the original target; no attack is sent here.
    // A changed target remains subject to the normal defence handoff checks.
    private static bool TryRestoreHealTarget(IntPtr hwnd, WowBReader reader, GreenMagic.Memory memory, WowBGuid wanted,
                                             WowBObservedUnit expectedPull, bool defenceOnly, bool defenceFactionFilter)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            if (_stop || ClickAimAbort.Requested || ObjectManager.WowB != reader || ObjectManager.Wow != memory ||
                (reader.IsExperimental70707 && !WowBGrindFactions.Experimental70707BarSafe(reader))) return false;
            if (Capture(out WowBWorldSnapshot? back) && back != null && back.TryGetSelectedTarget(out WowBObservedUnit selected) &&
                selected.Guid == wanted &&
                (defenceOnly && !defenceFactionFilter || FactionPullStillAllowed(reader, expectedPull, selected))) return true;
            ClickAimInput.TapKey(hwnd, TabVk, 40);
            if (ClickAimAbort.Wait(TabSettleMs)) return false;
        }
        return !_stop && !ClickAimAbort.Requested && ObjectManager.WowB == reader && ObjectManager.Wow == memory &&
            (!reader.IsExperimental70707 || WowBGrindFactions.Experimental70707BarSafe(reader)) &&
            Capture(out WowBWorldSnapshot? last) && last != null && last.TryGetSelectedTarget(out WowBObservedUnit finalTarget) &&
            finalTarget.Guid == wanted &&
            (defenceOnly && !defenceFactionFilter || FactionPullStillAllowed(reader, expectedPull, finalTarget));
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

        check("trialConfirmedKill", CountFightAsKill("killed with Lightning Bolt", true));
        check("trialUnknownOutcome", !CountFightAsKill("killed or fled after 8 s", true));
        check("trialUnknownMidReport", !CountFightAsKill("killed or fled while attacking", true));
        check("legacyKillAccountingUnchanged", CountFightAsKill("killed or fled after 8 s", false));
        check("trialNoKill", !CountFightAsKill("target gone and nothing landed", true));

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
            // 18:02 controlled sample: 0x808 (idle), 0x80808 (in combat),
            // 0x8 (idle again). 0x800 on its own must not hold combat true.
            check("combatUnknown", CombatFlagSaysFight(null));
            check("combatIdleBefore", !CombatFlagSaysFight(0x808));
            check("combatObservedFight", CombatFlagSaysFight(0x80808));
            check("combatIdleAfter", !CombatFlagSaysFight(0x8));
            check("combatOldStaleBit", !CombatFlagSaysFight(0x800));
            check("combatCandidateOnItsOwn", CombatFlagSaysFight(0x80000));
            check("remotePullYields", ShouldYieldRemotePull(94, 28, true)
                                     && ShouldYieldRemotePull(94, 28, false));
            check("nearAddYields", ShouldYieldRemotePull(20, 28, true));
            check("inRangeTargetWithoutAdd", !ShouldYieldRemotePull(20, 28, false));
            check("damagedTargetWithAddStays", !ShouldYieldRemotePull(20, 28, true, true));
            check("damagedButRemoteStillYields", ShouldYieldRemotePull(94, 28, true, true));
            check("closeTargetNotAbandoned", !ShouldYieldRemotePull(5, 28, true));

            check("viableSameLevel", LevelViable(2, 2));
            check("viableTheDumpedMobs", LevelViable(3, 2) && LevelViable(4, 2));
            check("viableTooHigh", !LevelViable(9, 2));
            check("viableGrey", !LevelViable(1, 9));
            check("viableUnknownLevel", LevelViable(0, 2) && LevelViable(4, 0));
            check("viableSixBelow", LevelViable(4, 10) && LevelViable(5, 10));
            check("notViableSevenBelow", !LevelViable(3, 10));
            check("viableThreeAbove", LevelViable(13, 10) && !LevelViable(14, 10));
            // Voluntary pulls stay level-limited, but defence must not ignore
            // an out-of-band attacker (below or above the normal pull range).
            check("lowNotPulledButEligibleForDefence", !LevelViable(3, 10) && DefenceFieldsValid(100, 0, false));
            check("highNotPulledButEligibleForDefence", !LevelViable(14, 10) && DefenceFieldsValid(100, 0, false));
            check("defenceStillRejectsDead", !DefenceFieldsValid(0, 0, false));
            check("defenceStillRejectsFlags", !DefenceFieldsValid(100, 0x200, false));
            check("defenceStillRejectsRefused", !DefenceFieldsValid(100, 0, true));
            check("selectionRejectsTooLow", !SelectionFieldsValid(10, 0, 1, 8));
            check("selectionAcceptsSixBelow", SelectionFieldsValid(10, 0, 4, 10));
            check("selectionAcceptsOther", SelectionFieldsValid(10, 0, 5, 7));
            check("selectionRechecksAfterLevel", !SelectionFieldsValid(10, 0, 11, 7)
                                                && SelectionFieldsValid(10, 0, 11, 8));
            check("selectionRejectsDead", !SelectionFieldsValid(0, 0, 5, 7));
            check("selectionRejectsFlagged", !SelectionFieldsValid(10, 0x200, 5, 7));
            check("selectionUnknownLevel", SelectionFieldsValid(10, 0, 0, 7));
            check("approachAt100", ShouldApproachPoolCandidate(false, true, 100));
            check("approachBoundary", !ShouldApproachPoolCandidate(false, true, 40) && ShouldApproachPoolCandidate(false, true, 40.01));
            check("approachNotOutsideOrUnknown", !ShouldApproachPoolCandidate(false, true, 100.01) && !ShouldApproachPoolCandidate(false, true, double.NaN));
            check("approachRequiresCandidateAndNoSelection", !ShouldApproachPoolCandidate(false, false, 90) && !ShouldApproachPoolCandidate(true, true, 90));
            check("preferNearerUnverified", PreferNearerPoolCandidate(true, true, false, 93.9, 67.4));
            check("keepTabConfirmed", !PreferNearerPoolCandidate(false, true, false, 93.9, 67.4));
            check("keepNearUnverified", !PreferNearerPoolCandidate(true, true, false, 75, 67.4));
            check("keepSameCandidate", !PreferNearerPoolCandidate(true, true, true, 94, 67));

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
            check("manaWaitBelow", ManaRecoveryNeeded(35, 12));
            check("manaWaitUnknown", ManaRecoveryNeeded(35, -1));
            check("manaWaitReached", !ManaRecoveryNeeded(35, 35));
            check("manaWaitDisabled", !ManaRecoveryNeeded(0, 0));
            check("engagementRadius", Math.Abs(SearchYd - 100.0) < 0.001);
            check("gatherSafetyNearbyAlive", GatherUnitBlocks(false, false, 12.0, true, 1, false, false));
            check("gatherSafetyAllowedPull", GatherUnitBlocks(false, false, 40.0, true, 1, true, false));
            check("gatherSafetyUnknown", GatherUnitBlocks(false, false, 35.0, false, 0, false, true));
            check("gatherSafetyUnknownFaction", GatherUnitBlocks(false, false, 35.0, true, 1, false, true));
            check("gatherSafetyUnlistedAtDistance", !GatherUnitBlocks(false, false, 35.0, true, 1, false, false));
            check("gatherSafetyCorpse", !GatherUnitBlocks(false, false, 10.0, true, 0, true, false));
            check("gatherSafetyOutOfRange", !GatherUnitBlocks(false, false, 40.1, true, 1, true, false));
            check("gatherSafetyPlayer", !GatherUnitBlocks(false, true, 10.0, true, 1, true, false));
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
            check("chaseNoClosingBound", ChaseNoClosingMs > StuckMs * StuckEscapes
                                           && ChaseNoClosingMs < UnreachablePauseMs);
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

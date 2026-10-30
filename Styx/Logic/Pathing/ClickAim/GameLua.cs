using System;
using System.Collections.Generic;
using System.Globalization;
using GreenMagic;

namespace Styx.Logic.Pathing;

/// <summary>
/// The client's own Lua, used as an API: <c>UnitHealth("target")</c>,
/// <c>UseAction("3")</c>, <c>TargetNearestEnemy()</c>.
///
/// This exists because the keyboard ran out. On 70291 the action bar does not answer to
/// key messages - <see cref="GrindRunner"/> presses 1-5, T and F, measures the target's
/// health and honestly reports that nothing happened - while the Lua state is open enough
/// for <c>GreenMagic/LuaBridge64.cs</c> to run a global function with one string argument.
/// That is exactly the shape a combat rotation needs: everything on the vanilla/Classic
/// action-bar API takes a slot number or a unit id, and Lua coerces "3" to 3 for a C
/// function that asks for a number.
///
/// What this is NOT: a promise that any of it works. Attaching can fail at four different
/// gates (process, Lua canary, trampoline shape, Lua thread), the payload can time out,
/// and a realm can refuse the call. Every failure is reported as a string, and every
/// rotation decision taken through here is still verified the same way key presses are -
/// by watching health move. Not one line of this claims a call is operational until the
/// user's log shows it.
///
/// It also cannot call anything with two arguments, or an expression: the payload does
/// <c>pcall(globals[name], "arg")</c> and nothing else. That rules out
/// <c>UnitReaction("target","player")</c> and every <c>C_</c> namespace, and is why
/// targeting stays with the client: <c>TargetNearestEnemy()</c> when Lua answers, the TAB
/// key when it does not.
/// </summary>
internal sealed class GameLua : IDisposable
{
    /// <summary>The client is <c>wowb.exe</c>; the bridge matches the name prefix.</summary>
    private const string ProcessName = "wowb";

    private const int CallTimeoutMs = 2000;

    private readonly LuaBridge64 _bridge;
    private readonly bool _eventsAllowed;
    private int _calls;
    private bool _dead;
    private int _takeoverFailures;   // takeovers that could not be placed, in a row

    private GameLua(LuaBridge64 bridge, bool eventsAllowed)
    {
        _bridge = bridge;
        _eventsAllowed = eventsAllowed;
    }

    /// <summary>True while the payload is resident and has not wedged.</summary>
    public bool Live { get { return !_dead && _bridge.IsStarted; } }

    /// <summary>
    /// True when a command runs on the client's OWN Lua thread, so the ones that raise a
    /// FrameScript event - <c>UseAction</c>, <c>CastSpellByName</c>,
    /// <c>TargetNearestEnemy</c> - may be sent at all.
    ///
    /// This is not politeness. On 2026-10-30 the grind reached its first hotspot, called
    /// <c>TargetNearestEnemy()</c> and the client died with
    ///
    ///     Error: ASSERT(FrameScript_IsLuaThread())
    ///     File: Engine/Source/FrameScript/FrameScriptEvents.cpp, Line 208
    ///
    /// Every call before that one had been a question (<c>UnitHealth</c>,
    /// <c>UnitExists</c>, <c>GetTime</c>) and answered fine, because a question raises no
    /// event. The first call that actually did something selected a mob, the client tried
    /// to dispatch PLAYER_TARGET_CHANGED, found itself on our injected thread and
    /// terminated. So: questions through the resident thread are fine, actions are only
    /// sent when the command runs on the thread the assert is asking about.
    /// </summary>
    public bool EventsAllowed { get { return _eventsAllowed && Live; } }

    /// <summary>
    /// The globals that raise a FrameScript event. Sending one of these from any other
    /// thread is a crash, not a refusal, so the bridge refuses it locally instead.
    /// </summary>
    private static bool RaisesEvent(string function)
    {
        return string.Equals(function, "UseAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "CastSpell", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "CastSpellByName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "TargetNearestEnemy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "TargetNearestPartyMember", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "TargetUnit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "AttackTarget", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "RunMacroText", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "StartAttack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function, "StopAttack", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Commands actually sent to the client - one log line per fight is enough.</summary>
    public int Calls { get { return _calls; } }

    /// <summary>The last thing that went wrong, for the log. Null while everything works.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Attaches to <c>wowb.exe</c> and starts the resident payload, or returns null with the
    /// reason in <paramref name="report"/>. Never throws: a grind loop that cannot get Lua
    /// must fall back to the keyboard, not die.
    /// </summary>
    public static GameLua? TryOpen(int serveSeconds, out string report)
    {
        int pid = 0;
        try
        {
            pid = LuaBridge64.FindProcessId(ProcessName);
        }
        catch (Exception ex)
        {
            report = "could not enumerate processes: " + ex.Message;
            return null;
        }

        if (pid == 0)
        {
            report = "no process whose name starts with '" + ProcessName + "'";
            return null;
        }

        /* Two ways to run a command, and only one of them may raise an event.
         *
         * ServerThread is the resident injected thread. It answers questions about the
         * client's Lua perfectly well - 1600+ answers before 2026-10-30 - and it cannot
         * make the client DO anything: the assert above is about which thread the command
         * runs on, not about what it is.
         *
         * LuaThreadHijack runs the command on the client's own Lua thread, which is exactly
         * the condition FrameScript_IsLuaThread() tests. That is the only mode allowed to
         * send UseAction, and it is tried first.
         */
        var hijack = new LuaBridge64();
        string hijackFailure;
        bool opened = TryStart(hijack, pid, LuaExecutionMode.LuaThreadHijack, false, serveSeconds, out hijackFailure);
        if (opened)
        {
            /* The setup (thread handle, CONTEXT round-trip) is green. The first command is
             * what proves a takeover, so run the cheapest real one now, while there is a
             * line to put it in. */
            string firstCall;
            try
            {
                hijack.Call("GetTime", string.Empty, 1, 2000);
                report = Describe(hijack, pid) + "; the client's own Lua thread can be taken over ("
                         + (string.IsNullOrEmpty(hijack.HijackHow) ? "no detail" : hijack.HijackHow) + ", "
                         + hijack.HijackAttempts.ToString(CultureInfo.InvariantCulture)
                         + " quiet moment(s)) - events may be raised";
                return new GameLua(hijack, true);
            }
            catch (Exception ex)
            {
                firstCall = ex.Message;
            }

            /* Setup was green but the takeover could not run a command. Do NOT fall back to
             * the resident thread for anything that raises an event: that is the crash. Keep
             * the bridge - questions still work and a later moment may be quiet enough - but
             * refuse every action locally. */
            report = Describe(hijack, pid) + "; but the first takeover failed: " + firstCall
                     + ". Questions still work; nothing that raises an event will be sent"
                     + " (the client asserts FrameScript_IsLuaThread() and dies)";
            return new GameLua(hijack, false);
        }

        var resident = new LuaBridge64();
        string residentFailure;
        if (!TryStart(resident, pid, LuaExecutionMode.ServerThread, true, serveSeconds, out residentFailure))
        {
            report = "Lua is not usable on pid " + pid.ToString(CultureInfo.InvariantCulture)
                     + ": no takeover (" + hijackFailure + ") and no resident thread (" + residentFailure + ")";
            return null;
        }

        report = Describe(resident, pid) + "; the Lua-thread takeover could not be set up ("
                 + hijackFailure + ") - READ-ONLY: questions only, because the client asserts "
                 + "FrameScript_IsLuaThread() and dies when an event is raised from our thread";
        return new GameLua(resident, false);
    }

    /// <summary>Attach, choose the execution mode, start. False (with the reason) on any gate.</summary>
    private static bool TryStart(LuaBridge64 bridge, int pid, LuaExecutionMode mode, bool guard,
                                 int serveSeconds, out string failure)
    {
        failure = "";
        try
        {
            bridge.Attach(pid);
        }
        catch (Exception ex)
        {
            /* The four gates all throw here, each with a sentence that says which one. */
            try { bridge.Dispose(); } catch { }
            failure = "attach to pid " + pid.ToString(CultureInfo.InvariantCulture) + " failed: " + ex.Message;
            return false;
        }

        bridge.ExecutionMode = mode;
        bridge.GuardEnabled = guard;      // the guard is a cross-thread safety net; a takeover needs no freeze
        try
        {
            bridge.Start(Math.Max(120, serveSeconds), 1);
            return true;
        }
        catch (Exception ex)
        {
            try { bridge.Dispose(); } catch { }
            failure = "the payload did not start: " + ex.Message;
            return false;
        }
    }

    private static string Describe(LuaBridge64 bridge, int pid)
    {
        return "pid " + pid.ToString(CultureInfo.InvariantCulture) + ", payload resident ("
               + LuaBridge64.PayloadSize.ToString(CultureInfo.InvariantCulture) + " bytes), execution "
               + bridge.ExecutionMode.ToString();
    }

    /// <summary>
    /// Calls <c>globals[function](arg)</c> and returns its first result, or null with
    /// <see cref="Error"/> set. Passing <paramref name="arg"/> null or empty calls it with
    /// no arguments, which is what <c>TargetNearestEnemy()</c> needs.
    /// </summary>
    public LuaValue[]? Call(string function, string? arg, int results)
    {
        if (_dead) { Error = "the payload is wedged; nothing more will be sent"; return null; }

        if (!_eventsAllowed && RaisesEvent(function))
        {
            Error = function + " raises a FrameScript event, and this bridge is not running commands "
                    + "on the client's own Lua thread; sending it would kill the client "
                    + "(ASSERT(FrameScript_IsLuaThread()), 2026-10-30). Nothing was sent.";
            return null;
        }

        try
        {
            LuaValue[] value = _bridge.Call(function, arg ?? string.Empty, results, CallTimeoutMs);
            _calls++;
            Error = null;
            return value;
        }
        catch (TimeoutException ex)
        {
            /* A wedged payload keeps the control block mid-command; every later call would
             * throw the same way and eventually hang. Stop touching the client. */
            _dead = true;
            Error = function + ": " + ex.Message;
            return null;
        }
        catch (LuaTakeoverException ex)
        {
            /* No quiet moment to take the Lua thread over in. Nothing was written into the
             * client, but a command that cannot be placed is a command that cannot be sent:
             * give the moment a couple more tries, then stop spending the client's time. */
            _takeoverFailures++;
            Error = function + ": " + ex.Message;
            if (_takeoverFailures >= 3)
            {
                _dead = true;
                Error += " - three takeovers in a row could not be placed; this bridge is done";
            }
            return null;
        }
        catch (Exception ex)
        {
            Error = function + (string.IsNullOrEmpty(arg) ? "()" : "(\"" + arg + "\")") + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>A number-valued global, or null. Marks the bridge dead on a wedge.</summary>
    public double? Number(string function, string? arg = null)
    {
        LuaValue[]? v = Call(function, arg, 1);
        if (v == null || v.Length == 0) return null;
        if (v[0].Type == LuaType.Nil) return null;
        if (v[0].Type != LuaType.Number)
        {
            Error = function + " returned " + v[0].Type + ", not a number";
            return null;
        }
        return v[0].Number;
    }

    /// <summary>A string-valued global, or null.</summary>
    public string? Text(string function, string? arg = null)
    {
        LuaValue[]? v = Call(function, arg, 1);
        if (v == null || v.Length == 0) return null;
        if (v[0].Type == LuaType.Nil) return null;
        if (v[0].Type != LuaType.String)
        {
            Error = function + " returned " + v[0].Type + ", not a string";
            return null;
        }
        return v[0].Text;
    }

    /// <summary>A boolean-valued global. A missing global is null, not false.</summary>
    public bool? Flag(string function, string? arg = null)
    {
        LuaValue[]? v = Call(function, arg, 1);
        if (v == null || v.Length == 0) return null;
        if (v[0].Type == LuaType.Nil) return null;
        if (v[0].Type != LuaType.Boolean)
        {
            Error = function + " returned " + v[0].Type + ", not a boolean";
            return null;
        }
        return v[0].Boolean;
    }

    /// <summary>True when the global exists at all: <c>nil value</c> in the error means it does not.</summary>
    public bool Exists(string function, string? arg = null)
    {
        Call(function, arg, 1);
        string? err = Error;
        if (err == null) return true;
        return err.IndexOf("nil value", StringComparison.OrdinalIgnoreCase) < 0
               && err.IndexOf("attempt to call", StringComparison.OrdinalIgnoreCase) < 0;
    }

    // ---------------------------------------------------------------- action bar
    /// <summary>One filled action-bar slot, as the client describes it.</summary>
    internal sealed class ActionSlot
    {
        public int Slot;

        /// <summary>The icon path, e.g. <c>Interface\Icons\Spell_Fire_Firebolt</c>.</summary>
        public string Icon = "";

        /// <summary>The client says it can be used right now (enough mana, reagents, no cooldown state).</summary>
        public bool Usable;

        /// <summary>The client says the target is in the ability's range. Null when it has no range.</summary>
        public bool? InRange;

        /// <summary>Seconds the slot stays on cooldown; 0 when ready.</summary>
        public double Cooldown;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "slot {0} [{1}] usable={2} range={3} cd={4:F1}s",
                Slot, System.IO.Path.GetFileNameWithoutExtension(Icon),
                Usable ? "yes" : "no",
                InRange.HasValue ? (InRange.Value ? "yes" : "no") : "?",
                Cooldown);
        }
    }

    /// <summary>
    /// Reads slots 1..<paramref name="lastSlot"/> and returns the ones that hold something.
    /// The slot count is a constant rather than a client query because the vanilla/Classic
    /// global for it changed name between patches and a wrong name would silently return
    /// the empty bar.
    /// </summary>
    public List<ActionSlot> ReadActionBar(int lastSlot = 12)
    {
        var slots = new List<ActionSlot>();
        double now = Number("GetTime") ?? 0;
        for (int slot = 1; slot <= lastSlot; slot++)
        {
            string index = slot.ToString(CultureInfo.InvariantCulture);

            bool? has = Flag("HasAction", index);
            if (has != true) continue;

            var entry = new ActionSlot { Slot = slot };
            entry.Icon = Text("GetActionTexture", index) ?? "";
            entry.Usable = Flag("IsUsableAction", index) == true;

            /* IsActionInRange returns 1/nil rather than true/nil on the older API, and the
             * bridge reports what it saw - so accept both and treat nil as "no range". */
            LuaValue[]? range = Call("IsActionInRange", index, 1);
            if (range != null && range.Length > 0 && range[0].Type != LuaType.Nil)
            {
                if (range[0].Type == LuaType.Boolean) entry.InRange = range[0].Boolean;
                else if (range[0].Type == LuaType.Number) entry.InRange = range[0].Number != 0;
            }

            entry.Cooldown = Cooldown(slot, now);

            slots.Add(entry);
        }
        return slots;
    }

    /// <summary>
    /// Seconds left on one slot's cooldown, 0 when it is ready.
    ///
    /// <c>GetActionCooldown</c> returns two values, start time and duration, and both are 0
    /// when the slot is ready. The remaining time is computed against <c>GetTime()</c> rather
    /// than trusted as "duration left", because the API's first value is an absolute stamp.
    /// </summary>
    public double Cooldown(int slot, double now)
    {
        LuaValue[]? cd = Call("GetActionCooldown", slot.ToString(CultureInfo.InvariantCulture), 2);
        if (cd == null || cd.Length < 2) return 0;
        double start = cd[0].Type == LuaType.Number ? cd[0].Number : 0;
        double duration = cd[1].Type == LuaType.Number ? cd[1].Number : 0;
        if (duration <= 0) return 0;
        double left = start + duration - now;
        return left > 0 ? left : 0;
    }

    /// <summary>
    /// Re-reads whether each slot's ability reaches the current target, from where the
    /// character is standing now.
    ///
    /// <c>IsActionInRange</c> is a property of the moment, not of the slot: a Lightning Bolt
    /// is in range at 30 yd and out of it at 40. Singular decides when to move with
    /// <c>Movement.CreateMoveToTargetBehavior(true, 20f)</c> and lets the spell's own range
    /// do the rest; reading this is how the same decision can be made without knowing where
    /// a spell came from or what it is called.
    /// </summary>
    public bool AnySlotInRange(List<ActionSlot> bar)
    {
        for (int i = 0; i < bar.Count; i++)
        {
            ActionSlot entry = bar[i];
            if (!entry.Usable) continue;
            string index = entry.Slot.ToString(CultureInfo.InvariantCulture);
            LuaValue[]? range = Call("IsActionInRange", index, 1);
            if (range == null || range.Length == 0 || range[0].Type == LuaType.Nil) continue;
            bool inRange = range[0].Type == LuaType.Boolean ? range[0].Boolean : range[0].Number != 0;
            entry.InRange = inRange;
            if (inRange) return true;
        }
        return false;
    }

    /// <summary>Re-reads cooldowns for slots already known to be filled.</summary>
    public void RefreshCooldowns(List<ActionSlot> bar)
    {
        double now = Number("GetTime") ?? 0;
        for (int i = 0; i < bar.Count; i++) bar[i].Cooldown = Cooldown(bar[i].Slot, now);
    }

    /// <summary>
    /// The player's health as a percentage, or null when the client will not say. Two calls,
    /// so it is done once per loop iteration, not per cast.
    /// </summary>
    public double? HealthPercent()
    {
        double? max = Number("UnitHealthMax", "player");
        if (max == null || max.Value <= 0) return null;
        double? now = Number("UnitHealth", "player");
        if (now == null) return null;
        return now.Value * 100.0 / max.Value;
    }

    /// <summary>
    /// True while the player is casting or channelling.
    ///
    /// This is Singular's <c>Spell.WaitForCast(true)</c>, and it matters more here than it
    /// looks: pressing a second ability in the middle of a cast interrupts the first one, so
    /// a loop that fires every 700 ms without asking can keep a caster permanently at the
    /// start of a cast and never land anything. <c>UnitCastingInfo</c>/<c>UnitChannelInfo</c>
    /// return nil when idle, and have done since vanilla.
    /// </summary>
    public bool IsCasting()
    {
        LuaValue[]? cast = Call("UnitCastingInfo", "player", 2);
        if (cast != null && cast.Length > 0 && cast[0].Type != LuaType.Nil) return true;
        LuaValue[]? channel = Call("UnitChannelInfo", "player", 2);
        return channel != null && channel.Length > 0 && channel[0].Type != LuaType.Nil;
    }

    /// <summary>Fires an action-bar slot. This is the bot's attack.</summary>
    public bool UseSlot(int slot)
    {
        return Call("UseAction", slot.ToString(CultureInfo.InvariantCulture), 1) != null;
    }

    /// <summary>Casts by name, for the day a routine knows the spellbook.</summary>
    public bool CastSpell(string name)
    {
        return Call("CastSpellByName", name, 1) != null;
    }

    /// <summary>The client's own nearest-enemy selection, the same thing TAB does.</summary>
    public bool TargetNearestEnemy()
    {
        return Call("TargetNearestEnemy", null, 1) != null;
    }

    /// <summary>
    /// What this client answers to, printed once. Each line is a real call whose result is
    /// shown, so a name that does not exist on 70291 shows up as an error instead of being
    /// assumed.
    /// </summary>
    public List<string> Probe()
    {
        var lines = new List<string>();
        lines.Add("events " + (EventsAllowed ? "ALLOWED - commands run on the client's own Lua thread"
                                             : "BLOCKED - questions only (an event raised from our thread kills the client)"));
        lines.Add(Line("UnitName('player')", () => Text("UnitName", "player") ?? "nil"));
        lines.Add(Line("UnitLevel('player')", () => Num(Number("UnitLevel", "player"))));
        lines.Add(Line("UnitHealth('player')", () => Num(Number("UnitHealth", "player"))));
        lines.Add(Line("UnitHealthMax('player')", () => Num(Number("UnitHealthMax", "player"))));
        lines.Add(Line("UnitExists('target')", () => Flag("UnitExists", "target")?.ToString() ?? "nil"));
        lines.Add(Line("UnitIsDead('target')", () => Flag("UnitIsDead", "target")?.ToString() ?? "nil"));
        lines.Add(Line("GetZoneText()", () => Text("GetZoneText") ?? "nil"));
        lines.Add(Line("GetNumPartyMembers()", () => Num(Number("GetNumPartyMembers"))));
        lines.Add(Line("HasAction(1)", () => Flag("HasAction", "1")?.ToString() ?? "nil"));
        lines.Add(Line("IsUsableAction(1)", () => Flag("IsUsableAction", "1")?.ToString() ?? "nil"));
        lines.Add(Line("GetActionTexture(1)", () => Text("GetActionTexture", "1") ?? "nil"));
        lines.Add(Line("IsActionInRange(1)", () => Num(Number("IsActionInRange", "1"))));
        lines.Add(Line("GetActionCooldown(1)", () => Num(Number("GetActionCooldown", "1"))));
        lines.Add(Line("UnitCastingInfo('player')", () => Text("UnitCastingInfo", "player") ?? "nil"));
        lines.Add(Line("GetTime()", () => Num(Number("GetTime"))));
        return lines;
    }

    private string Line(string what, Func<string> call)
    {
        string? err = null;
        string value;
        try
        {
            value = call();
        }
        catch (Exception ex)
        {
            value = "<" + ex.Message + ">";
        }
        err = Error;
        return what + " -> " + value + (err == null ? "" : "   (" + err + ")");
    }

    private static string Num(double? value)
    {
        return value.HasValue ? value.Value.ToString("F0", CultureInfo.InvariantCulture) : "nil";
    }

    public void Dispose()
    {
        try { _bridge.Stop(); } catch { }
        try { _bridge.Dispose(); } catch { }
    }
}

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
    private int _calls;
    private bool _dead;

    private GameLua(LuaBridge64 bridge)
    {
        _bridge = bridge;
    }

    /// <summary>True while the payload is resident and has not wedged.</summary>
    public bool Live { get { return !_dead && _bridge.IsStarted; } }

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

        var bridge = new LuaBridge64();
        try
        {
            bridge.Attach(pid);
        }
        catch (Exception ex)
        {
            /* The four gates all throw here, each with a sentence that says which one. */
            try { bridge.Dispose(); } catch { }
            report = "attach to pid " + pid.ToString(CultureInfo.InvariantCulture) + " failed: " + ex.Message;
            return null;
        }

        try
        {
            bridge.Start(Math.Max(120, serveSeconds), 1);
        }
        catch (Exception ex)
        {
            try { bridge.Dispose(); } catch { }
            report = "pid " + pid.ToString(CultureInfo.InvariantCulture) + " attached, but the payload did not start: " + ex.Message;
            return null;
        }

        report = "pid " + pid.ToString(CultureInfo.InvariantCulture) + ", payload resident (" + LuaBridge64.PayloadSize + " bytes)"
                 + ", execution " + bridge.ExecutionMode.ToString();
        return new GameLua(bridge);
    }

    /// <summary>
    /// Calls <c>globals[function](arg)</c> and returns its first result, or null with
    /// <see cref="Error"/> set. Passing <paramref name="arg"/> null or empty calls it with
    /// no arguments, which is what <c>TargetNearestEnemy()</c> needs.
    /// </summary>
    public LuaValue[]? Call(string function, string? arg, int results)
    {
        if (_dead) { Error = "the payload is wedged; nothing more will be sent"; return null; }

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
        lines.Add(Line("UnitName('player')", () => Text("UnitName", "player") ?? "nil"));
        lines.Add(Line("UnitLevel('player')", () => Num(Number("UnitLevel", "player"))));
        lines.Add(Line("UnitHealth('player')", () => Num(Number("UnitHealth", "player"))));
        lines.Add(Line("UnitHealthMax('player')", () => Num(Number("UnitHealthMax", "player"))));
        lines.Add(Line("UnitExists('target')", () => Flag("UnitExists", "target")?.ToString() ?? "nil"));
        lines.Add(Line("UnitIsDead('target')", () => Flag("UnitIsDead", "target")?.ToString() ?? "nil"));
        lines.Add(Line("HasAction(1)", () => Flag("HasAction", "1")?.ToString() ?? "nil"));
        lines.Add(Line("IsUsableAction(1)", () => Flag("IsUsableAction", "1")?.ToString() ?? "nil"));
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

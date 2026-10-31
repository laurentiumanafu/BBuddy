using System;
using System.Collections.Generic;
using Styx.Logic.Profiles;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// What the Start button means on this client.
///
/// The window is the interface the user works in: Load Profile, pick a bot, Start. On a
/// 3.3.5a client that starts <c>TreeRoot</c>, the behaviour tree, and every bot is
/// implemented as a tree of actions over the 32-bit object model. On <c>wowb.exe</c> none
/// of that can run - <c>TreeRoot.Start</c> refuses, because the wrappers are still 32-bit -
/// so the button did nothing at all, which is why everything up to now had to be driven
/// from .cmd files and a request file.
///
/// This is the translation layer: with the 64-bit client attached and the Grind bot
/// selected, Start runs <see cref="GrindRunner"/> on whatever profile the window loaded.
/// Same button, same profile list, same Stop.
///
/// It is deliberately strict about which bot it will start. Only Grind has a 64-bit
/// implementation; running Quest or BGBuddy would silently do nothing, and a button that
/// silently does nothing is the exact problem this fixes.
/// </summary>
internal static class ClickAimUi
{
    /// <summary>The one bot that has a 64-bit implementation: <c>Bots/Grind/LevelBot.cs</c>.</summary>
    public const string GrindBotName = "Grind";
    public const string GatherBotName = "GatherBuddy";

    /// <summary>True when the attached client is the 64-bit one this port exists for.</summary>
    public static bool IsWowB
    {
        get
        {
            try { return ObjectManager.Wow != null && ObjectManager.Wow.Is64BitTarget; }
            catch { return false; }
        }
    }

    /// <summary>The profile the window loaded, or "" when none.</summary>
    public static string LoadedProfile
    {
        get
        {
            if (_loaded != null) return _loaded;
            try { return ProfileManager.XmlLocation ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Load Profile, on this client: remember the path and say what is in the file, without
    /// building the legacy <c>Profile</c> object.
    ///
    /// That object is a 3.3.5a construct - it compiles profile code, asks the area manager to
    /// resolve a zone and hands the tree a nested profile tree - and none of it is used by a
    /// grind loop that reads the XML itself. Parsing it would only add a way for Load to fail
    /// on a profile that then works perfectly well.
    /// </summary>
    public static string Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return "no such file: " + (path ?? "");

        string name = System.IO.Path.GetFileName(path);

        List<ProfileGrindArea> areas;
        try { areas = ProfileRoute.LoadGrindAreas(path); }
        catch (Exception ex) { return "could not read " + name + ": " + ex.Message; }

        int waypoints = 0;
        try { waypoints = ProfileRoute.Load(path).Count; }
        catch { waypoints = 0; }

        if (areas.Count == 0 && waypoints == 0)
            return "read " + name + ", but it has no <GrindArea> and no Hotspot/RunTo - "
                   + "nothing in it can be run";

        _loaded = path;

        if (areas.Count > 0)
        {
            int hotspots = 0;
            for (int i = 0; i < areas.Count; i++) hotspots += areas[i].Hotspots.Count;
            string first = areas[0].Name.Length > 0 ? areas[0].Name : "(unnamed)";
            return "loaded " + name + ": " + areas.Count + " grind area(s), " + hotspots
                   + " hotspot(s), first \"" + first + "\". Press Start to grind.";
        }

        return "loaded " + name + ": " + waypoints + " waypoint(s) and no <GrindArea>. "
               + "Start walks them once instead of grinding.";
    }

    /// <summary>True while anything is moving: the grind loop or a profile walk.</summary>
    public static bool Running
    {
        get { return GrindRunner.Running || GatherRunner.Running || ClickAimDiagnostics.RouteRunning; }
    }

    /// <summary>
    /// Starts the grind loop. Returns the sentence for the log and the status bar; the
    /// caller checks <see cref="Running"/> to know whether anything actually started.
    /// </summary>
    public static string Start(string? botName)
    {
        if (!IsWowB)
            return "the attached client is not wowb.exe; the normal bot loop handles it";

        if (Running)
            return "a bot or route is already running";

        if (string.Equals(botName, GatherBotName, StringComparison.OrdinalIgnoreCase))
        {
            string gatherPath = LoadedProfile;
            if (gatherPath.Length == 0) return "load a profile with hotspots first";
            if (!GatherRunner.Start(gatherPath)) return "GatherBuddy did not start; see the log";
            return "experimental GatherBuddy started. Set your professions in Gather settings; "
                + "combat uses the Grind rotation on a nearby TAB-selected NPC; attacker identity remains unverified";
        }

        if (!string.Equals(botName, GrindBotName, StringComparison.OrdinalIgnoreCase))
        {
            return "on wowb.exe select Grind or GatherBuddy; the other bots need the legacy object model.";
        }

        string path = LoadedProfile;
        if (path.Length == 0)
            return "no profile is loaded - press Load Profile and choose the .xml to grind";

        /* Two kinds of profile, two kinds of run. A levelling/grind profile has <GrindArea>
         * blocks: circle them and fight, forever. A profile with plain Hotspot/RunTo
         * waypoints is a route: walk it once. Which one this is decided by the file, not by
         * the user, because the file already says. */
        int areas = 0;
        try { areas = ProfileRoute.LoadGrindAreas(path).Count; }
        catch (Exception ex) { return "could not read \"" + System.IO.Path.GetFileName(path) + "\": " + ex.Message; }

        string name = System.IO.Path.GetFileName(path);

        if (areas > 0)
        {
            if (!GrindRunner.Start(path, 0))
                return "the grind loop refused \"" + name + "\" - the reason is in the lines above";

            return "grinding \"" + name + "\" (" + areas.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + " area(s)) - hotspots from its <GrindArea>, area chosen by level band, "
                   + "combat from the action bar in memory, pressed as keys, targets only from the "
                   + "client's own TAB filter - no Lua";
        }

        if (!ClickAimDiagnostics.StartProfile(path, 0, 0, false, false))
            return "\"" + name + "\" has neither <GrindArea> nor Hotspot/RunTo waypoints - nothing to walk";

        return "walking \"" + name + "\" once - it has waypoints but no <GrindArea>, so there is "
               + "nothing to circle and fight";
    }

    /// <summary>Stops whatever is running. Safe to call when nothing is.</summary>
    public static string Stop()
    {
        /* Requesting the abort is unconditional and comes first. Even with no loop registered as
         * running, a key pressed by the last one can still be held down, and letting go of it is
         * the difference between "stopped" and "walks away from the window". */
        ClickAimAbort.Request();
        ClickAimDiagnostics.RequestStop();
        if (!Running) return "nothing is running";
        GatherRunner.Stop();
        GrindRunner.Stop();
        return "stopped: the walk and the turn loop end on this tick";
    }

    /// <summary>
    /// Pause, in this port: stop the loop and remember the profile, so Resume starts the same
    /// one again. The behaviour tree's Pause has nothing to pause here.
    /// </summary>
    public static string Pause(string? botName)
    {
        if (!Running) return "nothing to pause";
        string path = LoadedProfile;
        Stop();
        _paused = path.Length > 0 ? path : null;
        return _paused == null
            ? "paused"
            : "paused - Resume starts \"" + System.IO.Path.GetFileName(_paused) + "\" again";
    }

    public static string Resume(string? botName)
    {
        if (_paused == null) return Start(botName);
        _paused = null;
        if (Running) return "already running";
        return Start(botName);
    }

    public static bool Paused { get { return _paused != null; } }

    private static string? _paused;
    private static string? _loaded;
}

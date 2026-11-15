using System;
using System.IO;
using Styx.Logic.Pathing;

internal static class Program
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("Grind profile fixture failed: " + name); }

    private static void Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "bbuddy-profile-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, """
                <HBProfile><MinLevel>1</MinLevel><MaxLevel>62</MaxLevel>
                  <SubProfile><Name>Northshire 1-3</Name><MinLevel>1</MinLevel><MaxLevel>3</MaxLevel>
                    <GrindArea><Factions>7 32</Factions><TargetMinLevel>1</TargetMinLevel><TargetMaxLevel>5</TargetMaxLevel>
                      <Hotspots><Hotspot X="0" Y="0" Z="10"/><Hotspot X="300" Y="0" Z="10"/></Hotspots>
                    </GrindArea><AvoidMobs><Mob Name="Example" Entry="116"/></AvoidMobs>
                  </SubProfile>
                  <SubProfile><Name>Next</Name><MinLevel>3</MinLevel><MaxLevel>5</MaxLevel>
                    <GrindArea><Factions>14</Factions><TargetMinLevel>3</TargetMinLevel><TargetMaxLevel>6</TargetMaxLevel>
                      <Hotspots><Hotspot X="500" Y="0" Z="5"/></Hotspots>
                    </GrindArea>
                  </SubProfile>
                  <AvoidMobs><Mob Name="Global" Entry="44548"/></AvoidMobs>
                </HBProfile>
                """);
            var areas = ProfileRoute.LoadGrindAreas(path);
            Check(areas.Count == 2 && areas[0].Name == "Northshire 1-3", "parse separate subprofiles");
            var a = areas[0];
            Check(a.PlayerMinLevel == 1 && a.PlayerMaxLevel == 3 && a.TargetMinLevel == 1 && a.TargetMaxLevel == 5 &&
                  a.HasExplicitTargetMinLevel, "player versus explicit target levels");
            Check(GrindProfilePolicy.ValidLevels(a) && GrindProfilePolicy.PlayerInBand(a, 3) && !GrindProfilePolicy.PlayerInBand(a, 4), "select by player band");
            Check(GrindProfilePolicy.PlayerInBand(areas[1], 3), "overlapping player bands remain eligible");
            Check(GrindProfilePolicy.AllowsTarget(a, 100, 0, 5), "100-yard inclusive and upper target level");
            Check(!GrindProfilePolicy.AllowsTarget(a, 100.01, 0, 5), "outside every hotspot");
            Check(GrindProfilePolicy.AllowsTarget(a, 399.9, 0, 1), "any active area hotspot counts");
            Check(!GrindProfilePolicy.AllowsTarget(a, 0, 0, 0) && !GrindProfilePolicy.AllowsTarget(a, 0, 0, 6), "unknown and wrong mob levels withheld");
            Check(!GrindProfilePolicy.AllowsTarget(a, double.NaN, 0, 1), "unknown coordinates withheld");
            Check(a.AvoidEntries.Contains(116) && !areas[1].AvoidEntries.Contains(116) &&
                  a.AvoidEntries.Contains(44548) && areas[1].AvoidEntries.Contains(44548), "area-specific and global AvoidMobs");
            Check(GrindFactionRules.TryCreate(GrindFactionMode.Allow, a.Factions, "", out var rules, out _) &&
                rules.Allows(true, 7) && rules.Allows(true, 32) && !rules.Allows(true, 14) && !rules.Allows(false, 7), "strict profile factions");
            var river = new ProfileGrindArea { PlayerMinLevel = 1, PlayerMaxLevel = 59, TargetMinLevel = 34, TargetMaxLevel = 40, HasExplicitTargetMinLevel = true, Factions = "7 38 16" };
            river.Hotspots.Add(new RoutePoint { X = -9220.5, Y = -4548.3, Z = 16.3 });
            Check(GrindProfilePolicy.AllowsLowerTarget(river, 34, 43) && GrindProfilePolicy.AllowsLowerTarget(river, 36, 43), "explicit low band accepted");
            Check(!GrindProfilePolicy.AllowsLowerTarget(river, 33, 43) && !GrindProfilePolicy.AllowsLowerTarget(river, 41, 43), "outside target band denied");
            Check(!GrindProfilePolicy.AllowsLowerTarget(null, 36, 43) && !GrindProfilePolicy.AllowsLowerTarget(river, 0, 43) &&
                  !GrindProfilePolicy.AllowsLowerTarget(river, 36, 0) && !GrindProfilePolicy.AllowsLowerTarget(river, 36, 60), "unknown and out-of-band states deny override");
            Check(GrindProfilePolicy.AllowsTarget(river, -9220.5, -4548.3, 36) &&
                  !GrindProfilePolicy.AllowsTarget(river, -9000, -4548.3, 36), "profile lower override does not bypass hotspot radius");
            river.HasExplicitTargetMinLevel = false;
            Check(!GrindProfilePolicy.AllowsLowerTarget(river, 36, 43), "fallback minimum does not waive six-level rule");
            river.HasExplicitTargetMinLevel = true; river.InvalidProfileData = true;
            Check(!GrindProfilePolicy.AllowsLowerTarget(river, 36, 43), "invalid profile denies override");
            File.WriteAllText(path, "<HBProfile><SubProfile><MinLevel>34</MinLevel><MaxLevel>59</MaxLevel><GrindArea><Factions>38</Factions><Hotspot X='0' Y='0'/></GrindArea></SubProfile></HBProfile>");
            var fallback = ProfileRoute.LoadGrindAreas(path)[0];
            Check(fallback.TargetMinLevel == 34 && !fallback.HasExplicitTargetMinLevel &&
                  !GrindProfilePolicy.AllowsLowerTarget(fallback, 36, 43), "fallback parser keeps standard cutoff");
            File.WriteAllText(path, "<HBProfile><SubProfile><MinLevel>bad</MinLevel><MaxLevel>3</MaxLevel><GrindArea><Factions>7</Factions><TargetMinLevel>1</TargetMinLevel><TargetMaxLevel>5</TargetMaxLevel><Hotspot X='0' Y='0'/></GrindArea></SubProfile></HBProfile>");
            Check(!GrindProfilePolicy.ValidLevels(ProfileRoute.LoadGrindAreas(path)[0]), "malformed player band rejected");
            File.WriteAllText(path, "<HBProfile><SubProfile><MinLevel>1</MinLevel><MaxLevel>3</MaxLevel><AvoidMobs><Mob Entry='bad'/></AvoidMobs><GrindArea><Factions>7</Factions><TargetMinLevel>1</TargetMinLevel><TargetMaxLevel>5</TargetMaxLevel><Hotspot X='0' Y='0'/></GrindArea></SubProfile></HBProfile>");
            Check(!GrindProfilePolicy.ValidLevels(ProfileRoute.LoadGrindAreas(path)[0]), "malformed avoidance rejected");
            Console.WriteLine("Synthetic profile parsing and policy fixtures passed (no live game test).");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

namespace Styx.Logic.Pathing
{
    public readonly struct WoWPoint { public WoWPoint(float x, float y, float z) { } }
    internal static class ClickAimMath { public static double Len(double x, double y) => Math.Sqrt(x*x + y*y); }
    internal static class ClickAimCalibration { public static string DefaultPath => "Settings/test.txt"; }
}

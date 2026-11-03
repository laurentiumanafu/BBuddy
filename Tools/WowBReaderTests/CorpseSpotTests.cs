#if WOWB_READER_TESTS
using System;
using System.Collections.Generic;
using Styx.Logic.Pathing;

internal static class CorpseSpotTests
{
    internal static void Run()
    {
        var corpse = new WoWPoint(0, 0, 0);
        var current = new WoWPoint(0, 30, 0);
        var npc = new List<WoWPoint> { new WoWPoint(0, 0, 0) };
        if (!WowBCorpseSpotPolicy.InRange(current, corpse, 38) ||
            WowBCorpseSpotPolicy.InRange(new WoWPoint(39, 0, 0), corpse, 38) ||
            WowBCorpseSpotPolicy.InRange(new WoWPoint(0, 0, 16), corpse, 38) ||
            WowBCorpseSpotPolicy.InRange(new WoWPoint(float.NaN, 0, 0), corpse, 38) ||
            WowBCorpseSpotPolicy.ClearEnough(corpse, npc, 25) ||
            !WowBCorpseSpotPolicy.ClearEnough(current, npc, 25) ||
            !WowBCorpseSpotPolicy.ClearEnough(current, new List<WoWPoint>(), 25) ||
            WowBCorpseSpotPolicy.ClearEnough(current, new List<WoWPoint> { new WoWPoint(float.NaN, 0, 0) }, 25))
            throw new Exception("Corpse proximity or NPC clearance policy failed");
        var crowded = new List<WoWPoint> { new WoWPoint(32, 0, 0), new WoWPoint(25, 5, 0) };
        var ranked = WowBCorpseSpotPolicy.ScoutLocations(corpse, current, crowded);
        if (ranked.Count != 4 || ranked[0].Position.X >= 0)
            throw new Exception("Scout ranking did not favor the side away from NPCs");
        var surrounding = new List<WoWPoint>();
        for (int i = 0; i < 16; i++)
            surrounding.Add(new WoWPoint((float)(32 * Math.Cos(i * Math.PI / 8)), (float)(32 * Math.Sin(i * Math.PI / 8)), 0));
        var scouts = WowBCorpseSpotPolicy.ScoutLocations(corpse, current, surrounding);
        var directions = new HashSet<int>();
        foreach (var scout in scouts)
        {
            directions.Add(scout.Direction);
            if (!WowBCorpseSpotPolicy.InRange(scout.Position, corpse, 38) ||
                Math.Abs(WowBCorpseSpotPolicy.Distance(scout.Position, corpse) - 32) > 0.01)
                throw new Exception("Scout location outside the 32-yard ring");
        }
        if (scouts.Count != 4 || directions.Count != 4)
            throw new Exception("Crowded corpse must still have four distinct scout directions");
        var fallback = WowBCorpseSpotPolicy.FallbackLocations(corpse, current);
        if (fallback.Count != 9 || WowBCorpseSpotPolicy.Distance(fallback[0], corpse) < 15 ||
            WowBCorpseSpotPolicy.Distance(fallback[8], corpse) != 0 ||
            WowBCorpseSpotPolicy.ScoutLocations(corpse, current, new List<WoWPoint> { new WoWPoint(float.NaN, 0, 0) }).Count != 0)
            throw new Exception("Fallback inward positions or invalid NPC guard failed");
        if (WowBCorpseSpotPolicy.ScoutLocations(corpse, new WoWPoint(50, 0, 0), crowded).Count != 0 ||
            !WowBCorpseSpotPolicy.GroundArea(0) || !WowBCorpseSpotPolicy.GroundArea(1) ||
            !WowBCorpseSpotPolicy.GroundArea(4) || WowBCorpseSpotPolicy.GroundArea(2) || WowBCorpseSpotPolicy.GroundArea(3))
            throw new Exception("Corpse ring bounds or destination-area policy failed");
        Console.WriteLine("Corpse spot geometry fixtures passed (no live NPC, mesh or resurrection test).");
    }
}
#endif

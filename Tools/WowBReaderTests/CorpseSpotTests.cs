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
        var ring = WowBCorpseSpotPolicy.RankRing(corpse, current, crowded, 32);
        if (ring.Count == 0 || ring[0].X >= 0) throw new Exception("Candidate ranking did not favor the side away from NPCs");
        foreach (var point in ring)
            if (!WowBCorpseSpotPolicy.InRange(point, corpse, 38) || !WowBCorpseSpotPolicy.ClearEnough(point, crowded, WowBCorpseSpotPolicy.MinNpcClearance))
                throw new Exception("Unsafe or out-of-range candidate included");
        var surrounding = new List<WoWPoint>();
        for (int i = 0; i < 16; i++)
            surrounding.Add(new WoWPoint((float)(32 * Math.Cos(i * Math.PI / 8)), (float)(32 * Math.Sin(i * Math.PI / 8)), 0));
        if (WowBCorpseSpotPolicy.RankRing(corpse, current, surrounding, 32).Count != 0 ||
            WowBCorpseSpotPolicy.RankRing(corpse, current, crowded, 40).Count != 0 ||
            WowBCorpseSpotPolicy.RankRing(corpse, new WoWPoint(50, 0, 0), crowded, 32).Count != 0 ||
            !WowBCorpseSpotPolicy.GroundArea(0) || !WowBCorpseSpotPolicy.GroundArea(1) ||
            !WowBCorpseSpotPolicy.GroundArea(4) || WowBCorpseSpotPolicy.GroundArea(2) || WowBCorpseSpotPolicy.GroundArea(3))
            throw new Exception("Corpse ring bounds or destination-area policy failed");
        Console.WriteLine("Corpse spot geometry fixtures passed (no live NPC, mesh or resurrection test).");
    }
}
#endif

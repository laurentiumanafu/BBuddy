#if WOWB_READER_TESTS
using System;
using System.IO;
using GreenMagic;
using Styx.Logic.Pathing;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing
{
    internal static class FactionTests
    {
        private static void Check(bool value, string name) { if (!value) throw new Exception("Faction test failed: " + name); }
        public static void Run(Memory memory, WowBGuid player, WowBGuid npc, ulong npcUnit, ulong npcRecord)
        {
            Check(GrindFactionSettings.TryLoad(out var defaults, out _) && !defaults.Enabled && defaults.Allows(false, 0), "default off");
            Check(!GrindFactionRules.TryCreate(true, "", out _, out _), "enabled empty rejected");
            Check(!GrindFactionRules.TryCreate(true, "189, 0", out _, out _), "zero rejected");
            Check(!GrindFactionRules.TryCreate(false, "189, xx", out _, out _), "invalid rejected even when disabled");
            Check(GrindFactionRules.TryCreate(true, "189, 4 189", out var parsed, out _) && parsed.KeyText == "4, 189" && parsed.Allows(true, 4) && !parsed.Allows(true, 5) && !parsed.Allows(false, 4), "canonicalization and unknown deny");
            Check(GrindFactionSettings.SaveList("189, 4", out _) && GrindFactionSettings.TryLoad(out var loaded, out _) && loaded.Enabled && loaded.KeyText == "4, 189", "persist and reload");
            Check(!GrindFactionSettings.SaveList("bad", out _) && GrindFactionSettings.TryLoad(out loaded, out _) && loaded.Enabled, "invalid save does not replace file");
            File.WriteAllText(GrindFactionSettings.PathName, "Enabled=False\nKeys=4, 189\n");
            Check(GrindFactionSettings.TryLoad(out loaded, out _) && loaded.Enabled, "older unchecked populated list activates");
            Check(WowBGrindFactions.PrepareForGrind(out _), "known signatures prepare filter");
            var reader = ObjectManager.WowB!;
            Check(reader.TryCaptureWorldSnapshot(out var snapshot) && snapshot!.TryGetUnit(npc, out _), "npc snapshot available");
            snapshot!.TryGetUnit(npc, out var unit);
            Check(WowBGrindFactions.AllowsPull(reader, unit, out _) && !unit.IsLocalPlayer, "allowed key");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 5);
            Check(!WowBGrindFactions.AllowsPull(reader, unit, out bool unknown) && !unknown, "disallowed key");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 0);
            Check(!WowBGrindFactions.AllowsPull(reader, unit, out unknown) && unknown, "zero key unknown");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 4);
            memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit + 8);
            Check(!WowBGrindFactions.AllowsPull(reader, unit, out unknown) && unknown, "reused record fails closed");
            memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit);

            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), default);
            string result = WowBGrindFactions.ScanNearby();
            Check(result.Contains("Key 4 |") && result.Contains(npc.ToString()) && !result.Contains(player.ToString()), "scan without selected target");
            memory.F32(npcRecord + WowBOffsets.RecordPositionOffset, 201);
            Check(!WowBGrindFactions.ScanNearby().Contains("Key 4 |"), "outside radius excluded");
            memory.F32(npcRecord + WowBOffsets.RecordPositionOffset, 1);
            ClickAimUi.Running = true;
            Check(WowBGrindFactions.ScanNearby().Contains("Stop the bot"), "running scan denied");
            ClickAimUi.Running = false; ClickAimUi.Paused = true;
            Check(WowBGrindFactions.ScanNearby().Contains("Stop the bot"), "paused scan denied");
            ClickAimUi.Paused = false;
            int reads = 0;
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + WowBOffsets.UnitDataFactionTemplateOffset && count == 4 && ++reads == 2)
                    memory.U32(address, 5);
            };
            result = WowBGrindFactions.ScanNearby();
            memory.OnRead = null;
            Check(!result.Contains("Key 4 |") && !result.Contains("Key 5 |") && result.Contains("Unreadable/changed keys: 1"), "unstable key omitted");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 4);
            reads = 0;
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + WowBOffsets.UnitDataFactionTemplateOffset && count == 4 && ++reads == 2)
                    memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit + 8);
            };
            result = WowBGrindFactions.ScanNearby();
            memory.OnRead = null;
            Check(!result.Contains("Key 4 |"), "changed record not listed");
            memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit);
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), npc);
            memory.Remove(memory.GetAbsolute64(0x27EE18D));
            Check(!WowBGrindFactions.PrepareForGrind(out _) && WowBGrindFactions.ScanNearby().Contains("signatures"), "signature mismatch denied");
            memory.Bytes(memory.GetAbsolute64(0x27EE18D), Convert.FromHexString("488D0DCC553505E8F7A7E6FD8B935C1E0000"));
            File.WriteAllText(GrindFactionSettings.PathName, "Enabled=True\nKeys=broken\n");
            Check(!WowBGrindFactions.PrepareForGrind(out _), "malformed settings refuse start");
            Check(GrindFactionSettings.SaveList("", out _) && WowBGrindFactions.PrepareForGrind(out _) && WowBGrindFactions.AllowsPull(null, unit, out _), "disabled policy preserves previous behaviour");
            Console.WriteLine("Grind faction settings and nearby scan synthetic fixtures passed");
        }
    }
}
#endif

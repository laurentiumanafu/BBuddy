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
            Check(!GrindFactionRules.TryCreate(GrindFactionMode.Blacklist, "189", "", out _, out _), "empty active blacklist rejected");
            Check(GrindFactionRules.TryCreate(GrindFactionMode.Blacklist, "189", "57, 55, 57", out var denied, out _) && denied.BlacklistKeyText == "55, 57" && denied.Allows(true, 189) && !denied.Allows(true, 57) && !denied.Allows(false, 189) && !denied.Allows(true, 0), "blacklist excludes only listed keys but denies unknown");
            Check(GrindFactionSettings.Save(GrindFactionMode.Blacklist, "189, 4", "57, 55", out _) && GrindFactionSettings.TryLoad(out loaded, out _) && loaded.Mode == GrindFactionMode.Blacklist && loaded.AllowedKeyText == "4, 189" && loaded.BlacklistKeyText == "55, 57", "blacklist persists without losing allowlist");
            Check(WowBGrindFactions.PrepareForGrind(out _), "blacklist signatures prepare filter");
            var blackReader = ObjectManager.WowB!;
            Check(blackReader.TryCaptureWorldSnapshot(out var blackSnapshot) && blackSnapshot != null && blackSnapshot.TryGetUnit(npc, out var blackUnit) && WowBGrindFactions.AllowsPull(blackReader, blackUnit, out _), "non-blacklisted key accepted");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 57);
            blackSnapshot!.TryGetUnit(npc, out var blockedUnit);
            Check(!WowBGrindFactions.AllowsPull(blackReader, blockedUnit, out bool blackUnknown) && !blackUnknown, "blacklisted key blocked");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 0);
            Check(!WowBGrindFactions.AllowsPull(blackReader, blockedUnit, out blackUnknown) && blackUnknown, "blacklist fails closed for unknown key");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 4);
            Check(GrindFactionSettings.Save(GrindFactionMode.Off, "189, 4", "57", out _) && GrindFactionSettings.TryLoad(out loaded, out _) && !loaded.Enabled && loaded.AllowedKeyText == "4, 189" && loaded.BlacklistKeyText == "57", "explicit off preserves both lists");
            Check(GrindFactionSettings.Save(GrindFactionMode.Allow, "189, 4", "57", out _) && GrindFactionSettings.TryLoad(out loaded, out _) && loaded.Mode == GrindFactionMode.Allow && !loaded.Allows(true, 57), "allow mode ignores inactive blacklist");
            Check(!GrindFactionSettings.Save(GrindFactionMode.Blacklist, "189", "bad", out _) && GrindFactionSettings.TryLoad(out loaded, out _) && loaded.Mode == GrindFactionMode.Allow, "invalid blacklist does not replace settings");
            File.WriteAllText(GrindFactionSettings.PathName, "Mode=Blacklist\nKeys=189\nBlacklistKeys=57\nEnabled=False\n");
            Check(!GrindFactionSettings.TryLoad(out _, out _), "ambiguous legacy/new modes refused");
            File.WriteAllText(GrindFactionSettings.PathName, "Mode=Blacklist\nKeys=189\nBlacklistKeys=\n");
            Check(!GrindFactionSettings.TryLoad(out _, out _), "empty persisted blacklist refused");
            Check(GrindFactionSettings.Save(GrindFactionMode.Allow, "189, 4", "57", out _), "restore allow policy");
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

            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), npc);
            byte[] readableWindow = new byte[64]; Array.Fill(readableWindow, (byte)0x12);
            memory.Bytes(npcUnit + 0x1000, readableWindow);
            string info = WowBTargetInfo.Capture();
            Check(info.Contains("70291 read-only target diagnostic") && info.Contains(npc.ToString()) && info.Contains("faction-template key: 4") && info.Contains("unit +0x1E40") && info.Contains("merchant status") && info.Contains("unit +0x1000: 12121212") && info.Contains("<unreadable>"), "target info captures labelled bounded raw data");
            ClickAimUi.Running = true;
            Check(WowBTargetInfo.Capture().Contains("Stop the bot"), "target info denied while running");
            ClickAimUi.Running = false;
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + 0x80 && count == 64)
                    memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), default);
            };
            info = WowBTargetInfo.Capture(); memory.OnRead = null;
            Check(info.Contains("discarded") && !info.Contains("unit fields:"), "changed selection discards capture");
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), npc);
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + 0x80 && count == 64)
                    memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 5);
            };
            info = WowBTargetInfo.Capture(); memory.OnRead = null;
            Check(info.Contains("discarded"), "changed faction discards target info");
            memory.U32(npcUnit + WowBOffsets.UnitDataFactionTemplateOffset, 4);
            memory.OnRead = (address, count) =>
            {
                if (address == npcUnit + 0x80 && count == 64)
                    memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit + 8);
            };
            info = WowBTargetInfo.Capture(); memory.OnRead = null;
            Check(info.Contains("discarded"), "changed identity discards target info");
            memory.U64(npcRecord + WowBOffsets.RecordDataBlockPtrOffset, npcUnit);
            memory.Guid(memory.GetAbsolute64(WowBOffsets.PlayerTargetGuid), default);
            Check(WowBTargetInfo.Capture().Contains("Select an NPC"), "target info requires selected NPC");
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
            Check(WowBTargetInfo.Capture().Contains("signatures"), "target info rejects unverified build");
            memory.Bytes(memory.GetAbsolute64(0x27EE18D), Convert.FromHexString("488D0DCC553505E8F7A7E6FD8B935C1E0000"));
            File.WriteAllText(GrindFactionSettings.PathName, "Enabled=True\nKeys=broken\n");
            Check(!WowBGrindFactions.PrepareForGrind(out _), "malformed settings refuse start");
            Check(GrindFactionSettings.SaveList("", out _) && WowBGrindFactions.PrepareForGrind(out _) && WowBGrindFactions.AllowsPull(null, unit, out _), "disabled policy preserves previous behaviour");
            Console.WriteLine("Grind faction settings and nearby scan synthetic fixtures passed");
        }
    }
}
#endif

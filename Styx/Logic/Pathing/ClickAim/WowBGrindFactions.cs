using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.WoWInternals;
using Styx.Offsets;

namespace Styx.Logic.Pathing;

// 70291-only reads. These checks are not proof of hostility or attackability.
internal static class WowBGrindFactions
{
    private const ulong HealthRva = 0x1D63190, LookupRva = 0x27EE18D;
    private const string HealthSignature = "488BC4554157488D68A14881ECB80000";
    private const string LookupSignature = "488D0DCC553505E8F7A7E6FD8B935C1E0000";
    private static Memory? _boundMemory;
    private static WowBReader? _boundReader;
    private static WowBGuid _experimentalTarget;

    private static bool CheckBuild(Memory memory)
    {
        if (!memory.Is64BitTarget) return false;
        byte[]? hp = memory.ReadBytes64(memory.GetAbsolute64(HealthRva), 16);
        byte[]? lookup = memory.ReadBytes64(memory.GetAbsolute64(LookupRva), 18);
        return hp != null && lookup != null && Convert.ToHexString(hp) == HealthSignature &&
               Convert.ToHexString(lookup) == LookupSignature;
    }

    public static bool PrepareForGrind(out string reason)
    {
        _boundMemory = null; _boundReader = null; _experimentalTarget = default; reason = "";
        if (!GrindFactionSettings.TryLoad(out GrindFactionRules rules, out reason)) return false;
        if (ObjectManager.WowB?.IsExperimental70707 == true)
            return PrepareExperimental70707(rules, out reason);
        if (!rules.Enabled) { GrindFactionSettings.Activate(rules); return true; }
        try
        {
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null) { reason = "attach to wowb.exe first"; return false; }
            using var uncached = memory.TemporaryCacheState(false);
            if (!CheckBuild(memory) || ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            { reason = "70291 faction signatures or attachment could not be confirmed"; return false; }
            _boundMemory = memory; _boundReader = reader;
            GrindFactionSettings.Activate(rules);
            return true;
        }
        catch (Exception ex) { reason = "faction filter initialization failed: " + ex.Message; return false; }
    }

    private static bool PrepareExperimental70707(GrindFactionRules rules, out string reason)
    {
        reason = "";
        if (rules.Mode != GrindFactionMode.Allow)
        { reason = "70707 trial requires Allow mode and an explicitly approved faction list"; return false; }
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !reader.IsExperimental70707 || !memory.Is64BitTarget)
        { reason = "70707 attachment unavailable"; return false; }
        bool armed = false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            // These image-specific anchors are partial guards, not full build validation.
            if (!Signature(memory, 0x2080D2C, "488D0DCD38A004E808A843FE8B93541E0000") ||
                !Signature(memory, 0x20AAF97, "F2480F2A83281E0000") ||
                !Signature(memory, 0x2890305, "448B8C86C01B0000") ||
                !Signature(memory, 0xA538FD, "488D0D5CB32E06488D34C1488B04C1"))
            { reason = "70707 experimental signatures did not match"; return false; }
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit player) ||
                !snapshot.TryGetSelectedTarget(out WowBObservedUnit target) || target.IsLocalPlayer || target.HasPlayerVtable ||
                !Experimental70707Key(memory, reader, target, out uint key) || !rules.Allows(true, key))
            { reason = "select the intended monster and add its candidate faction to Allow mode"; return false; }
            double distance = ClickAimMath.Len(target.Location.X - player.Location.X, target.Location.Y - player.Location.Y);
            if (!double.IsFinite(distance) || distance > 15.0)
            { reason = "70707 trial requires the selected monster within 15 yards"; return false; }
            reader.SetExperimentalGrindActive(true);
            if (!Experimental70707BarSafe(reader))
            { reason = "70707 trial requires slots 1 Attack, 2 Lightning Bolt and 3 Healing Wave"; return false; }
            if (!reader.TryReadMapId(out _) || !reader.TryGetPlayerObject(out _) ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields self) || self.Health == 0 ||
                !reader.TryGetUnitFields(target.UnitAddress, out WowBReader.WowBUnitFields enemy) || enemy.Health == 0 ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                reader.ReadTargetGuid() != target.Guid || !Experimental70707Key(memory, reader, target, out uint again) || key != again)
            { reason = "70707 candidate map, player, mana, action bar or selected monster did not validate"; return false; }
            _experimentalTarget = target.Guid; _boundMemory = memory; _boundReader = reader;
            GrindFactionSettings.Activate(rules);
            armed = true;
            return true;
        }
        catch (Exception ex) { reason = "70707 trial initialization failed: " + ex.Message; return false; }
        finally { if (!armed) reader.SetExperimentalGrindActive(false); }
    }

    internal static bool Experimental70707BarSafe(WowBReader reader)
    {
        if (!reader.IsExperimental70707 || !reader.ExperimentalGrindActive) return false;
        var bar = reader.ReadActionBar();
        return bar.Count == 3 && bar[0].Index == 1 && bar[0].Raw == 6603 &&
            bar[1].Index == 2 && bar[1].Raw == 403 && bar[2].Index == 3 && bar[2].Raw == 331;
    }

    private static bool Signature(Memory memory, ulong rva, string expected)
    {
        byte[]? raw = memory.ReadBytes64(memory.GetAbsolute64(rva), expected.Length / 2);
        return raw != null && raw.Length * 2 == expected.Length && Convert.ToHexString(raw) == expected;
    }

    internal static WowBReader? ExperimentalBoundReader => _boundReader?.IsExperimental70707 == true ? _boundReader : null;

    internal static void ClaimExperimentalGrindThread()
    {
        _boundReader?.ClaimExperimentalGrindThread();
    }

    internal static void EndExperimental70707()
    {
        WowBReader? reader = _boundReader;
        if (reader?.IsExperimental70707 == true) reader.SetExperimentalGrindActive(false);
        _experimentalTarget = default;
    }

    // Recheck full GUID, object back-pointer and pool record pointer. Even this
    // cannot detect a change-away-and-back between non-atomic process reads.
    private static bool SameIdentity(Memory memory, WowBObservedUnit unit)
    {
        if (unit.RecordAddress < 0x10000UL || unit.UnitAddress < 0x10000UL ||
            unit.RecordAddress >= 0x00007FFFFFFFF000UL || unit.UnitAddress >= 0x00007FFFFFFFF000UL) return false;
        byte[]? ptr = memory.ReadBytes64(unit.RecordAddress + WowBOffsets.RecordDataBlockPtrOffset, 8);
        byte[]? recordGuid = memory.ReadBytes64(unit.RecordAddress + WowBOffsets.RecordGuidLoOffset, 16);
        byte[]? header = memory.ReadBytes64(unit.UnitAddress + WowBOffsets.UnitObjectBackPtrOffset, 24);
        return ptr != null && recordGuid != null && header != null && BitConverter.ToUInt64(ptr, 0) == unit.UnitAddress &&
               BitConverter.ToUInt64(recordGuid, 0) == unit.Guid.Low && BitConverter.ToUInt64(recordGuid, 8) == unit.Guid.High &&
               BitConverter.ToUInt64(header, 0) == unit.RecordAddress &&
               BitConverter.ToUInt64(header, 8) == unit.Guid.Low && BitConverter.ToUInt64(header, 16) == unit.Guid.High;
    }

    private static bool StableKey(Memory memory, WowBReader reader, WowBObservedUnit unit, out uint key)
    {
        key = 0;
        if (unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable || ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            return false;
        using var uncached = memory.TemporaryCacheState(false);
        if (!SameIdentity(memory, unit) || !reader.TryReadFactionTemplateId(unit.UnitAddress, out uint first) || first == 0 ||
            !reader.TryReadFactionTemplateId(unit.UnitAddress, out uint second) || first != second || !SameIdentity(memory, unit) ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        key = first;
        return true;
    }

    // Diagnostic reads must not depend on whether a Grind session has bound a reader.
    // Verify build and identity each time; this is not a merchant/hostility verdict.
    internal static bool ValidateDiagnosticUnit(Memory memory, WowBReader reader, WowBObservedUnit unit)
    {
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            return ObjectManager.Wow == memory && ObjectManager.WowB == reader &&
                   CheckBuild(memory) && SameIdentity(memory, unit) &&
                   ObjectManager.Wow == memory && ObjectManager.WowB == reader;
        }
        catch { return false; }
    }

    public static bool TryReadBoundKey(WowBReader? reader, WowBObservedUnit unit, out uint key)
    {
        key = 0;
        try
        {
            return reader != null && _boundMemory != null && reader == _boundReader &&
                (reader.IsExperimental70707
                    ? reader.ExperimentalGrindActive && !_experimentalTarget.IsEmpty && unit.Guid == _experimentalTarget && Experimental70707Key(_boundMemory, reader, unit, out key)
                    : StableKey(_boundMemory, reader, unit, out key));
        }
        catch { key = 0; return false; }
    }

    // Unknown (including key 0) never authorizes an attack with the filter on.
    public static bool AllowsPull(WowBReader? reader, WowBObservedUnit unit, out bool unknown)
    {
        unknown = false;
        GrindFactionRules rules = GrindFactionSettings.Rules;
        if (!rules.Enabled) return true;
        if (!TryReadBoundKey(reader, unit, out uint key)) { unknown = true; return false; }
        return rules.Allows(true, key);
    }

    private sealed class Row
    {
        public WowBObservedUnit Unit;
        public uint Key;
        public double Distance;
        public string Health = "unknown";
        public uint Level;
    }

    public static string ScanNearby()
    {
        if (ClickAimUi.Running || ClickAimUi.Paused) return "Stop the bot and any route before scanning.";
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !memory.Is64BitTarget) return "Attach to wowb.exe and enter the world first.";
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (reader.IsExperimental70707) return ScanExperimental70707(memory, reader);
            if (!CheckBuild(memory)) return "70291 signatures did not match; no faction scan performed.";
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetLocalPlayer(out WowBObservedUnit player)) return "The player/world snapshot could not be read.";
            var rows = new List<Row>();
            int unreadable = 0;
            foreach (WowBObservedUnit unit in before.Units)
            {
                if (ClickAimUi.Running || ClickAimUi.Paused) return "The bot started during the scan; results discarded.";
                if (unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable) continue;
                double distance = ClickAimMath.Len(unit.Location.X - player.Location.X, unit.Location.Y - player.Location.Y);
                if (!double.IsFinite(distance) || distance > 100) continue;
                if (!StableKey(memory, reader, unit, out uint key)) { unreadable++; continue; }
                var row = new Row { Unit = unit, Key = key, Distance = distance };
                if (reader.TryGetUnitFields(unit.UnitAddress, out WowBReader.WowBUnitFields fields))
                { row.Health = fields.Health == 0 ? "dead" : "alive"; row.Level = fields.Level; }
                rows.Add(row);
            }
            if (ClickAimUi.Running || ClickAimUi.Paused || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                !after.TryGetLocalPlayer(out WowBObservedUnit playerAfter) || playerAfter.Guid != player.Guid ||
                playerAfter.RecordAddress != player.RecordAddress || playerAfter.UnitAddress != player.UnitAddress)
                return "Player, attachment or bot state changed during the scan; results discarded.";
            var valid = new List<Row>();
            foreach (Row row in rows)
            {
                if (ClickAimUi.Running || ClickAimUi.Paused) return "The bot started during the scan; results discarded.";
                if (!after.TryGetUnit(row.Unit.Guid, out WowBObservedUnit current) ||
                    current.RecordAddress != row.Unit.RecordAddress || current.UnitAddress != row.Unit.UnitAddress ||
                    !StableKey(memory, reader, current, out uint key) || key != row.Key)
                { unreadable++; continue; }
                valid.Add(row);
            }
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused)
                return "Attachment or bot state changed during the scan; results discarded.";
            valid.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var result = new StringBuilder("Nearby NPC faction-template keys (within 100 horizontal yd, no selection required):\n");
            if (valid.Count == 0) result.AppendLine("No readable NPC faction keys were observed.");
            var groups = new SortedDictionary<uint, List<Row>>();
            foreach (Row row in valid)
            {
                if (!groups.TryGetValue(row.Key, out List<Row>? group))
                { group = new List<Row>(); groups.Add(row.Key, group); }
                group.Add(row);
            }
            foreach (var group in groups)
            {
                result.AppendFormat(CultureInfo.InvariantCulture, "Key {0} | {1} NPC(s) | nearest {2:F1} yd\n", group.Key, group.Value.Count, group.Value[0].Distance);
                foreach (Row row in group.Value)
                    result.AppendFormat(CultureInfo.InvariantCulture, "  {0:F1} yd | level {1} | {2} | GUID {3}\n",
                        row.Distance, row.Level == 0 ? "?" : row.Level.ToString(CultureInfo.InvariantCulture), row.Health, row.Unit.Guid);
            }
            result.AppendFormat(CultureInfo.InvariantCulture, "Unreadable/changed keys: {0}. Health and distance are initial observations. NPC names, friendliness and attacker identity are not known. The non-atomic snapshot can omit units or change during reads.", unreadable);
            return result.ToString();
        }
        catch (Exception ex) { return "Faction scan failed: " + ex.Message; }
    }

    // A repeated raw word is not proof of a faction key or reaction. The
    // 70707 trial additionally requires explicit selection and an allowlist.
    private static bool Experimental70707Key(Memory memory, WowBReader reader, WowBObservedUnit unit, out uint key)
    {
        key = 0;
        using var uncached = memory.TemporaryCacheState(false);
        if (!reader.IsExperimental70707 || unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable ||
            ObjectManager.Wow != memory || ObjectManager.WowB != reader || !SameIdentity(memory, unit)) return false;
        byte[]? vt = memory.ReadBytes64(unit.UnitAddress, 8);
        byte[]? first = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.FactionKeyCandidateOffset, 4);
        byte[]? second = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.FactionKeyCandidateOffset, 4);
        byte[]? vtAfter = memory.ReadBytes64(unit.UnitAddress, 8);
        if (vt == null || vt.Length != 8 || BitConverter.ToUInt64(vt, 0) != memory.ImageBase64 + Wow70707Offsets.UnitVtable ||
            vtAfter == null || vtAfter.Length != 8 || BitConverter.ToUInt64(vtAfter, 0) != BitConverter.ToUInt64(vt, 0) ||
            first == null || first.Length != 4 || second == null || second.Length != 4 ||
            BitConverter.ToUInt32(first, 0) == 0 || BitConverter.ToUInt32(first, 0) != BitConverter.ToUInt32(second, 0) ||
            !SameIdentity(memory, unit) || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        key = BitConverter.ToUInt32(first, 0);
        return true;
    }

    private static string ScanExperimental70707(Memory memory, WowBReader reader)
    {
        if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
            !before.TryGetLocalPlayer(out WowBObservedUnit player)) return "70707 candidate player/world snapshot could not be read.";
        var rows = new List<Row>();
        int unreadable = 0;
        foreach (WowBObservedUnit unit in before.Units)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) return "The bot started during the scan; results discarded.";
            if (unit.Guid.IsEmpty || unit.IsLocalPlayer || unit.HasPlayerVtable) continue;
            double distance = ClickAimMath.Len(unit.Location.X - player.Location.X, unit.Location.Y - player.Location.Y);
            if (!double.IsFinite(distance) || distance > 100) continue;
            if (!Experimental70707Key(memory, reader, unit, out uint key)) { unreadable++; continue; }
            rows.Add(new Row { Unit = unit, Key = key, Distance = distance });
        }
        if (ClickAimUi.Running || ClickAimUi.Paused || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
            !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
            !after.TryGetLocalPlayer(out WowBObservedUnit playerAfter) || playerAfter.Guid != player.Guid ||
            playerAfter.RecordAddress != player.RecordAddress || playerAfter.UnitAddress != player.UnitAddress)
            return "Player, attachment or bot state changed during the scan; results discarded.";
        var groups = new SortedDictionary<uint, List<Row>>();
        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (Row row in rows)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) return "The bot started during the scan; results discarded.";
            if (!after.TryGetUnit(row.Unit.Guid, out WowBObservedUnit current) ||
                current.RecordAddress != row.Unit.RecordAddress || current.UnitAddress != row.Unit.UnitAddress ||
                !Experimental70707Key(memory, reader, current, out uint key) || key != row.Key)
            { unreadable++; continue; }
            if (!groups.TryGetValue(key, out List<Row>? group))
            { group = new List<Row>(); groups.Add(key, group); }
            group.Add(row);
        }
        if (ObjectManager.Wow != memory || ObjectManager.WowB != reader || ClickAimUi.Running || ClickAimUi.Paused)
            return "Attachment or bot state changed during the scan; results discarded.";
        var result = new StringBuilder("70707 RESEARCH ONLY: candidate faction-key words (no selection required; approximate 100 yd radius):\n");
        if (groups.Count == 0) result.AppendLine("No readable candidate keys were observed.");
        foreach (var group in groups)
        {
            result.AppendFormat(CultureInfo.InvariantCulture, "Candidate {0} | {1} unit(s) | nearest approximately {2:F1} yd\n", group.Key, group.Value.Count, group.Value[0].Distance);
            foreach (Row row in group.Value)
                result.AppendFormat(CultureInfo.InvariantCulture, "  approximately {0:F1} yd | GUID {1}\n", row.Distance, row.Unit.Guid);
        }
        result.AppendFormat(CultureInfo.InvariantCulture,
            "Unreadable/changed candidates: {0}. Vtable, position and field meanings remain unverified; no code signatures checked here. These observations do not establish NPC names, friendliness or attackability. The separate 70707 trial requires an explicitly selected allowlisted monster. Reads are non-atomic and can omit units.", unreadable);
        return result.ToString();
    }
}

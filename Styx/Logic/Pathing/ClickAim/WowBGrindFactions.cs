using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.Offsets;

namespace Styx.Logic.Pathing;

// Build-specific faction candidates. These checks are not proof of hostility or attackability.
internal static class WowBGrindFactions
{
    private const ulong HealthRva = 0x1D63190, LookupRva = 0x27EE18D;
    private const string HealthSignature = "488BC4554157488D68A14881ECB80000";
    private const string LookupSignature = "488D0DCC553505E8F7A7E6FD8B935C1E0000";
    private static Memory? _boundMemory;
    private static WowBReader? _boundReader;
    private static ulong[]? _experimentalBar;
    private static int _preparing;

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
        reason = "another session is starting";
        if (System.Threading.Interlocked.CompareExchange(ref _preparing, 1, 0) != 0) return false;
        try
        {
            if (_boundReader?.HasExperimentalGrindSession == true)
            { reason = "another 70707 session is active"; return false; }
            return PrepareCore(out reason);
        }
        finally { System.Threading.Volatile.Write(ref _preparing, 0); }
    }

    private static bool PrepareCore(out string reason)
    {
        _boundMemory = null; _boundReader = null; _experimentalBar = null; reason = "";
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
        if (!rules.Enabled)
        { reason = "70707 requires a nonempty Allow or Blacklist faction filter"; return false; }
        Memory? memory = ObjectManager.Wow;
        WowBReader? reader = ObjectManager.WowB;
        if (memory == null || reader == null || !reader.IsExperimental70707 || !memory.Is64BitTarget)
        { reason = "70707 attachment unavailable"; return false; }
        bool armed = false;
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            // These image-specific anchors are partial guards, not full build validation.
            if (!CheckExperimentalBuild(memory, out string signatureReport))
            { reason = "70707 experimental signature check failed: " + signatureReport; return false; }
            Logging.Write("[70707] partial signature check: " + signatureReport);
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit player))
            { reason = "70707 player snapshot unavailable"; return false; }
            reader.SetExperimentalGrindActive(true);
            if (!reader.TryReadExperimentalActionSnapshot(out ulong[] rawBar) || reader.ReadActionBar().Count == 0)
            { reason = "70707 action bar unreadable or empty"; return false; }
            _experimentalBar = rawBar;
            if (!Experimental70707BarSafe(reader))
            { reason = "70707 action bar changed"; return false; }
            if (!reader.TryReadMapId(out _) || !reader.TryGetPlayerObject(out _) ||
                !reader.TryGetUnitFields(player.UnitAddress, out WowBReader.WowBUnitFields self) || self.Health == 0 ||
                ObjectManager.Wow != memory || ObjectManager.WowB != reader)
            { reason = "70707 candidate map, player or supported power type unavailable"; return false; }
            if (self.PlayerPowerTypeCandidate == 1)
                Logging.Write("[70707] player power type candidate 1 (rage): value unavailable; mana recovery is skipped and resource-dependent abilities are withheld, but auto-attack is available");
            if (!TryReadGhostFlagCandidate(memory, reader, player, out bool ghostFlag))
            { reason = "70707 ghost flag candidate unavailable or changed; refusing to start"; return false; }
            if (ghostFlag)
            { reason = "70707 ghost flag candidate is set; manually recover before starting"; return false; }
            _boundMemory = memory; _boundReader = reader;
            GrindFactionSettings.Activate(rules);
            armed = true;
            return true;
        }
        catch (Exception ex) { reason = "70707 session initialization failed: " + ex.Message; return false; }
        finally { if (!armed) { reader.SetExperimentalGrindActive(false); _experimentalBar = null; } }
    }

    internal static bool Experimental70707BarSafe(WowBReader reader)
    {
        ulong[]? expected = _experimentalBar;
        if (!reader.IsExperimental70707 || !reader.ExperimentalGrindActive || expected == null || expected.Length != 12 ||
            !reader.TryReadExperimentalActionSnapshot(out ulong[] bar) || bar.Length != expected.Length) return false;
        for (int i = 0; i < bar.Length; i++)
            if (bar[i] != expected[i]) return false;
        return true;
    }

    private static bool CheckExperimentalBuild(Memory memory) => CheckExperimentalBuild(memory, out _);

    // Code pages in this client can become unreadable after having matched. A
    // failed read is not a contradictory signature. Require at least two
    // independently matching anchors, but never overlook a readable mismatch.
    // The subsequent player, map, action bar and per-target checks still apply.
    private static bool CheckExperimentalBuild(Memory memory, out string report)
    {
        if (!memory.Is64BitTarget) { report = "target is not x64"; return false; }
        ulong[] rvas = { 0x2080D2C, 0x20AAF97, 0x2890305, 0xA538FD };
        string[] expected = { "488D0DCD38A004E808A843FE8B93541E0000", "F2480F2A83281E0000",
                              "448B8C86C01B0000", "488D0D5CB32E06488D34C1488B04C1" };
        int matches = 0, mismatches = 0;
        var observations = new List<string>();
        for (int i = 0; i < rvas.Length; i++)
        {
            byte[]? raw = memory.ReadBytes64(memory.GetAbsolute64(rvas[i]), expected[i].Length / 2);
            if (raw == null || raw.Length * 2 != expected[i].Length)
                raw = memory.ReadBytes64(memory.GetAbsolute64(rvas[i]), expected[i].Length / 2);
            string status;
            if (raw == null || raw.Length * 2 != expected[i].Length) status = "unreadable";
            else if (Convert.ToHexString(raw) == expected[i]) { matches++; status = "match"; }
            else { mismatches++; status = "mismatch=" + Convert.ToHexString(raw); }
            observations.Add("0x" + rvas[i].ToString("X", CultureInfo.InvariantCulture) + ":" + status);
        }
        report = string.Join(", ", observations) + "; " + matches.ToString(CultureInfo.InvariantCulture) + "/4 matched (minimum 2; no mismatches)";
        return matches >= 2 && mismatches == 0;
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
        _experimentalBar = null;
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

    // 70707 trace identities are checked separately from gameplay authorization.
    // This is read-only evidence and does not arm a Grind session.
    internal static bool TraceBuild70707(Memory memory, WowBReader reader)
        => reader.IsExperimental70707 && ObjectManager.Wow == memory && ObjectManager.WowB == reader && CheckExperimentalBuild(memory);

    internal static bool TraceIdentity70707(Memory memory, WowBReader reader, WowBObservedUnit unit, bool local)
    {
        try
        {
            if (!reader.IsExperimental70707 || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                unit.Guid.IsEmpty || unit.IsLocalPlayer != local || unit.HasPlayerVtable != local || !SameIdentity(memory, unit)) return false;
            byte[]? vt = memory.ReadBytes64(unit.UnitAddress, 8);
            ulong expected = local ? Wow70707Offsets.PlayerVtable : Wow70707Offsets.UnitVtable;
            return vt != null && vt.Length == 8 && BitConverter.ToUInt64(vt, 0) == memory.ImageBase64 + expected &&
                (local ? reader.TryReadTraceLocalGuid(out WowBGuid guid) && guid == unit.Guid :
                         reader.TryReadTraceTargetGuid(out WowBGuid selectedGuid) && selectedGuid == unit.Guid) &&
                ObjectManager.Wow == memory && ObjectManager.WowB == reader && SameIdentity(memory, unit);
        }
        catch { return false; }
    }

    // Static local-player branch plus one user-confirmed ghost trace. This is
    // a veto only: an unset bit is not proof of life or permission to recover.
    internal static bool TryReadGhostFlagCandidate(Memory memory, WowBReader reader, WowBObservedUnit player, out bool set)
    {
        set = false;
        try
        {
            if (!reader.IsExperimental70707 || !reader.ExperimentalGrindActive ||
                player.UnitAddress >= 0x00007FFFFFFFFFFFUL - Wow70707Offsets.GhostFlagsCandidateOffset ||
                !TraceIdentity70707(memory, reader, player, true)) return false;
            byte[]? first = memory.ReadBytes64(player.UnitAddress + Wow70707Offsets.GhostFlagsCandidateOffset, 1);
            byte[]? second = memory.ReadBytes64(player.UnitAddress + Wow70707Offsets.GhostFlagsCandidateOffset, 1);
            if (first == null || first.Length != 1 || second == null || second.Length != 1 || first[0] != second[0] ||
                !reader.ExperimentalGrindActive || !TraceIdentity70707(memory, reader, player, true)) return false;
            set = (first[0] & Wow70707Offsets.GhostFlagCandidate) != 0;
            return true;
        }
        catch { return false; }
    }

    // Diagnostic reads must not depend on whether a Grind session has bound a reader.
    // Verify build and identity each time; this is not a merchant/hostility verdict.
    internal static bool ValidateDiagnosticUnit(Memory memory, WowBReader reader, WowBObservedUnit unit)
    {
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !(reader.IsExperimental70707 ? CheckExperimentalBuild(memory) : CheckBuild(memory)) ||
                !SameIdentity(memory, unit)) return false;
            if (reader.IsExperimental70707)
            {
                byte[]? vt = memory.ReadBytes64(unit.UnitAddress, 8);
                if (unit.IsLocalPlayer || unit.HasPlayerVtable || vt == null || vt.Length != 8 ||
                    BitConverter.ToUInt64(vt, 0) != memory.ImageBase64 + Wow70707Offsets.UnitVtable) return false;
            }
            return ObjectManager.Wow == memory && ObjectManager.WowB == reader && SameIdentity(memory, unit);
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
                    ? reader.ExperimentalGrindActive && Experimental70707Key(_boundMemory, reader, unit, out key)
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

    // A repeated raw word is not proof of a faction key or reaction.
    // Each 70707 unit is independently revalidated for the active filter.
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

    private static uint ExperimentalLevelCandidate(Memory memory, WowBReader reader, WowBObservedUnit unit, uint expectedKey)
    {
        if (!Experimental70707Key(memory, reader, unit, out uint firstKey) || firstKey != expectedKey) return 0;
        byte[]? first = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.LevelCandidateOffset, 4);
        byte[]? second = memory.ReadBytes64(unit.UnitAddress + Wow70707Offsets.LevelCandidateOffset, 4);
        if (first == null || second == null || first.Length != 4 || second.Length != 4) return 0;
        uint level = BitConverter.ToUInt32(first, 0);
        if (level == 0 || level > 255 || level != BitConverter.ToUInt32(second, 0) ||
            !Experimental70707Key(memory, reader, unit, out uint lastKey) || lastKey != expectedKey) return 0;
        return level;
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
            row.Level = ExperimentalLevelCandidate(memory, reader, current, key);
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
                result.AppendFormat(CultureInfo.InvariantCulture, "  approximately {0:F1} yd | candidate level {1} | GUID {2}\n",
                    row.Distance, row.Level == 0 ? "?" : row.Level.ToString(CultureInfo.InvariantCulture), row.Unit.Guid);
        }
        result.AppendFormat(CultureInfo.InvariantCulture,
            "Unreadable/changed candidates: {0}. Vtable, position and field meanings remain unverified; no code signatures checked here. These observations do not establish NPC names, friendliness or attackability. Experimental 70707 Grind uses the selected Allow or Blacklist filter. Reads are non-atomic and can omit units.", unreadable);
        return result.ToString();
    }
}

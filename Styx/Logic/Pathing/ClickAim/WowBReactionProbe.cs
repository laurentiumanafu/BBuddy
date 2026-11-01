using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Styx.Helpers;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>Read-only, user-labelled observations; not a reaction classifier.</summary>
internal static class WowBReactionProbe
{
    private const int WindowSize = 0x2800;
    private const int ChunkSize = 0x100;
    private const ulong HealthHandlerRva = 0x1D63190;
    private const string ExpectedHealthSignature = "488BC4554157488D68A14881ECB80000";
    private const ulong FactionLookupSiteRva = 0x27EE18D;
    private const string FactionLookupSignature = "488D0DCC553505E8F7A7E6FD8B935C1E0000";

    public static string PathName => Path.Combine(Logging.ApplicationPath, "Settings", "wowb-reaction-samples.jsonl");

    public static string Capture(string label)
    {
        if (label != "friendly" && label != "neutral_attackable" && label != "aggressive")
            return "Unknown label; no sample saved.";
        if (ClickAimUi.Running || ClickAimUi.Paused)
            return "Stop the bot and any route before taking a sample.";
        var memory = ObjectManager.Wow;
        var reader = ObjectManager.WowB;
        if (memory == null || reader == null || !memory.Is64BitTarget)
            return "Attach to wowb.exe and enter the world first.";
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            byte[]? signature = memory.ReadBytes64(memory.GetAbsolute64(HealthHandlerRva), 16);
            if (signature == null || Convert.ToHexString(signature) != ExpectedHealthSignature)
                return "The 70291 diagnostic signature did not match; no sample saved. A signature alone is not full build verification.";
            if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                !before.TryGetLocalPlayer(out WowBObservedUnit player) ||
                !before.TryGetSelectedTarget(out WowBObservedUnit target) || target.IsLocalPlayer || target.HasPlayerVtable)
                return "Select a living NPC and try again; the selected NPC and player must both be in the validated snapshot.";

            List<Chunk> playerBytes = ReadChunks(memory, player.UnitAddress);
            List<Chunk> targetBytes = ReadChunks(memory, target.UnitAddress);
            DbDiagnostic? dbProbe = CaptureDbDiagnostic(memory);
            if (playerBytes.Count == 0 || targetBytes.Count == 0)
                return "Unit bytes were unreadable; no sample saved.";
            if (ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                after.LocalGuid != before.LocalGuid || after.TargetGuid != before.TargetGuid ||
                !after.TryGetLocalPlayer(out WowBObservedUnit playerAfter) ||
                !after.TryGetSelectedTarget(out WowBObservedUnit targetAfter) ||
                playerAfter.RecordAddress != player.RecordAddress || playerAfter.UnitAddress != player.UnitAddress ||
                targetAfter.RecordAddress != target.RecordAddress || targetAfter.UnitAddress != target.UnitAddress)
                return "Player or target changed during sampling; no sample saved. Keep the same NPC selected and retry.";
            uint map;
            uint? mapId = reader.TryReadMapId(out map) ? map : null;
            var sample = new
            {
                schema = 1, utc = DateTime.UtcNow.ToString("O"), label,
                diagnosticSignature = ExpectedHealthSignature,
                mapId, playerGuid = player.Guid.ToString(), targetGuid = target.Guid.ToString(),
                // Observed for gameobjects; this is only a hint for NPC independence, not a validated NPC template id.
                targetEntryHint = (target.Guid.High >> 6) & 0x7FFFFFUL,
                targetRecord = target.RecordAddress.ToString("X"), targetUnit = target.UnitAddress.ToString("X"),
                playerChunks = playerBytes, targetChunks = targetBytes, dbProbe
            };
            string path = PathName;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.AppendAllText(path, JsonSerializer.Serialize(sample) + Environment.NewLine);
            string result = "Saved " + label + " NPC " + target.Guid + " (" + targetBytes.Count + "/" + (WindowSize / ChunkSize) + " chunks; table diagnostic " + (dbProbe == null ? "unavailable" : "included") + ") to " + path;
            Logging.Write("[Reaction probe] " + result + ". Labels are supplied by the user; no classification is inferred.");
            return result;
        }
        catch (Exception ex)
        {
            return "Reaction sample failed; no classification was made: " + ex.Message;
        }
    }

    // Bounded read-only metadata for the statically identified table instance.
    // This does not resolve rows or read/write the client by calling its functions.
    private static DbDiagnostic? CaptureDbDiagnostic(GreenMagic.Memory memory)
    {
        try
        {
            byte[]? signature = memory.ReadBytes64(memory.GetAbsolute64(FactionLookupSiteRva), 18);
            if (signature == null || Convert.ToHexString(signature) != FactionLookupSignature) return null;
            ulong root = memory.GetAbsolute64(WowBOffsets.FactionTemplateTableRva);
            byte[]? head = ReadBounded(memory, root, 0x220);
            if (head == null) return null;
            ulong meta = BitConverter.ToUInt64(head, 8);
            ulong storage = BitConverter.ToUInt64(head, 0x160);
            byte[]? metaBytes = ReadBounded(memory, meta, 0x100);
            byte[]? storageBytes = ReadBounded(memory, storage, 0x100);
            ulong table = storageBytes == null ? 0 : BitConverter.ToUInt64(storageBytes, 0);
            byte[]? vtableBytes = ReadBounded(memory, table, 0x40);
            byte[]? after = ReadBounded(memory, root, 0x220);
            if (after == null || BitConverter.ToUInt64(after, 8) != meta ||
                BitConverter.ToUInt64(after, 0x160) != storage) return null;
            return new DbDiagnostic
            {
                root = root.ToString("X"), rootBytes = Convert.ToBase64String(head),
                metadata = meta.ToString("X"), metadataBytes = Encode(metaBytes),
                storage = storage.ToString("X"), storageBytes = Encode(storageBytes),
                storageVtable = table.ToString("X"), storageVtableBytes = Encode(vtableBytes)
            };
        }
        catch { return null; }
    }

    private static byte[]? ReadBounded(GreenMagic.Memory memory, ulong address, int count)
    {
        if (address <= 0x10000UL || address >= 0x00007FFFFFFFFFFFUL - (ulong)count) return null;
        byte[]? bytes = memory.ReadBytes64(address, count);
        return bytes != null && bytes.Length == count ? bytes : null;
    }

    private static string? Encode(byte[]? bytes) => bytes == null ? null : Convert.ToBase64String(bytes);

    private sealed class DbDiagnostic
    {
        public string root { get; set; } = "";
        public string rootBytes { get; set; } = "";
        public string metadata { get; set; } = "";
        public string? metadataBytes { get; set; }
        public string storage { get; set; } = "";
        public string? storageBytes { get; set; }
        public string storageVtable { get; set; } = "";
        public string? storageVtableBytes { get; set; }
    }

    private static List<Chunk> ReadChunks(GreenMagic.Memory memory, ulong address)
    {
        var chunks = new List<Chunk>();
        for (int offset = 0; offset < WindowSize; offset += ChunkSize)
        {
            byte[]? bytes = memory.ReadBytes64(address + (ulong)offset, ChunkSize);
            if (bytes != null && bytes.Length == ChunkSize)
                chunks.Add(new Chunk { offset = offset, bytes = Convert.ToBase64String(bytes) });
        }
        return chunks;
    }

    private sealed class Chunk
    {
        public int offset { get; set; }
        public string bytes { get; set; } = "";
    }
}
